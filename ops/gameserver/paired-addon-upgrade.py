#!/usr/bin/env python3
"""One claimed addon/nVault transition; unknown effects retain matching pending."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import stat
import sys

DIRECTORY = Path(__file__).resolve().parent
sys.path.insert(0, str(DIRECTORY))
import player_storage_pair as pair

spec = importlib.util.spec_from_file_location('paired_upgrade_base', DIRECTORY / 'addon-upgrade.py')
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
policy = base.policy
require = pair.require
Uncertain = base.Uncertain
GAME, AGENT = base.GAME, base.AGENT
HOOKS = ('probe', 'backup', 'runtime')


def read_plan(path, expected, server=base.SERVER, config=base.CONFIG):
    plan = pair.decode(pair.reference({'path': str(path), 'sha256': expected}))
    return validate_plan(plan, server, config)


def validate_plan(plan, server, config):
    require(set(plan) == {'schema', 'operationId', 'previous', 'previousModule', 'candidate',
                          'baselines', 'hooks'} and type(plan['schema']) is int and plan['schema'] == 1)
    require(re.fullmatch('[a-z0-9][a-z0-9-]{0,63}', plan['operationId']) is not None)
    _, old = pair.package(plan['previous'], False)
    new_manifest, new = pair.package(plan['candidate'], True)
    old[pair.MODULE] = pair.reference(plan['previousModule'], 16 * 1024 * 1024)
    require(new_manifest['predecessor'] == {'addonManifestSha256': plan['previous']['sha256'],
                                           'moduleSha256': pair.digest(old[pair.MODULE])})
    require(old[pair.PLUGIN] != new[pair.PLUGIN] and old[pair.MODULE] != new[pair.MODULE])
    require(all(old[p] == new[p] for p in (pair.CONFIG, pair.LOADER, pair.DICTIONARY)),
            'This transition preserves configuration, loader and dictionary.')
    require(set(plan['baselines']) == {'previous', 'candidate'})
    before, after = [pair.reference(plan['baselines'][key]) for key in ('previous', 'candidate')]
    old_receipt = pair.receipt(plan['previous']['sha256'], {p: pair.digest(v) for p, v in old.items()})
    new_receipt = pair.receipt(plan['candidate']['sha256'], {p: pair.digest(v) for p, v in new.items()}, True)
    require(pair.transition(before, server, config, old, new, old_receipt, new_receipt) == after,
            'Candidate baseline is not the exact reviewed transition.')
    require(set(plan['hooks']) == set(HOOKS))
    for hook in plan['hooks'].values():
        pair.reference(hook)
    return plan


class Journal(base.Journal):
    def matching_pending(self):
        try:
            require(policy.read_json(self.pending) == {'schema': 1, 'planSha256': self.identity})
        except Exception as error:
            raise Uncertain('Pending identity changed; stop without rollback or clearing it.') from error

    def step(self, name, **details):
        self.matching_pending()
        return super().step(name, **details)

    def finish(self, name):
        require(name in base.TERMINAL)
        self.matching_pending()
        return super().finish(name)


def replace_exact(path, expected, target, metadata, journal, phase):
    """CAS of one file, with durable intent. Any incomplete write is uncertain."""
    require(pair.read(path, 16 * 1024 * 1024) == expected, 'Static target changed.')
    require(not any(p.is_symlink() for p in (path.parent, *path.parent.parents)))
    parent = path.parent.stat()
    require(parent.st_uid == 0 and parent.st_mode & 0o022 == 0)
    journal.step(phase, previousSha256=pair.digest(expected), targetSha256=pair.digest(target))
    temporary = path.with_name(path.name + '.pair-new')
    require(not temporary.exists() and not temporary.is_symlink(), 'Unresolved static temporary file.')
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(descriptor, 'wb') as stream:
            stream.write(target)
            stream.flush()
            os.fchown(stream.fileno(), metadata['uid'], metadata['gid'])
            os.fchmod(stream.fileno(), metadata['mode'])
            os.utime(stream.fileno(), ns=(metadata['atimeNs'], metadata['mtimeNs']))
            os.fsync(stream.fileno())
        require(pair.read(path, 16 * 1024 * 1024) == expected, 'Static target changed before replace.')
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    except BaseException as error:
        raise Uncertain('Static replace requires reconciliation.') from error
    require(pair.read(path, 16 * 1024 * 1024) == target)


class Host(base.Host):
    def __init__(self, plan, journal):
        super().__init__(plan, journal)
        self.server, self.config_root = base.SERVER, base.CONFIG
        self.initialize()

    def initialize(self):
        _, self.previous = pair.package(self.plan['previous'], False)
        _, self.candidate = pair.package(self.plan['candidate'], True)
        self.previous[pair.MODULE] = pair.reference(self.plan['previousModule'], 16 * 1024 * 1024)
        self.before, self.after = [pair.reference(self.plan['baselines'][key]) for key in ('previous', 'candidate')]
        self.receipt_path = self.config_root / 'weapon-selection-installed'
        self.old_receipt = pair.receipt(self.plan['previous']['sha256'], {p: pair.digest(v) for p, v in self.previous.items()})
        self.new_receipt = pair.receipt(self.plan['candidate']['sha256'], {p: pair.digest(v) for p, v in self.candidate.items()}, True)
        self.changes = [(self.server / p, self.previous[p], self.candidate[p]) for p in (pair.MODULE, pair.PLUGIN)]
        self.changes += [(self.receipt_path, self.old_receipt, self.new_receipt), (self.base, self.before, self.after)]
        self.metadata = {}

    def hook(self, kind, phase, directory, manifest_hash):
        hook = self.plan['hooks'][kind]
        pair.reference(hook)
        # Hash-bound root scripts implement the reviewed transport/read probes.
        # No credentials or player values appear in argv or coordinator stdout.
        try:
            raw = base.run(['bash', hook['path'], phase, str(directory), self.journal.identity, manifest_hash], timeout=120)
            result = pair.decode(raw)
            require(result.get('schema') == 1 and result.get('state') in ('verified', 'refused', 'uncertain')
                    and result.get('planSha256') == self.journal.identity and result.get('phase') == phase)
            common = {'schema', 'state', 'planSha256', 'phase'}
            fields = {'probe': {'samples', 'p99Us', 'maxUs'},
                'runtime': {'manifestSha256', 'addonVersion', 'storageHealthy', 'processContract'},
                'backup': {'plaintextSha256', 'restoredSha256', 'semanticSha256', 'oldReaderSemanticSha256',
                           'newReaderSemanticSha256', 'encrypted', 'offHost', 'independentRecovery'}}
            require(set(result) == common | (fields[kind] if result['state'] == 'verified' else set()))
        except BaseException as error:
            raise Uncertain('Missing or unclassified hook outcome requires reconciliation.') from error
        policy.write_json(directory / ('hook-' + kind + '-' + phase + '.json'), result)
        if result['state'] == 'uncertain':
            raise Uncertain('Hook reported an uncertain side effect.')
        require(result['state'] == 'verified', 'Hook explicitly refused this phase.')
        return result

    def validate_static(self, which=None):
        expected = {name: h for h, name in pair.baseline_rows(self.before)}
        changed = {str(path): (old, new) for path, old, new in self.changes}
        for path, old, new in self.changes:
            actual = pair.read(path, 16 * 1024 * 1024)
            require(actual in (old, new), 'Unknown static pair or baseline state.')
            if which is not None:
                require(actual == (new if which == 'candidate' else old), 'Mixed pair is not runnable.')
            if str(path) in self.metadata:
                recorded, info = self.metadata[str(path)], path.stat()
                require((info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
                        == (recorded['uid'], recorded['gid'], recorded['mode']), 'Static metadata drift.')
            require(not path.with_name(path.name + '.pair-new').exists()
                    and not path.with_name(path.name + '.pair-new').is_symlink(), 'Incomplete static replace.')
        for name, h in expected.items():
            if name not in changed:
                require(pair.digest(pair.read(Path(name), 16 * 1024 * 1024)) == h, 'Unrelated baseline drift.')

    def remember_metadata(self):
        for index, (path, old, new) in enumerate(self.changes):
            require(pair.read(path, 16 * 1024 * 1024) == old)
            info = path.stat()
            self.metadata[str(path)] = dict(uid=info.st_uid, gid=info.st_gid,
                mode=stat.S_IMODE(info.st_mode), atimeNs=info.st_atime_ns, mtimeNs=info.st_mtime_ns)
            target = self.journal.directory / ('previous-' + str(index))
            with target.open('xb') as stream:
                os.fchmod(stream.fileno(), 0o600)
                stream.write(old); stream.flush(); os.fsync(stream.fileno())
        policy.write_json(self.journal.directory / 'static-metadata.json', self.metadata)

    def consumers(self):
        root = self.server / pair.AMXX
        plugins = sorted((root / 'plugins').rglob('*.amxx'))
        require(len(plugins) <= 200)
        total = 0
        for path in plugins:
            raw = pair.read(path, 8 * 1024 * 1024)
            total += len(raw)
            require(total <= 64 * 1024 * 1024)
            uses = any(name.startswith('nvault_') for name in pair.native_names(raw))
            require(not uses or path == self.server / pair.PLUGIN, 'Unexpected nVault consumer.')
        modules = list((root / 'modules').glob('*.so'))
        require(len(modules) <= 64)
        for path in modules:
            raw = pair.read(path, 16 * 1024 * 1024)
            require(b'GetVaultMngr' not in raw or path == self.server / pair.MODULE,
                    'Unexpected native vault-manager consumer.')
        self.data_layout()

    def data_layout(self):
        entries = list(self.vault.iterdir())
        require(set(p.name for p in entries) <= set(base.bundle.NAMES), 'Retained or unknown vault files require reconciliation.')
        owner = self.vault.stat()
        require(stat.S_IMODE(owner.st_mode) == 0o700)
        for path in entries:
            info = path.lstat()
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == owner.st_uid
                    and info.st_gid == owner.st_gid and stat.S_IMODE(info.st_mode) in (0o600, 0o640))
            require(info.st_size <= (4 if path.suffix == '.journal' else 16) * 1024 * 1024)

    def snapshot(self):
        self.guard('quiescent')
        self.data_layout()
        files = base.bundle.inventory(self.vault)
        return files, pair.semantic(files)

    def backup(self, phase, manifest_hash, expected_semantic=None):
        self.backup_guard()
        files, semantic = self.snapshot()
        if expected_semantic is not None and semantic != expected_semantic:
            raise Uncertain('Closed semantic comparison failed; preserve data and reconcile.')
        directory = self.journal.directory / phase
        directory.mkdir(mode=0o700)
        archive = base.bundle.create(self.vault)
        require(base.bundle.inventory(self.vault) == files)
        with (directory / 'closed.tar').open('xb') as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(archive); stream.flush(); os.fsync(stream.fileno())
        policy.write_json(directory / 'expected.json', dict(schema=1, plaintextSha256=pair.digest(archive), semanticSha256=semantic))
        result = self.hook('backup', phase, directory, manifest_hash)
        require(result.get('plaintextSha256') == result.get('restoredSha256') == pair.digest(archive)
                and result.get('semanticSha256') == result.get('oldReaderSemanticSha256')
                == result.get('newReaderSemanticSha256') == semantic
                and all(result.get(key) is True for key in ('encrypted', 'offHost', 'independentRecovery')),
                'Closed off-host backup/reopen evidence rejected.')
        if base.bundle.inventory(self.vault) != files:
            raise Uncertain('Backup hook changed live data; preserve it and reconcile.')
        self.backup_guard()
        return files, semantic

    def backup_guard(self):
        # The coordinator already holds capture/transition locks. Reacquiring
        # them through player-data-capture.sh would deadlock; use its closed
        # bundle transport and actual baseline/quiescence guard in-process.
        self.guard('quiescent')
        base.run(['bash', str(DIRECTORY / 'player-data-schedule-guard.sh'), 'quiescent'])

    def install(self, which, closed):
        self.guard('quiescent')
        self.validate_static()
        for index, (path, old, new) in enumerate(self.changes):
            if self.snapshot()[0] != closed:
                raise Uncertain('Unexpected closed data change during installation.')
            actual = pair.read(path, 16 * 1024 * 1024)
            target = new if which == 'candidate' else old
            if actual != target:
                replace_exact(path, actual, target, self.metadata[str(path)], self.journal,
                              which + '-replace-' + str(index))
        if self.snapshot()[0] != closed:
            raise Uncertain('Unexpected closed data change after installation.')
        self.validate_static(which)

    def mapped(self):
        group = base.run(['systemctl', 'show', GAME, '--property=ControlGroup', '--value']).strip()
        require(group.startswith('/system.slice/') and '..' not in Path(group).parts)
        root = Path('/sys/fs/cgroup') / group.lstrip('/')
        files = [root / 'cgroup.procs', *root.glob('*/cgroup.procs')]
        require(len(files) <= 64)
        engines = []
        for file in files:
            raw = file.read_bytes()
            require(len(raw) <= 8192)
            for pid in raw.splitlines():
                process = Path('/proc') / str(int(pid))
                if Path(os.readlink(process / 'exe')).name == 'hlds_linux':
                    maps = (process / 'maps').read_bytes()
                    require(len(maps) <= 1024 * 1024)
                    engines.append(maps.decode())
        require(len(engines) == 1)
        module = self.server / pair.MODULE
        info = module.stat()
        device = f'{os.major(info.st_dev):02x}:{os.minor(info.st_dev):02x}'
        require(any(len(p := line.split(None, 5)) == 6 and p[5] == str(module)
                    and p[3] == device and int(p[4]) == info.st_ino for line in engines[0].splitlines()),
                'Loaded nVault must match the verified installed file.')

    def accept_pair(self, which, phase):
        self.validate_static(which)
        self.guard('running')
        before = self.unit(GAME)
        require(before['ActiveState'] == 'active' and before['NRestarts'] == '0')
        self.mapped()
        item = self.plan['candidate' if which == 'candidate' else 'previous']
        result = self.hook('runtime', phase, self.journal.directory, item['sha256'])
        require(result.get('manifestSha256') == item['sha256'] and result.get('storageHealthy') is True
                and result.get('addonVersion') == ('0.40.0' if which == 'candidate' else '0.39.0')
                and result.get('processContract') == (1 if which == 'candidate' else 0))
        require(self.unit(GAME) == before)


def execute(plan, journal, host):
    journal.claim(plan)
    stopped, started_candidate, agent = False, False, None
    try:
        host.guard('preflight')
        host.validate_static('previous')
        host.consumers()
        host.remember_metadata()
        agent = host.unit(AGENT)
        host.accept_pair('previous', 'pre-runtime')
        result = host.hook('probe', 'storage-probe', journal.directory, plan['candidate']['sha256'])
        require(type(result.get('samples')) is int and result['samples'] == 10000
                and type(result.get('p99Us')) is int and 0 <= result['p99Us'] <= 2000
                and type(result.get('maxUs')) is int and result['p99Us'] <= result['maxUs'] <= 120000000)
        require(host.empty(), 'Occupied server refuses installation.')
        journal.step('stopping')
        stopped = True
        host.stop()
        closed, semantic = host.backup('backup-before', plan['previous']['sha256'])
        # Revalidate frozen packages/scripts after the potentially long backup.
        require(policy.read_json(journal.directory / 'plan.json') == plan)
        validate_plan(plan, host.server, host.config_root)
        host.install('candidate', closed)
        journal.step('starting-candidate')
        host.start()
        started_candidate = True
        host.accept_pair('candidate', 'candidate-runtime')
        require(host.unit(AGENT) == agent, 'Agent changed during transition.')
        if not host.empty():
            raise Uncertain('Occupied server prevents closed acceptance; retain pending.')
        journal.step('closing-candidate')
        host.stop()
        host.backup('backup-after', plan['candidate']['sha256'], semantic)
        host.validate_static('candidate')
        journal.step('reopening-candidate')
        host.start()
        host.accept_pair('candidate', 'candidate-reopened')
        host.guard('baseline')
        require(host.unit(AGENT) == agent)
        return journal.finish('accepted')
    except Uncertain:
        journal.step('reconciliation-required')
        raise
    except Exception as error:
        journal.step('failed', errorCategory=type(error).__name__)
        if not stopped:
            return journal.finish('refused')
        try:
            require(agent is not None and host.unit(AGENT) == agent)
            host.validate_static()  # Unknown mixtures or unrelated drift never trigger a blind rollback.
            state = host.unit(GAME)['ActiveState']
            require(state in ('active', 'inactive'))
            if state == 'active':
                host.validate_static('candidate' if started_candidate else 'previous')
                require(host.empty(), 'Occupied server prevents rollback.')
                journal.step('rollback-stopping')
                host.stop()
            closed, _ = host.snapshot()
            if started_candidate:
                host.backup('backup-rollback', plan['candidate']['sha256'])
            host.install('previous', closed)
            journal.step('starting-previous')
            host.start()
            host.accept_pair('previous', 'rollback-runtime')
            host.guard('baseline')
            require(host.unit(AGENT) == agent)
            return journal.finish('rolled-back')
        except BaseException:
            journal.step('reconciliation-required')
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--plan-sha256', required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    require(os.geteuid() == 0 and not any(k.startswith('GOLDSRCOPS_') for k in os.environ))
    plan = read_plan(args.plan, args.plan_sha256)
    journal = Journal(policy.private_path(base.STATE, directory=True), args.plan_sha256)
    with policy.lock(base.STATE / 'schedule.lock'):
        with policy.lock(base.CONFIG / 'player-data-backup.lock', shared=True):
            for name in ('pending.json', 'agent-upgrade-pending.json', 'addon-upgrade-pending.json'):
                require(not (base.STATE / name).exists() and not (base.STATE / name).is_symlink())
            with policy.lock(base.CONFIG / 'game-event-pilot-install.lock', shared=True):
                host = Host(plan, journal)
                host.guard('identity')
                if args.apply:
                    result = execute(plan, journal, host)
                    print('PAIRED_ADDON_UPGRADE=' + result)
                    if result != 'accepted':
                        raise SystemExit(1)
                else:
                    host.guard('preflight')
                    host.validate_static('previous')
                    host.consumers()
                    print('PAIRED_ADDON_UPGRADE=verified-plan')


if __name__ == '__main__':
    os.umask(0o077)
    def interrupted(_signal, _frame):
        raise Uncertain('Interrupted transition requires reconciliation.')
    for number in (signal.SIGHUP, signal.SIGTERM, signal.SIGINT):
        signal.signal(number, interrupted)
    try:
        main()
    except Exception:
        print('PAIRED_ADDON_UPGRADE=failed; inspect_private_journal_before_retry', file=sys.stderr)
        sys.exit(1)
