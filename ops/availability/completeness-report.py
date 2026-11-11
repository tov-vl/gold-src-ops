#!/usr/bin/env python3
"""Bounded, offline diagnostic projection. Never evaluates or changes the SLI."""
import argparse
import bisect
import datetime as dt
from decimal import Decimal
import json
from pathlib import Path
import re
import sys

UTC = dt.timezone.utc
REVISION = 'archive-completeness-v1'
IDENTITY = {'monitor_revision': 'v2-4-shadow-001', 'location': 'Frankfurt'}
OUTCOMES = {'good', 'dns_error', 'connect_error', 'tls_error', 'timeout',
            'redirect', 'http_error', 'monitor_error'}
FIELDS = {'scheduled_at_utc', 'started_at_utc', 'completed_at_utc', 'monitor_revision',
          'location', 'role', 'execution_id', 'outcome', 'http_status', 'duration_ms'}


def require(value):
    if not value:
        raise ValueError('Invalid bounded completeness evidence')


def decode(raw, maximum):
    require(0 < len(raw) <= maximum)

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result

    return json.loads(raw.decode('utf-8-sig'), object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False))


def read(path, maximum):
    with Path(path).open('rb') as stream:
        return decode(stream.read(maximum + 1), maximum)


def moment(value, minute=False):
    require(type(value) is str)
    match = re.fullmatch(r'(20\d\d-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d{1,7}))?(Z|\+00:00)', value)
    require(match is not None)
    whole = dt.datetime.strptime(match[1], '%Y-%m-%dT%H:%M:%S').replace(tzinfo=UTC)
    result = Decimal(int(whole.timestamp())) + Decimal('0.' + (match[2] or '0'))
    require(not minute or result % 60 == 0)
    return result


def iso(value):
    whole = int(value)
    fraction = value - whole
    suffix = format(fraction, 'f')[1:].rstrip('0') if fraction else ''
    return dt.datetime.fromtimestamp(whole, UTC).strftime('%Y-%m-%dT%H:%M:%S') + suffix + 'Z'


def integer(value, maximum=20000):
    require(type(value) is int and 0 <= value <= maximum)
    return value


def records(path):
    result = []
    with Path(path).open('rb') as stream:
        total = 0
        while line := stream.readline(16385):
            total += len(line)
            require(total <= 16 * 1024 * 1024 and len(line) <= 16384 and len(result) < 20000)
            row = decode(line, 16384)
            require(type(row) is dict and set(row) == FIELDS)
            require(row['role'] in ('primary', 'diagnostic') and row['outcome'] in OUTCOMES)
            for name in ('execution_id', 'monitor_revision', 'location'):
                require(type(row[name]) is str and 0 < len(row[name]) <= 256)
            scheduled = moment(row['scheduled_at_utc'], True)
            started = moment(row['started_at_utc']) if row['started_at_utc'] is not None else None
            completed = moment(row['completed_at_utc']) if row['completed_at_utc'] is not None else None
            require(started is None or completed is None or started <= completed)
            status = row['http_status']
            require(status is None or (type(status) is int and 100 <= status <= 599))
            duration = row['duration_ms']
            require(duration is None or (type(duration) is int and 0 <= duration <= 2**63 - 1))
            require(row['outcome'] != 'good' or status in (None, 200))
            result.append((row, scheduled, started, completed))
    return result


def uncovered(intervals, start, end):
    cursor, result = start, []
    for left, right in sorted((max(start, a), min(end, b)) for a, b in intervals):
        if right <= left:
            continue
        if left > cursor:
            result.append({'start_utc': iso(cursor), 'end_exclusive_utc': iso(left),
                           'minutes': int((left - cursor) / 60),
                           'boundary': 'closed_gap'})
        cursor = max(cursor, right)
    if cursor < end:
        result.append({'start_utc': iso(cursor), 'end_exclusive_utc': iso(end),
                       'minutes': int((end - cursor) / 60), 'boundary': 'open_tail'})
    return result


def coverage(document, start, end, exporter_revision):
    if document is None:
        return {'status': 'unknown', 'basis': 'no_reviewed_receipts_supplied'}
    require(type(document) is dict and set(document) == {'schema_version', 'exporter_revision', 'receipts'})
    require(type(document['schema_version']) is int and document['schema_version'] == 1)
    require(document['exporter_revision'] == exporter_revision)
    receipts = document['receipts']
    require(type(receipts) is list and len(receipts) <= 20)
    seen, scheduled, manual = set(), [], []
    for row in receipts:
        require(type(row) is dict and set(row) == {'run_id', 'event', 'window_start_utc', 'window_end_utc',
                                                  'run_attempt', 'archive_job_succeeded', 'archive_receipt_verified'})
        run_id = integer(row['run_id'], 2**63 - 1)
        require(run_id > 0 and run_id not in seen)
        seen.add(run_id)
        require(type(row['run_attempt']) is int and row['run_attempt'] == 1)
        require(row['archive_job_succeeded'] is True and row['archive_receipt_verified'] is True)
        require(row['event'] in ('schedule', 'workflow_dispatch'))
        left, right = moment(row['window_start_utc'], True), moment(row['window_end_utc'], True)
        require(60 * 60 <= right - left <= 24 * 60 * 60)
        (scheduled if row['event'] == 'schedule' else manual).append((left, right))
    return {'status': 'supplied_receipts', 'basis': 'caller_reviewed_completed_archive_receipts',
            'scheduled_receipt_count': len(scheduled), 'manual_receipt_count': len(manual),
            'scheduled_uncovered': uncovered(scheduled, start, end),
            'scheduled_and_manual_uncovered': uncovered(scheduled + manual, start, end),
            'object_integrity': 'not_checked', 'schedule_freshness': 'not_evaluated'}


def build(source, evaluation, exporter_revision, receipt_document=None):
    require(type(exporter_revision) is str and re.fullmatch('[a-f0-9]{40}', exporter_revision))
    require(type(evaluation) is dict and evaluation.get('evaluator_revision') == 'availability-evaluator-v1')
    require(all(evaluation.get(k) == v for k, v in IDENTITY.items()))
    start, end = moment(evaluation['window_start_utc'], True), moment(evaluation['window_end_utc'], True)
    require(end - start == 86400 and moment(evaluation['evaluated_at_utc']) >= end + 300)
    require(integer(evaluation['expected_slot_count']) == 1440)
    require(integer(evaluation['evaluated_slot_count']) == 1440 and integer(evaluation['pending_slot_count']) == 0)
    slots, executions, duplicates = {}, {}, 0
    for item in source:
        row, scheduled, started, completed = item
        if row['role'] != 'primary' or any(row[k] != v for k, v in IDENTITY.items()) or not start <= scheduled < end:
            continue
        require(completed is not None and completed // 60 == scheduled // 60)
        key = row['execution_id']
        if key in executions:
            require(executions[key] == item)
            duplicates += 1
            continue
        executions[key] = item
        slots.setdefault(scheduled, []).append(item)
    missing = [start + i * 60 for i in range(1440) if start + i * 60 not in slots]
    ignored, good = 0, 0
    for candidates in slots.values():
        selected = min(candidates, key=lambda r: (r[2] if r[2] is not None else r[3], r[3], r[0]['execution_id']))
        good += selected[0]['outcome'] == 'good'
        ignored += len(candidates) - 1
    for name, expected in {'missing_slot_count': len(missing), 'good_slot_count': good,
                           'bad_slot_count': 1440 - good, 'duplicate_record_count': duplicates,
                           'ignored_non_canonical_attempt_count': ignored}.items():
        require(integer(evaluation[name]) == expected)
    timestamps = sorted({r[3] for r in executions.values()})
    details = []
    for slot in missing:
        index = bisect.bisect_left(timestamps, slot)
        details.append({'slot_start_utc': iso(slot),
                        'previous_source_utc': iso(timestamps[index - 1]) if index else None,
                        'next_source_utc': iso(timestamps[index]) if index < len(timestamps) else None})
    return {'schema_version': 1, 'diagnostics_revision': REVISION, 'exporter_revision': exporter_revision,
            **IDENTITY, 'role': 'primary', 'window_start_utc': iso(start), 'window_end_exclusive_utc': iso(end),
            'expected_slots': 1440, 'populated_slots': len(slots), 'missing_slots': len(missing),
            'multiple_observation_slots': sum(len(v) > 1 for v in slots.values()),
            'missing_slot_details': details, 'archive_coverage': coverage(receipt_document, start, end, exporter_revision),
            'sli_policy': 'unchanged', 'neighbor_scope': 'selected_primary_window_only',
            'provider_cause': 'not_established'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', required=True)
    parser.add_argument('--evaluation', required=True)
    parser.add_argument('--exporter-revision', required=True)
    parser.add_argument('--receipts')
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        result = build(records(args.input), read(args.evaluation, 1024 * 1024), args.exporter_revision,
                       read(args.receipts, 32768) if args.receipts else None)
        raw = (json.dumps(result, ensure_ascii=True, separators=(',', ':'), allow_nan=False) + '\n').encode()
        require(len(raw) <= 1024 * 1024)
        with Path(args.output).open('xb') as stream:
            stream.write(raw)
    except (OSError, ValueError, TypeError, KeyError, OverflowError, IndexError):
        print('Completeness diagnostics failed validation; input and paths withheld.', file=sys.stderr)
        return 1
    print('Completeness diagnostics verified: {} populated, {} missing slots.'.format(result['populated_slots'], result['missing_slots']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
