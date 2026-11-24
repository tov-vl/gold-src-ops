"""Build independent PostgreSQL fixtures, exercise PITR preflight, and restore from verified bytes."""

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

from lab import Lab, resolve_images, wait_for
from pitr_chain import SEGMENT_SIZE, required_range, wal_name
from versioned_archive import describe, rehearse_versions

HERE = Path(__file__).resolve().parent
TARGET = 'chain-restore-point'


def publish_catalog(folder, document):
    destination = folder / 'chain.json'
    destination.write_text(json.dumps(document, sort_keys=True, indent=2) + '\n', encoding='utf-8')
    return describe(destination)['sha256']


def preflight(folder, anchor, expected_code=None):
    process = subprocess.run([sys.executable, '-B', str(HERE / 'pitr_chain.py'), '--archive', str(folder),
                              '--catalog-sha256', anchor], capture_output=True, text=True, timeout=45)
    if not process.stdout:
        raise RuntimeError(f'Preflight returned no JSON: {process.stderr[-800:]}')
    result = json.loads(process.stdout)
    if expected_code:
        assert process.returncode == 2 and result['status'] == 'rejected' and result['code'] == expected_code, result
    else:
        assert process.returncode == 0 and result['status'] == 'verified', result
    return result


def stage_base(lab, source_base, folder):
    folder.mkdir()
    for name in ('base.tar.gz', 'backup_manifest'):
        lab.docker('cp', f'{lab.primary}:{source_base}/{name}', str(folder / name))
    return json.loads((folder / 'backup_manifest').read_bytes())


def source_identity(lab):
    cluster = lab.sql(lab.primary, 'SELECT system_identifier::text FROM pg_control_system()')
    timeline = int(lab.sql(lab.primary, 'SELECT timeline_id FROM pg_control_checkpoint()'))
    return cluster, timeline


def stage_wal(lab, wait_for, name, dest):
    expected = wait_for(f'Archive did not publish exact segment {name}',
                        lambda: lab.archive_read(lab.primary_archive, '/sha/wal/' + name), 35)
    lab.docker('cp', f'{lab.primary_archive}:/archive/{name}', str(dest))
    identity = describe(dest)
    assert identity['bytes'] == SEGMENT_SIZE and identity['sha256'] == expected, name
    return identity


def chain_for(lab, folder, manifest, shared_wal, cluster, timeline, target):
    first, last, _ = required_range(manifest, target['lsn'], timeline)
    (folder / 'wal').mkdir()
    records = {}
    for number in range(first, last + 1):
        name = wal_name(timeline, number)
        path = shared_wal / name
        if path.exists():
            identity = describe(path)
        else:
            identity = stage_wal(lab, wait_for, name, path)
        shutil.copyfile(path, folder / 'wal' / name)
        records[name] = identity
    catalog = dict(version=1, postgresql_major=16, system_identifier=cluster, timeline=timeline,
                   wal_segment_bytes=SEGMENT_SIZE, target=target,
                   base=dict(archive=describe(folder / 'base.tar.gz'), manifest=describe(folder / 'backup_manifest')),
                   wal=records)
    return catalog, publish_catalog(folder, catalog)


def foreign_segment(lab, folder, output, desired, expected_cluster):
    output.mkdir()
    sandbox = Lab(lab.context, output, lab.images)
    try:
        sandbox.initialize('0')
        cluster, _ = source_identity(sandbox)
        assert cluster != expected_cluster, 'Source identities must be independent'
        for step in range(12):
            picked = sandbox.mark(f'foreign-probe-{step}')
            if picked == desired:
                name = folder / picked
                identity = stage_wal(sandbox, wait_for, picked, name)
                sandbox.stop_source()
                sandbox.verify_network()
                return name, identity, cluster
            assert picked < desired, f'Foreign generator passed segment {desired}'
        raise RuntimeError('Foreign generator never produced the exact filename')
    finally:
        failures = sandbox.cleanup()
        assert not failures, failures


def negative_checks(lab, older, anchored_catalog, anchor, admitted, scratch, output, cluster):
    names = admitted['segments']
    assert len(names) >= 3, 'Require an internal WAL segment for the negative case'
    selected = names[len(names) // 2]
    staged = scratch / 'negative'
    shutil.copytree(older, staged)
    path = staged / 'wal' / selected
    path.unlink()
    missing = preflight(staged, anchor, 'missing-wal')
    assert missing['detail'] == selected

    shutil.copyfile(older / 'wal' / selected, path)
    with path.open('r+b') as stream:
        stream.seek(4096)
        before = stream.read(1)
        assert len(before) == 1
        stream.seek(4096)
        stream.write(bytes([before[0] ^ 1]))
    corrupt = preflight(staged, anchor, 'checksum-mismatch')
    assert corrupt['detail'] == selected

    foreign, foreign_identity, foreign_cluster = foreign_segment(
        lab, scratch, output / 'foreign-lab', selected, cluster)
    shutil.copyfile(foreign, path)
    foreign_catalog = {**anchored_catalog, 'wal': {**anchored_catalog['wal'], selected: foreign_identity}}
    foreign_anchor = publish_catalog(staged, foreign_catalog)
    wrong_cluster = preflight(staged, foreign_anchor, 'wal-cluster-mismatch')
    assert wrong_cluster['detail'] == selected and foreign_identity == describe(path)
    return dict(missing=missing, corrupt=corrupt, foreign=wrong_cluster,
                foreign_system_identifier=foreign_cluster,
                foreign_digest_authenticated_for_negative_fixture=True)


def retention_check(older, newer, old_anchor, new_anchor, admitted_a, admitted_b, scratch):
    required_a = set(admitted_a['segments'])
    required_b = set(admitted_b['segments'])
    protected = required_a | required_b
    old_only = required_a - required_b
    assert old_only and required_a & required_b, 'Require shared WAL and an older exclusive range'
    simulated = scratch / 'retention-a'
    shutil.copytree(older, simulated)
    for segment in sorted(old_only):
        (simulated / 'wal' / segment).unlink()
    older_broken = preflight(simulated, old_anchor, 'missing-wal')
    assert older_broken['detail'] in old_only
    newer_viable = preflight(newer, new_anchor)
    return dict(status='verified', older_base=admitted_a['base_sha256'], newer_base=admitted_b['base_sha256'],
                target=admitted_a['target'], required_by_older=sorted(required_a),
                required_by_newer=sorted(required_b), protected_when_both_retained=sorted(protected),
                expired_older_base_candidates=sorted(old_only), bytes_if_older_retired=len(old_only)*SEGMENT_SIZE,
                old_chain_verdict_if_removed=older_broken, newer_chain_verdict=newer_viable['status'],
                removed_from_only_a_disposable_copy=True)


def native_verify(lab, folder):
    command = ('mkdir /tmp/base && tar -xzf /verified/base.tar.gz -C /tmp/base && '
               'pg_verifybackup --manifest-path=/verified/backup_manifest --wal-directory=/verified/wal /tmp/base')
    result = lab.docker('run', '--pull', 'never', '--network', 'none', '--rm',
                        '--mount', f'type=bind,source={folder},target=/verified,readonly',
                        '--entrypoint', '/bin/sh', lab.images['postgres'], '-ec', command, timeout=75)
    assert 'backup successfully verified' in result.stdout, result.stdout
    return result.stdout.strip()


def restore_from_stage(lab, base_name, folder, catalog, scratch):
    flat = scratch / f'verified-for-restore-{base_name}'
    flat.mkdir()
    shutil.copyfile(folder / 'base.tar.gz', flat / 'base.tar.gz')
    (flat / 'base.tar.gz.sha256').write_bytes(catalog['base']['archive']['sha256'].encode('ascii') + b'\n')
    for name, identity in catalog['wal'].items():
        path = flat / name
        shutil.copyfile(folder / 'wal' / name, path)
        assert describe(path) == identity
        (flat / f'{name}.sha256').write_bytes(identity['sha256'].encode('ascii') + b'\n')
    assert describe(flat / 'base.tar.gz') == catalog['base']['archive']
    alias = lab.name + '-verified-' + base_name
    options = ['--mount', f'type=bind,source={flat},target=/archive,readonly', '-e', 'LAB_READ_ONLY=1']
    lab.create(alias, lab.images['archive'], options, ['python','-B','/lab/archive_server.py'])
    lab.wait_for_storage(alias)
    if lab.is_running(lab.primary):
        lab.stop_source()
    return lab.restore(f'verified-{base_name}-restore', alias, TARGET, [1, 2, 3])


def run(lab, scratch):
    started = time.monotonic()
    lab.initialize('0')
    cluster, timeline = source_identity(lab)
    older, newer, shared = (scratch / name for name in ('older', 'newer', 'archive-wal'))
    shared.mkdir()
    manifest_a = stage_base(lab, '/tmp/lab-backup', older)
    lab.sql(lab.primary, 'INSERT INTO lab_receipt VALUES (2)')
    lab.mark('before-second-base')
    lab.docker('exec', lab.primary, 'pg_basebackup', '-h', '/var/run/postgresql', '-U', 'postgres',
               '-D', '/tmp/lab-backup-second', '--format=tar', '--gzip', '--wal-method=none', '--checkpoint=fast',
               timeout=90)
    manifest_b = stage_base(lab, '/tmp/lab-backup-second', newer)
    lab.sql(lab.primary, 'INSERT INTO lab_receipt VALUES (3)')
    target = dict(name=TARGET, lsn=lab.sql(lab.primary, f"SELECT pg_create_restore_point('{TARGET}')"))
    lab.sql(lab.primary, 'INSERT INTO lab_receipt VALUES (4)')
    lab.sql(lab.primary, 'SELECT pg_switch_wal()')
    catalog_a, anchor_a = chain_for(lab, older, manifest_a, shared, cluster, timeline, target)
    catalog_b, anchor_b = chain_for(lab, newer, manifest_b, shared, cluster, timeline, target)
    preflight_started = time.monotonic()
    admitted_a = preflight(older, anchor_a)
    admitted_b = preflight(newer, anchor_b)
    preflight_seconds = round(time.monotonic()-preflight_started, 3)
    negatives = negative_checks(lab, older, catalog_a, anchor_a, admitted_a,
                                scratch, lab.output, cluster)
    retention = retention_check(older, newer, anchor_a, anchor_b, admitted_a, admitted_b, scratch)
    native_check = {name: native_verify(lab, folder)
                    for name, folder in (('older', older), ('newer', newer))}
    exact_stage = scratch / 'from-versioned-store'
    versioned = rehearse_versions(older, exact_stage, scratch, lab.output, admitted_a)
    verified_restore = restore_from_stage(lab, 'older', exact_stage, catalog_a, scratch)
    recent_only_restore = restore_from_stage(lab, 'newer', newer, catalog_b, scratch)
    retention['restore_without_older_wal'] = recent_only_restore
    lab.verify_network()
    for label, catalog in (('older', catalog_a), ('newer', catalog_b)):
        (lab.output / f'catalog-{label}.json').write_text(json.dumps(catalog, sort_keys=True, indent=2) + '\n',
                                                       encoding='utf-8')
    return (dict(status='passed', source_stopped_during_restore=True, later_row_excluded=True,
                 admitted_older=admitted_a, admitted_newer=admitted_b,
                 both_positive_preflights_seconds=preflight_seconds, negatives=negatives, native_verifybackup=native_check,
                 restore=verified_restore, versioned_restore=versioned,
                 elapsed_seconds=round(time.monotonic()-started, 3)), retention)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--context')
    parser.add_argument('--pull', action='store_true')
    args = parser.parse_args()
    if not __debug__:
        raise RuntimeError('The rehearsal requires fixture assertions')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    lab = Lab(args.context, output, resolve_images(args.context, args.pull))
    with tempfile.TemporaryDirectory(prefix='staging-', dir=output) as name:
        scratch = Path(name).resolve()
        assert scratch.parent == output and not scratch.is_symlink()
        try:
            result, retention = run(lab, scratch)
        except Exception:
            for name in lab.containers:
                logs = lab.docker('logs', '--tail', '100', name, check=False)
                (output / (name.removeprefix(lab.name + '-') + '-diagnostic.log')).write_text(
                    logs.stdout + logs.stderr, encoding='utf-8')
            raise
        finally:
            problems = lab.cleanup()
            assert not problems, problems
    result['cleanup'] = True
    for filename, data in (('result.json', result), ('retention.json', retention)):
        (output / filename).write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
