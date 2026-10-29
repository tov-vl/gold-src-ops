#!/usr/bin/env python3
"""Isolated passive-observation, failure, privacy and command-boundary checks."""
import copy
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops/gameserver'))
import agent_diagnostics as contract


def module(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


monitor = module('agent_monitor', 'ops/production/agent-monitor.py')
endpoint = module('agent_endpoint', 'ops/gameserver/agent-status.py')
NOW = dt.datetime(2026, 10, 29, 10, tzinfo=dt.timezone.utc)


def snapshot():
    cycle = {'lastStartedAtUtc': (NOW - dt.timedelta(seconds=2)).isoformat(),
             'lastCompletedAtUtc': (NOW - dt.timedelta(seconds=1)).isoformat(),
             'lastSuccessfulAtUtc': (NOW - dt.timedelta(seconds=1)).isoformat(),
             'stoppedAtUtc': None, 'outcome': 'idle'}
    return {'schemaVersion': 1, 'observedAtUtc': NOW.isoformat(), 'queueCapacity': 10000,
            'spoolImportEnabled': True, 'deliveryEnabled': True,
            'queue': {'pending': 0, 'inFlight': 0, 'deadLetter': 0, 'spoolReceipts': 0, 'nextSequenceNumber': 77},
            'spool': {'ready': 0, 'processing': 0, 'accepted': 0, 'rejected': 0, 'temporary': 0},
            'oldestWaitingAgeSeconds': None, 'spoolAvailable': True,
            'health': {key: {'state': 'running', 'staleAfterSeconds': 30, 'cycle': copy.deepcopy(cycle)}
                       for key in ('import', 'delivery')}}


def samples(value, now=NOW):
    return {(name, loop): v for name, v, loop in monitor.observe(value, now)}


class ContractTests(unittest.TestCase):
    def test_idle_is_live_without_any_event_or_source_identity(self):
        raw = snapshot()
        raw.update(sourceInstanceId='private-source', serverAddress='private-address', payload='private-payload')
        value = contract.project(raw, True, NOW)
        result = samples(value)
        self.assertEqual(result['observation_success', None], 1)
        self.assertEqual(result['queue_pending', None], 0)
        self.assertEqual(result['loop_state', 'import'], contract.STATES['running'])
        self.assertEqual(result['loop_state', 'delivery'], contract.STATES['running'])
        body = monitor.payload(monitor.observe(value, NOW), NOW)
        for secret in ('private-source', 'private-address', 'private-payload', 'nextSequenceNumber', 'spoolReceipts'):
            self.assertNotIn(secret, body.decode())
            self.assertNotIn(secret, json.dumps(value))
        decoded = json.loads(body)
        metrics = decoded['resourceMetrics'][0]['scopeMetrics'][0]['metrics']
        labels = [p['attributes'] for item in metrics for p in item['gauge']['dataPoints'] if 'attributes' in p]
        self.assertTrue(all(x in ([{'key': 'loop', 'value': {'stringValue': 'import'}}],
                                  [{'key': 'loop', 'value': {'stringValue': 'delivery'}}]) for x in labels))

    def test_delivery_failure_retry_block_and_recovery_preserve_queue(self):
        raw = snapshot()
        raw['queue'].update(pending=3, inFlight=1, deadLetter=2)
        raw['spool'].update(ready=5, processing=1, rejected=2)
        raw['oldestWaitingAgeSeconds'] = 120
        for outcome in ('failed', 'retrying', 'blocked', 'idle'):
            with self.subTest(outcome=outcome):
                raw['health']['delivery']['cycle']['outcome'] = outcome
                value = contract.project(raw, True, NOW)
                result = samples(value)
                self.assertEqual(result['loop_state', 'delivery'], contract.STATES['running' if outcome == 'idle' else outcome])
                self.assertEqual(result['queue_pending', None], 3)
                self.assertEqual(result['queue_in_flight', None], 1)
                self.assertEqual(result['queue_dead_letter', None], 2)
                self.assertEqual(result['spool_rejected', None], 2)
                self.assertEqual(result['oldest_waiting_age_seconds', None], 120)

    def test_stuck_cycle_and_inclusive_budget_are_recomputed(self):
        raw = snapshot()
        loop = raw['health']['import']['cycle']
        loop.update(lastStartedAtUtc=(NOW - dt.timedelta(seconds=30)).isoformat(),
                    lastCompletedAtUtc=(NOW - dt.timedelta(seconds=40)).isoformat(),
                    lastSuccessfulAtUtc=(NOW - dt.timedelta(seconds=40)).isoformat())
        value = contract.project(raw, True, NOW)
        self.assertEqual(samples(value)['loop_state', 'import'], contract.STATES['running'])
        self.assertEqual(samples(value, NOW + dt.timedelta(microseconds=1))['loop_state', 'import'], contract.STATES['stale'])

    def test_disabled_stopped_and_missing_cycle_are_distinct(self):
        raw = snapshot()
        raw['deliveryEnabled'] = False
        raw['health']['delivery'].update(state='disabled', cycle=None, staleAfterSeconds=0)
        raw['health']['import'].update(state='unavailable', cycle=None)
        value = contract.project(raw, True, NOW)
        self.assertEqual(samples(value)['loop_state', 'delivery'], contract.STATES['disabled'])
        self.assertEqual(samples(value)['loop_state', 'import'], contract.STATES['unavailable'])
        stopped = contract.project(snapshot(), False, NOW)
        self.assertEqual(samples(stopped)['loop_state', 'import'], contract.STATES['stopped'])

    def test_old_or_future_snapshot_never_becomes_a_fresh_healthy_zero(self):
        value = contract.project(snapshot(), True, NOW)
        for moment in (NOW - dt.timedelta(seconds=6), NOW + dt.timedelta(seconds=31)):
            result = samples(value, moment)
            self.assertEqual(set(result), {('observation_success', None), ('observed_timestamp_seconds', None)})
            self.assertEqual(result['observation_success', None], 0)
        self.assertEqual(samples(value, NOW + dt.timedelta(seconds=30))['observation_success', None], 1)

    def test_malformed_source_discards_all_partial_counts(self):
        valid = contract.project(snapshot(), True, NOW)
        for key, replacement in (('pending', True), ('queueCapacity', 0), ('pending', -1),
                                 ('oldestWaitingAgeSeconds', 10), ('schemaVersion', True), ('available', 'true')):
            value = copy.deepcopy(valid)
            value[key] = replacement
            with self.subTest(key=key, replacement=replacement):
                result = samples(value)
                self.assertEqual(result, {('observation_success', None): 0,
                                          ('observed_timestamp_seconds', None): NOW.timestamp()})
        unavailable = samples({'schemaVersion': 1, 'available': False})
        self.assertNotIn(('queue_pending', None), unavailable)

    def test_broken_cycle_and_nonfinite_or_duplicate_json_are_refused(self):
        for data in (b'{"a":1,"a":2}', b'{"v":NaN}', b'x' * (contract.MAX_JSON + 1)):
            with self.assertRaises(ValueError):
                contract.decode(data)
        value = contract.project(snapshot(), True, NOW)
        value['loops']['delivery']['lastSuccessfulAtUtc'] = (NOW + dt.timedelta(seconds=1)).isoformat()
        self.assertEqual(samples(value)['observation_success', None], 0)

    def test_endpoint_executes_only_passive_status_and_never_reads_the_secret(self):
        raw = snapshot()
        calls = []
        def run(arguments, timeout=10):
            calls.append([str(x) for x in arguments])
            if arguments[0] == endpoint.GUARD:
                return b'PERSISTENT_GAME_EVENT_GUARD=passed'
            if arguments[0] == 'systemctl':
                return b'LoadState=loaded\nActiveState=active\nSubState=running\n'
            return json.dumps(raw).encode()
        def trusted(path, mode):
            if path.name == 'game-event-pilot-installed':
                return ('bundle_sha256=' + 'a' * 64).encode()
            return b'GameEventAgent__QueuePath=/fixed/queue.db\n'
        class Clock(dt.datetime):
            @staticmethod
            def now(_zone):
                return NOW
        with patch.object(endpoint, 'run', side_effect=run), patch.object(endpoint, 'trusted', side_effect=trusted), \
             patch.object(endpoint.GUARD.__class__, 'stat', return_value=type('Info', (), {'st_uid': 0, 'st_mode': 0o100755})()), \
             patch.object(endpoint.GUARD.__class__, 'is_symlink', return_value=False), patch.object(endpoint.dt, 'datetime', Clock):
            value = endpoint.capture()
        self.assertTrue(value['available'])
        self.assertEqual(calls[-1][-2:], ['status', '--json'])
        self.assertEqual(calls[0][-1], '--guard-agent')
        self.assertFalse(any('initialize-queue' in call or 'import-spool' in call or 'run' in call for call in calls))

    def test_ssh_transport_has_one_fixed_read_command_and_no_retry(self):
        with patch.object(monitor, 'private'), patch.object(monitor.subprocess, 'Popen') as process:
            child = process.return_value.__enter__.return_value
            child.returncode = 0
            child.communicate.return_value = (b'{"schemaVersion":1,"available":false}', b'not exported')
            self.assertFalse(monitor.remote()['available'])
            arguments = process.call_args.args[0]
            self.assertEqual(arguments[-2:], ['goldsrcops-player-backup', 'agent-status'])
            self.assertIn('StrictHostKeyChecking=yes', arguments)
            self.assertIn('ClearAllForwardings=yes', arguments)
            self.assertIn('ConnectionAttempts=1', arguments)
            process.assert_called_once()

    def test_dashboard_current_values_are_instant_and_freshness_gated(self):
        dashboard = json.loads((ROOT / 'ops/production/agent-monitor-dashboard.json').read_bytes())
        self.assertEqual(dashboard['uid'], 'goldsrcops-game-event-agent')
        self.assertFalse(dashboard['editable'])
        for panel in dashboard['panels']:
            for target in panel.get('targets', []):
                self.assertIn('observation_success', target['expr'])
                self.assertIn('source_observed_timestamp_seconds', target['expr'])
                self.assertIn('<= 180', target['expr'])
                if panel['type'] == 'stat':
                    self.assertTrue(target['instant'])
                    self.assertFalse(target['range'])

    def test_history_series_select_their_own_metric(self):
        dashboard = json.loads((ROOT / 'ops/production/agent-monitor-dashboard.json').read_bytes())
        metrics = {'Pending': 'queue_pending', 'In-flight': 'queue_in_flight', 'Dead-letter': 'queue_dead_letter',
                   'Ready': 'spool_ready', 'Processing': 'spool_processing', 'Rejected': 'spool_rejected'}
        found = set()
        for panel in dashboard['panels']:
            if panel['type'] == 'timeseries':
                for target in panel['targets']:
                    name = target['legendFormat']
                    self.assertTrue(target['expr'].startswith('(goldsrcops_game_event_agent_' + metrics[name] + '{'))
                    found.add(name)
        self.assertEqual(found, set(metrics))


if __name__ == '__main__':
    unittest.main(verbosity=2)
