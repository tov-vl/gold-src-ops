#!/usr/bin/env python3
"""Root-only disposable dashboard claim, rollback and drift failure rehearsal."""
import copy
import base64
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('dashboard_rollout', ROOT / 'ops/production/agent-monitor-dashboard.py')
rollout = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rollout)


class Tests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='agent-dashboard-')
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        path = root / 'dashboard.json'
        path.write_bytes((ROOT / 'ops/production/agent-monitor-dashboard.json').read_bytes())
        path.chmod(0o644)
        self.hash = rollout.digest(path.read_bytes())
        self.current = None
        self.calls = []
        self.interrupt = False
        self.expected = json.loads(path.read_bytes())
        def api(connection, method, path, value=None):
            self.calls.append((method, path, value))
            if path.endswith('/goldsrcops-operations'):
                return 200, {'meta': {'folderUid': 'existing-folder'}}
            if method == 'GET':
                return (404, {}) if self.current is None else (200, self.current)
            if method == 'POST':
                self.assertFalse(value['overwrite'])
                self.assertEqual(value['folderUid'], 'existing-folder')
                body = copy.deepcopy(value['dashboard'])
                body.update(id=22, version=1)
                self.current = {'meta': {'provisioned': False}, 'dashboard': body}
                if self.interrupt:
                    raise TimeoutError('Lost response after committed request')
                return 200, {'status': 'success', 'uid': rollout.UID}
            if method == 'DELETE':
                self.current = None
                return 200, {}
            raise AssertionError('Unexpected operation')
        for item in (patch.object(rollout, 'STATE', root / 'state'), patch.object(rollout, 'DASHBOARD', path),
                     patch.object(rollout, 'context', return_value=('private-target', {})), patch.object(rollout, 'api', side_effect=api)):
            item.start()
            self.addCleanup(item.stop)

    def test_install_then_exact_rollback_removes_only_new_uid(self):
        rollout.execute('install', self.hash)
        self.assertFalse((rollout.STATE / 'pending.json').exists())
        self.assertTrue((rollout.STATE / 'accepted.json').exists())
        rollout.execute('rollback', self.hash)
        self.assertIsNone(self.current)
        deletes = [path for method, path, _ in self.calls if method == 'DELETE']
        self.assertEqual(deletes, ['/api/dashboards/uid/' + rollout.UID])
        self.assertTrue((rollout.STATE / 'rolled-back.json').exists())

    def test_existing_dashboard_and_wrong_hash_refuse_before_write(self):
        with self.assertRaises(ValueError):
            rollout.execute('install', '0' * 64)
        self.assertEqual(self.calls, [])
        self.current = {'meta': {'provisioned': False}, 'dashboard': self.expected}
        with self.assertRaises(ValueError):
            rollout.execute('install', self.hash)
        self.assertFalse(any(method != 'GET' for method, _, _ in self.calls))

    def test_lost_post_reply_keeps_claim_and_never_retries(self):
        self.interrupt = True
        with self.assertRaises(TimeoutError):
            rollout.execute('install', self.hash)
        self.assertIsNotNone(self.current)
        self.assertTrue((rollout.STATE / 'pending.json').exists())
        count = len(self.calls)
        with self.assertRaises(ValueError):
            rollout.execute('install', self.hash)
        self.assertEqual(len(self.calls), count)

    def test_operator_edit_prevents_rollback_delete(self):
        rollout.execute('install', self.hash)
        self.current['dashboard']['title'] = 'Changed by operator'
        self.current['dashboard']['version'] = 2
        with self.assertRaises(ValueError):
            rollout.execute('rollback', self.hash)
        self.assertFalse(any(method == 'DELETE' for method, _, _ in self.calls))

    def test_provisioned_resource_is_never_deleted(self):
        rollout.execute('install', self.hash)
        self.current['meta']['provisioned'] = True
        with self.assertRaises(ValueError):
            rollout.execute('rollback', self.hash)
        self.assertFalse(any(method == 'DELETE' for method, _, _ in self.calls))


class ContextTests(unittest.TestCase):
    def test_existing_password_file_line_ending_is_not_sent_as_password(self):
        container = {'image': rollout.IMAGE, 'running': True,
                     'networks': {'goldsrcops_telemetry': {'IPAddress': '10.0.0.10'}}}
        result = type('Result', (), {'returncode': 0, 'stdout': json.dumps(container).encode()})()
        password = b'fixture-monitor-only-password'
        for ending in (b'', b'\n', b'\r\n'):
            with patch.object(rollout, 'trusted', return_value=password + ending), \
                 patch.object(rollout.subprocess, 'run', return_value=result):
                address, headers = rollout.context()
                self.assertEqual(address, '10.0.0.10')
                self.assertEqual(base64.b64decode(headers['Authorization'].split(' ', 1)[1]), b'admin:' + password)
        with patch.object(rollout, 'trusted', return_value=password + b'\n\n'), \
             patch.object(rollout.subprocess, 'run', return_value=result), self.assertRaises(ValueError):
            rollout.context()


if __name__ == '__main__':
    if os.geteuid() != 0:
        raise SystemExit('Run the disposable fixture as root.')
    unittest.main(verbosity=2)
