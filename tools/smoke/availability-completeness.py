#!/usr/bin/env python3
"""Offline boundary and sanitization tests for the diagnostic projection."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'ops/availability/completeness-report.py'
spec = importlib.util.spec_from_file_location('completeness', SCRIPT)
report = importlib.util.module_from_spec(spec)
spec.loader.exec_module(report)
PIN = 'a' * 40
START = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)


def text(index, seconds=0):
    return (START + dt.timedelta(minutes=index, seconds=seconds)).isoformat().replace('+00:00', 'Z')


def row(index, **values):
    result = dict(scheduled_at_utc=text(index), started_at_utc=None, completed_at_utc=text(index, 20),
                  monitor_revision='v2-4-shadow-001', location='Frankfurt', role='primary',
                  execution_id='private-marker-' + str(index), outcome='good', http_status=200, duration_ms=1)
    result.update(values)
    return result


def source(rows):
    return [(r, report.moment(r['scheduled_at_utc']),
             report.moment(r['started_at_utc']) if r['started_at_utc'] else None,
             report.moment(r['completed_at_utc'])) for r in rows]


def evaluation(missing=0, ignored=0, duplicates=0, bad=None):
    return dict(evaluator_revision='availability-evaluator-v1', window_start_utc=text(0),
                window_end_utc=text(1440), evaluated_at_utc=text(1445),
                **report.IDENTITY, expected_slot_count=1440, evaluated_slot_count=1440,
                pending_slot_count=0, good_slot_count=1440 - (missing if bad is None else bad),
                bad_slot_count=missing if bad is None else bad, missing_slot_count=missing,
                duplicate_record_count=duplicates, ignored_non_canonical_attempt_count=ignored)


def receipt(run_id=1, event='schedule', left=0, right=360, **values):
    result = dict(run_id=run_id, event=event, window_start_utc=text(left), window_end_utc=text(right),
                  run_attempt=1, archive_job_succeeded=True, archive_receipt_verified=True)
    result.update(values)
    return result


def receipts(*rows):
    return dict(schema_version=1, exporter_revision=PIN, receipts=list(rows))


class CompletenessTests(unittest.TestCase):
    def test_eight_slot_regression_and_multiple_observation(self):
        missing = {22, 36, 178, 553, 1334, 1398, 1399, 1403}
        rows = [row(i) for i in range(1440) if i not in missing]
        rows.append(row(19, execution_id='second-observation', completed_at_utc=text(19, 59.856)))
        value = report.build(source(rows), evaluation(8, ignored=1), PIN)
        self.assertEqual([text(i) for i in sorted(missing)], [r['slot_start_utc'] for r in value['missing_slot_details']])
        self.assertEqual((1432, 8, 1), (value['populated_slots'], value['missing_slots'], value['multiple_observation_slots']))
        self.assertEqual('unknown', value['archive_coverage']['status'])
        self.assertNotIn('private-marker', json.dumps(value))

    def test_millisecond_and_tick_precision_neighbors(self):
        rows = [row(i) for i in range(1440) if i != 1]
        rows[0]['completed_at_utc'] = text(0, 59.856)
        rows[1]['completed_at_utc'] = '2026-01-01T00:02:00.0000001Z'
        detail = report.build(source(rows), evaluation(1), PIN)['missing_slot_details'][0]
        self.assertEqual('2026-01-01T00:00:59.856Z', detail['previous_source_utc'])
        self.assertEqual('2026-01-01T00:02:00.0000001Z', detail['next_source_utc'])

    def test_half_open_window_and_other_population(self):
        rows = [row(i) for i in range(1440)] + [row(-1), row(1440), row(0, role='diagnostic'), row(0, location='Other')]
        self.assertEqual(0, report.build(source(rows), evaluation(), PIN)['missing_slots'])

    def test_empty_population_bounded_and_edge_neighbors(self):
        value = report.build([], evaluation(1440), PIN)
        self.assertEqual(1440, len(value['missing_slot_details']))
        self.assertIsNone(value['missing_slot_details'][0]['previous_source_utc'])
        self.assertIsNone(value['missing_slot_details'][-1]['next_source_utc'])
        self.assertLess(len(json.dumps(value)), 1024 * 1024)

    def test_bad_observation_is_not_missing(self):
        rows = [row(i) for i in range(1440)]
        rows[10].update(outcome='http_error', http_status=503)
        value = report.build(source(rows), evaluation(bad=1), PIN)
        self.assertEqual(0, value['missing_slots'])

    def test_canonical_first_attempt_is_not_replaced_by_success(self):
        rows = [row(i) for i in range(1440)]
        rows[0].update(outcome='http_error', http_status=503)
        rows.append(row(0, execution_id='retry', completed_at_utc=text(0, 40)))
        report.build(source(rows), evaluation(ignored=1, bad=1), PIN)
        with self.assertRaises(ValueError):
            report.build(source(rows), evaluation(ignored=1), PIN)

    def test_duplicates_and_conflicts(self):
        rows = [row(i) for i in range(1440)]
        report.build(source(rows + [rows[0]]), evaluation(duplicates=1), PIN)
        with self.assertRaises(ValueError):
            report.build(source(rows + [row(0, duration_ms=2)]), evaluation(duplicates=1), PIN)

    def test_population_counts_and_identity_fail_closed(self):
        for field, value in [('missing_slot_count', 1), ('expected_slot_count', 1439), ('pending_slot_count', 1),
                             ('monitor_revision', 'untrusted'), ('evaluator_revision', 'unknown'), ('good_slot_count', True)]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                report.build(source([row(i) for i in range(1440)]), evaluation() | {field: value}, PIN)

    def test_utc_and_minute_validation(self):
        for value in ['2026-01-01T00:00:00+03:00', '2026-02-30T00:00:00Z', 'x', '2026-01-01T00:00:00.00000001Z']:
            with self.subTest(value=value), self.assertRaises(ValueError):
                report.moment(value)
        with self.assertRaises(ValueError):
            report.moment('2026-01-01T00:00:00.001Z', True)

    def test_coverage_clip_overlap_and_manual_separation(self):
        value = report.coverage(receipts(receipt(left=-60, right=600), receipt(2, left=540, right=1200),
                                        receipt(3, 'workflow_dispatch', 0, 1440)),
                                report.moment(text(0)), report.moment(text(1440)), PIN)
        self.assertEqual([{'start_utc': text(1200), 'end_exclusive_utc': text(1440), 'minutes': 240,
                           'boundary': 'open_tail'}], value['scheduled_uncovered'])
        self.assertEqual([], value['scheduled_and_manual_uncovered'])
        self.assertNotIn('run_id', json.dumps(value))

    def test_closed_gaps_and_exclusive_edges(self):
        value = report.coverage(receipts(receipt(left=0, right=360), receipt(2, left=420, right=1440)),
                                report.moment(text(0)), report.moment(text(1440)), PIN)
        self.assertEqual(60, value['scheduled_uncovered'][0]['minutes'])
        self.assertEqual('closed_gap', value['scheduled_uncovered'][0]['boundary'])

    def test_unknown_is_not_empty_verified_coverage(self):
        self.assertEqual('unknown', report.coverage(None, 0, 86400, PIN)['status'])
        value = report.coverage(receipts(), report.moment(text(0)), report.moment(text(1440)), PIN)
        self.assertEqual(1440, value['scheduled_uncovered'][0]['minutes'])
        self.assertEqual('open_window', value['scheduled_uncovered'][0]['boundary'])

    def test_leading_uncovered_interval_is_not_a_closed_gap(self):
        value = report.coverage(receipts(receipt(left=60, right=1440)),
                                report.moment(text(0)), report.moment(text(1440)), PIN)
        self.assertEqual('open_head', value['scheduled_uncovered'][0]['boundary'])
        self.assertEqual(60, value['scheduled_uncovered'][0]['minutes'])

    def test_receipt_guards(self):
        for extra in [dict(run_attempt=2), dict(event='push'), dict(archive_job_succeeded=False),
                      dict(archive_receipt_verified=False), dict(run_id=True), dict(secret='do-not-echo')]:
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                report.coverage(receipts(receipt(**extra)), report.moment(text(0)), report.moment(text(1440)), PIN)
        for document in [receipts(receipt(), receipt()), receipts(*[receipt(i + 1) for i in range(21)]),
                         receipts() | {'exporter_revision': 'b' * 40}]:
            with self.assertRaises(ValueError):
                report.coverage(document, report.moment(text(0)), report.moment(text(1440)), PIN)

    def test_json_and_file_limits(self):
        for raw in [b'{"a":1,"a":2}', b'{"a":NaN}', b'\xff', b'']:
            with self.assertRaises(ValueError):
                report.decode(raw, 100)
        with self.assertRaises(ValueError):
            report.decode(b'{}', 1)
        with tempfile.TemporaryDirectory(prefix='availability-completeness-') as directory:
            path = Path(directory) / 'segment.jsonl'
            for raw in [b'x' * 16385, b'{}\n', b'\n']:
                path.write_bytes(raw)
                with self.assertRaises(ValueError):
                    report.records(path)

    def test_input_record_bounds_and_unknown_fields(self):
        with tempfile.TemporaryDirectory(prefix='availability-completeness-') as directory:
            path = Path(directory) / 'records.jsonl'
            path.write_text(json.dumps(row(0) | {'provider_secret': 'do-not-echo'}), encoding='utf-8')
            with self.assertRaises(ValueError):
                report.records(path)
            path.write_text((json.dumps(row(0)) + '\n') * 20001, encoding='utf-8')
            with self.assertRaises(ValueError):
                report.records(path)

    def test_neighbor_never_borrows_from_outside_window(self):
        rows = [row(i) for i in range(1, 1439)] + [row(-1), row(1440)]
        value = report.build(source(rows), evaluation(2), PIN)
        self.assertIsNone(value['missing_slot_details'][0]['previous_source_utc'])
        self.assertIsNone(value['missing_slot_details'][-1]['next_source_utc'])

    def test_cli_retains_only_projection_and_refuses_overwrite(self):
        with tempfile.TemporaryDirectory(prefix='availability-completeness-') as directory:
            root = Path(directory)
            segment, result, output = root / 'raw.jsonl', root / 'evaluation.json', root / 'diagnostics.json'
            segment.write_text('\n'.join(json.dumps(row(i)) for i in range(1440)), encoding='utf-8')
            result.write_text(json.dumps(evaluation()), encoding='utf-8')
            command = [sys.executable, '-B', str(SCRIPT), '--input', str(segment), '--evaluation', str(result),
                       '--exporter-revision', PIN, '--output', str(output)]
            first = subprocess.run(command, capture_output=True, timeout=15)
            self.assertEqual(0, first.returncode, first.stderr)
            original = output.read_bytes()
            self.assertNotIn(b'private-marker', original)
            second = subprocess.run(command, capture_output=True, timeout=15)
            self.assertEqual(1, second.returncode)
            self.assertEqual(original, output.read_bytes())
            self.assertNotIn(str(root).encode(), second.stderr)


if __name__ == '__main__':
    unittest.main()
