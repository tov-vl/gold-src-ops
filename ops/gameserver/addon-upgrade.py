#!/usr/bin/env python3
"""One claimed addon transition; unknown outcomes require operator reconciliation."""
import argparse
import importlib.util
import fcntl
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time

DIRECTORY = Path(__file__).resolve().parent
sys.path.insert(0, str(DIRECTORY))
import player_data_schedule as policy


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, DIRECTORY / filename)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


baseline = module('upgrade_baseline', 'player-data-baseline.py')
bundle = module('upgrade_vault', 'player-data-bundle.py')
STATE = baseline.STATE
CONFIG = baseline.CONFIG
SERVER = baseline.SERVER
GAME = 'goldsrcops-gameserver.service'
AGENT = 'goldsrcops-game-event-agent.service'
TERMINAL = {'accepted', 'rolled-back', 'refused'}


class Uncertain(RuntimeError):
    pass


def run(args, timeout=45, pass_fds=()):
    # A timeout or signal can interrupt a host mutation. Kill its process group,
    # retain the claim and never interpret that result as permission to retry.
    with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          start_new_session=True, pass_fds=pass_fds) as process:
        try:
            out, _ = process.communicate(timeout=timeout)
        except BaseException as error:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise Uncertain('Command interrupted.') from error
        policy.require(process.returncode == 0 and len(out) <= 65536, 'Command failed.')
        return out.decode('utf-8')


def digest_file(path):
    return baseline.digest(baseline.trusted_bytes(Path(path), 16 * 1024 * 1024))


def package(item, kind):
    policy.require(set(item) == {'directory', 'sha256'})
    directory = Path(item['directory'])
    policy.require(directory.is_absolute() and re.fullmatch('[a-f0-9]{64}', item['sha256']))
    raw = baseline.trusted_bytes(directory / 'manifest.json')
    policy.require(baseline.digest(raw) == item['sha256'])
    value = json.loads(raw)
    weapon = kind == 'weapon-selection'
    names = ('goldsrcops_weapon_selection.amxx', 'goldsrcops-weapon-selection.cfg',
             'plugins-goldsrcops-weapons.ini', 'goldsrcops-player-menu.txt') if weapon else (
             'goldsrcops_map_menu.amxx', 'goldsrcops-map-menu.cfg',
             'plugins-goldsrcops-maps.ini', 'goldsrcops-map-menu.txt')
    paths = [f'cstrike/addons/amxmodx/{folder}/{name}' for folder, name in zip(
        ('plugins', 'configs/plugins', 'configs', 'data/lang'), names)]
    policy.require(value['schemaVersion'] == 2 and value['enabledByDefault'] is True
                   and value['productionInstallSupported'] is True
                   and value['purpose'] == ('game-host-addon' if weapon else 'map-menu-addon')
                   and value['amxxVersion'] == '1.10.0.5481' and value['reApiVersion'] == '5.24.0.300'
                   and re.fullmatch('[a-f0-9]{64}', value['sourceSha256'])
                   and len(value['payload']) == 4
                   and sorted(x['path'] for x in value['payload']) == sorted(paths))
    hashes = {x['path']: x['sha256'] for x in value['payload']}
    for path in paths:
        policy.require(digest_file(directory / path) == hashes[path])
    config = baseline.trusted_bytes(directory / paths[1], 256)
    command = b'goldsrcops_weapons_enabled "1"\n' if weapon else b'goldsrcops_maps_enabled "1"\n'
    allowed = (command, command + b'goldsrcops_player_lookup_enabled "1"\n') if weapon else (command,)
    policy.require(config in allowed, 'Unexpected addon configuration.')
    policy.require(baseline.trusted_bytes(directory / paths[2], 256) == (names[0] + '\n').encode(),
                   'Unexpected addon loader.')
    return paths, hashes


def read_plan(path, expected):
    raw = baseline.trusted_bytes(path, 16384)
    policy.require(re.fullmatch('[a-f0-9]{64}', expected) and baseline.digest(raw) == expected)
    plan = json.loads(raw)
    policy.require(set(plan) == {'schema', 'operationId', 'kind', 'previous', 'candidate', 'baselineSha256', 'acceptance'}
                   and plan['schema'] == 1 and re.fullmatch('[a-z0-9][a-z0-9-]{0,63}', plan['operationId'])
                   and plan['kind'] in ('weapon-selection', 'map-menu'))
    paths, old = package(plan['previous'], plan['kind'])
    _, new = package(plan['candidate'], plan['kind'])
    policy.require(old[paths[0]] != new[paths[0]] and old[paths[2]] == new[paths[2]],
                   'A changed binary and unchanged loader are required.')
    policy.require(re.fullmatch('[a-f0-9]{64}', plan['baselineSha256']))
    check = plan['acceptance']
    policy.require(set(check) == {'path', 'sha256'} and Path(check['path']).is_absolute()
                   and digest_file(check['path']) == check['sha256'])
    return plan


class Journal:
    def __init__(self, root, identity):
        self.root, self.identity = root, identity
        self.pending = root / 'addon-upgrade-pending.json'
        self.directory = root / ('addon-upgrade-' + identity)

    def claim(self, plan):
        policy.require(not self.pending.exists() and not self.pending.is_symlink(),
                       'An addon transition requires reconciliation.')
        policy.require(not self.directory.exists() and not self.directory.is_symlink(),
                       'This plan was already claimed; inspect its result.')
        policy.require(len(list(self.root.glob('addon-upgrade-*'))) < 52, 'Review evidence capacity.')
        self.directory.mkdir(mode=0o700)
        policy.write_json(self.directory / 'plan.json', plan)
        policy.write_json(self.pending, {'schema': 1, 'planSha256': self.identity})
        self.step('claimed')

    def step(self, name, **details):
        record = {'schema': 1, 'planSha256': self.identity, 'phase': name,
                  'observedUtc': policy.now().isoformat(), **details}
        policy.write_json(self.directory / (name + '.json'), record)
        policy.write_json(self.directory / 'status.json', record, replace=True)

    def finish(self, name):
        policy.require(name in TERMINAL)
        self.step(name)
        policy.private_path(self.pending).unlink()
        return name


class Host:
    def __init__(self, plan, journal, transition_fd=None):
        self.transition_fd = transition_fd
        self.plan, self.journal = plan, journal
        self.base = baseline.BASELINE
        self.vault = SERVER / 'cstrike/addons/amxmodx/data/vault'
        self.config = policy.read_json(CONFIG / 'player-data-backup.json')
        policy.require(self.config['schema'] == 1 and self.config['vaultDirectory'] == str(self.vault))
        addresses = json.loads(run(['ip', '-j', 'address', 'show']))
        local = {x['local'] for i in addresses for x in i['addr_info'] if x['family'] == 'inet'}
        endpoint = run(['bash', str(DIRECTORY / 'player-data-schedule-guard.sh'), 'endpoint'])
        policy.require(self.config['host'] in local and int(endpoint) == self.config['port'])

    def guard(self, phase):
        run(['bash', str(DIRECTORY / 'addon-upgrade-guard.sh'), phase])

    def unit(self, name):
        fields = dict(x.split('=', 1) for x in run(['systemctl', 'show', name,
                      '--property=ActiveState,SubState,InvocationID,NRestarts']).splitlines())
        policy.require(set(fields) == {'ActiveState', 'SubState', 'InvocationID', 'NRestarts'})
        return fields

    def empty(self):
        return policy.probe(self.config)

    def stop(self):
        try:
            run(['systemctl', 'stop', GAME], timeout=60)
        except Exception as error:
            raise Uncertain('Stop outcome requires inspection.') from error
        self.guard('quiescent')

    def start(self):
        try:
            run(['systemctl', 'start', GAME])
        except Exception as error:
            raise Uncertain('Start outcome requires inspection.') from error

    def vault_hashes(self):
        self.guard('quiescent')
        return {name: baseline.digest(data) if data is not None else None
                for name, data in bundle.inventory(self.vault).items()}

    def prepare(self, item, expected):
        return baseline.prepare(self.base, SERVER, CONFIG, Path(item['directory']) / 'manifest.json',
                                item['sha256'], expected, self.plan['kind'])

    def install(self, target, predecessor):
        args = ['bash', str(DIRECTORY / 'weapon-selection-addon.sh')]
        if self.plan['kind'] == 'map-menu':
            args.append('--map-menu')
        fd = getattr(self, 'transition_fd', None)
        if fd is not None:
            args += ['--transition-lock-fd', str(fd)]
        run(args + ['--upgrade', '--bundle', target['directory'], '--manifest-sha256', target['sha256'],
                    '--expected-manifest-sha256', predecessor['sha256'], '--apply'],
            pass_fds=() if fd is None else (fd,))

    def accept(self, item):
        before = self.unit(GAME)
        policy.require(before['NRestarts'] == '0', 'Unexpected game restart.')
        self.guard('running')
        deadline = time.monotonic() + 30
        while True:
            try:
                self.empty()  # A player joining after start is allowed.
                break
            except (ValueError, OSError):
                if time.monotonic() >= deadline:
                    raise
                time.sleep(1)
        check = self.plan['acceptance']
        policy.require(digest_file(check['path']) == check['sha256'])
        # Reviewed root-owned read-only script must check the running version,
        # plugin health and AMXX errors for this exact manifest. No shell strings.
        run(['bash', check['path'], self.plan['kind'], item['sha256']])
        policy.require(self.unit(GAME) == before, 'Game changed during acceptance.')

    def refresh(self, item, expected, phase):
        old, new = self.prepare(item, expected)
        self.journal.step(phase, previousSha256=baseline.digest(old), acceptedSha256=baseline.digest(new))
        before = {name: self.unit(name) for name in (GAME, AGENT)}
        if old != new:
            baseline.apply(self.base, self.journal.directory / (phase + '-evidence'), old, new, item['sha256'])
        policy.require(baseline.digest(baseline.trusted_bytes(self.base)) == baseline.digest(new))
        policy.require(not self.base.with_name(self.base.name + '.new').exists(), 'Incomplete baseline write.')
        self.guard('baseline')
        policy.require(before == {name: self.unit(name) for name in (GAME, AGENT)})
        return baseline.digest(new)


def execute(plan, journal, host):
    journal.claim(plan)
    previous, candidate = plan['previous'], plan['candidate']
    current_baseline = plan['baselineSha256']
    stopped = False
    vaults = None
    try:
        host.guard('preflight')
        agent = host.unit(AGENT)
        old, new = host.prepare(previous, current_baseline)
        policy.require(old == new, 'Predecessor baseline is not current.')
        host.accept(previous)
        journal.step('preflight-passed', game=host.unit(GAME), agent=agent,
                     baselineSha256=current_baseline)
        policy.require(host.empty(), 'Server is occupied.')
        journal.step('stopping')
        stopped = True
        host.stop()
        vaults = host.vault_hashes()
        journal.step('installing', vaultHashes=vaults)
        host.install(candidate, previous)
        policy.require(host.vault_hashes() == vaults, 'Closed player data changed.')
        host.prepare(candidate, current_baseline)  # Payload, receipt and unrelated invariants.
        journal.step('starting')
        host.start()
        host.accept(candidate)
        policy.require(host.unit(AGENT) == agent, 'Agent changed.')
        journal.step('runtime-accepted')
        current_baseline = host.refresh(candidate, current_baseline, 'baseline-forward')
        policy.require(host.unit(AGENT) == agent, 'Agent changed.')
        return journal.finish('accepted')
    except Uncertain:
        journal.step('reconciliation-required', failedPhase=policy.read_json(journal.directory / 'status.json')['phase'])
        raise
    except Exception as error:
        journal.step('failed', failedPhase=policy.read_json(journal.directory / 'status.json')['phase'],
                     errorCategory=type(error).__name__)
        if not stopped:
            return journal.finish('refused')
        # A completed failure is recoverable only from a verified known package.
        # Do not retry stop/start/install after an interrupted command.
        try:
            journal.step('rollback-checking')
            policy.require(host.unit(AGENT) == agent, 'Agent changed.')
            # A baseline write may have completed before a later guard failed.
            forward = journal.directory / 'baseline-forward.json'
            if forward.exists():
                allowed = policy.read_json(forward)
                actual = baseline.digest(baseline.trusted_bytes(host.base))
                policy.require(actual in (allowed['previousSha256'], allowed['acceptedSha256']))
                current_baseline = actual
            installed = None
            for item in (previous, candidate):
                try:
                    host.prepare(item, current_baseline)
                    installed = item
                    break
                except ValueError:
                    pass
            policy.require(installed is not None, 'Installed artifact is uncertain.')
            state = host.unit(GAME)['ActiveState']
            policy.require(state in ('active', 'inactive'), 'Game state is uncertain.')
            if state == 'active':
                policy.require(host.empty(), 'Occupied server prevents rollback.')
                journal.step('rollback-stopping')
                host.stop()
            retained = host.vault_hashes()
            if state == 'inactive' and vaults is not None:
                policy.require(retained == vaults, 'Closed player data changed; reconcile without restore.')
            journal.step('rollback-installing', vaultHashes=retained)
            if installed != previous:
                host.install(previous, candidate)
            policy.require(host.vault_hashes() == retained, 'Rollback changed player data.')
            host.prepare(previous, current_baseline)
            journal.step('rollback-starting')
            host.start()
            host.accept(previous)
            host.refresh(previous, current_baseline, 'baseline-rollback')
            policy.require(host.unit(AGENT) == agent, 'Agent changed.')
            return journal.finish('rolled-back')
        except BaseException:
            journal.step('reconciliation-required', failedPhase=policy.read_json(journal.directory / 'status.json')['phase'])
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--plan-sha256', required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true')
    mode.add_argument('--inspect', action='store_true')
    args = parser.parse_args()
    policy.require(os.geteuid() == 0 and re.fullmatch('[a-f0-9]{64}', args.plan_sha256))
    journal = Journal(policy.private_path(STATE, directory=True), args.plan_sha256)
    if args.inspect:
        record = policy.read_json(journal.directory / 'status.json')
        print(json.dumps({'phase': record['phase'], 'pending': journal.pending.exists()}))
        return
    plan = read_plan(args.plan, args.plan_sha256)
    # Frozen source is staged root-owned together; reject host-path overrides.
    policy.require(not any(k.startswith('GOLDSRCOPS_') for k in os.environ))
    with policy.lock(STATE / 'schedule.lock'):
        with policy.lock(CONFIG / 'player-data-backup.lock', shared=True):
            policy.require(not (STATE / 'pending.json').exists() and not (STATE / 'pending.json').is_symlink())
            with policy.lock(CONFIG / 'game-event-pilot-install.lock', shared=True) as lock_fd:
                # Keep the same open-file-description lock through the shell installer.
                # Descriptors 6-9 belong to existing shell workflows.
                fd = fcntl.fcntl(lock_fd, fcntl.F_DUPFD_CLOEXEC, 10)
                try:
                    host = Host(plan, journal, fd)
                    host.guard('identity')
                    if not args.apply:
                        host.guard('preflight')
                        old, new = host.prepare(plan['previous'], plan['baselineSha256'])
                        policy.require(old == new)
                        print('ADDON_UPGRADE=verified-plan')
                        return
                    result = execute(plan, journal, host)
                    print('ADDON_UPGRADE=' + result)
                    if result != 'accepted':
                        sys.exit(1)
                finally:
                    os.close(fd)


if __name__ == '__main__':
    os.umask(0o077)
    def interrupted(_signal, _frame):
        raise Uncertain('Interrupted; inspect the durable phase before further action.')
    for number in (signal.SIGHUP, signal.SIGTERM, signal.SIGINT):
        signal.signal(number, interrupted)
    try:
        main()
    except Exception:
        print('ADDON_UPGRADE=failed; inspect_private_journal_before_retry', file=sys.stderr)
        sys.exit(1)
