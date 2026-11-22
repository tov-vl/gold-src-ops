"""Bounded server-side observations for the gameplay pilot preflight."""
import io
import json
import os
from pathlib import Path
import re
import socket
import sys
import urllib.request
from decimal import Decimal

from acceptance_checks import (
    addon_receipt, baseline_entries, canonical_mounts, configured_endpoint,
    refuse_local_rcon, require, run, sha, trusted, validate_profile as validate_acceptance,
)

GAME_CONFIG = Path('/etc/goldsrcops/gameserver')
AMXX = Path('/var/lib/goldsrc/server/cstrike/addons/amxmodx')
BACKUP_STATE = Path('/var/lib/goldsrcops-player-backup')
MAPS = ('de_dust2', 'de_inferno', 'de_nuke', 'de_train', 'cs_office')
MAP_FILES = {
    'plugin': 'plugins/goldsrcops_map_menu.amxx',
    'config': 'configs/plugins/goldsrcops-map-menu.cfg',
    'loader': 'configs/plugins-goldsrcops-maps.ini',
    'dictionary': 'data/lang/goldsrcops-map-menu.txt',
}
WEAPON_FILES = {
    'plugin': 'plugins/goldsrcops_weapon_selection.amxx',
    'config': 'configs/plugins/goldsrcops-weapon-selection.cfg',
    'loader': 'configs/plugins-goldsrcops-weapons.ini',
    'dictionary': 'data/lang/goldsrcops-player-menu.txt',
    'nvault': 'modules/nvault_amxx_i386.so',
}
DIGEST = r'[a-f0-9]{64}'
VERSION = r'[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}'
NUMBER = r'-?[0-9]{1,6}(?:\.[0-9]{1,6})?'


def validate_profile(profile):
    require(set(profile) == {'schema', 'acceptance', 'sshConfigSha256', 'mapAddon', 'engine'})
    require(type(profile['schema']) is int and profile['schema'] == 1)
    validate_acceptance(profile['acceptance'])
    map_addon = profile['mapAddon']
    require(set(map_addon) == {'version', 'manifestSha256', 'payload'})
    require(re.fullmatch(VERSION, profile['acceptance']['version']) and re.fullmatch(VERSION, map_addon['version']))
    require(set(map_addon['payload']) == set(MAP_FILES))
    require(all(re.fullmatch(DIGEST, value) for value in
                (profile['sshConfigSha256'], map_addon['manifestSha256'], *map_addon['payload'].values())))
    engine = profile['engine']
    require(set(engine) == {'protocol', 'rehlds', 'build'})
    require(type(engine['protocol']) is int and 1 <= engine['protocol'] <= 999)
    require(type(engine['build']) is int and 1 <= engine['build'] <= 999999)
    require(re.fullmatch(r'[0-9][a-zA-Z0-9.-]{1,31}', engine['rehlds']))


def observed(read):
    try:
        return read()
    except (OSError, ValueError, KeyError):
        return None


def check(checks, field, expected, actual):
    checks[field] = {'expected': expected, 'observed': actual}


def receipt(path):
    raw = trusted(path, 8192)
    items = [line.split('=', 1) for line in raw.decode('ascii').splitlines()]
    require(all(len(item) == 2 for item in items) and len(dict(items)) == len(items))
    return dict(items), sha(raw)


def value_if_matches(value, pattern):
    return value if isinstance(value, str) and re.fullmatch(pattern, value) else None


def check_receipt(checks, name, receipt_path, schema, expected_manifest, payload, files):
    readback = observed(lambda: receipt(receipt_path))
    fields, digest = readback if readback else ({}, None)
    check(checks, f'{name}.enabled', 'enabled', value_if_matches(fields.get('state'), r'enabled|disabled'))
    check(checks, f'{name}.receiptSchema', schema, value_if_matches(fields.get('schema_version'), r'[0-9]{1,3}'))
    check(checks, f'{name}.manifest', expected_manifest, value_if_matches(fields.get('manifest_sha256'), DIGEST))
    for kind, relative in files.items():
        frozen = payload.get(kind)
        if frozen is None:
            continue
        key = kind + '_sha256'
        check(checks, f'{name}.receipt.{kind}', frozen, value_if_matches(fields.get(key), DIGEST))
        digest_on_host = observed(lambda: sha(trusted(AMXX / relative, 16 * 1024 * 1024)))
        check(checks, f'{name}.file.{kind}', frozen, digest_on_host)
    return fields, digest


def game_info(target, parse_info):
    request = b'\xff\xff\xff\xffTSource Engine Query\0'
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.settimeout(3)
        connection.connect((target['host'], target['port']))
        connection.send(request)
        packet = connection.recv(4097)
        if packet.startswith(b'\xff\xff\xff\xffA'):
            require(len(packet) == 9)
            connection.send(request + packet[5:])
            packet = connection.recv(4097)
    name, folder, players, bots = parse_info(packet)
    require(name == target['expectedName'] and folder == 'cstrike')
    # GoldSrc includes an address string where Source includes a protocol byte.
    offset = 5 if packet[4] == 0x6D else 6
    map_name = packet[offset:].split(b'\0', 3)[2 if packet[4] == 0x6D else 1].decode('ascii')
    require(re.fullmatch(r'[a-z0-9_]{1,32}', map_name))
    return {'map': map_name, 'players': players, 'bots': bots}


def game_host(profile):
    from importlib.util import module_from_spec, spec_from_file_location

    target = profile['acceptance']
    config = json.loads(trusted(GAME_CONFIG / 'player-data-backup.json'))
    require(all(config[key] == target[key] for key in ('host', 'port', 'expectedName')))
    checks = {}
    unit = dict(line.split('=', 1) for line in run([
        'systemctl', 'show', 'goldsrcops-gameserver.service', '--property=ActiveState,InvocationID',
    ], timeout=8).decode().splitlines())
    check(checks, 'game.active', 'active', value_if_matches(unit.get('ActiveState'), r'[a-z-]{1,24}'))
    invocation = value_if_matches(unit.get('InvocationID'), r'[a-f0-9]{32}')
    check(checks, 'game.invocationAvailable', True, bool(invocation))
    pending = any((BACKUP_STATE / name).exists() or (BACKUP_STATE / name).is_symlink()
                  for name in ('pending.json', 'addon-upgrade-pending.json', 'agent-upgrade-pending.json'))
    check(checks, 'backup.pending', False, pending)

    baseline = observed(lambda: trusted(GAME_CONFIG / 'player-data-backup-baseline.sha256', mode=0o600))
    baseline_matches = baseline is not None and sha(baseline) == target['baselineSha256']
    check(checks, 'backup.baseline', target['baselineSha256'], sha(baseline) if baseline is not None else None)
    entries = observed(lambda: baseline_entries(baseline)) if baseline_matches else None
    frozen_weapon = {kind: entries.get(str(AMXX / relative)) for kind, relative in WEAPON_FILES.items()} if entries else {}
    check(checks, 'weapon.baselineEntries', len(WEAPON_FILES), sum(value is not None for value in frozen_weapon.values()))
    weapon, weapon_digest = check_receipt(checks, 'weapon', GAME_CONFIG / 'weapon-selection-installed',
                                              '3', target['manifestSha256'], frozen_weapon, WEAPON_FILES)
    check(checks, 'weapon.processStorage', '1', value_if_matches(weapon.get('nvault_process_contract'), r'[01]'))
    if entries and weapon:
        try:
            addon_receipt(weapon, target, entries)
        except ValueError:
            valid_receipt = False
        else:
            valid_receipt = True
        check(checks, 'weapon.receiptContract', True, valid_receipt)
    _, map_digest = check_receipt(checks, 'mapAddon', GAME_CONFIG / 'map-menu-installed',
                                  '2', profile['mapAddon']['manifestSha256'], profile['mapAddon']['payload'], MAP_FILES)

    # Reuse the installed backup policy's packet parser and bounds. It reads A2S_INFO only.
    policy_path = Path('/opt/goldsrcops-player-backup/ops/gameserver/player_data_schedule.py')
    sys.path.insert(0, str(policy_path.parent))
    try:
        spec = spec_from_file_location('pilot_backup_policy', policy_path)
        policy = module_from_spec(spec)
        spec.loader.exec_module(policy)
        a2s = observed(lambda: game_info(target, policy.parse_info))
    finally:
        sys.path.pop(0)
    check(checks, 'game.allowedMap', True, a2s['map'] in MAPS if a2s else None)
    check(checks, 'game.players', 0, a2s['players'] if a2s else None)
    check(checks, 'game.bots', 0, a2s['bots'] if a2s else None)
    return {'checks': checks, 'a2s': a2s, 'invocation': invocation,
            'weaponReceiptSha256': weapon_digest, 'mapReceiptSha256': map_digest}


def marker(text, prefix):
    lines = [line.split()[1:] for line in text.splitlines() if line.startswith(prefix + ' ')]
    if len(lines) != 1:
        return {}
    pairs = [token.split('=', 1) for token in lines[0]]
    if any(len(pair) != 2 for pair in pairs) or len(dict(pairs)) != len(pairs):
        return {}
    return dict(pairs)


def single(text, pattern):
    values = re.findall(pattern, text, re.MULTILINE)
    return values[0] if len(values) == 1 else None


def cvar(text, name):
    values = re.findall(rf'^"?{re.escape(name)}"?[ \t]*(?:is|=)[ \t]*'
                        r'(?:(?:"([^"\r\n]{1,64})")|([A-Za-z0-9_.:-]{1,64}))[ \t]*\r?$',
                        text, re.MULTILINE)
    return (values[0][0] or values[0][1]) if len(values) == 1 else None


def runtime_observations(replies, profile, expected_cvars):
    checks = {}
    target = profile['acceptance']
    weapons = replies.get('goldsrcops_weapons_status', '')
    stats = replies.get('goldsrcops_stats_status', '')
    maps = replies.get('goldsrcops_maps_status', '')
    for name, text, prefix, fields, version in (
        ('weapon', weapons, 'WEAPON_SELECTION_STATUS', {'enabled': '1'}, target['version']),
        ('rules', weapons, 'PLAYER_RULES_STATUS', {'source': 'live_cvars'}, target['version']),
        ('profile', stats, 'PLAYER_PROFILE_STATUS', {'source': 'loaded_player_state'}, target['version']),
        ('preferences', weapons, 'PLAYER_PREFERENCES_STATUS', {'storage': 'nvault', 'errors': '0'}, target['version']),
        ('stats', stats, 'PLAYER_STATS_STATUS', {'storage': 'nvault', 'errors': '0'}, target['version']),
        ('ranks', stats, 'PLAYER_LEADERBOARD_STATUS', {'ready': '1', 'errors': '0'}, target['version']),
        ('heal', weapons, 'KILL_HEAL_STATUS', {'enabled': '1', 'amount': '15'}, target['version']),
        ('ammo', weapons, 'KILL_AMMO_STATUS', {'enabled': '1'}, target['version']),
        ('mapAddon', maps, 'MAP_MENU_STATUS', {'enabled': '1', 'cycle_ready': '1', 'maps': '5', 'timeleft': 'native',
                                              'voting_enabled': '1', 'language_bridge': '1'},
         profile['mapAddon']['version']),
    ):
        observed_fields = marker(text, prefix)
        check(checks, f'{name}.version', version, value_if_matches(observed_fields.get('version'), VERSION))
        for field, expected in fields.items():
            actual = value_if_matches(observed_fields.get(field), r'[a-z0-9_]{1,32}')
            check(checks, f'{name}.{field}', expected, actual)
    for field, expected in expected_cvars.items():
        actual = cvar(replies.get(field, ''), field)
        if field == 'mapchangecfgfile':
            actual = value_if_matches(actual, r'[A-Za-z0-9_.-]{1,64}')
        else:
            actual = value_if_matches(actual, NUMBER)
            actual = format(Decimal(actual).normalize(), 'f') if actual is not None else None
        check(checks, field, expected, actual)
    time_left = value_if_matches(cvar(replies.get('mp_timeleft', ''), 'mp_timeleft'), r'[0-9]{1,3}:[0-5][0-9]')
    check(checks, 'game.timeLeftAvailable', True, True if time_left is not None else None)
    version = replies.get('version', '')
    protocol = single(version, r'^Protocol version ([0-9]{1,3})\r?$')
    build = single(version, r'^Build date: [^\r\n]{1,80} \(([0-9]{1,6})\)\r?$')
    engine = {'protocol': int(protocol) if protocol else None, 'build': int(build) if build else None,
              'rehlds': single(version, r'^ReHLDS version: ([0-9][a-zA-Z0-9.-]{1,31})\r?$')}
    for field, value in engine.items():
        check(checks, 'engine.' + field, profile['engine'][field], value)
    return {'checks': checks, 'engine': engine, 'timeLeft': time_left}


def control_plane(profile, protocol, expected_cvars):
    target = profile['acceptance']
    managed = GAME_CONFIG.exists() or GAME_CONFIG.is_symlink()
    addresses = json.loads(run(['ip', '-j', '-4', 'address', 'show']))
    local = {entry['local'] for item in addresses for entry in item['addr_info'] if entry['family'] == 'inet'}
    refuse_local_rcon(target['host'], local, managed)
    with urllib.request.urlopen('https://api.goldsrcops.com/api/public/server', timeout=8) as response:
        raw = response.read(65537)
    require(len(raw) <= 65536)
    endpoint = json.loads(raw)
    require(endpoint.get('state') == 'online' and all(endpoint[key] == target[key] for key in ('host', 'port')))
    ids = run(['docker', 'ps', '-q', '--filter', 'label=com.docker.compose.service=api']).decode().split()
    require(len(ids) == 1)
    (before,) = json.loads(run(['docker', 'inspect', ids[0]]))
    require(before['State']['Running'])
    configured_endpoint(before, target)
    mounts = [mount for mount in before['Mounts'] if mount['Destination'] == '/run/secrets/rcon-password']
    require(len(mounts) == 1 and mounts[0]['Type'] == 'bind' and not mounts[0]['RW'])
    password = protocol.read_password(io.BytesIO(trusted(Path(mounts[0]['Source']), 130, owner=1654, mode=0o400)))
    address = (target['host'], target['port'])
    replies = {}
    for command in protocol.PILOT_COMMANDS:
        response = observed(lambda: protocol.query_pilot(address, password, command))
        if response is None:
            # A transport or authentication failure ends the command sequence.
            break
        replies[command] = response
    (after,) = json.loads(run(['docker', 'inspect', ids[0]]))
    require(after['State']['Running'] and after['State']['StartedAt'] == before['State']['StartedAt']
            and after['RestartCount'] == before['RestartCount'] and after['Image'] == before['Image']
            and canonical_mounts(after['Mounts']) == canonical_mounts(before['Mounts']))
    configured_endpoint(after, target)
    return runtime_observations(replies, profile, expected_cvars)


def remote_main(role, profile, protocol, expected_cvars):
    try:
        require(os.geteuid() == 0)
        validate_profile(profile)
        if role == 'game':
            result = game_host(profile)
        else:
            require(role == 'runtime' and set(expected_cvars) == set(protocol.PILOT_CVARS) - {'mp_timeleft'})
            result = control_plane(profile, protocol, expected_cvars)
        print(json.dumps(result, separators=(',', ':')))
    except Exception:
        # Never transmit SSH stderr, host paths, secret metadata or RCON response text.
        raise SystemExit(1) from None
