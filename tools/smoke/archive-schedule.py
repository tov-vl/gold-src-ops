#!/usr/bin/env python3
"""Offline schedule routing, public observation and failure-boundary checks."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('archive_monitor', ROOT / 'ops/availability/archive-monitor.py')
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)
NOW = dt.datetime(2026, 11, 11, 12, tzinfo=dt.timezone.utc)


def run():
    return {'id': 123, 'workflow_id': monitor.WORKFLOW, 'event': 'schedule', 'head_branch': 'main',
        'head_sha': 'a' * 40, 'created_at': '2026-11-11T10:00:00Z', 'run_attempt': 1,
        'status': 'completed', 'conclusion': 'success'}


def document(value=None):
    return {'total_count': 1, 'workflow_runs': [run() if value is None else value]}


def jobs(_):
    return {'total_count': 1, 'jobs': [{'run_id': 123, 'run_attempt': 1, 'name': 'Archive Primary Evidence',
        'status': 'completed', 'conclusion': 'success', 'started_at': '2026-11-11T10:00:05Z',
        'completed_at': '2026-11-11T10:01:00Z', 'steps': [{'name': name, 'status': 'completed',
        'conclusion': 'success'} for name in ('Verify pinned exporter revision', 'Export and archive primary evidence')]}]}


class Tests(unittest.TestCase):
    def test_success_is_age_of_schedule_creation_not_recent_metadata(self):
        value = run(); value['updated_at'] = '2026-11-11T11:59:00Z'
        result = dict(monitor.observe(document(value), jobs, NOW))
        self.assertEqual(result['last_success_timestamp_seconds'], NOW.timestamp() - 7200)
        self.assertEqual(result['has_success'], 1)

    def test_manual_wrong_branch_and_other_workflow_are_rejected(self):
        for name, value in [('event', 'workflow_dispatch'), ('head_branch', 'other'), ('workflow_id', 1),
                            ('head_sha', 'invalid'), ('id', True), ('created_at', '2026-11-11T12:00:01Z')]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                bad = run(); bad[name] = value
                monitor.observe(document(bad), jobs, NOW)

    def test_rerun_cannot_refresh_success(self):
        value = run(); value['run_attempt'] = 2
        result = dict(monitor.observe(document(value), lambda _: self.fail('Rerun jobs queried'), NOW))
        self.assertEqual(result['has_success'], 0)

    def test_empty_history_is_known_no_success(self):
        value = dict(monitor.observe({'total_count': 0, 'workflow_runs': []}, jobs, NOW))
        self.assertEqual((value['observation_success'], value['has_success']), (1, 0))

    def test_failed_skipped_cancelled_cannot_prove_archive(self):
        for conclusion in ('failure', 'skipped', 'cancelled', 'timed_out', 'neutral'):
            value = run(); value['conclusion'] = conclusion
            result = dict(monitor.observe(document(value), lambda _: self.fail('Failed jobs queried'), NOW))
            self.assertEqual((result['has_success'], result['latest_run_failed']), (0, 1))

    def test_running_latest_preserves_older_completed_success(self):
        value = run(); value.update(id=124, created_at='2026-11-11T11:59:00Z', status='in_progress', conclusion=None)
        result = dict(monitor.observe({'total_count': 2, 'workflow_runs': [value, run()]}, jobs, NOW))
        self.assertEqual(result['has_success'], 1)

    def test_invalid_jobs_steps_and_times_fail_closed(self):
        changes = [lambda j: j.update(total_count=2), lambda j: j.update(total_count=True), lambda j: j.update(jobs=[]),
            lambda j: j['jobs'][0].update(conclusion='skipped'),
            lambda j: j['jobs'][0].update(name='Other'),
            lambda j: j['jobs'][0].update(run_id=456),
            lambda j: j['jobs'][0].update(run_attempt=2),
            lambda j: j['jobs'][0].update(run_attempt=True),
            lambda j: j['jobs'][0].update(completed_at='2026-11-11T12:00:01Z'),
            lambda j: j['jobs'][0].update(started_at='2026-11-11T09:59:59Z'),
            lambda j: j['jobs'][0].update(completed_at='2026-11-11T10:16:00Z'),
            lambda j: j['jobs'][0]['steps'][1].update(conclusion='skipped')]
        for change in changes:
            value = jobs(123); change(value)
            with self.subTest(change=change), self.assertRaises(ValueError):
                monitor.observe(document(), lambda _: value, NOW)

    def test_duplicate_unsorted_and_truncated_envelopes_rejected(self):
        older = run(); older.update(id=122, created_at='2026-11-11T09:00:00Z')
        for value in ({'total_count': True, 'workflow_runs': []}, {'total_count': 1, 'workflow_runs': []},
            {'total_count': 2, 'workflow_runs': [run(), run()]},
            {'total_count': 2, 'workflow_runs': [older, run()]}):
            with self.subTest(value=value), self.assertRaises(ValueError):
                monitor.observe(value, jobs, NOW)

    def test_http_failure_rate_limit_timeout_invalid_body_publish_only_failure(self):
        for error in (ValueError(), TimeoutError(), OSError(), monitor.http.client.HTTPException()):
            with self.subTest(error=error):
                def fail(_):
                    raise error
                value = dict(monitor.collect(NOW, fail))
                self.assertEqual(set(value), {'observation_success', 'observed_timestamp_seconds'})
                self.assertEqual(value['observation_success'], 0)

    def test_json_duplicate_nonfinite_oversize_rejected(self):
        for raw in (b'{"x":1,"x":2}', b'{"x":NaN}', b'x' * 524289, b'', b'\xff'):
            with self.subTest(raw=raw[:20]), self.assertRaises((ValueError, UnicodeError)):
                monitor.decode(raw)

    def test_no_redirect_auth_or_retry_and_fixed_https_destination(self):
        class Response:
            status = 302
            def getheader(self, name, default=None):
                return 'application/json' if name == 'Content-Type' else default
        class Connection:
            def __init__(self, host, timeout):
                self.test = (host, timeout)
            def request(self, method, path, headers):
                require = self.test == ('api.github.com', 8) and method == 'GET' and 'Authorization' not in headers
                if not require:
                    raise AssertionError('Transport boundary changed')
            def getresponse(self):
                return Response()
            def close(self):
                pass
        with patch.object(monitor.http.client, 'HTTPSConnection', Connection), self.assertRaises(ValueError):
            monitor.request('/fixed-test')

    def test_numeric_payload_excludes_provider_fields(self):
        samples = monitor.observe(document(), jobs, NOW)
        raw = monitor.payload(samples, NOW)
        self.assertNotIn(b'head_sha', raw)
        self.assertNotIn(b'github.com', raw)
        body = json.loads(raw)
        self.assertEqual(len(body['resourceMetrics'][0]['scopeMetrics'][0]['metrics']), 5)
        for value in (True, float('nan'), -1):
            with self.assertRaises(ValueError):
                monitor.payload([('has_success', value)], NOW)

    def test_schedule_only_24_hours_preserves_manual_and_pinned_wrapper(self):
        text = (ROOT / '.github/workflows/availability-evidence.yml').read_text(encoding='utf-8')
        self.assertIn("REQUESTED_LOOKBACK_MINUTES: ${{ github.event_name == 'schedule' && '1440' || inputs.lookback_minutes }}", text)
        self.assertIn('default: 360', text)
        self.assertIn("ExporterAssemblyPath = ", text)
        self.assertIn("--overlap-minutes', '10'", text)
        self.assertIn('group: availability-evidence-writer-v1', text)
        self.assertIn('cancel-in-progress: false', text)


if __name__ == '__main__':
    unittest.main(verbosity=2)
