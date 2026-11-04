#!/usr/bin/env python3
"""Synthetic storage failures, privacy, passive reads and fixed command checks."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops/gameserver'))
import storage_diagnostics as contract


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


endpoint = module('storage_endpoint', 'ops/gameserver/storage-status.py')
monitor = module('storage_monitor', 'ops/production/storage-monitor.py')
NOW = dt.datetime(2026, 11, 4, 10, tzinfo=dt.timezone.utc)


def snapshot():
    return {'schemaVersion': 1, 'observedAtUtc': NOW.isoformat(), 'roles': {
        role: {'available': True, 'bytesTotal': 100 * 1024**3, 'bytesAvailable': 30 * 1024**3,
               'inodesTotal': 1000000, 'inodesAvailable': 300000} for role in contract.ROLES}}


def samples(value):
    return {(name, role): number for name, number, role in monitor.observe(value, NOW)}


class Tests(unittest.TestCase):
    def test_complete_snapshot_has_only_fixed_roles_and_numeric_metrics(self):
        value = snapshot()
        self.assertEqual(contract.validate(value, NOW), NOW)
        output = samples(value)
        self.assertEqual(output['observation_success', None], 1)
        body = json.loads(monitor.payload(monitor.observe(value, NOW), NOW))
        resource = body['resourceMetrics'][0]
        self.assertEqual(resource['resource']['attributes'][0]['value']['stringValue'], monitor.SERVICE)
        labels = [p['attributes'] for m in resource['scopeMetrics'][0]['metrics'] for p in m['gauge']['dataPoints'] if 'attributes' in p]
        self.assertEqual({x[0]['value']['stringValue'] for x in labels}, set(contract.ROLES))
        self.assertTrue(all(len(x) == 1 and x[0]['key'] == 'role' for x in labels))

    def test_unavailable_role_does_not_export_fake_capacity_or_hide_other_roles(self):
        value = snapshot(); value['roles']['queue'] = {'available': False}
        output = samples(value)
        self.assertEqual(output['observation_success', None], 1)
        self.assertEqual(output['role_available', 'queue'], 0)
        self.assertNotIn(('bytes_available', 'queue'), output)
        self.assertEqual(output['bytes_available', 'players'], 30 * 1024**3)

    def test_unsupported_inodes_are_unknown_not_zero_or_full(self):
        value = snapshot(); value['roles']['queue'].update(inodesTotal=None, inodesAvailable=None)
        output = samples(value)
        self.assertEqual(output['inodes_supported', 'queue'], 0)
        self.assertNotIn(('inodes_available', 'queue'), output)

    def test_zero_capacity_available_is_a_valid_full_filesystem(self):
        value = snapshot(); value['roles']['queue'].update(bytesAvailable=0, inodesAvailable=0)
        output = samples(value)
        self.assertEqual(output['observation_success', None], 1)
        self.assertEqual(output['bytes_available', 'queue'], 0)
        self.assertEqual(output['inodes_available', 'queue'], 0)

    def test_invalid_schema_counts_and_private_fields_fail_closed(self):
        mutations = [lambda x:x.update(schemaVersion=True), lambda x:x.update(path='/private'),
            lambda x:x['roles'].pop('backup'), lambda x:x['roles'].update(unbounded={'available':False}),
            lambda x:x['roles']['queue'].update(bytesAvailable=-1),
            lambda x:x['roles']['queue'].update(bytesAvailable=True),
            lambda x:x['roles']['queue'].update(bytesTotal=0),
            lambda x:x['roles']['queue'].update(bytesTotal=2**63),
            lambda x:x['roles']['queue'].update(bytesAvailable=2**62),
            lambda x:x['roles']['queue'].update(inodesTotal=None),
            lambda x:x['roles']['queue'].update(inodesAvailable=1000001),
            lambda x:x['roles']['queue'].update(device='private-device'),
            lambda x:x['roles'].update(queue={'available':False,'bytesTotal':100})]
        for mutation in mutations:
            value = snapshot(); mutation(value)
            with self.subTest(value=value):
                self.assertEqual(samples(value), {('observation_success',None):0,('observed_timestamp_seconds',None):NOW.timestamp()})

    def test_old_future_naive_and_bad_timestamps_refuse(self):
        for time in (NOW-dt.timedelta(seconds=31),NOW+dt.timedelta(seconds=6),NOW.replace(tzinfo=None)):
            value = snapshot(); value['observedAtUtc'] = time.isoformat()
            self.assertEqual(samples(value)['observation_success',None],0)
        for delta in (-30,5):
            value = snapshot(); value['observedAtUtc'] = (NOW+dt.timedelta(seconds=delta)).isoformat()
            self.assertEqual(samples(value)['observation_success',None],1)

    def test_duplicate_oversized_and_nonfinite_json_refuse(self):
        for raw in (b'{"a":1,"a":2}', b' '*8193, b'{"a":NaN}',b'{"a":Infinity}'):
            with self.assertRaises(ValueError): contract.decode(raw)

    def test_payload_rejects_unbounded_labels_and_metrics(self):
        for point in [('bytes_available',1,'private-path'),('secret',1,'queue'),('bytes_available',-1,'queue'),('bytes_available',float('nan'),'queue')]:
            with self.assertRaises(ValueError): monitor.payload([point],NOW)

    def test_statvfs_uses_unprivileged_capacity_and_clamps_negative_availability(self):
        stats = SimpleNamespace(f_blocks=1000,f_frsize=4096,f_bavail=100,f_bfree=200,f_files=100,f_favail=10)
        with patch.object(Path,'is_symlink',return_value=False),patch.object(Path,'is_dir',return_value=True),patch.object(endpoint.os,'statvfs',return_value=stats,create=True):
            first = endpoint.observe(ROOT / 'fixture')
            self.assertEqual(first['bytesAvailable'],409600)
            stats.f_bavail=-1;stats.f_favail=-10
            second = endpoint.observe(ROOT / 'fixture')
            self.assertEqual(second['bytesAvailable'],0);self.assertEqual(second['inodesAvailable'],0)
            stats.f_files=0
            self.assertIsNone(endpoint.observe(ROOT / 'fixture')['inodesTotal'])

    def test_missing_symlink_and_invalid_stat_role_refuse_without_data_read(self):
        with patch.object(Path,'is_symlink',return_value=True),patch.object(endpoint.os,'statvfs',create=True) as stat:
            with self.assertRaises(ValueError): endpoint.observe(Path('/fixed'))
            stat.assert_not_called()
        with patch.object(Path,'is_symlink',return_value=False),patch.object(Path,'is_dir',return_value=False):
            with self.assertRaises(ValueError): endpoint.observe(Path('/fixed'))

    def test_capture_contains_no_exception_path_or_device(self):
        good = snapshot()['roles']['queue']
        with patch.object(endpoint,'observe',side_effect=[OSError('/private secret'),good,good,good]):
            value = endpoint.capture()
        self.assertEqual(value['roles']['queue'],{'available':False})
        self.assertNotIn('private',json.dumps(value));self.assertNotIn('secret',json.dumps(value))

    @unittest.skipUnless(hasattr(endpoint.os,'statvfs'),'Linux filesystem API')
    def test_real_passive_reads_preserve_files_and_do_not_create_directories(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); payload=base/'retain.bin';payload.write_bytes(b'keep-exact')
            before=(payload.read_bytes(),payload.stat().st_mtime_ns)
            with patch.object(endpoint,'PATHS',{role:base for role in contract.ROLES}):
                self.assertTrue(all(row['available'] for row in endpoint.capture()['roles'].values()))
            self.assertEqual(before,(payload.read_bytes(),payload.stat().st_mtime_ns))
            missing=base/'missing'
            with self.assertRaises(ValueError):endpoint.observe(missing)
            self.assertFalse(missing.exists())

    def test_transport_one_fixed_command_and_failures_refuse(self):
        with patch.object(monitor.transport,'private'),patch.object(monitor.subprocess,'Popen') as process:
            child=process.return_value.__enter__.return_value;child.returncode=0
            child.communicate.return_value=(json.dumps(snapshot()).encode(),b'private')
            self.assertEqual(monitor.remote()['schemaVersion'],1)
            args=process.call_args.args[0]
            self.assertEqual(args[-2:],['goldsrcops-player-backup','storage-status'])
            for flag in ('StrictHostKeyChecking=yes','ClearAllForwardings=yes','ConnectionAttempts=1'):self.assertIn(flag,args)
            self.assertEqual(child.communicate.call_args.kwargs['timeout'],25)
            process.assert_called_once()

    def test_dashboard_current_facts_expire_and_inode_facts_require_support(self):
        dashboard=json.loads((ROOT/'ops/production/storage-dashboard.json').read_bytes())
        self.assertEqual(dashboard['uid'],'goldsrcops-game-host-storage');self.assertFalse(dashboard['editable'])
        for panel in dashboard['panels']:
            for target in panel.get('targets',[]):
                self.assertTrue(target['instant']);self.assertFalse(target['range'])
                for field in ('observation_success','observed_timestamp_seconds','source_observed_timestamp_seconds','<= 180'):self.assertIn(field,target['expr'])
                if panel['id'] in (6,7):self.assertIn('inodes_supported',target['expr'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
