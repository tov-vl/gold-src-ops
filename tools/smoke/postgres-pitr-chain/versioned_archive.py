"""Stage pinned object versions for an isolated PITR recovery rehearsal."""

import hashlib
import json
from pathlib import Path
import re
import shutil
import tempfile

from pitr_chain import Rejected, SHA, read_json, require, verify

STAGED_PATH = re.compile(r'(?:chain\.json|base\.tar\.gz|backup_manifest|wal/(?:[0-9A-F]{24}|[0-9A-F]{8}\.history))')
VERSION = re.compile(r'[A-Za-z0-9_-]{1,128}')
BLOCK = 1024 * 1024


class AcknowledgementLost(ConnectionError):
    pass


class ArchiveFixture:
    """Keep each uploaded version on disk, including the bytes behind older keys."""

    def __init__(self, root):
        self.root = root
        self.root.mkdir()
        self.objects = {}
        self.sequence = 0

    def put(self, key, source, drop_acknowledgement=False):
        self.sequence += 1
        version = f'v-{self.sequence}'
        target = self.root / version
        shutil.copyfile(source, target)
        self.objects.setdefault(key, {})[version] = target
        if drop_acknowledgement:
            raise AcknowledgementLost()
        return version

    def versions(self, key):
        return tuple(self.objects.get(key, {}))

    def open_version(self, key, version):
        path = self.objects.get(key, {}).get(version)
        if path is None:
            raise Rejected('missing-object-version', key)
        return path.open('rb')


def describe(path):
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return dict(bytes=path.stat().st_size, sha256=digest)


def encode_snapshot(snapshot):
    body = (json.dumps(snapshot, indent=2, sort_keys=True) + '\n').encode()
    return body, hashlib.sha256(body).hexdigest()


def check_version(value):
    require(isinstance(value, str) and VERSION.fullmatch(value), 'invalid-object-version', repr(value))


def copy_checked(archive, key, version, identity, destination=None):
    check_version(version)
    size, sha = identity.get('bytes'), identity.get('sha256')
    require(type(size) is int and size > 0 and isinstance(sha, str) and SHA.fullmatch(sha),
            'invalid-versioned-identity', key)
    count = 0
    digest = hashlib.sha256()
    with archive.open_version(key, version) as stream:
        for chunk in iter(lambda: stream.read(BLOCK), b''):
            count += len(chunk)
            require(count <= size, 'object-version-size-mismatch', key)
            digest.update(chunk)
            if destination is not None:
                destination.write(chunk)
    require(count == size, 'object-version-size-mismatch', key)
    require(digest.hexdigest() == sha, 'object-version-sha-mismatch', key)


def stage_snapshot(archive, body, trusted_sha256, output):
    require(hashlib.sha256(body).hexdigest() == trusted_sha256,
            'snapshot-checksum-mismatch', 'versioned catalog')
    snapshot = read_json(body)
    require(type(snapshot.get('version')) is int and snapshot['version'] == 1,
            'unsupported-version-catalog', 'version')
    objects = snapshot['objects']
    require(isinstance(objects, dict) and {'chain.json', 'base.tar.gz', 'backup_manifest'} <= objects.keys()
            and all(STAGED_PATH.fullmatch(key) for key in objects), 'invalid-version-catalog-path', 'objects')
    require(not output.exists(), 'staged-archive-exists', output.name)
    parent = output.parent.resolve()
    with tempfile.TemporaryDirectory(prefix='pending-versions-', dir=parent) as name:
        provisional = Path(name).resolve()
        require(provisional.parent == parent, 'unsafe-stage', output.name)
        pending = provisional / 'stage'
        (pending / 'wal').mkdir(parents=True)
        for key, identity in objects.items():
            with (pending / key).open('xb') as target:
                copy_checked(archive, key, identity['object_version'], identity, target)
        result = verify(pending, snapshot['chain_catalog_sha256'])
        expected = {'chain.json', 'base.tar.gz', 'backup_manifest', *(f'wal/{name}' for name in result['segments'])}
        if 'timeline_history' in result:
            expected.add(f"wal/{result['timeline_history']}")
        require(objects.keys() == expected, 'version-catalog-wal-coverage', 'objects')
        pending.rename(output)
    return result


def reconcile_unknown_upload(archive, key, identity):
    versions = archive.versions(key)
    require(versions, 'missing-upload', key)
    require(len(versions) == 1, 'ambiguous-upload-versions', key)
    version, = versions
    copy_checked(archive, key, version, identity)
    return version


def expect_rejected(code, operation):
    try:
        operation()
    except Rejected as error:
        if error.code == code:
            return error.code
        raise
    raise RuntimeError(f'Expected rejection {code}')


def rehearse_versions(folder, stage, work, output, admitted):
    archive = ArchiveFixture(work / 'versioned-objects')
    items = ['chain.json', 'base.tar.gz', 'backup_manifest']
    if 'timeline_history' in admitted:
        items.append(f"wal/{admitted['timeline_history']}")
    items.extend(f'wal/{name}' for name in admitted['segments'])
    catalog = {key: {**describe(folder / key), 'object_version': archive.put(key, folder / key)} for key in items}
    snapshot = dict(version=1, chain_catalog_sha256=admitted['catalog_sha256'], objects=catalog)
    body, anchor = encode_snapshot(snapshot)

    target = items[-1]
    wrong = work / 'latest-wal-with-changed-content'
    shutil.copyfile(folder / target, wrong)
    with wrong.open('r+b') as stream:
        offset = 4096
        stream.seek(offset)
        byte = stream.read(1)
        require(len(byte) == 1, 'fixture-wal-size', target)
        stream.seek(offset)
        stream.write(bytes([byte[0] ^ 1]))
    latest = archive.put(target, wrong)
    pinned = catalog[target]['object_version']
    require(archive.versions(target)[-1] == latest and pinned != latest
            and describe(wrong)['sha256'] != catalog[target]['sha256'], 'fixture-version-overlap', target)

    def control(case, version, rejection):
        edited = {**snapshot, 'objects': {**catalog, target: {**catalog[target], 'object_version': version}}}
        mutated, fixture_anchor = encode_snapshot(edited)
        return expect_rejected(rejection, lambda: stage_snapshot(archive, mutated, fixture_anchor, work / case))

    result = stage_snapshot(archive, body, anchor, stage)
    require(result == admitted, 'staged-chain-changed', target)
    missing = control('missing-version', 'not-present', 'missing-object-version')
    changed = control('wrong-version-bytes', latest, 'object-version-sha-mismatch')
    untrusted = expect_rejected('snapshot-checksum-mismatch',
                                lambda: stage_snapshot(archive, body, '0' * 64, work / 'untrusted'))

    key = 'synthetic-upload-control'
    try:
        archive.put(key, folder / target, drop_acknowledgement=True)
    except AcknowledgementLost:
        reconciled = reconcile_unknown_upload(archive, key, catalog[target])
    else:
        raise RuntimeError('Fixture did not lose the upload acknowledgement')
    archive.put(key, folder / target)
    ambiguous = expect_rejected('ambiguous-upload-versions',
                                lambda: reconcile_unknown_upload(archive, key, catalog[target]))
    (output / 'versioned-catalog.json').write_bytes(body)
    return dict(status='verified', catalog_sha256=anchor, pinned_object_count=len(catalog),
                selected_wal=target, selected_version=pinned, newer_version=latest,
                no_latest_fallback=True, missing_version=missing, changed_bytes=changed,
                wrong_catalog_anchor=untrusted, reconciled_upload=reconciled,
                ambiguous_upload=ambiguous)
