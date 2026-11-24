"""Verify an anchored PostgreSQL 16 amd64 PITR catalog before restore."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import struct
import tarfile

SHA = re.compile(r'[0-9a-f]{64}')
LSN = re.compile(r'[0-9A-F]{1,8}/[0-9A-F]{1,8}')
TARGET = re.compile(r'[a-z][a-z0-9-]{0,62}')
SEGMENT_SIZE = 16 * 1024 * 1024
WAL_BLOCK_SIZE = 8192
WAL_MAGIC = 0xD113
CONTROL_VERSION = 1300
LONG_WAL = struct.Struct('<HHIQI4xQII')
LABEL_START = re.compile(r'^START WAL LOCATION: (\S+) \(file ([0-9A-F]{24})\)$', re.M)
LABEL_TIMELINE = re.compile(r'^START TIMELINE: ([0-9]+)$', re.M)
BASE_META = {'PG_VERSION': 16, 'global/pg_control': 8192, 'backup_label': 16384}


class Rejected(Exception):
    def __init__(self, code, detail):
        super().__init__(detail)
        self.code = code


def require(condition, code, detail):
    if not condition:
        raise Rejected(code, detail)


def pinned_path(path, predicate, missing):
    try:
        info = path.lstat()
    except FileNotFoundError as exc:
        raise Rejected(missing, path.name) from exc
    reparse = getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
    require(predicate(info.st_mode) and not reparse, 'unsafe-path', path.name)
    return info


def unique_keys(items):
    keys = {}
    for name, value in items:
        require(name not in keys, 'duplicate-key', name)
        keys[name] = value
    return keys


def read_json(content):
    return json.loads(content, object_pairs_hook=unique_keys)


def validate_object(stream, path, identity, size):
    require(isinstance(identity, dict) and type(identity.get('bytes')) is int
            and identity['bytes'] > 0 and isinstance(identity.get('sha256'), str)
            and SHA.fullmatch(identity['sha256']), 'invalid-object-identity', path.name)
    require(size == identity['bytes'], 'size-mismatch', path.name)
    require(hashlib.file_digest(stream, 'sha256').hexdigest() == identity['sha256'], 'checksum-mismatch', path.name)
    stream.seek(0)


def lsn_value(value):
    require(isinstance(value, str) and LSN.fullmatch(value), 'invalid-lsn', repr(value))
    high, low = (int(part, 16) for part in value.split('/'))
    return (high << 32) | low


def wal_name(timeline, number):
    log_id, index = divmod(number, (1 << 32) // SEGMENT_SIZE)
    return f'{timeline:08X}{log_id:08X}{index:08X}'


def required_range(manifest, target_lsn, timeline):
    require(manifest.get('PostgreSQL-Backup-Manifest-Version') == 1, 'unsupported-manifest', 'expected version 1')
    ranges = manifest.get('WAL-Ranges')
    require(isinstance(ranges, list) and len(ranges) == 1 and isinstance(ranges[0], dict),
            'unsupported-timeline-chain', 'expected a single base WAL range')
    row = ranges[0]
    require(type(row.get('Timeline')) is int and row['Timeline'] == timeline, 'base-timeline-mismatch', str(row))
    start = lsn_value(row.get('Start-LSN'))
    end = lsn_value(row.get('End-LSN'))
    target = lsn_value(target_lsn)
    require(0 < start < end <= target, 'unreachable-target', 'target precedes a usable base backup')
    first, last = start // SEGMENT_SIZE, (target - 1) // SEGMENT_SIZE
    return first, last, row


def verify_base(path, identity, system_id, timeline, first, manifest_start):
    size = pinned_path(path, stat.S_ISREG, 'missing-base').st_size
    with path.open('rb') as stream:
        validate_object(stream, path, identity, size)
        with tarfile.open(fileobj=stream, mode='r|gz') as bundle:
            parts = {}
            for entry in bundle:
                if entry.name not in BASE_META:
                    continue
                require(entry.isreg() and 0 < entry.size <= BASE_META[entry.name] and entry.name not in parts,
                        'invalid-base-metadata', entry.name)
                parts[entry.name] = bundle.extractfile(entry).read()
    require(set(parts) == set(BASE_META) and parts['PG_VERSION'].strip() == b'16',
            'unsupported-base', 'expected physical PostgreSQL 16 backup')
    control = parts['global/pg_control']
    require(len(control) == 8192, 'invalid-base-metadata', 'global/pg_control size')
    actual_id, version = struct.unpack_from('<QI', control)
    require(version == CONTROL_VERSION, 'unsupported-base', f'pg_control format {version}')
    require(actual_id == system_id, 'base-cluster-mismatch', 'pg_control system identifier')
    label = parts['backup_label'].decode('ascii')
    start, tli = LABEL_START.search(label), LABEL_TIMELINE.search(label)
    require(start is not None and tli is not None, 'invalid-base-metadata', 'missing backup_label WAL start or timeline')
    require(lsn_value(start[1]) == lsn_value(manifest_start) and start[2] == wal_name(timeline, first)
            and int(tli[1]) == timeline, 'base-manifest-mismatch', 'backup_label and manifest disagree')


def verify_wal(path, identity, system_id, timeline, number, inherited_timeline=None):
    size = pinned_path(path, stat.S_ISREG, 'missing-wal').st_size
    require(size == SEGMENT_SIZE, 'wal-segment-size', path.name)
    with path.open('rb') as stream:
        validate_object(stream, path, identity, size)
        magic, flags, actual_timeline, address, _, actual_id, segment_size, block_size = LONG_WAL.unpack(
            stream.read(LONG_WAL.size))
    require(magic == WAL_MAGIC and flags & 2 and not flags & ~15 and segment_size == SEGMENT_SIZE
            and block_size == WAL_BLOCK_SIZE, 'unsupported-wal-header', path.name)
    require(actual_id == system_id, 'wal-cluster-mismatch', path.name)
    require(actual_timeline in (timeline, inherited_timeline), 'wal-timeline-mismatch', path.name)
    require(address == number * SEGMENT_SIZE, 'wal-address-mismatch', path.name)


def parse_history(content, base_timeline, timeline):
    lines = [line.split(maxsplit=2) for line in content.decode('ascii').splitlines()
             if line.strip() and not line.lstrip().startswith('#')]
    require(len(lines) == 1 and len(lines[0]) >= 2 and base_timeline == 1 and timeline > base_timeline,
            'unsupported-timeline-chain', 'expected one direct fork from timeline 1')
    parent, switch = lines[0][:2]
    require(parent == str(base_timeline), 'timeline-history-parent-mismatch', parent)
    return lsn_value(switch)


def read_history(wal_root, identity, base_timeline, timeline):
    name = f'{timeline:08X}.history'
    require(identity['name'] == name, 'timeline-history-name-mismatch', name)
    path = wal_root / name
    size = pinned_path(path, stat.S_ISREG, 'missing-timeline-history').st_size
    require(size <= 4096, 'timeline-history-size', name)
    with path.open('rb') as stream:
        validate_object(stream, path, identity, size)
        fork = parse_history(stream.read(), base_timeline, timeline)
    return name, fork


def wal_plan(first, last, base_timeline, timeline, fork=None):
    if fork is None:
        yield from ((timeline, number, None) for number in range(first, last + 1))
        return
    parent_last, child_first = (fork - 1) // SEGMENT_SIZE, fork // SEGMENT_SIZE
    yield from ((base_timeline, number, None) for number in range(first, parent_last + 1))
    for number in range(child_first, last + 1):
        # When a fork occurs inside a segment, the first long page header can come from the parent.
        inherited = base_timeline if number == child_first and fork % SEGMENT_SIZE else None
        yield timeline, number, inherited


def verify(root, anchor):
    require(SHA.fullmatch(anchor), 'invalid-catalog-anchor', 'expected a trusted SHA-256')
    pinned_path(root, stat.S_ISDIR, 'missing-archive')
    catalog_path = root / 'chain.json'
    size = pinned_path(catalog_path, stat.S_ISREG, 'missing-catalog').st_size
    require(0 < size <= 1024 * 1024, 'catalog-size', 'catalog exceeds lab bounds')
    body = catalog_path.read_bytes()
    require(hashlib.sha256(body).hexdigest() == anchor, 'catalog-checksum-mismatch', 'chain.json')
    catalog = read_json(body)
    version = catalog.get('version')
    require(type(version) is int and version in (1, 2), 'unsupported-catalog', 'version')
    for field, expected in (('postgresql_major', 16), ('wal_segment_bytes', SEGMENT_SIZE)):
        require(type(catalog.get(field)) is int and catalog[field] == expected, 'unsupported-catalog', field)
    cluster = catalog['system_identifier']
    require(isinstance(cluster, str) and re.fullmatch(r'[1-9][0-9]{0,19}', cluster)
            and int(cluster) <= 0xffffffffffffffff, 'invalid-cluster', 'system_identifier')
    system_id, timeline = int(cluster), catalog['timeline']
    require(type(timeline) is int and 0 < timeline <= 0xffffffff, 'invalid-timeline', 'timeline')
    target = catalog['target']
    require(isinstance(target.get('name'), str) and TARGET.fullmatch(target['name']), 'invalid-target', 'target.name')
    manifest_path = root / 'backup_manifest'
    manifest_size = pinned_path(manifest_path, stat.S_ISREG, 'missing-backup-manifest').st_size
    require(manifest_size <= 32 * 1024 * 1024, 'manifest-size', 'manifest exceeds lab bounds')
    with manifest_path.open('rb') as stream:
        validate_object(stream, manifest_path, catalog['base']['manifest'], manifest_size)
        manifest = read_json(stream.read())
    base_timeline = timeline if version == 1 else catalog['base_timeline']
    require(type(base_timeline) is int and 0 < base_timeline <= 0xffffffff, 'invalid-base-timeline', 'base_timeline')
    first, last, range_row = required_range(manifest, target['lsn'], base_timeline)
    wal_root = root / 'wal'
    pinned_path(wal_root, stat.S_ISDIR, 'missing-wal-directory')
    history = fork = None
    if version == 2:
        history, fork = read_history(wal_root, catalog['history'], base_timeline, timeline)
        require(lsn_value(range_row['End-LSN']) <= fork < lsn_value(target['lsn']),
                'unreachable-timeline-target', 'fork must follow the base WAL range and strictly precede the target')
    plan = wal_plan(first, last, base_timeline, timeline, fork)
    records = catalog['wal']
    require(isinstance(records, dict), 'catalog-wal-coverage', 'invalid declared segment map')
    verify_base(root / 'base.tar.gz', catalog['base']['archive'], system_id, base_timeline,
                first, range_row['Start-LSN'])
    verified = []
    for segment_timeline, number, inherited_timeline in plan:
        name = wal_name(segment_timeline, number)
        require(name in records, 'catalog-wal-gap', name)
        verify_wal(wal_root / name, records[name], system_id, segment_timeline, number, inherited_timeline)
        verified.append(name)
    require(len(verified) == len(records), 'catalog-wal-coverage', 'declared WAL contains extra segments')
    result = dict(status='verified', catalog_sha256=anchor, system_identifier=cluster, timeline=timeline,
                  target=target, base_sha256=catalog['base']['archive']['sha256'],
                  base_wal_start=range_row['Start-LSN'], base_wal_end=range_row['End-LSN'], segments=verified)
    if history is not None:
        result['timeline_history'] = history
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, required=True)
    parser.add_argument('--catalog-sha256', required=True)
    args = parser.parse_args()
    try:
        result = verify(args.archive, args.catalog_sha256)
    except Rejected as error:
        print(json.dumps(dict(status='rejected', code=error.code, detail=str(error))))
        return 2
    except (ValueError, AttributeError, KeyError, TypeError, OSError, tarfile.TarError, struct.error):
        print(json.dumps(dict(status='rejected', code='invalid-archive', detail='archive schema or container is invalid')))
        return 2
    print(json.dumps(result, indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
