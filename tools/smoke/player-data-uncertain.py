#!/usr/bin/env python3
"""Offline acceptance of uncertain-slot closure; no network, game or backup transport."""
import datetime as dt
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'ops/gameserver'))
import player_data_schedule as policy
import player_data_reconciliation as contract


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


control = load('uncertain_control', 'ops/production/player-data-scheduled.py')
monitor = load('uncertain_monitor', 'ops/production/player-data-monitor.py')
SLOT = '2026-11-01'
WHEN = dt.datetime(2026, 11, 1, 1, tzinfo=dt.timezone.utc)
LATER = WHEN + dt.timedelta(days=2)


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


class UncertainTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)
        self.ledger = policy.Ledger(self.root)
        with mock.patch.object(policy, 'now', return_value=WHEN):
            self.cycle, _ = self.ledger.claim(SLOT)
        self.pending = self.ledger.pending.read_bytes()
        self.success = {'schema': 1, 'state': 'verified', 'slot': '2026-10-25',
                        'workload': 'goldsrcops-player-data-v1', 'snapshotId': 'a' * 64,
                        'bundleSha256': 'b' * 64, 'capturedUtc': (WHEN - dt.timedelta(days=7)).isoformat()}
        policy.write_json(self.root / 'last-success.json', self.success)
        self.success_raw = (self.root / 'last-success.json').read_bytes()
        self.proof = {'schema': 1, 'slot': SLOT, 'state': contract.PROOF_STATE,
                      'observedUtc': (LATER - dt.timedelta(seconds=15)).isoformat(),
                      'gameStartedUtc': (WHEN + dt.timedelta(days=1)).isoformat(),
                      'gameInvocationId': 'c' * 32, 'gamePendingAbsent': True, 'gameSlotAbsent': True}
        self.proof_path = self.root / 'reviewed-game-proof.json'
        self.write(self.proof_path, self.proof)
        self.units = {'timer': {'LoadState': 'loaded', 'ActiveState': 'active', 'UnitFileState': 'enabled',
                               'Persistent': 'no', 'TimersCalendar': '{ OnCalendar=Sun *-*-* 04:00:00 Europe/Moscow ; next_elapse=x }'},
                      'service': {'LoadState': 'loaded', 'ActiveState': 'inactive', 'Result': 'success'}}

    def tearDown(self):
        self.temp.cleanup()

    def write(self, path, value):
        path.write_text(json.dumps(value) + '\n', encoding='utf-8')
        path.chmod(0o600)

    def close(self, **overrides):
        args = dict(ledger=self.ledger, slot=SLOT, proof_path=self.proof_path,
                    pending_hash=sha(self.pending), success_hash=sha(self.success_raw),
                    proof_hash=sha(self.proof_path.read_bytes()), clock=lambda: LATER)
        args.update(overrides)
        return control.close_uncertain(**args)

    def observe(self):
        return monitor.observe(self.root, self.units, LATER, (WHEN - dt.timedelta(days=8)).isoformat())

    def assert_untouched(self):
        self.assertEqual(self.ledger.pending.read_bytes(), self.pending)
        self.assertEqual((self.cycle / 'result.json').read_bytes(), self.pending)
        self.assertEqual((self.root / 'last-success.json').read_bytes(), self.success_raw)
        self.assertEqual({p.name for p in self.cycle.iterdir()}, {'result.json'})

    def test_closure_retains_uncertainty_and_exact_bytes_without_transport(self):
        proof_raw = self.proof_path.read_bytes()
        with (mock.patch.object(control.Transport, 'remote', side_effect=AssertionError('No SSH')),
              mock.patch.object(control.Transport, 'archive', side_effect=AssertionError('No archive'))):
            record = self.close()
        self.assertEqual(record['state'], 'closed-uncertain')
        self.assertFalse(self.ledger.pending.exists())
        self.assertEqual((self.cycle / 'original-pending.json').read_bytes(), self.pending)
        self.assertEqual((self.cycle / 'original-result.json').read_bytes(), self.pending)
        evidence = policy.read_json(self.cycle / 'reconciliation.json')
        self.assertEqual(evidence['gameProof'].encode(), proof_raw)
        self.assertEqual((self.root / 'last-success.json').read_bytes(), self.success_raw)
        data = self.observe()
        self.assertEqual(data['observation_success'], 1)
        self.assertEqual(data['last_attempt_status'], 9)
        self.assertEqual(data['requires_reconciliation'], 0)
        self.assertEqual(data['last_success_timestamp_seconds'], WHEN.timestamp() - 7 * 86400)
        self.assertEqual(data['fresh'], 0)
        self.assertEqual(data['copy_present'], 1)
        with self.assertRaisesRegex(ValueError, 'older than eight days'):
            policy.freshness(self.root, LATER)

    def test_same_slot_cannot_capture_again_and_new_slot_can_be_claimed(self):
        record = self.close()
        with self.assertRaises(ValueError):
            self.close()
        transport = mock.Mock()
        with mock.patch.object(policy, 'now', return_value=LATER):
            self.assertEqual(control.cycle(self.ledger, transport, clock=lambda: WHEN), record)
        transport.remote.assert_not_called()
        transport.archive.assert_not_called()
        with mock.patch.object(policy, 'now', return_value=WHEN + dt.timedelta(days=7)):
            _, existing = self.ledger.claim('2026-11-08')
        self.assertIsNone(existing)

    def test_failed_service_and_old_copy_remain_visible(self):
        self.close()
        self.units['service'].update(ActiveState='failed', Result='exit-code')
        data = self.observe()
        self.assertEqual(data['observation_success'], 1)
        self.assertEqual(data['service_failed'], 1)
        self.assertEqual(data['last_attempt_status'], 6)
        self.assertEqual(data['fresh'], 0)

    def test_invalid_or_stale_host_proof_never_writes(self):
        for key, value in (('state', 'not-started'), ('slot', '2026-11-08'), ('schema', True),
                           ('gameInvocationId', 'bad'), ('gamePendingAbsent', 1), ('gameSlotAbsent', False),
                           ('observedUtc', (LATER - dt.timedelta(minutes=3)).isoformat()),
                           ('observedUtc', (LATER + dt.timedelta(seconds=1)).isoformat()),
                           ('gameStartedUtc', WHEN.isoformat()),
                           ('gameStartedUtc', LATER.isoformat()), ('extra', 'unreviewed')):
            with self.subTest(key=key, value=value):
                proof = dict(self.proof, **{key: value})
                self.write(self.proof_path, proof)
                with self.assertRaises(ValueError):
                    self.close()
                self.assert_untouched()

    def test_window_hash_and_oversized_proof_guards(self):
        for changes in ({'clock': lambda: WHEN}, {'pending_hash': '0' * 64},
                        {'success_hash': '0' * 64}, {'proof_hash': '0' * 64}):
            with self.assertRaises(ValueError):
                self.close(**changes)
            self.assert_untouched()
        self.proof_path.write_bytes(self.proof_path.read_bytes() + b' ' * 4096)
        with self.assertRaises(ValueError):
            self.close()
        self.assert_untouched()

    def test_original_json_duplicates_and_result_byte_drift_fail_closed(self):
        raw = self.pending.rstrip().replace(b'"schema":1', b'"schema":1,"schema":1')
        self.assertNotEqual(raw, self.pending.rstrip())
        self.ledger.pending.write_bytes(raw)
        (self.cycle / 'result.json').write_bytes(raw)
        with self.assertRaises(ValueError):
            self.close(pending_hash=sha(raw))
        self.assertEqual({p.name for p in self.cycle.iterdir()}, {'result.json'})
        self.ledger.pending.write_bytes(self.pending)
        (self.cycle / 'result.json').write_bytes(self.pending + b' ')
        with self.assertRaises(ValueError):
            self.close()
        self.assertTrue(self.ledger.pending.exists())

    def test_existing_archive_and_symlink_proof_are_rejected(self):
        (self.cycle / 'player-data.tar').write_bytes(b'uncertain archive')
        with self.assertRaises(ValueError):
            self.close()
        (self.cycle / 'player-data.tar').unlink()
        self.proof_path.rename(self.root / 'proof-original.json')
        self.proof_path.symlink_to(self.root / 'proof-original.json')
        with self.assertRaises(ValueError):
            self.close()
        self.assert_untouched()

    def test_interruptions_preserve_pending_and_prohibit_retry(self):
        with mock.patch.object(self.ledger, 'finish', side_effect=OSError('Interrupted')):
            with self.assertRaises(OSError):
                self.close()
        self.assertTrue(self.ledger.pending.exists())
        self.assertTrue((self.cycle / 'reconciliation.json').is_file())
        with self.assertRaises(ValueError):
            self.close()
        with self.assertRaises(ValueError):
            self.ledger.claim('2026-11-08')
        self.assertEqual(self.observe()['requires_reconciliation'], 1)

    def test_result_published_but_pending_retained_stays_blocked(self):
        def interrupted_finish(cycle, record):
            policy.write_json(cycle / 'result.json', record, replace=True)
            raise OSError('Interrupted before removing pending')
        with mock.patch.object(self.ledger, 'finish', side_effect=interrupted_finish):
            with self.assertRaises(OSError):
                self.close()
        data = self.observe()
        self.assertEqual(data['requires_reconciliation'], 1)
        self.assertTrue(self.ledger.pending.exists())
        with self.assertRaises(ValueError):
            self.close()

    def test_corrupt_proof_retained_bytes_or_success_invalidates_reader_and_claim(self):
        self.close()
        for name in ('original-pending.json', 'original-result.json', 'last-success.json', 'reconciliation.json'):
            with self.subTest(name=name):
                path = self.root / name if name == 'last-success.json' else self.cycle / name
                saved = path.read_bytes()
                path.write_bytes(saved + b' ')
                # A whitespace-only change to evidence has the same meaning; alter its bound proof instead.
                if name == 'reconciliation.json':
                    value = json.loads(saved)
                    value['gameProofSha256'] = '0' * 64
                    self.write(path, value)
                self.assertEqual(self.observe()['observation_success'], 0)
                with mock.patch.object(policy, 'now', return_value=LATER):
                    with self.assertRaises(ValueError):
                        self.ledger.claim(SLOT)
                path.write_bytes(saved)
        self.assertEqual(self.observe()['observation_success'], 1)


if __name__ == '__main__':
    unittest.main()
