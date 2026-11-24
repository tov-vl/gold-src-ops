"""Verify an anchored PostgreSQL 16 amd64 PITR catalog on one timeline before restore."""

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


def verify_wal(path, identity, system_id, timeline, number):
    size = pinned_path(path, stat.S_ISREG, 'missing-wal').st_size
    require(size == SEGMENT_SIZE, 'wal-segment-size', path.name)
    with path.open('rb') as stream:
        validate_object(stream, path, identity, size)
        magic, flags, actual_timeline, address, _, actual_id, segment_size, block_size = LONG_WAL.unpack(
            stream.read(LONG_WAL.size))
    require(magic == WAL_MAGIC and flags & 2 and not flags & ~15 and segment_size == SEGMENT_SIZE
            and block_size == WAL_BLOCK_SIZE, 'unsupported-wal-header', path.name)
    require(actual_id == system_id, 'wal-cluster-mismatch', path.name)
    require(actual_timeline == timeline, 'wal-timeline-mismatch', path.name)
    require(address == number * SEGMENT_SIZE, 'wal-address-mismatch', path.name)


def verify(root, anchor):
    require(SHA.fullmatch(anchor), 'invalid-catalog-anchor', 'expected a trusted SHA-256')
    pinned_path(root, stat.S_ISDIR, 'missing-archive')
    catalog_path = root / 'chain.json'
    size = pinned_path(catalog_path, stat.S_ISREG, 'missing-catalog').st_size
    require(0 < size <= 1024 * 1024, 'catalog-size', 'catalog exceeds lab bounds')
    body = catalog_path.read_bytes()
    require(hashlib.sha256(body).hexdigest() == anchor, 'catalog-checksum-mismatch', 'chain.json')
    catalog = read_json(body)
    for field, expected in (('version', 1), ('postgresql_major', 16), ('wal_segment_bytes', SEGMENT_SIZE)):
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
    first, last, range_row = required_range(manifest, target['lsn'], timeline)
    records = catalog['wal']
    require(isinstance(records, dict) and len(records) == last - first + 1,
            'catalog-wal-coverage', 'declared segments do not span the base through the target')
    verify_base(root / 'base.tar.gz', catalog['base']['archive'], system_id, timeline, first, range_row['Start-LSN'])
    wal_root = root / 'wal'
    pinned_path(wal_root, stat.S_ISDIR, 'missing-wal-directory')
    verified = []
    for number in range(first, last + 1):
        name = wal_name(timeline, number)
        require(name in records, 'catalog-wal-gap', name)
        verify_wal(wal_root / name, records[name], system_id, timeline, number)
        verified.append(name)
    return dict(status='verified', catalog_sha256=anchor, system_identifier=cluster, timeline=timeline,
                target=target, base_sha256=catalog['base']['archive']['sha256'],
                base_wal_start=range_row['Start-LSN'], base_wal_end=range_row['End-LSN'], segments=verified)


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
