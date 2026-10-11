#!/usr/bin/env python3
"""Offline regression for accepted addon baseline transitions and no-capture reconciliation."""
import datetime as dt
import hashlib
import importlib.util
import json
import io
import tarfile
import os
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops/gameserver'))
import player_data_schedule as policy


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


baseline = load('baseline', 'ops/gameserver/player-data-baseline.py')
game = load('capture', 'ops/gameserver/player-data-scheduled-capture.py')
control = load('control', 'ops/production/player-data-scheduled.py')
monitor = load('monitor', 'ops/production/player-data-monitor.py')
SLOT = '2026-10-11'
WHEN = dt.datetime(2026, 10, 11, 1, tzinfo=policy.UTC)
LATER = WHEN + dt.timedelta(hours=2)


def sha(data):
    return hashlib.sha256(data).hexdigest()


class MaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        path.write_bytes(data); path.chmod(0o600)
        return path

    def fixture(self, kind='weapon-selection'):
        self.server, self.config = self.root / 'server', self.root / 'config'
        self.config.mkdir(mode=0o700)
        names = ('goldsrcops_weapon_selection.amxx', 'goldsrcops-weapon-selection.cfg',
                 'plugins-goldsrcops-weapons.ini', 'goldsrcops-player-menu.txt') if kind == 'weapon-selection' else (
                 'goldsrcops_map_menu.amxx', 'goldsrcops-map-menu.cfg', 'plugins-goldsrcops-maps.ini', 'goldsrcops-map-menu.txt')
        self.relatives = [f'cstrike/addons/amxmodx/{folder}/{name}' for folder, name in zip(
                         ('plugins', 'configs/plugins', 'configs', 'data/lang'), names)]
        self.paths = [self.write(self.server / r, b'old') for r in self.relatives]
        self.receipt = self.write(self.config / (kind + '-installed'), b'old receipt')
        self.invariant = self.write(self.root / 'invariant', b'must stay unchanged')
        old = ''.join(sha(p.read_bytes()) + '  ' + str(p) + '\n' for p in self.paths + [self.receipt, self.invariant])
        self.old = old.encode()
        self.bp = self.write(self.config / 'baseline.sha256', self.old)
        for p in self.paths: self.write(p, b'accepted-' + p.name.encode())
        manifest = {'schemaVersion': 2, 'productionInstallSupported': True, 'enabledByDefault': True,
                    'purpose': 'game-host-addon' if kind == 'weapon-selection' else 'map-menu-addon',
                    'payload': [{'path': r, 'sha256': sha(p.read_bytes())} for r,p in zip(self.relatives,self.paths)]}
        self.manifest = self.write(self.root / 'manifest.json', json.dumps(manifest).encode())
        self.mh = sha(self.manifest.read_bytes())
        receipt = 'schema_version=2\nstate=enabled\nmanifest_sha256=' + self.mh + '\n'
        for key,p in zip(('plugin','config','loader','dictionary'),self.paths):
            receipt += key + '_sha256=' + sha(p.read_bytes()) + '\n'
        self.write(self.receipt, receipt.encode())
        self.kind = kind

    def prepare(self, **kwargs):
        args = dict(baseline=self.bp, server=self.server, config=self.config, manifest=self.manifest,
                    manifest_hash=self.mh, baseline_hash=sha(self.old), kind=self.kind)
        args.update(kwargs)
        return baseline.prepare(**args)

    def test_accepted_upgrade_updates_only_allowlisted_entries_and_retains_original(self):
        self.fixture(); old,new = self.prepare()
        self.assertEqual(self.bp.read_bytes(), old)
        evidence=self.root/'evidence'
        baseline.apply(self.bp,evidence,old,new,self.mh)
        self.assertEqual((evidence/'previous.sha256').read_bytes(),old)
        self.assertEqual(self.bp.read_bytes(),new)
        self.assertIn(sha(self.invariant.read_bytes()).encode(),new)
        with self.assertRaises(ValueError):self.prepare()
        self.assertEqual((evidence/'previous.sha256').read_bytes(),old)

    def test_interrupted_baseline_replace_preserves_old_bytes_and_review_evidence(self):
        self.fixture();old,new=self.prepare();evidence=self.root/'evidence'
        with mock.patch.object(baseline.os,'replace',side_effect=OSError()):
            with self.assertRaises(OSError):baseline.apply(self.bp,evidence,old,new,self.mh)
        self.assertEqual(self.bp.read_bytes(),old)
        self.assertEqual((evidence/'previous.sha256').read_bytes(),old)
        self.assertFalse((evidence/'result.json').exists())
        with self.assertRaises(ValueError):baseline.apply(self.bp,evidence,old,new,self.mh)

    def test_map_addon_refresh_uses_its_own_inventory(self):
        self.fixture('map-menu');self.assertNotEqual(*self.prepare())

    def test_unaccepted_payload_receipt_and_unrelated_drift_are_rejected(self):
        self.fixture()
        for p in (self.paths[0],self.paths[3],self.receipt,self.invariant):
            old=p.read_bytes();p.write_bytes(b'drift')
            with self.assertRaises((ValueError,KeyError)):self.prepare()
            p.write_bytes(old)
        self.assertEqual(self.bp.read_bytes(),self.old)

    def test_hash_inventory_and_symlink_guards(self):
        self.fixture()
        for kwargs in ({'manifest_hash':'0'*64},{'baseline_hash':'0'*64}):
            with self.assertRaises(ValueError):self.prepare(**kwargs)
        self.bp.write_bytes(self.old.splitlines(keepends=True)[-1])
        with self.assertRaises(ValueError):self.prepare(baseline_hash=sha(self.bp.read_bytes()))
        self.bp.write_bytes(self.old)
        p=self.paths[0];saved=p.read_bytes();p.unlink();p.symlink_to(self.invariant)
        with self.assertRaises(ValueError):self.prepare()
        p.unlink();self.write(p,saved)
        self.receipt.chmod(0o666)
        with self.assertRaises(ValueError):self.prepare()

    def setup_cycle(self):
        self.ledger=policy.Ledger(self.root)
        with mock.patch.object(policy,'now',return_value=WHEN): self.cycle,_=self.ledger.claim(SLOT)
        success={'schema':1,'state':'verified','workload':'goldsrcops-player-data-v1',
                 'capturedUtc':(WHEN-dt.timedelta(days=3)).isoformat(),'snapshotId':'b'*64,'bundleSha256':'a'*64}
        policy.write_json(self.root/'last-success.json',success)
        self.original_pending=self.ledger.pending.read_bytes()
        self.original_result=(self.cycle/'result.json').read_bytes()
        self.success_bytes=(self.root/'last-success.json').read_bytes()
        self.proof={'schema':1,'slot':SLOT,'state':'not-started','observedUtc':LATER.isoformat(),
                    'gameStartedUtc':(WHEN-dt.timedelta(days=1)).isoformat(),'gameInvocationId':'c'*32}
        self.transport=mock.Mock()
        self.transport.remote.side_effect=lambda *args:json.dumps(self.proof).encode()

    def reconcile(self,**kwargs):
        args=dict(ledger=self.ledger,transport=self.transport,slot=SLOT,
                  pending_hash=sha(self.original_pending),success_hash=sha(self.success_bytes),clock=lambda:LATER)
        args.update(kwargs)
        return control.reconcile_no_capture(**args)

    def test_proved_absence_preserves_originals_copy_age_and_prevents_same_slot_retry(self):
        self.setup_cycle();result=self.reconcile()
        self.assertEqual(result['state'],'reconciled-no-capture')
        self.assertFalse(self.ledger.pending.exists())
        self.assertEqual((self.cycle/'original-pending.json').read_bytes(),self.original_pending)
        self.assertEqual((self.cycle/'original-result.json').read_bytes(),self.original_result)
        self.assertEqual((self.root/'last-success.json').read_bytes(),self.success_bytes)
        self.transport.remote.assert_called_once_with('status',SLOT)
        self.transport.archive.assert_not_called()
        retry_transport=mock.Mock()
        self.assertEqual(control.cycle(self.ledger,retry_transport,lambda:WHEN),result)
        retry_transport.remote.assert_not_called()
        with mock.patch.object(policy,'now',return_value=WHEN+dt.timedelta(days=7)):
            next_cycle,existing=self.ledger.claim('2026-10-18')
        self.assertIsNone(existing)

    def test_reconciliation_refuses_window_and_last_success_change(self):
        self.setup_cycle()
        with self.assertRaises(ValueError):self.reconcile(clock=lambda:WHEN)
        self.transport.remote.assert_not_called()
        def changed(*args):
            self.write(self.root/'last-success.json',self.success_bytes+b' ')
            return json.dumps(self.proof).encode()
        self.transport.remote.side_effect=changed
        with self.assertRaises(ValueError):self.reconcile()
        self.assertEqual(self.ledger.pending.read_bytes(),self.original_pending)
        self.assertFalse((self.cycle/'reconciliation.json').exists())

    def test_unknown_remote_outcome_retains_pending(self):
        self.setup_cycle();self.transport.remote.side_effect=TimeoutError()
        with self.assertRaises(TimeoutError):self.reconcile()
        self.assertEqual(self.ledger.pending.read_bytes(),self.original_pending)
        with self.assertRaises(ValueError):self.ledger.claim('2026-10-18')

    def test_remote_capture_restart_stale_or_wrong_slot_refuses_reconciliation(self):
        self.setup_cycle()
        for key,value in [('state','captured'),('slot','2026-10-18'),
                          ('gameStartedUtc',(WHEN+dt.timedelta(seconds=1)).isoformat()),
                          ('observedUtc',(LATER-dt.timedelta(minutes=10)).isoformat())]:
            old=self.proof[key];self.proof[key]=value
            with self.assertRaises(ValueError):self.reconcile()
            self.proof[key]=old
        self.assertEqual(self.ledger.pending.read_bytes(),self.original_pending)

    def test_local_hash_drift_or_archive_evidence_refuses_reconciliation(self):
        self.setup_cycle()
        with self.assertRaises(ValueError):self.reconcile(pending_hash='0'*64)
        self.write(self.cycle/'player-data.tar',b'captured')
        with self.assertRaises(ValueError):self.reconcile()
        self.transport.remote.assert_not_called()

    def test_interrupted_finish_keeps_pending_and_evidence_without_retry(self):
        self.setup_cycle()
        with mock.patch.object(self.ledger,'finish',side_effect=OSError()):
            with self.assertRaises(OSError):self.reconcile()
        self.assertTrue(self.ledger.pending.exists())
        self.assertTrue((self.cycle/'reconciliation.json').exists())
        with self.assertRaises(ValueError):self.reconcile()
        with self.assertRaises(ValueError):self.ledger.claim('2026-10-18')

    def test_monitor_shows_reconciled_state_without_renewing_copy_or_hiding_service_failure(self):
        self.setup_cycle();self.reconcile()
        units={'timer':{'LoadState':'loaded','ActiveState':'active','UnitFileState':'enabled','Persistent':'no',
                       'TimersCalendar':'{ OnCalendar=Sun *-*-* 04:00:00 Europe/Moscow ; next_elapse=x }'},
               'service':{'LoadState':'loaded','ActiveState':'inactive','Result':'success'}}
        data=monitor.observe(self.root,units,LATER,(WHEN-dt.timedelta(days=4)).isoformat())
        self.assertEqual(data['observation_success'],1)
        self.assertEqual(data['last_attempt_status'],8)
        self.assertEqual(data['requires_reconciliation'],0)
        self.assertEqual(data['last_success_timestamp_seconds'],(WHEN-dt.timedelta(days=3)).timestamp())
        units['service']['Result']='exit-code'
        data=monitor.observe(self.root,units,LATER,(WHEN-dt.timedelta(days=4)).isoformat())
        self.assertEqual(data['service_failed'],1)
        (self.cycle/'reconciliation.json').unlink()
        data=monitor.observe(self.root,units,LATER,(WHEN-dt.timedelta(days=4)).isoformat())
        self.assertEqual(data['observation_success'],0)

    def test_game_absence_requires_running_process_predating_slot_and_no_ledger_entries(self):
        fields='ActiveState=active\nSubState=running\nMainPID=123\nInvocationID='+'c'*32+'\nExecMainStartTimestamp=Sat 2026-10-10 01:00:00 UTC\n'
        def inspect(text=fields):
            with mock.patch.object(game.subprocess,'run',return_value=subprocess.CompletedProcess([],0,text.encode(),b'')):
                return game.no_capture_status(self.root,SLOT,lambda:LATER)
        self.assertEqual(inspect()['state'],'not-started')
        for text in (fields.replace('active','inactive'),fields.replace('MainPID=123','MainPID=0'),
                     fields.replace('Sat 2026-10-10 01:00:00','Sun 2026-10-11 01:00:01')):
            with self.assertRaises(ValueError):inspect(text)
        (self.root/SLOT).symlink_to(self.root/'missing')
        with self.assertRaises(ValueError):inspect()
        (self.root/SLOT).unlink()
        self.write(self.root/'pending.json',b'{}')
        with self.assertRaises(ValueError):inspect()


    def manual_fixture(self):
        self.setup_cycle(); self.reconcile()
        self.captured = WHEN + dt.timedelta(minutes=30)
        vault = self.root / 'inputs' / 'vault'
        for name in control.bundle.NAMES:
            if name.endswith('.vault'): self.write(vault / name, b'fixture')
        manifest, members = control.bundle.inspect(control.bundle.create(vault))
        manifest['captured_utc'] = self.captured.isoformat()
        data = io.BytesIO()
        with tarfile.open(fileobj=data, mode='w', format=tarfile.USTAR_FORMAT) as archive:
            for name, raw in dict(members, **{'manifest.json': json.dumps(manifest).encode()}).items():
                info = tarfile.TarInfo(name); info.size = len(raw); info.mode = 0o600
                archive.addfile(info, io.BytesIO(raw))
        self.manual_data = data.getvalue(); self.manual_hash = sha(self.manual_data)
        self.operations = []
        for index, action in enumerate(('Archive', 'Recover')):
            directory = self.root / 'inputs' / action
            self.write(directory / 'readback' / 'player-data.tar', self.manual_data)
            operation = {'schemaVersion': 1, 'action': action, 'state': 'verified',
                         'bundleSha256': self.manual_hash, 'snapshotId': 'd' * 64,
                         'startedUtc': (self.captured + dt.timedelta(minutes=index * 10 + 1)).isoformat(),
                         'completedUtc': (self.captured + dt.timedelta(minutes=index * 10 + 2)).isoformat()}
            self.operations.append(self.write(directory / 'operation.json', json.dumps(operation).encode()))
        self.units = {'timer': {'LoadState': 'loaded', 'ActiveState': 'active', 'UnitFileState': 'enabled',
                               'Persistent': 'no', 'TimersCalendar': '{ OnCalendar=Sun *-*-* 04:00:00 Europe/Moscow ; next_elapse=x }'},
                      'service': {'LoadState': 'loaded', 'ActiveState': 'inactive', 'Result': 'success'}}

    def register(self, **kwargs):
        args = dict(root=self.root, archive_path=self.operations[0], recovery_path=self.operations[1],
                    archive_hash=sha(self.operations[0].read_bytes()), recovery_hash=sha(self.operations[1].read_bytes()),
                    bundle_hash=self.manual_hash, clock=lambda: LATER)
        args.update(kwargs)
        return control.register_manual(**args)

    def observation(self, moment=LATER):
        return monitor.observe(self.root, self.units, moment, (WHEN-dt.timedelta(days=4)).isoformat())

    def test_manual_copy_updates_capture_age_but_preserves_weekly_reconciliation_and_is_idempotent(self):
        self.manual_fixture()
        with mock.patch.object(policy, 'run', side_effect=AssertionError('No external calls')):
            record = self.register(); self.assertEqual(self.register(), record)
        self.assertEqual((self.root/'last-success.json').read_bytes(), self.success_bytes)
        self.assertEqual((self.cycle/'original-pending.json').read_bytes(), self.original_pending)
        data = self.observation()
        self.assertEqual((data['observation_success'], data['copy_source'], data['last_attempt_status']), (1,2,8))
        self.assertEqual(data['last_success_timestamp_seconds'], self.captured.timestamp())
        self.assertEqual(data['requires_reconciliation'], 0)
        self.assertEqual(policy.freshness(self.root, self.captured+dt.timedelta(days=8))['state'], 'fresh')
        self.assertEqual(self.observation(self.captured+dt.timedelta(days=8,seconds=1))['fresh'], 0)
        with self.assertRaises(ValueError): policy.freshness(self.root, self.captured+dt.timedelta(days=8,seconds=1))

    def test_manual_copy_does_not_hide_service_failure_or_pending(self):
        self.manual_fixture(); self.register(); self.units['service']['Result'] = 'exit-code'
        self.assertEqual(self.observation()['service_failed'], 1)
        self.assertEqual(self.observation()['last_attempt_status'], 6)
        self.units['service']['Result'] = 'success'
        policy.write_json(self.root/'pending.json', json.loads(self.original_pending))
        self.assertEqual(self.observation()['requires_reconciliation'], 1)
        with self.assertRaises(ValueError): self.register()
        with self.assertRaises(ValueError): policy.freshness(self.root, LATER)

    def test_new_weekly_copy_wins_without_removing_manual_evidence(self):
        self.manual_fixture(); self.register()
        next_time = WHEN+dt.timedelta(days=7)
        self.write(self.root/'2026-10-18'/'result.json', json.dumps(dict(
            schema=1,state='verified',workload='goldsrcops-player-data-v1',slot='2026-10-18',
            startedUtc=next_time.isoformat(),completedUtc=(next_time+dt.timedelta(seconds=20)).isoformat(),
            capturedUtc=(next_time+dt.timedelta(seconds=10)).isoformat(),snapshotId='e'*64,bundleSha256='f'*64)).encode())
        self.write(self.root/'last-success.json',(self.root/'2026-10-18'/'result.json').read_bytes())
        data=self.observation(next_time+dt.timedelta(minutes=20))
        self.assertEqual((data['observation_success'],data['copy_source'],data['last_attempt_status']),(1,1,1))
        self.assertEqual(data['last_success_timestamp_seconds'],(next_time+dt.timedelta(seconds=10)).timestamp())

    def test_manual_registration_rejects_wrong_hash_unknown_archive_and_mismatched_snapshot(self):
        self.manual_fixture()
        with self.assertRaises(ValueError): self.register(archive_hash='0'*64)
        for path, field, value in [(self.operations[0],'state','uploaded'),(self.operations[1],'snapshotId','e'*64),
                                   (self.operations[1],'action','Archive'),(self.operations[1],'completedUtc',WHEN.isoformat())]:
            old=path.read_bytes(); record=json.loads(old); record[field]=value
            self.write(path,json.dumps(record).encode())
            with self.assertRaises(ValueError): self.register()
            path.write_bytes(old)
        self.assertFalse((self.root/'manual-copies').exists())

    def test_manual_registration_requires_both_exact_private_readbacks(self):
        self.manual_fixture(); path=self.operations[1].parent/'readback'/'player-data.tar'
        old=path.read_bytes(); path.write_bytes(b'corrupt')
        with self.assertRaises(ValueError): self.register()
        path.write_bytes(old); path.chmod(0o644)
        with self.assertRaises(ValueError): self.register()
        path.chmod(0o600); path.unlink(); path.symlink_to(self.operations[0].parent/'readback'/'player-data.tar')
        with self.assertRaises(ValueError): self.register()
        self.assertFalse((self.root/'manual-copies').exists())

    def test_manual_registration_rejects_stale_future_and_duplicate_json_keys(self):
        self.manual_fixture()
        for moment in (self.captured-dt.timedelta(seconds=1),self.captured+dt.timedelta(days=8,seconds=1)):
            with self.assertRaises(ValueError): self.register(clock=lambda:moment)
        path=self.operations[0]; raw=path.read_bytes(); path.write_bytes(raw[:-1]+b',"state":"verified"}')
        with self.assertRaises(ValueError): self.register()
        self.assertFalse((self.root/'manual-copies').exists())

    def test_interrupted_manual_registration_is_unknown_without_weekly_mutation_or_retry(self):
        self.manual_fixture(); real=policy.write_json
        def fail(path,*args,**kwargs):
            if path.name=='success.json': raise OSError('interrupted')
            return real(path,*args,**kwargs)
        with mock.patch.object(policy,'write_json',side_effect=fail):
            with self.assertRaises(OSError): self.register()
        self.assertEqual((self.root/'last-success.json').read_bytes(),self.success_bytes)
        self.assertEqual(self.observation()['observation_success'],0)
        with self.assertRaises((ValueError,FileNotFoundError)): self.register()

    def test_corrupt_retained_manual_proof_and_symlink_are_unknown(self):
        self.manual_fixture(); self.register(); path=self.root/'manual-copies'/self.manual_hash/'archive.json'
        old=path.read_bytes(); path.write_bytes(old+b' ')
        self.assertEqual(self.observation()['observation_success'],0)
        path.write_bytes(old); path.unlink(); path.symlink_to(self.operations[0])
        self.assertEqual(self.observation()['observation_success'],0)

    def test_manual_registry_capacity_is_bounded_without_deletion(self):
        self.manual_fixture(); self.register(); registry=self.root/'manual-copies'
        template=registry/self.manual_hash
        for index in range(52):
            digest=f'{index:064x}'; target=registry/digest
            shutil.copytree(template,target)
            record=json.loads((target/'success.json').read_bytes()); record['bundleSha256']=digest
            for name in ('archive','recovery'):
                path=target/(name+'.json'); operation=json.loads(path.read_bytes()); operation['bundleSha256']=digest
                self.write(path,json.dumps(operation).encode()); record[name+'Sha256']=sha(path.read_bytes())
            self.write(target/'success.json',json.dumps(record).encode())
        self.assertEqual(self.observation()['observation_success'],0)
        with self.assertRaises(ValueError): self.register()
        self.assertEqual(len(list(registry.iterdir())),53)


    def test_manual_copy_cannot_mask_corrupt_weekly_success_or_missing_cycle(self):
        self.manual_fixture(); self.register()
        saved=(self.root/'last-success.json').read_bytes(); record=json.loads(saved)
        record['snapshotId']='invalid'; self.write(self.root/'last-success.json',json.dumps(record).encode())
        self.assertEqual(self.observation()['observation_success'],0)
        with self.assertRaises(ValueError): policy.freshness(self.root,LATER)
        self.write(self.root/'last-success.json',saved)
        shutil.rmtree(self.cycle)
        self.assertEqual(self.observation()['missed_cycle'],1)
        self.assertEqual(self.observation()['copy_source'],2)


if __name__ == '__main__':
    unittest.main()
