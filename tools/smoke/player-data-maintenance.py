#!/usr/bin/env python3
"""Offline regression for accepted addon baseline transitions and no-capture reconciliation."""
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
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


if __name__ == '__main__':
    unittest.main()
