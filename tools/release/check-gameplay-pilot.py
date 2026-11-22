#!/usr/bin/env python3
"""Check the current gameplay pilot baseline over the existing read-only paths."""
import argparse
import base64
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

from gameplay_pilot_checks import check, require, validate_profile

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]
RUNTIME_PROTOCOL = REPO / 'ops/gameserver/addon_runtime_protocol.py'


class CollectionUnavailable(Exception):
    pass


def load_profile(path, digest):
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 8192)
    raw = path.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == digest)
    profile = json.loads(raw)
    validate_profile(profile)
    return profile


def expected_cvars():
    names = {
        'mp_forcerespawn', 'mp_respawn_immunitytime', 'mp_respawn_immunity_force_unset',
        'mp_freezetime', 'mp_friendlyfire', 'mp_roundtime', 'mp_c4timer', 'mp_timelimit',
        'mp_autoteambalance', 'mp_limitteams', 'mapchangecfgfile',
    }
    source = (REPO / 'ops/gameserver/fast-reentry-loadout-v1.cfg').read_text(encoding='utf-8')
    entries = [match.groups() for line in source.splitlines()
               if (match := re.fullmatch(r'([a-z0-9_]+) "([A-Za-z0-9_.-]+)"', line)) and match[1] in names]
    require(len(entries) == len(names) and set(dict(entries)) == names)
    return dict(entries)


def verify_ssh_config(profile):
    config = Path(profile['acceptance']['sshConfig'])
    require(config.is_file() and not config.is_symlink() and config.stat().st_size <= 65536)
    require(hashlib.sha256(config.read_bytes()).hexdigest() == profile['sshConfigSha256'])


def ssh_failure(result):
    if result.returncode != 255:
        return 'remote-check-refused'
    error = result.stderr.lower()
    if b'timed out' in error or b'timeout' in error:
        return 'ssh-connect-timeout'
    if b'host key verification failed' in error or b'remote host identification has changed' in error:
        return 'ssh-host-key-refused'
    if b'permission denied' in error:
        return 'ssh-authentication-refused'
    return 'ssh-transport-failed'


def invoke(role, profile, expected):
    target = profile['acceptance']
    alias = target['gameAlias' if role == 'game' else 'controlPlaneAlias']
    code = ['import base64,sys,types']
    for name, path in (('acceptance_checks', HERE / 'acceptance_checks.py'),
                       ('runtime_protocol', RUNTIME_PROTOCOL),
                       ('gameplay_pilot_checks', HERE / 'gameplay_pilot_checks.py')):
        encoded = base64.b64encode(path.read_bytes()).decode('ascii')
        code.extend((f'sys.modules[{name!r}] = types.ModuleType({name!r})',
                     f'exec(compile(base64.b64decode({encoded!r}), {name!r}, "exec"), sys.modules[{name!r}].__dict__)'))
    code.append(f'sys.modules["gameplay_pilot_checks"].remote_main({role!r}, {profile!r}, '
                f'sys.modules["runtime_protocol"], {expected!r})')
    args = ['ssh', '-F', target['sshConfig'], '-T', '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
            '-o', 'ClearAllForwardings=yes', '-o', 'ConnectionAttempts=1', '-o', 'ConnectTimeout=10',
            alias, 'sudo -n python3 -I -B -']
    try:
        result = subprocess.run(args, input=('\n'.join(code) + '\n').encode(), capture_output=True,
                                timeout=100 if role == 'runtime' else 40)
    except subprocess.TimeoutExpired:
        raise CollectionUnavailable('collection-deadline') from None
    except OSError:
        raise CollectionUnavailable('ssh-launch-failed') from None
    if result.returncode:
        raise CollectionUnavailable(ssh_failure(result))
    try:
        require(0 < len(result.stdout) <= 16384)
        value = json.loads(result.stdout)
        require(isinstance(value, dict) and isinstance(value.get('checks'), dict))
    except ValueError:
        raise CollectionUnavailable('invalid-server-response') from None
    return value


def make_header(report, expected_cvars):
    checks = report['checks']
    game = report.get('a2s') or {}
    engine = report.get('engine') or {}

    def value(field):
        return checks.get(field, {}).get('observed')

    def text(value):
        return str(value) if value is not None else 'unavailable'

    def listed(fields):
        return ', '.join(f'{field}={text(value(field))}' for field in fields)

    return '\n'.join((
        '## Server Preflight',
        '',
        '- Observed UTC: ' + report['observedAtUtc'],
        '- Baseline result: ' + report['result'],
        '- Weapon addon: ' + listed(('weapon.version', 'weapon.manifest', 'weapon.file.plugin', 'weapon.file.nvault'))
        + ', receipt=' + text(report.get('weaponReceiptSha256')),
        '- Map addon: ' + listed(('mapAddon.version', 'mapAddon.manifest', 'mapAddon.file.plugin'))
        + ', receipt=' + text(report.get('mapReceiptSha256')),
        '- Engine and profile: ReHLDS ' + text(engine.get('rehlds')) + ', protocol ' + text(engine.get('protocol'))
        + ', build ' + text(engine.get('build')) + ', ' + text(value('mapchangecfgfile')),
        '- Map, time left, players and bots: ' + ', '.join(map(text, (game.get('map'), report.get('timeLeft'),
                                                                    game.get('players'), game.get('bots')))),
        '- Effective gameplay values: ' + listed(expected_cvars),
        '- Healing and refill: ' + listed(('heal.enabled', 'heal.amount', 'ammo.enabled')),
        '- Storage: ' + listed(('preferences.storage', 'preferences.errors', 'stats.storage', 'stats.errors')),
    ))


def preflight(profile, *, collect=invoke):
    started = dt.datetime.now(dt.timezone.utc)
    expected = expected_cvars()
    checks = {}
    collection_errors = {}

    def read_snapshot(role, stage):
        try:
            return collect(role, profile, expected)
        except CollectionUnavailable as error:
            collection_errors[stage] = str(error)
            return None

    before = read_snapshot('game', 'game.before')
    runtime = None
    check(checks, 'game.snapshot', True, True if before is not None else None)
    if before is not None:
        checks.update(before['checks'])
        if all(row['observed'] is not None and row['observed'] == row['expected'] for row in before['checks'].values()):
            runtime = read_snapshot('runtime', 'runtime')
            check(checks, 'runtime.snapshot', True, True if runtime is not None else None)
            if runtime is not None:
                checks.update(runtime['checks'])
            after = read_snapshot('game', 'game.after')
            if after is None:
                stable = None
            else:
                stable = before == after
            check(checks, 'game.stableDuringSample', True, stable)
    drift = {name: row for name, row in checks.items() if row['observed'] is not None and row['observed'] != row['expected']}
    unavailable = [name for name, row in checks.items() if row['observed'] is None]
    report = {
        'schema': 1, 'startedAtUtc': started.isoformat(), 'observedAtUtc': dt.datetime.now(dt.timezone.utc).isoformat(),
        'result': 'drift' if drift else 'unavailable' if unavailable else 'ready',
        'checks': checks, 'drift': drift, 'unavailable': unavailable, 'collectionErrors': collection_errors,
        'a2s': before.get('a2s') if before else None,
        'weaponReceiptSha256': before.get('weaponReceiptSha256') if before else None,
        'mapReceiptSha256': before.get('mapReceiptSha256') if before else None,
        'engine': runtime.get('engine') if runtime else None, 'timeLeft': runtime.get('timeLeft') if runtime else None,
    }
    report['pilotHeader'] = make_header(report, expected)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--profile-sha256', required=True)
    parser.add_argument('--output', type=Path, help='Write a new private JSON report with the Markdown pilot header.')
    parser.add_argument('--execute', action='store_true', help='Use the pinned SSH profile and one server observation.')
    args = parser.parse_args()
    try:
        profile = load_profile(args.profile, args.profile_sha256)
        verify_ssh_config(profile)
        if args.output and (args.output.exists() or args.output.is_symlink()):
            parser.error('the output already exists')
    except (OSError, ValueError, TypeError, KeyError):
        parser.error('the profile or SSH configuration failed validation')
    result = preflight(profile) if args.execute else {'result': 'validated-plan', 'network': False}
    presentation = json.dumps(result, ensure_ascii=True, indent=2) + '\n'
    if args.output:
        with args.output.open('x', encoding='utf-8') as target:
            target.write(presentation)
    print(presentation, end='')
    return int(result['result'] not in ('validated-plan', 'ready'))


if __name__ == '__main__':
    sys.exit(main())
