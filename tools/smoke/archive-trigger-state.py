"""Offline crash/admission checks using disposable ledgers and synthetic evidence."""
import copy
import datetime as dt
import importlib.util
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('archive_trigger_state',
    ROOT / 'ops/availability/archive_trigger_state.py')
state = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(state)
NOW = dt.datetime(2026, 11, 14, 12, 53, tzinfo=state.UTC)
FILES = {'.github/workflows/availability-evidence.yml': 'a' * 64}


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / 'state.db'
        self.ledger = state.Ledger.create(self.path, NOW)
        self.generation = str(uuid.uuid4())

    def tearDown(self):
        self.ledger.close()
        self.temp.cleanup()

    def prepare(self, now=NOW):
        return self.ledger.prepare(self.generation, state.stamp(state.newest_slot(now)),
                                   'b' * 40, FILES, now)

    def accepted(self):
        row = self.prepare()
        self.ledger.begin(row['claim'], NOW)
        return self.ledger.accepted(row['claim'], 123, NOW)

    def evidence(self, row):
        start, end = state.window(row['slot'])
        receipt = dict(schema=1, claim=row['claim'], generation=self.generation,
            slot=row['slot'], exporter='b'*40, manifest=FILES.copy(), run_id=123,
            window_start=start, window_end=end, sha256='c'*64, bytes=512)
        proof = dict(run_id=123, attempt=1, event='workflow_dispatch', branch='main',
            workflow=350269402, head='d'*40, manifest=FILES.copy(),
            created=state.stamp(NOW), completed=state.stamp(NOW),
            job_success=True, pin_success=True, receipt_success=True)
        return receipt, proof

    def reopen(self):
        self.ledger.close()
        self.ledger = state.Ledger(self.path)

    def test_slot_and_exact_window(self):
        row = self.prepare()
        self.assertEqual(state.window(row['slot']),
                         ('2026-11-13T12:48:00Z', '2026-11-14T12:48:00Z'))
        self.assertEqual(state.newest_slot(NOW-dt.timedelta(seconds=1)).hour, 11)

    def test_single_attempt_commits_before_transport(self):
        row = self.prepare()
        calls = []
        def send(request):
            calls.append(request)
            observer = state.Ledger(self.path)
            try:
                self.assertEqual(observer.get(row['claim'])['state'], 'Attempting')
            finally:
                observer.close()
            return {'status': 200, 'workflow_run_id': 123}
        state.attempt_once(self.ledger, row['claim'], NOW, send)
        with self.assertRaises(ValueError):
            state.attempt_once(self.ledger, row['claim'], NOW, send)
        self.assertEqual(len(calls), 1)

    def test_timeout_persists_unknown_and_does_not_expose_transport_text(self):
        row = self.prepare()
        def send(_):
            raise TimeoutError('private-provider-response')
        with self.assertRaisesRegex(ValueError, '^dispatch-uncertain$'):
            state.attempt_once(self.ledger, row['claim'], NOW, send)
        self.reopen()
        self.assertEqual(self.ledger.get(row['claim'])['state'], 'Unknown')

    def test_abrupt_interruption_leaves_committed_attempting(self):
        row = self.prepare()
        def send(_):
            raise KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            state.attempt_once(self.ledger, row['claim'], NOW, send)
        self.reopen()
        self.assertEqual(self.ledger.get(row['claim'])['state'], 'Attempting')

    def test_unexpected_dispatch_response_is_unknown(self):
        row = self.prepare()
        with self.assertRaises(ValueError):
            state.attempt_once(self.ledger, row['claim'], NOW,
                               lambda _: {'status': 204, 'workflow_run_id': 123})
        self.assertEqual(self.ledger.get(row['claim'])['state'], 'Unknown')

    def test_restart_before_send_retains_prepared_claim(self):
        self.prepare()
        self.reopen()
        with self.assertRaisesRegex(ValueError, 'outstanding-claim'):
            self.prepare(NOW+dt.timedelta(hours=1))

    def test_crash_after_committing_attempt_never_resends(self):
        row = self.prepare()
        self.ledger.begin(row['claim'], NOW)
        self.reopen()
        self.assertEqual(self.ledger.get(row['claim'])['state'], 'Attempting')
        with self.assertRaises(ValueError):
            self.ledger.begin(row['claim'], NOW)
        with self.assertRaises(ValueError):
            self.prepare(NOW+dt.timedelta(hours=1))

    def test_response_loss_blocks_future_generation_too(self):
        row = self.prepare()
        self.ledger.begin(row['claim'], NOW)
        self.ledger.unknown(row['claim'], NOW)
        self.generation = str(uuid.uuid4())
        with self.assertRaisesRegex(ValueError, 'outstanding-claim'):
            self.prepare(NOW+dt.timedelta(hours=1))

    def test_two_connections_cannot_prepare_two_claims(self):
        other = state.Ledger(self.path)
        try:
            self.prepare()
            with self.assertRaises(ValueError):
                other.prepare(str(uuid.uuid4()), state.stamp(NOW), 'b'*40, FILES, NOW)
        finally:
            other.close()

    def test_locked_database_fails_without_admission(self):
        other = state.Ledger(self.path)
        try:
            other.connection.execute('BEGIN IMMEDIATE')
            with self.assertRaises(sqlite3.OperationalError):
                self.prepare()
            other.connection.execute('ROLLBACK')
            self.prepare()
        finally:
            other.close()

    def test_success_is_not_dispatch_acceptance(self):
        row = self.accepted()
        self.assertEqual(row['state'], 'Accepted')
        receipt, proof = self.evidence(row)
        self.assertEqual(self.ledger.verify(row['claim'], receipt, proof, NOW)['state'], 'Verified')
        with self.assertRaises(sqlite3.IntegrityError):
            self.prepare()
        self.prepare(NOW+dt.timedelta(hours=1))

    def test_bad_receipts_and_job_proofs_block_new_writes(self):
        mutations = [
            ('receipt', 'schema', True), ('receipt', 'run_id', True),
            ('receipt', 'generation', str(uuid.uuid4())), ('receipt', 'bytes', 0),
            ('receipt', 'bytes', True), ('receipt', 'exporter', 'e'*40),
            ('receipt', 'window_end', '2026-11-14T12:49:00Z'),
            ('receipt', 'sha256', 'invalid'), ('receipt', 'extra', 'provider-text'),
            ('proof', 'attempt', 2), ('proof', 'attempt', True),
            ('proof', 'event', 'schedule'), ('proof', 'branch', 'other'),
            ('proof', 'workflow', 1), ('proof', 'pin_success', False),
            ('proof', 'receipt_success', False), ('proof', 'job_success', False),
            ('proof', 'completed', '2026-11-14T12:54:00Z'),
            ('proof', 'manifest', {'other.py': 'a'*64})]
        row = self.accepted()
        receipt, proof = self.evidence(row)
        for target, key, value in mutations:
            with self.subTest(target=target, key=key, value=value):
                with tempfile.TemporaryDirectory() as folder:
                    clone_path = Path(folder)/'copy.db'
                    destination = sqlite3.connect(clone_path)
                    self.ledger.connection.backup(destination)
                    destination.close()
                    clone = state.Ledger(clone_path)
                    try:
                        r, p = copy.deepcopy(receipt), copy.deepcopy(proof)
                        (r if target == 'receipt' else p)[key] = value
                        with self.assertRaises(ValueError):
                            clone.verify(row['claim'], r, p, NOW)
                        self.assertEqual(clone.get(row['claim'])['state'], 'Unknown')
                        with self.assertRaises(ValueError):
                            clone.prepare(self.generation, state.stamp(NOW+dt.timedelta(hours=1)),
                                          'b'*40, FILES, NOW+dt.timedelta(hours=1))
                    finally:
                        clone.close()

    def test_unrelated_head_change_is_allowed(self):
        row = self.accepted()
        receipt, proof = self.evidence(row)
        proof['head'] = 'e'*40
        self.assertEqual(self.ledger.verify(row['claim'], receipt, proof, NOW)['state'], 'Verified')

    def test_stale_and_future_slots_rejected(self):
        for delta in (-1, 1):
            with self.subTest(delta=delta), self.assertRaises(ValueError):
                self.ledger.prepare(self.generation, state.stamp(NOW+dt.timedelta(hours=delta)),
                                    'b'*40, FILES, NOW)

    def test_stale_prepared_claim_cannot_begin(self):
        row = self.prepare()
        with self.assertRaisesRegex(ValueError, 'stale-slot'):
            self.ledger.begin(row['claim'], NOW+dt.timedelta(hours=1))
        self.assertEqual(self.ledger.get(row['claim'])['state'], 'Prepared')

    def test_clock_regression_rolls_back(self):
        self.prepare()
        with self.assertRaisesRegex(ValueError, 'clock-regressed'):
            self.prepare(NOW-dt.timedelta(seconds=1))

    def test_missing_store_does_not_create(self):
        missing = self.path.parent/'missing.db'
        with self.assertRaises(ValueError):
            state.Ledger(missing)
        self.assertFalse(missing.exists())

    def test_create_never_overwrites(self):
        before = self.path.read_bytes()
        with self.assertRaises(ValueError):
            state.Ledger.create(self.path, NOW)
        self.assertEqual(self.path.read_bytes(), before)

    def test_unknown_schema_is_not_migrated(self):
        self.ledger.connection.execute('PRAGMA user_version=2')
        with self.assertRaisesRegex(ValueError, 'unsupported-schema'):
            state.Ledger(self.path)
        self.assertEqual(self.ledger.connection.execute('PRAGMA user_version').fetchone()[0], 2)

    def test_capacity_failure_does_not_authorize_send(self):
        old = state.LIMIT_CLAIMS
        state.LIMIT_CLAIMS = 0
        try:
            with self.assertRaisesRegex(ValueError, 'claim-capacity'):
                self.prepare()
        finally:
            state.LIMIT_CLAIMS = old
        self.prepare()

    def test_byte_capacity_rolls_back(self):
        old = state.LIMIT_BYTES
        state.LIMIT_BYTES = 1
        try:
            with self.assertRaisesRegex(ValueError, 'state-capacity'):
                self.prepare()
        finally:
            state.LIMIT_BYTES = old
        self.prepare()

    def test_corrupt_store_is_not_reinitialized(self):
        corrupt = self.path.parent/'corrupt.db'
        corrupt.write_bytes(b'invalid sqlite database')
        with self.assertRaises(sqlite3.DatabaseError):
            state.Ledger(corrupt)
        self.assertEqual(corrupt.read_bytes(), b'invalid sqlite database')

    def test_missing_exclusive_index_fails_closed(self):
        self.ledger.connection.execute('DROP INDEX one_live_claim')
        with self.assertRaisesRegex(ValueError, 'state-schema'):
            state.Ledger(self.path)

    def test_failed_known_claim_cannot_be_retried(self):
        row = self.prepare()
        self.ledger.transition(row['claim'], 'Failed-known', NOW)
        with self.assertRaises(ValueError):
            self.ledger.begin(row['claim'], NOW)

    def test_retire_terminal_only_and_preserve_protected_window(self):
        row = self.prepare()
        self.ledger.transition(row['claim'], 'Failed-known', NOW)
        future = NOW+dt.timedelta(days=30)
        self.assertEqual(self.ledger.retire(future, NOW), 0)
        live = self.prepare(future)
        self.assertEqual(self.ledger.retire(future, future), 1)
        self.assertEqual(self.ledger.get(live['claim'])['state'], 'Prepared')
        self.assertEqual(self.ledger.connection.execute('SELECT sum(claims) FROM retirement').fetchone()[0], 1)

    def test_invalid_paths_and_nonfinite_sizes_fail_closed(self):
        for path in ('../escape', '/absolute', 'a//b', 'a/./b'):
            with self.subTest(path=path), self.assertRaises(ValueError):
                state.manifest({path: 'a'*64})


if __name__ == '__main__':
    unittest.main()
