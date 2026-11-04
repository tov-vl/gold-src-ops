#!/usr/bin/env python3
"""Synthetic resource pressure, parser failures, privacy and passive-read checks."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops/gameserver'))
import resource_diagnostics as contract


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


endpoint = module('resource_endpoint', 'ops/gameserver/resource-status.py')
monitor = module('resource_monitor', 'ops/production/resource-monitor.py')
NOW = dt.datetime(2026, 11, 4, 15, tzinfo=dt.timezone.utc)
MEMINFO = 'MemTotal: 4194304 kB\nMemFree: 1 kB\nMemAvailable: 2097152 kB\nSwapTotal: 1048576 kB\nSwapFree: 524288 kB\n'


def snapshot():
    return {'schemaVersion': 1, 'observedAtUtc': NOW.isoformat(),
            'memory': {'available': True, 'totalBytes': 4 * 1024**3, 'availableBytes': 2 * 1024**3,
                       'swapTotalBytes': 1024**3, 'swapFreeBytes': 512 * 1024**2},
            'cpu': {'available': True, 'busyRatio': 0.2, 'stealRatio': 0.05, 'sampleSeconds': 1.0}}


class Tests(unittest.TestCase):
    def test_complete_snapshot_has_bounded_unlabelled_numeric_metrics(self):
        value = snapshot()
        self.assertEqual(contract.validate(value, NOW), NOW)
        result = dict(monitor.observe(value, NOW))
        self.assertEqual(result['observation_success'], 1)
        body = json.loads(monitor.payload(list(result.items()), NOW))
        row = body['resourceMetrics'][0]
        self.assertEqual(row['resource']['attributes'], [
            {'key': 'service.name', 'value': {'stringValue': monitor.SERVICE}}])
        metrics = row['scopeMetrics'][0]['metrics']
        self.assertEqual(len(metrics), len(monitor.METRICS))
        self.assertTrue(all(set(p) == {'timeUnixNano', 'asDouble'} for m in metrics for p in m['gauge']['dataPoints']))
        self.assertLessEqual(len(monitor.payload(list(result.items()), NOW)), contract.MAX_JSON)

    def test_one_unavailable_group_preserves_other_group_without_fake_values(self):
        for name, removed, retained in (('memory', 'memory_available_bytes', 'cpu_busy_ratio'),
                                        ('cpu', 'cpu_busy_ratio', 'memory_available_bytes')):
            value = snapshot()
            value[name] = {'available': False}
            rows = dict(monitor.observe(value, NOW))
            self.assertEqual(rows['observation_success'], 1)
            self.assertEqual(rows[name + '_available'], 0)
            self.assertNotIn(removed, rows)
            self.assertIn(retained, rows)

    def test_invalid_schema_private_fields_numbers_and_ratios_fail_closed(self):
        mutations = [lambda v: v.update(schemaVersion=True), lambda v: v.update(host='private'),
            lambda v: v.pop('cpu'), lambda v: v['memory'].update(totalBytes=0),
            lambda v: v['memory'].update(availableBytes=-1), lambda v: v['memory'].update(availableBytes=True),
            lambda v: v['memory'].update(availableBytes=2**63), lambda v: v['memory'].update(swapFreeBytes=2**40),
            lambda v: v['memory'].update(available=False), lambda v: v['memory'].update(path='/private'),
            lambda v: v['cpu'].update(busyRatio=1.1), lambda v: v['cpu'].update(stealRatio=-0.1),
            lambda v: v['cpu'].update(busyRatio=True), lambda v: v['cpu'].update(busyRatio=float('nan')),
            lambda v: v['cpu'].update(busyRatio=0.9, stealRatio=0.2),
            lambda v: v['cpu'].update(sampleSeconds=0.74), lambda v: v['cpu'].update(sampleSeconds=3.01)]
        for change in mutations:
            value = snapshot()
            change(value)
            with self.subTest(change=change):
                self.assertEqual(monitor.observe(value, NOW), [
                    ('observation_success', 0), ('observed_timestamp_seconds', NOW.timestamp())])

    def test_duplicate_oversized_and_nonfinite_json_refuse(self):
        for raw in (b'{"a":1,"a":2}', b' ' * 4097, b'{"a":NaN}', b'{"a":Infinity}'):
            with self.assertRaises(ValueError):
                contract.decode(raw)

    def test_bad_or_naive_stale_and_future_times_refuse(self):
        for value in ('bad', NOW.replace(tzinfo=None).isoformat(),
                      (NOW - dt.timedelta(seconds=31)).isoformat(), (NOW + dt.timedelta(seconds=6)).isoformat()):
            row = snapshot()
            row['observedAtUtc'] = value
            self.assertEqual(dict(monitor.observe(row, NOW))['observation_success'], 0)
        for delta in (-30, 5):
            row = snapshot()
            row['observedAtUtc'] = (NOW + dt.timedelta(seconds=delta)).isoformat()
            self.assertEqual(dict(monitor.observe(row, NOW))['observation_success'], 1)

    def test_zero_available_memory_and_no_swap_are_truthful(self):
        row = snapshot()
        row['memory'].update(availableBytes=0, swapTotalBytes=0, swapFreeBytes=0)
        values = dict(monitor.observe(row, NOW))
        self.assertEqual(values['memory_available_bytes'], 0)
        self.assertEqual(values['swap_total_bytes'], 0)
        self.assertEqual(values['observation_success'], 1)

    def test_memory_uses_memavailable_not_memfree_and_requires_exact_units(self):
        with patch.object(endpoint, 'read', return_value=MEMINFO):
            value = endpoint.memory()
        self.assertEqual(value['availableBytes'], 2 * 1024**3)
        self.assertEqual(value['swapFreeBytes'], 512 * 1024**2)
        for text in (MEMINFO.replace('MemAvailable', 'Missing'), MEMINFO + 'MemTotal: 1 kB\n',
                     MEMINFO.replace('2097152 kB', '-1 kB'), MEMINFO.replace('kB', 'MB'),
                     MEMINFO.replace('MemAvailable: 2097152 kB', 'MemAvailable: 999999999 kB')):
            with patch.object(endpoint, 'read', return_value=text), self.assertRaises(ValueError):
                endpoint.memory()

    def test_cpu_uses_deltas_excludes_guest_and_keeps_steal_separate(self):
        before = [100] * 8
        # Total delta 100: 20 executed, 5 stolen, 65 idle and 10 iowait.
        after = [110, 100, 105, 165, 110, 102, 103, 105]
        with patch.object(endpoint, 'counters', side_effect=[before, after]), \
             patch.object(endpoint.time, 'monotonic', side_effect=[10, 11]), patch.object(endpoint.time, 'sleep') as sleep:
            value = endpoint.cpu()
            sleep.assert_called_once_with(1)
        self.assertEqual(value, {'available': True, 'busyRatio': 0.2, 'stealRatio': 0.05, 'sampleSeconds': 1})
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / 'stat'
            path.write_bytes(b'cpu 1 2 3 4 5 6 7 8 999 999\ncpu0 private-not-read\n')
            with patch.object(endpoint, 'CPU', path):
                self.assertEqual(endpoint.counters(), list(range(1, 9)))

    def test_cpu_counter_regression_zero_delta_and_bad_sampling_window_refuse(self):
        for before, after, seconds in (([2]*8, [1]*8, 1), ([1]*8, [1]*8, 1),
                                        ([1]*8, [2]*8, 0.1), ([1]*8, [2]*8, 4),
                                        ([100]*8, [101, 101, 101, 101, 99, 101, 101, 101], 1)):
            with patch.object(endpoint, 'counters', side_effect=[before, after]), \
                 patch.object(endpoint.time, 'monotonic', side_effect=[10, 10 + seconds]), \
                 patch.object(endpoint.time, 'sleep'), self.assertRaises(ValueError):
                endpoint.cpu()

    def test_malformed_cpu_and_oversized_fixed_reads_refuse(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / 'stat'
            for raw in (b'cpu0 1 2 3 4 5 6 7 8\n', b'cpu -1 2 3 4 5 6 7 8\n', b'cpu 1 2\n',
                        b'cpu ' + b'1 ' * 600, b'cpu 1 2 3 4 5 6 7 8'):
                path.write_bytes(raw)
                with patch.object(endpoint, 'CPU', path), self.assertRaises(ValueError):
                    endpoint.counters()
            path.write_bytes(b'a' * 101)
            with self.assertRaises(ValueError):
                endpoint.read(path, 100)

    def test_capture_failure_never_exports_private_exception_and_keeps_other_group(self):
        row = snapshot()
        with patch.object(endpoint, 'memory', side_effect=OSError('/private secret')), \
             patch.object(endpoint, 'cpu', return_value=row['cpu']):
            value = endpoint.capture()
        self.assertEqual(value['memory'], {'available': False})
        self.assertEqual(value['cpu'], row['cpu'])
        self.assertNotIn('private', json.dumps(value))
        self.assertNotIn('secret', json.dumps(value))

    def test_reads_preserve_contents_mtime_and_inventory(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / 'meminfo'
            path.write_text(MEMINFO, encoding='ascii')
            before = (path.read_bytes(), path.stat().st_mtime_ns, sorted(Path(name).iterdir()))
            with patch.object(endpoint, 'MEMORY', path):
                endpoint.memory()
            self.assertEqual(before, (path.read_bytes(), path.stat().st_mtime_ns, sorted(Path(name).iterdir())))

    def test_payload_rejects_duplicate_unbounded_and_nonfinite_metrics(self):
        for values in ([('private-host', 1)], [('cpu_busy_ratio', -1)], [('cpu_busy_ratio', float('inf'))],
                       [('cpu_busy_ratio', True)], [('cpu_busy_ratio', 1), ('cpu_busy_ratio', 1)]):
            with self.assertRaises(ValueError):
                monitor.payload(values, NOW)

    def test_transport_is_single_fixed_no_forwarding_command(self):
        with patch.object(monitor.transport, 'private'), patch.object(monitor.subprocess, 'Popen') as process:
            child = process.return_value.__enter__.return_value
            child.returncode = 0
            child.communicate.return_value = (json.dumps(snapshot()).encode(), b'private')
            self.assertEqual(monitor.remote(), snapshot())
            self.assertEqual(process.call_args.args[0][-2:], ['goldsrcops-player-backup', 'resource-status'])
            for flag in ('ConnectionAttempts=1', 'StrictHostKeyChecking=yes', 'ClearAllForwardings=yes'):
                self.assertIn(flag, process.call_args.args[0])
            child.communicate.assert_called_once_with(timeout=25)
            process.assert_called_once()

    def test_transport_timeout_kills_owned_group_and_does_not_retry(self):
        with patch.object(monitor.transport, 'private'), patch.object(monitor.subprocess, 'Popen') as process, \
             patch.object(monitor.os, 'killpg', create=True) as kill, \
             patch.object(monitor.signal, 'SIGKILL', 9, create=True):
            child = process.return_value.__enter__.return_value
            child.pid = 123
            child.communicate.side_effect = subprocess.TimeoutExpired('fixed', 25)
            with self.assertRaises(subprocess.TimeoutExpired):
                monitor.remote()
            kill.assert_called_once_with(123, monitor.signal.SIGKILL)
            child.wait.assert_called_once()
            process.assert_called_once()

    def test_dashboard_facts_are_fresh_and_independent(self):
        value = json.loads((ROOT / 'ops/production/resource-dashboard.json').read_bytes())
        self.assertEqual(value['uid'], 'goldsrcops-game-host-resources')
        self.assertFalse(value['editable'])
        for panel in value['panels'][1:]:
            expr = panel['targets'][0]['expr']
            for name in ('observation_success', 'observed_timestamp_seconds', 'source_observed_timestamp_seconds', '<= 180'):
                self.assertIn(name, expr)
            if panel['id'] in range(3, 8):
                self.assertIn('memory_available{', expr)
            if panel['id'] >= 8:
                self.assertIn('cpu_available{', expr)
            self.assertTrue(panel['targets'][0]['instant'])
            self.assertFalse(panel['targets'][0]['range'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
