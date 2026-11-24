"""Rehearse restoration before and after a real PostgreSQL timeline fork."""

import argparse
import json
from pathlib import Path
import re
import shutil
import tempfile
import time

from lab import Lab, resolve_images, wait_for
from pitr_chain import SEGMENT_SIZE, lsn_value, parse_history, required_range, wal_name, wal_plan
from rehearse_chain import (describe, native_verify, preflight, publish_catalog,
                             restore_from_stage, source_identity, stage_base)
from versioned_archive import rehearse_versions

BEFORE = 'before-timeline-fork'
AFTER = 'after-timeline-fork'


def archived(lab, name, destination):
    expected = wait_for(f'Archive did not publish {name}',
                        lambda: lab.archive_read(lab.primary_archive, '/sha/wal/' + name), 35)
    lab.docker('cp', f'{lab.primary_archive}:/archive/{name}', str(destination))
    identity = describe(destination)
    assert identity['sha256'] == expected, name
    return identity


def source_target(lab, server, target_name):
    return dict(name=target_name, lsn=lab.sql(server, f"SELECT pg_create_restore_point('{target_name}')"))


def promote_branch(lab, suffix, parent):
    server = lab.name + '-' + suffix
    archive = lab.primary_archive
    lab.start_recovery(server, archive, [
        'hot_standby=on', 'archive_mode=on',
        f'archive_command=sh /lab/wal_io.sh send {archive} "/wal/%f" "%p"',
        f'recovery_target_timeline={parent}', f'recovery_target_name={BEFORE}', 'recovery_target_action=pause',
    ])
    wait_for(f'{suffix} did not pause at the named target',
             lambda: lab.ready(server) and lab.sql(server, 'SELECT pg_get_wal_replay_pause_state()') == 'paused',
             35)
    assert lab.sql(server, 'SELECT pg_is_in_recovery()') == 't' and lab.ids(server) == [1, 2]
    assert lab.sql(server, 'SELECT pg_promote(true, 15)') == 't'
    assert lab.sql(server, 'SELECT pg_is_in_recovery()') == 'f' and lab.ids(server) == [1, 2]
    current = lab.sql(server, 'SELECT pg_walfile_name(pg_current_wal_insert_lsn())')
    child = int(current[:8], 16)
    assert child > parent
    return server, child


def stage(lab, folder, base, manifest, cluster, parent, timeline, target, history=None):
    folder.mkdir()
    wal = folder / 'wal'
    wal.mkdir()
    for name in ('base.tar.gz','backup_manifest'):
        shutil.copyfile(base / name, folder / name)
    first, last, _ = required_range(manifest, target['lsn'], parent)
    fork = None
    catalog = dict(version=1, postgresql_major=16, system_identifier=cluster, timeline=timeline,
                   wal_segment_bytes=SEGMENT_SIZE, target=target,
                   base=dict(archive=describe(folder/'base.tar.gz'), manifest=describe(folder/'backup_manifest')))
    if history is not None:
        name = history.name
        shutil.copyfile(history, wal/name)
        fork = parse_history(history.read_bytes(), parent, timeline)
        catalog.update(version=2, base_timeline=parent, history=dict(name=name, **describe(wal/name)))
    catalog['wal'] = {}
    for line, number, _ in wal_plan(first, last, parent, timeline, fork):
        name = wal_name(line, number)
        identity = archived(lab, name, wal / name)
        assert identity['bytes'] == SEGMENT_SIZE
        catalog['wal'][name] = identity
    return catalog, publish_catalog(folder, catalog)


def physical_transition(lab, folder, switch, parent, timeline):
    row = lab.docker('run', '--pull', 'never', '--rm', '--network', 'none',
                     '--mount', f'type=bind,source={folder / "wal"},target=/wal,readonly',
                     '--entrypoint', 'pg_waldump', lab.images['postgres'], '--path=/wal',
                     f'--timeline={timeline}', f'--start={switch}', '--limit=1').stdout.strip()
    match = re.search(r'END_OF_RECOVERY.+tli (\d+); prev tli (\d+)', row)
    assert match and int(match[2]) == parent, row
    return int(match[1]), row


def controls(lab, staged, after, anchor, sibling, sibling_first, sibling_timeline, switch, scratch):
    switch_number = lsn_value(switch) // SEGMENT_SIZE
    broken = scratch / 'negative-timeline'
    shutil.copytree(staged, broken)
    history = after['history']['name']
    (broken/'wal'/history).unlink()
    missing = preflight(broken, anchor, 'missing-timeline-history')
    assert missing['detail'] == history
    shutil.copyfile(staged/'wal'/history, broken/'wal'/history)
    target = next(reversed(after['wal']))
    assert target[8:] == sibling.name[8:] and target[:8] != sibling.name[:8]
    replacement = broken/'wal'/target
    shutil.copyfile(sibling, replacement)
    mistaken = {**after, 'wal': {**after['wal'], target: describe(replacement)}}
    negative_anchor = publish_catalog(broken, mistaken)
    foreign = preflight(broken, negative_anchor, 'wal-timeline-mismatch')
    assert foreign['detail'] == target

    first_case = scratch / 'sibling-first-wal'
    shutil.copytree(staged, first_case)
    first = wal_name(after['timeline'], switch_number)
    assert first in after['wal'] and first[8:] == sibling_first.name[8:]
    path = first_case / 'wal' / first
    shutil.copyfile(sibling_first, path)
    # Both first pages include the same parent prefix. Read the native fork record to distinguish them.
    with (staged / 'wal' / first).open('rb') as correct, path.open('rb') as substituted:
        assert correct.read(40) == substituted.read(40)
    actual, diagnostic = physical_transition(lab, first_case, switch, after['base_timeline'], after['timeline'])
    assert actual == sibling_timeline and actual != after['timeline'], diagnostic
    (lab.output/'sibling-native-record.log').write_text(diagnostic+'\n',encoding='utf-8')
    fork_control = dict(status='rejected', code='native-fork-timeline-mismatch',
                        expected=after['timeline'], actual=actual, inherited_header_matched=True)
    return missing, foreign, fork_control


def run(lab, scratch):
    started = time.monotonic()
    assert list(wal_plan(1, 2, 1, 2, 2*SEGMENT_SIZE)) == [(1, 1, None), (2, 2, None)]
    assert list(wal_plan(1, 2, 1, 2, 2*SEGMENT_SIZE+1)) == [(1, 1, None), (1, 2, None), (2, 2, 1)]
    lab.initialize('0')
    cluster, parent = source_identity(lab)
    assert parent == 1
    base = scratch / 'base'
    manifest = stage_base(lab, '/tmp/lab-backup', base)
    lab.sql(lab.primary, 'INSERT INTO lab_receipt VALUES (2)')
    before = source_target(lab, lab.primary, BEFORE)
    lab.sql(lab.primary, 'INSERT INTO lab_receipt VALUES (9)')
    lab.sql(lab.primary, 'SELECT pg_switch_wal()')
    before_folder = scratch / 'before'
    before_catalog, before_anchor = stage(lab, before_folder, base, manifest, cluster, parent, parent, before)
    verified_before = preflight(before_folder, before_anchor)
    native = native_verify(lab, before_folder)
    lab.stop_source()

    first, child = promote_branch(lab, 'child', parent)
    lab.sql(first, 'INSERT INTO lab_receipt VALUES (3)')
    lab.sql(first, 'SELECT pg_switch_wal()')
    lab.sql(first, 'INSERT INTO lab_receipt VALUES (4)')
    after = source_target(lab, first, AFTER)
    lab.sql(first, 'INSERT INTO lab_receipt VALUES (5)')
    lab.sql(first, 'SELECT pg_switch_wal()')
    history = scratch / f'{child:08X}.history'
    archived(lab, history.name, history)
    switch_lsn = parse_history(history.read_bytes(), parent, child)
    assert switch_lsn == lsn_value(before['lsn'])
    lab.docker('stop', '-t', '5', first)

    alternate, sibling_tli = promote_branch(lab, 'sibling', parent)
    assert sibling_tli > child
    lab.sql(alternate, 'INSERT INTO lab_receipt VALUES (7)')
    lab.sql(alternate, 'SELECT pg_switch_wal()')
    lab.sql(alternate, 'INSERT INTO lab_receipt VALUES (8)')
    sibling_target = source_target(lab, alternate, 'sibling-control')
    lab.sql(alternate, 'SELECT pg_switch_wal()')
    name = wal_name(sibling_tli, (lsn_value(sibling_target['lsn']) - 1)//SEGMENT_SIZE)
    sibling = scratch / name
    archived(lab, sibling.name, sibling)
    sibling_first = scratch / wal_name(sibling_tli, switch_lsn//SEGMENT_SIZE)
    assert sibling_first != sibling
    archived(lab, sibling_first.name, sibling_first)
    lab.docker('stop', '-t', '5', alternate)
    assert not any(lab.is_running(server) for server in (lab.primary, first, alternate))

    after_folder = scratch / 'after'
    after_catalog, after_anchor = stage(lab, after_folder, base, manifest, cluster, parent, child, after, history)
    verified_after = preflight(after_folder, after_anchor)
    actual_child, dump = physical_transition(lab, after_folder, before['lsn'], parent, child)
    assert actual_child == child, dump
    (lab.output/'fork-native-record.log').write_text(dump+'\n',encoding='utf-8')
    missing, wrong, first_wrong = controls(lab, after_folder, after_catalog, after_anchor, sibling,
                                           sibling_first, sibling_tli, before['lsn'], scratch)
    before_result = restore_from_stage(lab, 'before', before_folder, before_catalog, scratch, (1, 2))
    versioned_output = lab.output / 'versions'
    versioned_output.mkdir()
    selected = scratch / 'versioned-child'
    versioned = rehearse_versions(after_folder, selected, scratch, versioned_output, verified_after)
    after_result = restore_from_stage(lab, 'after', selected, after_catalog, scratch, (1, 2, 3, 4))
    for label, catalog in (('before', before_catalog), ('after', after_catalog)):
        (lab.output / f'catalog-{label}.json').write_text(json.dumps(catalog, sort_keys=True, indent=2)+'\n',
                                                       encoding='utf-8')
    lab.verify_network()
    return dict(status='passed', explicit_pg_promote=True, parent_timeline=parent, child_timeline=child,
                sibling_timeline=sibling_tli, switch_lsn=before['lsn'],
                source_clusters_stopped_during_restore=True, native_verifybackup=native,
                verified_before=verified_before, verified_after=verified_after,
                missing_history=missing, foreign_branch=wrong, foreign_fork_record=first_wrong,
                versioned_child=versioned,
                before=before_result, after=after_result,
                elapsed_seconds=round(time.monotonic()-started, 3))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
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
            result = run(lab, scratch)
        except Exception:
            for server in lab.containers:
                log = lab.docker('logs', '--tail', '100', server, check=False)
                (output / (server.removeprefix(lab.name + '-') + '-diagnostic.log')).write_text(
                    log.stdout + log.stderr, encoding='utf-8')
            raise
        finally:
            problems = lab.cleanup()
            assert not problems, problems
    result['cleanup'] = True
    body = json.dumps(result, indent=2) + '\n'
    (output / 'result.json').write_text(body, encoding='utf-8')
    print(body)


if __name__ == '__main__':
    main()
