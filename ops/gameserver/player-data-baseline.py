#!/usr/bin/env python3
"""Explicit, hash-bound backup baseline refresh after independent addon acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import player_data_schedule as policy

CONFIG = Path('/etc/goldsrcops/gameserver')
SERVER = Path('/var/lib/goldsrc/server')
STATE = Path('/var/lib/goldsrcops-player-backup')
BASELINE = CONFIG / 'player-data-backup-baseline.sha256'


def digest(data):
    return hashlib.sha256(data).hexdigest()


def trusted_bytes(path, limit=65536):
    policy.require(not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    policy.require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                   and info.st_uid == 0 and info.st_mode & 0o022 == 0 and info.st_size <= limit)
    return path.read_bytes()


def prepare(baseline, server, config, manifest, manifest_hash, baseline_hash, kind):
    # Expected hashes are operator-reviewed inputs, never learned from live drift.
    policy.require(all(re.fullmatch(r'[a-f0-9]{64}', h) for h in (manifest_hash, baseline_hash)))
    old = policy.private_path(baseline).read_bytes()
    policy.require(len(old) <= 65536 and digest(old) == baseline_hash)
    raw = trusted_bytes(manifest)
    policy.require(digest(raw) == manifest_hash)
    package = json.loads(raw)
    weapon = kind == 'weapon-selection'
    policy.require(kind in ('weapon-selection', 'map-menu'))
    names = ('goldsrcops_weapon_selection.amxx', 'goldsrcops-weapon-selection.cfg',
             'plugins-goldsrcops-weapons.ini', 'goldsrcops-player-menu.txt') if weapon else (
             'goldsrcops_map_menu.amxx', 'goldsrcops-map-menu.cfg',
             'plugins-goldsrcops-maps.ini', 'goldsrcops-map-menu.txt')
    relatives = [f'cstrike/addons/amxmodx/{folder}/{name}' for folder, name in zip(
        ('plugins', 'configs/plugins', 'configs', 'data/lang'), names)]
    policy.require(package['schemaVersion'] == 2 and package['productionInstallSupported'] is True
                   and package['enabledByDefault'] is True
                   and package['purpose'] == ('game-host-addon' if weapon else 'map-menu-addon')
                   and len(package['payload']) == 4
                   and sorted(x['path'] for x in package['payload']) == sorted(relatives))
    current = {}
    for entry in package['payload']:
        policy.require(re.fullmatch(r'[a-f0-9]{64}', entry['sha256']) is not None)
        path = server / entry['path']
        policy.require(digest(trusted_bytes(path, 16 * 1024 * 1024)) == entry['sha256'])
        current[str(path)] = entry['sha256']
    receipt = config / (kind + '-installed')
    receipt_bytes = trusted_bytes(receipt)
    pairs = [line.split('=', 1) for line in receipt_bytes.decode().splitlines()]
    policy.require(all(len(pair) == 2 for pair in pairs) and len(dict(pairs)) == len(pairs))
    fields = dict(pairs)
    policy.require(set(fields) == {'schema_version', 'state', 'manifest_sha256', 'plugin_sha256',
                                  'config_sha256', 'loader_sha256', 'dictionary_sha256'})
    policy.require(fields['schema_version'] == '2' and fields['state'] == 'enabled'
                   and fields['manifest_sha256'] == manifest_hash)
    for key, relative in zip(('plugin', 'config', 'loader', 'dictionary'), relatives):
        policy.require(fields[key + '_sha256'] == current[str(server / relative)])
    current[str(receipt)] = digest(receipt_bytes)
    updated, seen = [], set()
    for line in old.decode().splitlines():
        match = re.fullmatch(r'([a-f0-9]{64})  (/[^\r\n]+)', line)
        policy.require(match is not None)
        previous, name = match.groups()
        # Existing baselines can contain identical repeated invariant entries.
        actual = digest(trusted_bytes(Path(name), 16 * 1024 * 1024))
        if name in current:
            policy.require(actual == current[name])
            seen.add(name)
        else:
            policy.require(actual == previous, 'Unrelated baseline drift requires review.')
        updated.append(actual + '  ' + name + '\n')
    policy.require(seen == set(current), 'Baseline inventory must already cover the accepted addon.')
    return old, ''.join(updated).encode()


def apply(baseline, evidence, old, new, manifest_hash):
    policy.require(old != new, 'Baseline is already current.')
    policy.private_path(evidence.parent, directory=True)
    policy.require(not evidence.exists() and not evidence.is_symlink())
    evidence.mkdir(mode=0o700)
    policy.write_json(evidence / 'intent.json', {'schema': 1, 'manifestSha256': manifest_hash,
                      'previousSha256': digest(old), 'acceptedSha256': digest(new)})
    for name, data in (('previous.sha256', old), ('accepted.sha256', new)):
        with (evidence / name).open('xb') as f:
            os.chmod(f.name, 0o600)
            f.write(data); f.flush(); os.fsync(f.fileno())
    for directory in (evidence, evidence.parent):
        descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    temporary = baseline.with_name(baseline.name + '.new')
    with temporary.open('xb') as f:
        os.chmod(f.name, 0o600)
        f.write(new); f.flush(); os.fsync(f.fileno())
    policy.require(policy.private_path(baseline).read_bytes() == old)
    os.replace(temporary, baseline)
    descriptor = os.open(baseline.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
    policy.write_json(evidence / 'result.json', {'schema': 1, 'state': 'accepted',
                      'baselineSha256': digest(new), 'manifestSha256': manifest_hash})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--kind', choices=('weapon-selection', 'map-menu'), required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--manifest-sha256', required=True)
    parser.add_argument('--expected-baseline-sha256', required=True)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    policy.require(os.geteuid() == 0)
    # Lock order matches scheduled capture. Do not stop/start or touch player data.
    with policy.lock(STATE / 'schedule.lock'):
        with policy.lock(CONFIG / 'player-data-backup.lock', shared=True):
            with policy.lock(CONFIG / 'game-event-pilot-install.lock', shared=True):
                policy.require(not (STATE / 'pending.json').exists()
                               and not (STATE / 'pending.json').is_symlink())
                old, new = prepare(BASELINE, SERVER, CONFIG, args.manifest,
                                   args.manifest_sha256, args.expected_baseline_sha256, args.kind)
                if args.apply:
                    apply(BASELINE, STATE / ('baseline-' + digest(new)), old, new, args.manifest_sha256)
                print('PLAYER_BACKUP_BASELINE=' + ('updated' if args.apply else 'verified-plan'))


if __name__ == '__main__':
    os.umask(0o077)
    try:
        main()
    except Exception:
        print('PLAYER_BACKUP_BASELINE=failed; inspect_private_evidence', file=sys.stderr)
        sys.exit(1)
