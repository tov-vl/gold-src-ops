"""Closed paired-package contract and hash-frozen static baseline transition."""
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import struct
import zlib

AMXX = 'cstrike/addons/amxmodx/'
PLUGIN = AMXX + 'plugins/goldsrcops_weapon_selection.amxx'
MODULE = AMXX + 'modules/nvault_amxx_i386.so'
CONFIG = AMXX + 'configs/plugins/goldsrcops-weapon-selection.cfg'
LOADER = AMXX + 'configs/plugins-goldsrcops-weapons.ini'
DICTIONARY = AMXX + 'data/lang/goldsrcops-player-menu.txt'
PATHS = (PLUGIN, CONFIG, LOADER, DICTIONARY, MODULE)
FORBIDDEN = (b'NVAULT_FAIL', b'NVAULT_KILL', b'NVAULT_SLOW_IO',
             b'nvault_test_clock', b'nvault_test_failure')


def require(condition, message='Paired storage guard failed.'):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def hash_value(value):
    require(isinstance(value, str) and re.fullmatch('[a-f0-9]{64}', value) is not None)
    return value


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON field.')
        result[key] = value
    return result


def decode(raw):
    return json.loads(raw, object_pairs_hook=unique)


def read(path, maximum=65536):
    path = Path(path)
    require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0)
                 | getattr(os, 'O_NONBLOCK', 0) | getattr(os, 'O_BINARY', 0))
    with os.fdopen(fd, 'rb') as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= maximum)
        if os.name != 'nt':
            require(before.st_uid == 0 and before.st_mode & 0o022 == 0, 'Untrusted root input.')
        data = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
    identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns)
    require(len(data) <= maximum and identity(before) == identity(after) == identity(path.stat()))
    return data


def reference(item, maximum=65536):
    require(isinstance(item, dict) and set(item) == {'path', 'sha256'})
    data = read(Path(item['path']), maximum)
    require(digest(data) == hash_value(item['sha256']), 'Frozen input changed.')
    return data


def native_names(raw):
    require(24 <= len(raw) <= 8 * 1024 * 1024)
    magic, version, count = struct.unpack_from('<IHB', raw)
    require(magic == 0x414D5858 and version == 0x300 and 1 <= count <= 4
            and len(raw) >= 7 + count * 17, 'Unsupported AMXX header.')
    images = []
    for index in range(count):
        cells, disk, image, memory, offset = struct.unpack_from('<BIIII', raw, 7 + index * 17)
        require(7 + count * 17 <= offset <= len(raw) and 0 < disk <= len(raw) - offset
                and 56 <= image <= 16 * 1024 * 1024 and 56 <= memory <= 32 * 1024 * 1024)
        if cells == 4:
            decoder = zlib.decompressobj()
            decoded = decoder.decompress(raw[offset:offset + disk], image + 1)
            require(len(decoded) == image and decoder.eof and not decoder.unconsumed_tail and not decoder.unused_data)
            images.append(decoded)
    require(len(images) == 1)
    image = images[0]
    header = struct.unpack_from('<IHBBhh11i', image)
    code, data, heap, stack, cip, publics, natives, libraries, pubvars, tags, names = header[6:]
    require(header[1] == 0xF1E0 and header[2] == 8 and header[5] == 8
            and 56 <= publics <= natives <= libraries <= pubvars <= tags <= names < code <= len(image)
            and (libraries - natives) % 8 == 0 and (libraries - natives) // 8 <= 2048)
    result = set()
    for offset in range(natives, libraries, 8):
        _, name_at = struct.unpack_from('<II', image, offset)
        require(names + 2 <= name_at < code)
        end = image.find(b'\0', name_at, min(name_at + 128, code))
        require(end >= 0)
        name = image[name_at:end].decode('ascii')
        require(name and all(33 <= ord(c) <= 126 for c in name))
        result.add(name)
    return result


def receipt(manifest_hash, hashes, paired=False):
    fields = [('schema_version', '3' if paired else '2'), ('state', 'enabled'),
              ('manifest_sha256', hash_value(manifest_hash))]
    fields += [(name + '_sha256', hashes[path]) for name, path in
               zip(('plugin', 'config', 'loader', 'dictionary'), PATHS[:4])]
    if paired:
        fields += [('nvault_sha256', hashes[MODULE]), ('nvault_process_contract', '1')]
    return ''.join(key + '=' + value + '\n' for key, value in fields).encode('ascii')


def payload(directory, rows, paths):
    require(isinstance(rows, list) and len(rows) == len(paths))
    require(all(set(row) == {'path', 'sha256'} for row in rows))
    require(sorted(row['path'] for row in rows) == sorted(paths), 'Unexpected paired payload path.')
    result = {}
    for row in rows:
        raw = read(directory / row['path'], 16 * 1024 * 1024)
        require(digest(raw) == hash_value(row['sha256']))
        result[row['path']] = raw
    return result


def package(item, paired):
    require(set(item) == {'directory', 'sha256'})
    directory = Path(item['directory'])
    raw = read(directory / 'manifest.json')
    require(digest(raw) == hash_value(item['sha256']))
    value = decode(raw)
    if paired:
        require(set(value) == {'schema', 'purpose', 'addonVersion', 'processContract', 'amxxVersion',
                              'reApiVersion', 'predecessor', 'payload', 'nativeManifest', 'fixtureManifest'})
        require(type(value['schema']) is int and value['schema'] == 1
                and value['purpose'] == 'paired-player-storage' and value['addonVersion'] == '0.40.0'
                and type(value['processContract']) is int and value['processContract'] == 1)
        require(set(value['predecessor']) == {'addonManifestSha256', 'moduleSha256'})
        for h in value['predecessor'].values():
            hash_value(h)
        files = payload(directory, value['payload'], PATHS)
        for key, path in (('nativeManifest', 'proof/native-manifest.json'),
                          ('fixtureManifest', 'proof/fixture-manifest.json')):
            require(set(value[key]) == {'path', 'sha256'} and value[key]['path'] == path)
            reference({'path': str(directory / path), 'sha256': value[key]['sha256']})
        native = decode(read(directory / 'proof/native-manifest.json'))
        fixture = decode(read(directory / 'proof/fixture-manifest.json'))
        require(native['schema'] == 1 and native['upstream']['processContract'] == 1
                and native['upstream']['target'] == 'linux-i386' and native['reproducible'] is True
                and native['sha256'] == digest(files[MODULE]) != native['testingSha256'])
        require(digest(read(directory / 'proof/corresponding-source.zip', 32 * 1024 * 1024))
                == hash_value(native['correspondingSourceSha256']))
        require(fixture['schema'] == 1 and fixture['toolchain'] == {'amxx': '1.10.0.5481', 'reapi': '5.24.0.300'}
                and fixture['payload']['goldsrcops_weapon_selection.amxx'] == digest(files[PLUGIN]))
        require({'nvault_process_commit', 'nvault_process_version'} <= native_names(files[PLUGIN]))
        module = files[MODULE]
        require(module[:7] == b'\x7fELF\x01\x01\x01' and module[18:20] == b'\x03\x00'
                and all(marker not in module for marker in FORBIDDEN), 'Non-production nVault binary.')
    else:
        require(value['schemaVersion'] == 2 and value['purpose'] == 'game-host-addon'
                and value['productionInstallSupported'] is True and value['enabledByDefault'] is True)
        files = payload(directory, value['payload'], PATHS[:4])
        require(not {'nvault_process_commit', 'nvault_process_version'} & native_names(files[PLUGIN]),
                'Predecessor must use the stock module contract.')
    require(value['amxxVersion'] == '1.10.0.5481' and value['reApiVersion'] == '5.24.0.300')
    require(files[CONFIG] in (b'goldsrcops_weapons_enabled "1"\n',
            b'goldsrcops_weapons_enabled "1"\ngoldsrcops_player_lookup_enabled "1"\n'))
    require(files[LOADER] == b'goldsrcops_weapon_selection.amxx\n')
    return value, files


def baseline_rows(raw):
    require(len(raw) <= 65536 and raw.endswith(b'\n'))
    rows = []
    for line in raw.decode('utf-8').splitlines():
        match = re.fullmatch(r'([a-f0-9]{64})  (/[^\r\n]+)', line)
        require(match is not None)
        rows.append(match.groups())
    require(1 <= len(rows) <= 250)
    seen = {}
    for h, name in rows:
        require(name not in seen or seen[name] == h, 'Conflicting duplicate baseline entry.')
        seen[name] = h
    return rows


def transition(old, server, config, previous, candidate, old_receipt, new_receipt, persistent_changes=()):
    before = {str(server / path): digest(raw) for path, raw in previous.items()}
    after = {str(server / path): digest(raw) for path, raw in candidate.items()}
    receipt_path = str(config / 'weapon-selection-installed')
    before[receipt_path], after[receipt_path] = digest(old_receipt), digest(new_receipt)
    for path, previous_bytes, candidate_bytes in persistent_changes:
        require(str(path) not in before)
        before[str(path)], after[str(path)] = digest(previous_bytes), digest(candidate_bytes)
    rows = baseline_rows(old)
    require(set(before) <= {name for _, name in rows}, 'Baseline must already cover the whole pair.')
    for expected, name in rows:
        if name in before:
            require(expected == before[name], 'Predecessor baseline mismatch.')
    return ''.join(after.get(name, expected) + '  ' + name + '\n' for expected, name in rows).encode('utf-8')


def semantic(files):
    """Digest of sorted closed records including timestamps, never emit player data."""
    result = hashlib.sha256()
    for name in sorted(files):
        raw = files[name]
        if name.endswith('.journal'):
            require(raw in (None, b''), 'Closed canonical journal must be empty or absent.')
            continue
        require(raw is not None and 10 <= len(raw) <= 16 * 1024 * 1024)
        magic, version, count = struct.unpack_from('<IHI', raw)
        require(magic == 0x6E564C54 and version == 0x200 and count <= 65536)
        records, offset = {}, 10
        for _ in range(count):
            require(offset + 7 <= len(raw))
            stamp, keylen, vallen = struct.unpack_from('<IBH', raw, offset)
            offset += 7
            require(offset + keylen + vallen <= len(raw))
            key, value = raw[offset:offset + keylen], raw[offset + keylen:offset + keylen + vallen]
            require(key not in records and b'\0' not in key + value)
            records[key] = (stamp, value)
            offset += keylen + vallen
        require(offset == len(raw))
        result.update(name.encode() + b'\0' + struct.pack('<I', count))
        for key, (stamp, value) in sorted(records.items()):
            result.update(struct.pack('<IBH', stamp, len(key), len(value)) + key + value)
    return result.hexdigest()
