"""Read-only game release checks. Used by the local SSH coordinator, not installed."""
import hashlib
import io
import ipaddress
import json
import os
from pathlib import Path, PureWindowsPath
import re
import socket
import stat
import subprocess
import time
import traceback
import urllib.request
import uuid


def require(value):
    if not value:
        raise ValueError('Acceptance contract failed.')


def run(args, timeout=15):
    result = subprocess.run(args, capture_output=True, timeout=timeout)
    require(result.returncode == 0 and len(result.stdout) <= 1024 * 1024)
    return result.stdout


def trusted(path, limit=65536, owner=0, mode=None):
    require(not any(p.is_symlink() for p in (path, *path.parents)))
    details = path.stat()
    require(stat.S_ISREG(details.st_mode) and details.st_uid == owner
            and details.st_nlink == 1 and details.st_size <= limit
            and not details.st_mode & 0o022)
    if mode is not None:
        require(stat.S_IMODE(details.st_mode) == mode)
    with path.open('rb') as source:
        raw = source.read(limit + 1)
    require(len(raw) <= limit)
    return raw


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical_mounts(mounts):
    # Sort objects as a multiset: duplicates or changed options must still fail.
    return sorted(json.dumps(item, sort_keys=True, separators=(',', ':')) for item in mounts)


def validate_profile(profile):
    require(set(profile) == {'schema', 'sshConfig', 'gameAlias', 'controlPlaneAlias',
                            'host', 'port', 'expectedName', 'version', 'manifestSha256', 'baselineSha256'})
    require(type(profile['schema']) is int and profile['schema'] == 1)
    address = ipaddress.IPv4Address(profile['host'])
    require(not address.is_loopback and not address.is_unspecified and not address.is_multicast)
    require(type(profile['port']) is int and 1 <= profile['port'] <= 65535)
    require(isinstance(profile['expectedName'], str) and 1 <= len(profile['expectedName']) <= 128)
    require(re.fullmatch(r'\d+\.\d+\.\d+', profile['version']))
    for key in ('manifestSha256', 'baselineSha256'):
        require(re.fullmatch('[a-f0-9]{64}', profile[key]))
    for key in ('gameAlias', 'controlPlaneAlias'):
        require(re.fullmatch('[a-zA-Z][a-zA-Z0-9_-]{0,63}', profile[key]))
    require(profile['gameAlias'] != profile['controlPlaneAlias'])
    require(Path(profile['sshConfig']).is_absolute() or PureWindowsPath(profile['sshConfig']).is_absolute())


def refuse_local_rcon(host, local_addresses, managed):
    address = ipaddress.IPv4Address(host)
    require(not managed and not address.is_loopback and not address.is_unspecified
            and not address.is_multicast and str(address) not in local_addresses)


def unit(name):
    raw = run(['systemctl', 'show', name, '--property=ActiveState,InvocationID,NRestarts'])
    result = dict(line.split('=', 1) for line in raw.decode().splitlines())
    require(result['ActiveState'] == 'active' and result['NRestarts'] == '0')
    require(re.fullmatch('[a-f0-9]{32}', result['InvocationID']))
    return result


def baseline_entries(raw):
    lines = raw.decode().splitlines()
    require(1 <= len(lines) <= 250)
    entries = {}
    for line in lines:
        match = re.fullmatch(r'([a-f0-9]{64})  (/[^\r\n]+)', line)
        require(match is not None)
        require(match[2] not in entries or entries[match[2]] == match[1])
        entries[match[2]] = match[1]
    return entries


def game_host(profile):
    configuration = Path('/etc/goldsrcops/gameserver')
    state = Path('/var/lib/goldsrcops-player-backup')
    source = Path('/opt/goldsrcops-player-backup/ops/gameserver')
    before = {name: unit(name) for name in ('goldsrcops-gameserver.service', 'goldsrcops-game-event-agent.service')}
    baseline = trusted(configuration / 'player-data-backup-baseline.sha256', mode=0o600)
    require(sha(baseline) == profile['baselineSha256'])
    entries = baseline_entries(baseline)
    for path, expected in entries.items():
        # The frozen baseline, not operator-supplied path overrides, selects files.
        require(sha(trusted(Path(path), 16 * 1024 * 1024)) == expected)
    receipt = dict(line.split('=', 1) for line in trusted(configuration / 'weapon-selection-installed').decode().splitlines())
    require(receipt.get('schema_version') == '2' and receipt.get('state') == 'enabled'
            and receipt.get('manifest_sha256') == profile['manifestSha256'])
    for name in ('pending.json', 'addon-upgrade-pending.json'):
        require(not (state / name).exists() and not (state / name).is_symlink())
    config = json.loads(trusted(configuration / 'player-data-backup.json'))
    require(all(config[key] == profile[key] for key in ('host', 'port', 'expectedName')))
    # The installed compatible backup tree owns its A2S and baseline semantics.
    import importlib.util
    import sys
    sys.path.insert(0, str(source))
    spec = importlib.util.spec_from_file_location('acceptance_backup_policy', source / 'player_data_schedule.py')
    policy = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(policy)
    empty = policy.probe(config)  # Presence of players is allowed for this read-only check.
    run(['bash', str(source / 'addon-upgrade-guard.sh'), 'baseline'], timeout=40)
    log_directory = Path('/var/lib/goldsrc/server/cstrike/addons/amxmodx/logs')
    require(not any(p.is_symlink() for p in (log_directory, *log_directory.parents)))
    logs = list(log_directory.glob('error_*.log'))
    require(len(logs) <= 128)
    for path in logs:
        require(len(trusted(path, 1, owner=path.stat().st_uid)) == 0)
    require(before == {name: unit(name) for name in before})
    return {'result': 'passed', 'role': 'game-host', 'localA2s': True, 'empty': empty,
            'baselineFiles': len(entries), 'baselineSha256': profile['baselineSha256'],
            'manifestSha256': profile['manifestSha256'], 'pending': False, 'units': before}


def empty_lookup(text, nonce, now):
    lines = text.strip().splitlines()
    require(len(lines) == 2)
    first = re.fullmatch(r'GSLOOKUP 1 ([a-f0-9]{16}) ([0-9]{1,12}) 0 0', lines[0])
    last = re.fullmatch(r'GSPEND 1 ([a-f0-9]{16}) ([0-9]{1,12}) 0 0', lines[1])
    require(first is not None and last is not None and first.groups() == last.groups()
            and first[1] == nonce and -5 <= now - int(first[2]) <= 30)


def control_plane(profile, protocol):
    # All host guards run before touching the secret or opening an RCON socket.
    managed_path = Path('/etc/goldsrcops/gameserver')
    managed = managed_path.exists() or managed_path.is_symlink()
    addresses = json.loads(run(['ip', '-j', '-4', 'address', 'show']))
    local = {entry['local'] for item in addresses for entry in item['addr_info'] if entry['family'] == 'inet'}
    refuse_local_rcon(profile['host'], local, managed)
    with urllib.request.urlopen('https://api.goldsrcops.com/api/public/server', timeout=10) as response:
        raw = response.read(65537)
    require(len(raw) <= 65536)
    endpoint = json.loads(raw)
    require(endpoint.get('state') == 'online' and all(endpoint[k] == profile[k] for k in ('host', 'port')))
    ids = run(['docker', 'ps', '-q', '--filter', 'label=com.docker.compose.service=api']).decode().split()
    require(len(ids) == 1)
    container = json.loads(run(['docker', 'inspect', ids[0]]))[0]
    require(container['State']['Running'])
    mounts = [m for m in container['Mounts'] if m['Destination'] == '/run/secrets/rcon-password']
    require(len(mounts) == 1 and mounts[0]['Type'] == 'bind' and not mounts[0]['RW'])
    password = protocol.read_password(io.BytesIO(trusted(Path(mounts[0]['Source']), 130, owner=1654, mode=0o400)))
    address = (profile['host'], profile['port'])
    text = '\n'.join(protocol.query(address, password, command) for command in protocol.COMMANDS)
    protocol.verify(text, profile['version'])
    nonce = uuid.uuid4().hex[:16]
    name = ('release-check-' + uuid.uuid4().hex[:12]).encode().hex().upper()
    # One lookup only. Busy, auth failure or timeout is a refusal, never a retry.
    reply = protocol.query(address, password, 'goldsrcops_player_lookup ' + nonce + ' ' + name)
    empty_lookup(reply, nonce, time.time())
    after = json.loads(run(['docker', 'inspect', ids[0]]))[0]
    require(after['State']['Running'] and after['State']['StartedAt'] == container['State']['StartedAt']
            and after['RestartCount'] == container['RestartCount']
            and after['Image'] == container['Image']
            and canonical_mounts(after['Mounts']) == canonical_mounts(container['Mounts']))
    return {'result': 'passed', 'role': 'control-plane', 'version': profile['version'],
            'status': True, 'lookup': True, 'apiRuntimeUnchanged': True}


def remote_main(role, profile, protocol):
    try:
        require(os.geteuid() == 0 and role in ('game-host', 'control-plane'))
        validate_profile(profile)
        result = game_host(profile) if role == 'game-host' else control_plane(profile, protocol)
        print(json.dumps(result, separators=(',', ':')))
    except Exception as error:
        # Function/category only: no exception text, paths, subprocess output or RCON bodies.
        frames = traceback.extract_tb(error.__traceback__)
        check = frames[-2].name if len(frames) > 1 else 'entrypoint'
        print(json.dumps({'result': 'failed', 'role': role, 'check': check, 'category': type(error).__name__}))
        raise SystemExit(1)
