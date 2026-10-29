#!/usr/bin/env python3
"""Hash-bound agent-only transition. Durable claims prohibit uncertain retries."""
import argparse
import base64
import contextlib
import copy
import fcntl
import grp
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import sqlite3
import stat
import subprocess
import sys
import time
import zipfile

CONFIG = Path('/etc/goldsrcops/gameserver')
INSTALL = Path('/opt/goldsrcops/gameserver/game-event-pilot')
SYSTEMD = Path('/etc/systemd/system')
STATE = Path('/var/lib/goldsrcops-player-backup')
HOME = Path('/var/lib/goldsrc')
GUARD = Path('/usr/local/libexec/goldsrcops-game-event-persistent')
CAPTURE = Path('/opt/goldsrcops-player-backup/ops/gameserver/player-data-scheduled-capture.py')
GAME = 'goldsrcops-gameserver.service'
AGENT = 'goldsrcops-game-event-agent.service'
BINARY = 'agent/GoldSrcOps.GameEventAgent'
HASH = re.compile(r'[a-f0-9]{64}')


class Uncertain(RuntimeError):
    pass


def require(condition, message='Agent upgrade boundary rejected.'):
    if not condition:
        raise ValueError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def safe_path(path):
    require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    return path


def trusted(path, limit=1024 * 1024, mode=None):
    safe_path(path)
    info = path.stat()
    require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and info.st_uid == 0
            and info.st_mode & 0o022 == 0 and info.st_size <= limit)
    if mode is not None:
        require(stat.S_IMODE(info.st_mode) == mode)
    return path.read_bytes()


def pairs(raw):
    lines = [line.split('=', 1) for line in raw.decode().splitlines()]
    require(all(len(x) == 2 and x[0] and x[1] for x in lines))
    require(len(dict(lines)) == len(lines))
    return dict(lines)


def encode(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':')) + '\n').encode()


def replace_pairs(raw, changes):
    value = pairs(raw)
    require(changes.keys() <= value.keys())
    value.update(changes)
    return ''.join(k + '=' + v + '\n' for k, v in value.items()).encode()


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def write_new(path, data, mode=0o600, gid=0):
    safe_path(path)
    with path.open('xb') as output:
        os.fchmod(output.fileno(), mode)
        os.fchown(output.fileno(), 0, gid)
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    sync_directory(path.parent)


def atomic(path, data, mode, gid):
    temporary = path.with_name(path.name + '.agent-upgrade-new')
    write_new(temporary, data, mode, gid)
    os.replace(temporary, path)
    sync_directory(path.parent)


@contextlib.contextmanager
def locked(path):
    safe_path(path)
    descriptor = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        info = os.fstat(descriptor)
        require(info.st_uid == 0 and info.st_nlink == 1 and stat.S_ISREG(info.st_mode)
                and info.st_mode & 0o027 == 0)
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


def run(arguments, timeout=45):
    with subprocess.Popen([str(x) for x in arguments], stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, start_new_session=True) as process:
        try:
            out, _ = process.communicate(timeout=timeout)
        except BaseException as error:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            raise Uncertain('Command interrupted; reconcile the durable journal.') from error
        if process.returncode < 0:
            raise Uncertain('Command terminated by a signal.')
        require(process.returncode == 0 and len(out) <= 65536, 'Command failed.')
        return out.decode()


def unit(name):
    data = pairs(run(['systemctl', 'show', name, '-p', 'ActiveState', '-p', 'SubState',
                      '-p', 'MainPID', '-p', 'InvocationID', '-p', 'NRestarts']).encode())
    require(data['ActiveState'] == 'active' and data['SubState'] == 'running'
            and int(data['MainPID']) > 0)
    return data


def guards():
    for option in ('--guard-game', '--guard-agent'):
        require(run([GUARD, option]).strip() == 'PERSISTENT_GAME_EVENT_GUARD=passed')


def queue_snapshot():
    path = safe_path(HOME / 'game-event-agent/queue/game-event-agent.db')
    require(path.is_file())
    with contextlib.closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True, timeout=5)) as connection:
        connection.execute('PRAGMA query_only=ON')
        require(connection.execute('PRAGMA user_version').fetchone()[0] == 2)
        require(connection.execute('SELECT COUNT(*) FROM game_event_outbox').fetchone()[0] == 0,
                'Queue work requires reconciliation before upgrade.')
        require(connection.execute('SELECT COUNT(*) FROM game_event_spool_receipt').fetchone()[0] == 0,
                'Spool receipts require reconciliation before upgrade.')
        rows = connection.execute('SELECT source_instance_id, next_sequence_number FROM agent_state').fetchall()
        require(len(rows) == 1 and rows[0][1] > 0)
    spool = HOME / 'server/cstrike/addons/amxmodx/data/goldsrcops-spool'
    for folder in ('incoming', 'processing', 'accepted', 'rejected'):
        directory = safe_path(spool / folder)
        require(directory.is_dir() and not any(directory.iterdir()), 'Spool is not settled.')
    return {'identitySha256': sha(rows[0][0].encode()), 'nextSequence': rows[0][1]}


def inventory():
    return [CONFIG / 'game-event-pilot-installed', INSTALL / 'activation/state',
            CONFIG / 'game-event-pilot-enabled', SYSTEMD / AGENT,
            CONFIG / 'game-event-persistent-active', CONFIG / 'player-data-backup-baseline.sha256', CAPTURE]


def verify_baseline(raw, changes=None):
    changes = changes or {}
    result, seen = [], set()
    for line in raw.decode().splitlines():
        match = re.fullmatch(r'([a-f0-9]{64})  (/[^\r\n]+)', line)
        require(match is not None)
        expected, path = match.groups()
        require(sha(trusted(Path(path), 16 * 1024 * 1024)) == expected, 'Unrelated backup baseline drift.')
        if path in changes:
            expected = sha(changes[path])
            seen.add(path)
        result.append(expected + '  ' + path + '\n')
    require(seen == set(changes))
    return ''.join(result).encode()


def package(archive, expected):
    require(HASH.fullmatch(expected) is not None)
    raw = trusted(archive, 160 * 1024 * 1024, 0o600)
    require(sha(raw) == expected, 'Package hash mismatch.')
    with zipfile.ZipFile(archive) as bundle:
        entries = [item for item in bundle.infolist() if not item.is_dir()]
        require(len(entries) == 2 and {x.filename for x in entries} == {'manifest.json', BINARY})
        require(all(not stat.S_ISLNK(x.external_attr >> 16) and x.file_size <= 160 * 1024 * 1024 for x in entries))
        require(bundle.getinfo('manifest.json').file_size <= 4096)
        manifest = json.loads(bundle.read('manifest.json'))
        require(set(manifest) == {'schemaVersion', 'purpose', 'version', 'sourceRevision',
                                 'targetRuntime', 'queueSchemaVersion', 'payload'})
        require(manifest['schemaVersion'] == 1 and manifest['purpose'] == 'game-event-agent'
                and manifest['targetRuntime'] == 'linux-x64' and manifest['queueSchemaVersion'] == 2
                and re.fullmatch(r'[a-f0-9]{40}', manifest['sourceRevision'])
                and re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.]+)?', manifest['version']))
        require(len(manifest['payload']) == 1)
        item = manifest['payload'][0]
        binary = bundle.read(BINARY)
        require(item == {'path': BINARY, 'length': len(binary), 'sha256': sha(binary), 'mode': '0750'})
        return manifest, binary


def verify_release(directory, manifest):
    require(all(not p.is_symlink() for p in (directory, *directory.rglob('*'))))
    expected = {'manifest.json'}
    for item in manifest['payload']:
        name = item['path']
        require(re.fullmatch(r'[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)+', name)
                and all(x not in ('.', '..') for x in name.split('/')) and name not in expected)
        expected.add(name)
        raw = trusted(directory / name, 160 * 1024 * 1024, int(item['mode'], 8))
        require(len(raw) == item['length'] and sha(raw) == item['sha256'])
    actual = {str(p.relative_to(directory)) for p in directory.rglob('*') if not p.is_dir()}
    require(actual == expected)


def prepare(archive, expected, directory):
    guards()
    unit(AGENT)
    game = unit(GAME)
    queue = queue_snapshot()
    paths = inventory()
    old = {str(p): trusted(p) for p in paths}
    verify_baseline(old[str(paths[5])])
    anchor = b"    pending_addon = ledger.root / 'addon-upgrade-pending.json'\n"
    require(old[str(CAPTURE)].count(anchor) == 1 and b'agent-upgrade-pending.json' not in old[str(CAPTURE)],
            'Scheduled capture must match the reviewed legacy boundary.')
    pilot = pairs(old[str(paths[0])])
    require(HASH.fullmatch(pilot['bundle_sha256']) is not None)
    previous = INSTALL / 'releases' / pilot['bundle_sha256']
    raw_manifest = trusted(previous / 'manifest.json')
    require(sha(raw_manifest) == pilot['manifest_sha256'])
    manifest = json.loads(raw_manifest)
    verify_release(previous, manifest)
    candidate, binary = package(archive, expected)
    require(sum(x['path'] == BINARY for x in manifest['payload']) == 1)
    require(not directory.exists() and not directory.is_symlink())
    safe_path(directory).mkdir(mode=0o700)
    stage = directory / 'release'
    shutil.copytree(previous, stage, copy_function=shutil.copy2)
    (stage / BINARY).write_bytes(binary)
    os.chmod(stage / BINARY, 0o750)
    manifest = copy.deepcopy(manifest)
    manifest['bundleVersion'] = candidate['version']
    manifest['sourceRevision'] = candidate['sourceRevision']
    manifest['agentUpgrade'] = {'packageSha256': expected, 'previousManifestSha256': pilot['manifest_sha256']}
    manifest['payload'] = [candidate['payload'][0] if x['path'] == BINARY else x for x in manifest['payload']]
    (stage / 'manifest.json').write_bytes(encode(manifest))
    os.chmod(stage / 'manifest.json', 0o640)
    group = grp.getgrnam('goldsrc').gr_gid
    for path in (stage, *stage.rglob('*')):
        os.chown(path, 0, group)
        if path.is_dir():
            os.chmod(path, 0o750)
        else:
            with path.open('rb') as source:
                os.fsync(source.fileno())
    for path in sorted((p for p in stage.rglob('*') if p.is_dir()), reverse=True):
        sync_directory(path)
    sync_directory(stage)
    verify_release(stage, manifest)
    # The derived bundle retains every original game/producer byte. Its digest remains
    # an actual archive identity, preserving the existing guard and marker contract.
    derived = directory / 'derived-bundle.zip'
    with zipfile.ZipFile(derived, 'x', compression=zipfile.ZIP_DEFLATED) as output:
        for path in sorted(p for p in stage.rglob('*') if p.is_file()):
            output.write(path, str(path.relative_to(stage)))
    bundle_hash = sha(trusted(derived, 200 * 1024 * 1024))
    destination = INSTALL / 'releases' / bundle_hash
    require(not destination.exists())
    manifest_hash = sha(encode(manifest))
    new = dict(old)
    new[str(CAPTURE)] = old[str(CAPTURE)].replace(anchor,
        b"    pending_agent = ledger.root / 'agent-upgrade-pending.json'\n"
        b"    policy.require(not pending_agent.exists() and not pending_agent.is_symlink(),\n"
        b"                   'Agent upgrade requires reconciliation before scheduled capture.')\n" + anchor)
    new[str(paths[3])] = old[str(paths[3])].replace(str(previous).encode(), str(destination).encode())
    require(new[str(paths[3])] != old[str(paths[3])])
    new[str(paths[0])] = replace_pairs(old[str(paths[0])], {
        'bundle_version': candidate['version'], 'source_revision': candidate['sourceRevision'],
        'bundle_sha256': bundle_hash, 'manifest_sha256': manifest_hash,
        'service_unit_sha256': sha(new[str(paths[3])])})
    new[str(paths[1])] = replace_pairs(old[str(paths[1])], {
        'pilot_marker_sha256': sha(new[str(paths[0])]), 'bundle_sha256': bundle_hash,
        'manifest_sha256': manifest_hash})
    new[str(paths[2])] = replace_pairs(old[str(paths[2])], {'activation_state_sha256': sha(new[str(paths[1])])})
    new[str(paths[4])] = replace_pairs(old[str(paths[4])], {
        'pilot_marker_sha256': sha(new[str(paths[0])]), 'pilot_gate_sha256': sha(new[str(paths[2])]),
        'activation_state_sha256': sha(new[str(paths[1])]), 'manifest_sha256': manifest_hash,
        'agent_unit_sha256': sha(new[str(paths[3])])})
    new[str(paths[5])] = verify_baseline(old[str(paths[5])], {str(paths[3]): new[str(paths[3])], str(paths[4]): new[str(paths[4])]})
    refs = []
    for path in paths:
        info = path.stat()
        refs.append({'path': str(path), 'mode': stat.S_IMODE(info.st_mode), 'gid': info.st_gid,
                     'old': base64.b64encode(old[str(path)]).decode(), 'new': base64.b64encode(new[str(path)]).decode()})
    plan = {'schema': 1, 'packageSha256': expected, 'bundleSha256': bundle_hash,
            'manifestSha256': manifest_hash, 'game': game, 'queue': queue, 'refs': refs,
            'guardSha256': sha(trusted(GUARD)), 'workflowSha256': sha(trusted(Path(__file__).resolve())),
            'version': candidate['version'], 'sourceRevision': candidate['sourceRevision']}
    write_new(directory / 'plan.json', encode(plan))
    return sha(encode(plan))


def load_plan(directory, expected):
    require(HASH.fullmatch(expected) is not None)
    raw = trusted(directory / 'plan.json', mode=0o600)
    require(sha(raw) == expected)
    plan = json.loads(raw)
    require(plan['schema'] == 1 and [x['path'] for x in plan['refs']] == [str(x) for x in inventory()])
    require(sha(trusted(GUARD)) == plan['guardSha256']
            and sha(trusted(Path(__file__).resolve())) == plan['workflowSha256'])
    return plan


def verify_refs(plan, direction):
    for item in plan['refs']:
        path = Path(item['path'])
        require(trusted(path, mode=item['mode']) == base64.b64decode(item[direction], validate=True)
                and path.stat().st_gid == item['gid'], 'Reference drift requires reconciliation.')


def install_refs(plan, direction):
    # A journal persists before the first reference changes. Partial writes stay
    # guarded by the old/new hash graph and are never treated as a completed retry.
    entries = plan['refs']
    if direction == 'new':
        entries = entries[-1:] + entries[:-1]
    for item in entries:
        atomic(Path(item['path']), base64.b64decode(item[direction], validate=True), item['mode'], item['gid'])


def note(directory, phase):
    atomic(directory / 'status.json', encode({'phase': phase}), 0o600, 0)


def accept(plan):
    guards()
    require(unit(GAME) == plan['game'], 'Game process changed.')
    unit(AGENT)
    require(queue_snapshot() == plan['queue'], 'Queue identity or sequence changed.')
    verify_baseline(trusted(CONFIG / 'player-data-backup-baseline.sha256'))


def diagnostic_status(plan):
    environment = pairs(trusted(CONFIG / 'game-event-agent.env'))
    require(all(re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', key) for key in environment)
            and environment.get('GameEventAgent__Spool__Enabled') == 'true'
            and environment.get('GameEventAgent__Delivery__Enabled') == 'true')
    environment.update({'HOME': str(HOME), 'PATH': '/usr/bin:/bin',
                        'DOTNET_BUNDLE_EXTRACT_BASE_DIR': str(HOME / 'game-event-agent/dotnet-bundle'),
                        'GameEventAgent__Delivery__OAuth__ClientSecretFile': str(CONFIG / 'secrets/game-event-agent-client-secret')})
    binary = INSTALL / 'releases' / plan['bundleSha256'] / BINARY
    raw = run(['runuser', '-u', 'goldsrc', '--', 'env', '-i',
               *[key + '=' + value for key, value in environment.items()], binary, 'status', '--json'])
    value = json.loads(raw)
    require(value['schemaVersion'] == 1 and value['queue']['pending'] == 0
            and value['queue']['inFlight'] == 0 and value['queue']['deadLetter'] == 0
            and value['queue']['nextSequenceNumber'] == plan['queue']['nextSequence']
            and value['oldestWaitingAgeSeconds'] is None and value['spoolAvailable'] is True)
    for component in ('import', 'delivery'):
        loop = value['health'][component]
        require(loop['state'] == 'running' and loop['cycle']['outcome'] == 'idle'
                and loop['cycle']['lastSuccessfulAtUtc'] is not None, 'Idle cycle evidence is not ready.')
    return value


def execute(directory, plan, rollback=False):
    status = directory / 'status.json'
    if rollback:
        require(json.loads(trusted(status))['phase'] == 'accepted', 'Rollback requires a known accepted transition.')
        verify_refs(plan, 'new')
    else:
        require(not status.exists() and not status.is_symlink(), 'A claimed transition must not be repeated.')
        verify_refs(plan, 'old')
    accept(plan)
    pending = STATE / 'agent-upgrade-pending.json'
    require(not pending.exists() and not pending.is_symlink(), 'An agent transition requires reconciliation.')
    desired = 'old' if rollback else 'new'
    destination = INSTALL / 'releases' / plan['bundleSha256']
    if not rollback:
        require(not destination.exists())
        require(sha(trusted(directory / 'derived-bundle.zip', 200 * 1024 * 1024)) == plan['bundleSha256'])
        manifest = json.loads(trusted(directory / 'release/manifest.json'))
        require(sha(encode(manifest)) == plan['manifestSha256'])
        verify_release(directory / 'release', manifest)
    write_new(pending, encode({'schema': 1, 'planSha256': sha(trusted(directory / 'plan.json'))}))
    note(directory, 'rollback-prepared' if rollback else 'staging')
    try:
        if not rollback:
            os.rename(directory / 'release', destination)
            sync_directory(destination.parent)
        note(directory, 'rollback-stopping' if rollback else 'stopping')
        run(['systemctl', 'stop', AGENT])
        require(run(['systemctl', 'show', AGENT, '-p', 'ActiveState', '--value']).strip() == 'inactive')
        require(queue_snapshot() == plan['queue'])
        note(directory, 'rollback-installing' if rollback else 'installing')
        install_refs(plan, desired)
        run(['systemctl', 'daemon-reload'])
        guards()
        note(directory, 'rollback-starting' if rollback else 'starting')
        run(['systemctl', 'start', AGENT])
        for _ in range(10):
            try:
                accept(plan)
                if not rollback:
                    diagnostic_status(plan)
                break
            except ValueError:
                time.sleep(1)
        else:
            raise ValueError('Agent acceptance failed.')
        verify_refs(plan, desired)
        note(directory, 'rolled-back' if rollback else 'accepted')
        pending.unlink()
        sync_directory(STATE)
    except (Uncertain, KeyboardInterrupt, SystemExit):
        note(directory, 'reconciliation-required')
        raise
    except Exception:
        if rollback:
            note(directory, 'reconciliation-required')
            raise
        # Only a completed command failure permits this one bounded rollback. A
        # timeout/signal takes the separate branch above and never retries a mutation.
        try:
            note(directory, 'rollback-checking')
            require(unit(GAME) == plan['game'] and queue_snapshot() == plan['queue'])
            for item in plan['refs']:
                require(trusted(Path(item['path'])) in (
                    base64.b64decode(item['old'], validate=True), base64.b64decode(item['new'], validate=True)))
            run(['systemctl', 'stop', AGENT])
            require(queue_snapshot() == plan['queue'])
            install_refs(plan, 'old')
            run(['systemctl', 'daemon-reload'])
            guards()
            run(['systemctl', 'start', AGENT])
            accept(plan)
            verify_refs(plan, 'old')
            note(directory, 'rolled-back')
            pending.unlink()
            sync_directory(STATE)
        except BaseException:
            note(directory, 'reconciliation-required')
            raise
        raise ValueError('Candidate failed; previous agent restored and accepted.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('prepare', 'apply', 'rollback', 'inspect'))
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--package', type=Path)
    parser.add_argument('--sha256', required=True)
    args = parser.parse_args()
    require(os.geteuid() == 0 and not any(k.startswith('GOLDSRCOPS_') for k in os.environ))
    safe_path(args.directory)
    if args.operation == 'inspect':
        load_plan(args.directory, args.sha256)
        print(trusted(args.directory / 'status.json').decode().strip())
        return
    with locked(STATE / 'schedule.lock'), locked(CONFIG / 'player-data-backup.lock'), locked(CONFIG / 'game-event-pilot-install.lock'):
        require(not any((STATE / name).exists() or (STATE / name).is_symlink()
                        for name in ('pending.json', 'addon-upgrade-pending.json', 'agent-upgrade-pending.json')))
        if args.operation == 'prepare':
            require(args.package is not None)
            print('AGENT_UPGRADE_PLAN_SHA256=' + prepare(args.package, args.sha256, args.directory))
        else:
            require(args.package is None)
            plan = load_plan(args.directory, args.sha256)
            execute(args.directory, plan, args.operation == 'rollback')
            print('AGENT_UPGRADE=' + ('rolled-back' if args.operation == 'rollback' else 'accepted'))


if __name__ == '__main__':
    os.umask(0o077)
    def interrupted(_number, _frame):
        raise Uncertain('Interrupted; reconcile the durable journal.')
    for number in (signal.SIGHUP, signal.SIGTERM, signal.SIGINT):
        signal.signal(number, interrupted)
    try:
        main()
    except Exception:
        print('AGENT_UPGRADE=failed; inspect_private_evidence_before_retry', file=sys.stderr)
        sys.exit(1)
