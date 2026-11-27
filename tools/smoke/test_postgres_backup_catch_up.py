"""Fault and boundary checks for the isolated logical backup scheduler."""

from datetime import datetime, timedelta, timezone
from hashlib import sha256
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import Mock, patch

from postgres_backup_catch_up import (
    ARCHIVE_NAME, RECOVERABLE_TAG, RECOVERY_CHECKS, Scheduler, candidate_digest, decision_lock, read_json, replace_json,
)


class CatchUpTests(unittest.TestCase):
    def setUp(self):
        work = TemporaryDirectory()
        self.addCleanup(work.cleanup)
        self.root = Path(work.name)
        self.now = datetime(2026, 11, 27, tzinfo=timezone.utc)
        self.identity = 'gso-logical-0123456789ab'
        self.scheduler = self.restart()
        self.scheduler.initialize()
        self.sequence = 0
        self.verify = Mock()

    def restart(self):
        return Scheduler(self.root, self.identity, clock=lambda: self.now)

    def backup(self, _claim):
        self.sequence += 1
        self.now += timedelta(seconds=2)
        captured = self.now.isoformat()
        self.now += timedelta(seconds=3)
        return dict(Action='PostgreSQLBackup', Workload='ControlPlane',
                    BackupHost=self.identity, ArchiveName='goldsrcops-postgresql.dump',
                    SnapshotId=format(self.sequence, '064x'), SnapshotTime=captured,
                    CompletedAtUtc=self.now.isoformat())

    def seed(self):
        receipt = self.scheduler.execute('scheduled', self.backup, self.verify)
        self.assertEqual(receipt['decision'], 'verified')
        return receipt['snapshotId']

    def test_skip_then_catch_up_exactly_at_verified_age(self):
        previous = self.seed()
        self.now += timedelta(seconds=2699)
        restarted = self.restart()
        self.assertEqual(restarted.inspect('catch-up')[1]['decision'], 'fresh')
        self.now += timedelta(seconds=1)
        result = restarted.execute('catch-up', self.backup, self.verify)
        self.assertEqual(result['decision'], 'verified')
        self.assertNotEqual(result['snapshotId'], previous)
        self.assertEqual(restarted.inspect('scheduled')[1]['decision'], 'fresh')
        self.assertEqual(self.sequence, 2)

    def test_regular_job_suppresses_due_catch_up(self):
        self.seed()
        self.now += timedelta(minutes=45)
        scheduled = self.restart().execute('scheduled', self.backup, self.verify)
        catch_up = self.scheduler.execute('catch-up', self.backup, self.verify)
        self.assertEqual(scheduled['decision'], 'verified')
        self.assertEqual(catch_up['decision'], 'fresh')
        self.assertEqual(scheduled['snapshotId'], catch_up['snapshotId'])
        self.assertEqual(self.sequence, 2)

    def test_lock_prevents_write_when_another_worker_owns_decision(self):
        self.seed()
        self.now += timedelta(minutes=45)
        create = Mock()
        with decision_lock(self.scheduler.lock) as owner:
            self.assertTrue(owner)
            self.assertEqual(self.restart().execute('catch-up', create, self.verify)['decision'], 'busy')
        create.assert_not_called()
        self.assertEqual(self.scheduler.inspect('catch-up')[1]['decision'], 'due')

    def test_write_failure_and_verification_failure_hold_old_snapshot(self):
        for failed_phase in ('write', 'verification'):
            with self.subTest(phase=failed_phase):
                if self.sequence:
                    self.scheduler.state.unlink()
                    self.scheduler.initialized.unlink()
                    self.scheduler.initialize()
                previous = self.seed()
                self.now += timedelta(minutes=50)
                failure = Mock(side_effect=RuntimeError('test failure'))
                backup = failure if failed_phase == 'write' else self.backup
                verify = failure if failed_phase == 'verification' else self.verify
                result = self.scheduler.execute('catch-up', backup, verify)
                self.assertEqual((result['decision'], result['phase'], result['rpo']),
                                 ('failed', failed_phase, 'warning'))
                self.assertEqual(result['snapshotId'], previous)
                prevented = Mock()
                self.now += timedelta(minutes=11)
                blocked = self.restart().execute('scheduled', prevented, self.verify)
                self.assertEqual((blocked['decision'], blocked['rpo']), ('unresolved-attempt', 'breached'))
                self.assertEqual(blocked['snapshotId'], previous)
                prevented.assert_not_called()

    def test_lost_candidate_receipt_preserves_old_verified_point(self):
        previous = self.seed()
        self.now += timedelta(minutes=45)

        def without_receipt(claim):
            self.backup(claim)
            return None

        result = self.scheduler.execute('catch-up', without_receipt, self.verify)
        self.assertEqual((result['decision'], result['phase']), ('failed', 'receipt'))
        self.assertEqual(result['snapshotId'], previous)
        self.assertEqual(self.restart().inspect('catch-up')[1]['decision'], 'unresolved-attempt')

    def test_crash_after_claim_before_receipt_holds_even_after_restart(self):
        previous = self.seed()
        self.now += timedelta(minutes=45)
        with self.assertRaises(SystemExit):
            self.scheduler.execute('catch-up', Mock(side_effect=SystemExit), self.verify)
        create = Mock()
        result = self.restart().execute('scheduled', create, self.verify)
        self.assertEqual((result['decision'], result['snapshotId']), ('unresolved-attempt', previous))
        create.assert_not_called()

    def test_missing_or_corrupt_scheduler_receipt_fails_closed(self):
        self.seed()
        self.scheduler.state.unlink()
        self.assertEqual(self.restart().inspect('catch-up')[1], dict(decision='state-unavailable', rpo='unknown'))
        with self.assertRaises(FileExistsError):
            self.restart().initialize()
        self.scheduler.state.write_text('{broken', encoding='ascii')
        create = Mock()
        self.assertEqual(self.restart().execute('scheduled', create, self.verify)['decision'], 'state-unavailable')
        create.assert_not_called()

    def test_future_timestamps_or_clock_rollback_do_not_admit_backup(self):
        self.seed()
        self.now -= timedelta(seconds=1)
        self.assertEqual(self.restart().inspect('scheduled')[1]['decision'], 'state-unavailable')
        self.now += timedelta(minutes=45, seconds=1)
        state = json.loads(self.scheduler.state.read_bytes())
        state['attempt'] = dict(id='1' * 32, trigger='scheduled',
                                claimedAtUtc=(self.now + timedelta(seconds=1)).isoformat())
        replace_json(self.scheduler.state, state)
        self.assertEqual(self.restart().inspect('catch-up')[1]['decision'], 'state-unavailable')

    def test_rpo_warning_uses_earliest_coverage_boundary(self):
        self.seed()
        self.now += timedelta(seconds=2995)
        result = self.restart().inspect('catch-up')[1]
        self.assertEqual((result['verifiedAgeSeconds'], result['rpo']), (2995, 'warning'))
        self.now += timedelta(seconds=600)
        self.assertEqual(self.restart().inspect('catch-up')[1]['rpo'], 'warning')
        self.now += timedelta(microseconds=1)
        self.assertEqual(self.restart().inspect('catch-up')[1]['rpo'], 'breached')

    def test_unrelated_candidate_is_rejected_before_verification(self):
        previous = self.seed()
        self.now += timedelta(minutes=45)
        verify = Mock()

        def unrelated(claim):
            return {**self.backup(claim), 'BackupHost': 'unrelated'}

        result = self.scheduler.execute('catch-up', unrelated, verify)
        self.assertEqual((result['decision'], result['phase'], result['snapshotId']), ('failed', 'receipt', previous))
        verify.assert_not_called()
        self.assertIsNotNone(read_json(self.scheduler.state)['attempt'])

    def unresolved_with_receipt(self):
        previous = self.seed()
        self.now += timedelta(minutes=45)
        backup = Mock(side_effect=self.backup)
        receipt = {}

        def capture(claim):
            receipt.update(backup(claim))
            return receipt

        failed = self.scheduler.execute('catch-up', capture, Mock(side_effect=RuntimeError))
        self.assertEqual(failed['phase'], 'verification')
        entry = dict(id=receipt['SnapshotId'], time=receipt['SnapshotTime'], hostname=self.identity,
                     paths=[f'/{ARCHIVE_NAME}'], tags=[RECOVERABLE_TAG])
        return previous, receipt, entry

    def test_reconcile_exact_receipt_and_snapshot_preserves_state(self):
        previous, receipt, entry = self.unresolved_with_receipt()
        before = self.scheduler.state.read_bytes()
        reader = Mock(return_value=receipt)
        report = self.restart().reconcile(reader, Mock(return_value=[entry]))
        self.assertEqual((report['reconciliation'], report['candidateSnapshotId'], report['snapshotId']),
                         ('recover-candidate', entry['id'], previous))
        self.assertEqual(report['candidateCount'], 1)
        reader.assert_called_once_with(read_json(self.scheduler.state)['attempt']['id'])
        self.assertEqual(self.scheduler.state.read_bytes(), before)

    def test_no_candidate_observed_after_source_failure(self):
        previous = self.seed()
        self.now += timedelta(minutes=45)
        self.scheduler.execute('catch-up', Mock(side_effect=RuntimeError), self.verify)
        old = dict(id=previous, time=read_json(self.scheduler.state)['lastVerified']['snapshotAtUtc'],
                   hostname=self.identity, paths=[f'/{ARCHIVE_NAME}'], tags=[RECOVERABLE_TAG])
        report = self.restart().reconcile(Mock(side_effect=FileNotFoundError), Mock(return_value=[old]))
        self.assertEqual((report['reconciliation'], report['candidateCount'], report['snapshotId']),
                         ('no-candidate-observed', 0, previous))
        self.assertNotIn('candidateSnapshotId', report)

    def test_missing_receipt_or_multiple_candidates_remain_uncertain(self):
        _, receipt, entry = self.unresolved_with_receipt()
        report = self.scheduler.reconcile(Mock(side_effect=FileNotFoundError), Mock(return_value=[entry]))
        self.assertEqual((report['reconciliation'], report['reason']), ('uncertain', 'missing-receipt'))
        extra = {**entry, 'id': 'b' * 64}
        ambiguous = self.scheduler.reconcile(Mock(return_value=receipt), Mock(return_value=[entry, extra]))
        self.assertEqual((ambiguous['reconciliation'], ambiguous['reason']), ('uncertain', 'multiple-candidates'))
        self.assertNotIn('candidateSnapshotId', report)
        self.assertNotIn('candidateSnapshotId', ambiguous)

    def test_receipt_inventory_conflicts_do_not_select_candidate(self):
        _, receipt, entry = self.unresolved_with_receipt()
        for listed in ([], [{**entry, 'id': 'b' * 64}], [{**entry, 'tags': ['pending']}],
                       [{**entry, 'time': self.now.isoformat()}]):
            with self.subTest(listed=listed):
                report = self.scheduler.reconcile(Mock(return_value=receipt), Mock(return_value=listed))
                self.assertEqual((report['reconciliation'], report['reason']),
                                 ('uncertain', 'receipt-snapshot-mismatch'))
                self.assertNotIn('candidateSnapshotId', report)
        report = self.scheduler.reconcile(Mock(side_effect=ValueError), Mock(return_value=[entry]))
        self.assertEqual((report['reconciliation'], report['reason']), ('uncertain', 'invalid-receipt'))

    def test_reconcile_busy_and_missing_lock_have_no_read_or_write_effects(self):
        selection = Mock(side_effect=AssertionError('Should not inspect the repository'))
        with decision_lock(self.scheduler.lock) as acquired:
            self.assertTrue(acquired)
            report = self.restart().reconcile(selection, selection)
            self.assertEqual((report['decision'], report['reconciliation']), ('busy', 'not-assessed'))
        self.scheduler.lock.unlink()
        report = self.restart().reconcile(selection, selection)
        self.assertEqual(report['decision'], 'state-unavailable')
        self.assertFalse(self.scheduler.lock.exists())
        selection.assert_not_called()

    def test_unavailable_inventory_or_no_attempt_produces_no_candidate(self):
        read = Mock()
        report = self.scheduler.reconcile(read, read)
        self.assertEqual(report['reconciliation'], 'no-unresolved-attempt')
        read.assert_not_called()
        self.unresolved_with_receipt()
        report = self.scheduler.reconcile(read, Mock(side_effect=OSError))
        self.assertEqual((report['reconciliation'], report['reason']), ('uncertain', 'inventory-unavailable'))
        read.assert_not_called()

    def independent_receipt(self, candidate):
        state = read_json(self.scheduler.state)
        self.now += timedelta(seconds=10)
        return dict(version=1, action='PostgreSQLIndependentRecover', backupHost=self.identity,
                    claimId=state['attempt']['id'], priorVerified=state['lastVerified'],
                    snapshotId=candidate['SnapshotId'], candidateReceiptSha256=candidate_digest(candidate),
                    archiveSha256='a' * 64, projectionSha256='b' * 64,
                    recoveredAtUtc=self.now.isoformat(), checks=dict.fromkeys(RECOVERY_CHECKS, True))

    def store_proof(self, proof):
        path = self.root / 'recovery.json'
        data = json.dumps(proof).encode('ascii')
        path.write_bytes(data)
        return path, sha256(data).hexdigest()

    def test_resolution_advances_the_verified_point_once_and_unblocks_scheduled_backup(self):
        _, receipt, entry = self.unresolved_with_receipt()
        proof = self.independent_receipt(receipt)
        saved = self.store_proof(proof)
        pending = read_json(self.scheduler.state)['attempt']
        self.now += timedelta(seconds=10)
        archive = Mock(return_value=proof['archiveSha256'])
        sources = (Mock(return_value=receipt), Mock(return_value=[entry]), archive)
        result = self.restart().resolve(*saved, *sources)
        self.assertEqual((result['resolution'], result['snapshotId'], result['decision']),
                         ('resolved', entry['id'], 'fresh'))
        archive.assert_called_once_with(entry['id'])
        state = read_json(self.scheduler.state)
        self.assertEqual(state['lastVerified']['coverageFromUtc'], pending['claimedAtUtc'])
        self.assertEqual(state['lastVerified']['verifiedAtUtc'], proof['recoveredAtUtc'])
        self.assertEqual(state['lastResolution']['priorVerified'], proof['priorVerified'])
        self.assertEqual(state['lastResolution']['recoverReceiptSha256'], saved[1])
        self.assertIsNone(state['attempt'])
        published = self.scheduler.state.read_bytes()
        unused = Mock(side_effect=AssertionError('Repeated resolution must not read the repository'))
        self.assertEqual(self.restart().resolve(*saved, unused, unused, unused)['resolution'], 'already-resolved')
        self.assertEqual(self.scheduler.state.read_bytes(), published)
        self.now += timedelta(minutes=30)
        following = self.restart().execute('scheduled', self.backup, self.verify)
        self.assertEqual(following['decision'], 'verified')
        self.assertNotEqual(following['snapshotId'], entry['id'])
        renewed = self.restart().resolve(*saved, unused, unused, unused)
        self.assertEqual((renewed['resolution'], renewed['snapshotId']), ('already-resolved', following['snapshotId']))
        self.now += timedelta(minutes=45)
        self.restart().execute('catch-up', Mock(side_effect=RuntimeError), self.verify)
        stale = self.restart().resolve(*saved, unused, unused, unused)
        self.assertEqual((stale['resolution'], stale['reason']), ('not-applied', 'stale-recover-receipt'))

    def test_resolution_requires_valid_proof_exact_baseline_and_unambiguous_inventory(self):
        _, receipt, entry = self.unresolved_with_receipt()
        proof = self.independent_receipt(receipt)
        previous = self.scheduler.state.read_bytes()
        read_candidate = Mock(return_value=receipt)
        snapshots = Mock(return_value=[entry])
        unused = Mock(side_effect=AssertionError('Must refuse before reading the dump'))
        for changed, reason in (
                ({'version': True}, 'invalid-recover-receipt'),
                ({'checks': {**proof['checks'], 'readOnlyRepository': False}}, 'invalid-recover-receipt'),
                ({'recoveredAtUtc': (self.now + timedelta(seconds=1)).isoformat()}, 'invalid-recover-receipt'),
                ({'priorVerified': None}, 'stale-recover-receipt'),
                ({'claimId': 'd' * 32}, 'stale-recover-receipt'),
                ({'snapshotId': 'd' * 64}, 'candidate-not-confirmed'),
                ({'candidateReceiptSha256': 'd' * 64}, 'recover-candidate-mismatch'),
                ({'recoveredAtUtc': receipt['SnapshotTime']}, 'recover-candidate-mismatch')):
            with self.subTest(changed=changed):
                result = self.restart().resolve(*self.store_proof({**proof, **changed}),
                                                read_candidate, snapshots, unused)
                self.assertEqual((result['resolution'], result['reason']), ('not-applied', reason))
                self.assertEqual(self.scheduler.state.read_bytes(), previous)
        path, digest = self.store_proof(proof)
        changed_bytes = self.restart().resolve(path, 'd' * 64, unused, unused, unused)
        self.assertEqual(changed_bytes['reason'], 'invalid-recover-receipt')
        missing = self.restart().resolve(path, digest, Mock(side_effect=FileNotFoundError), snapshots, unused)
        self.assertEqual(missing['reason'], 'missing-receipt')
        extra = {**entry, 'id': 'd' * 64}
        ambiguous = self.restart().resolve(path, digest, read_candidate, Mock(return_value=[entry, extra]), unused)
        self.assertEqual(ambiguous['reason'], 'multiple-candidates')
        mismatch = self.restart().resolve(path, digest, read_candidate, snapshots, Mock(return_value='d' * 64))
        self.assertEqual(mismatch['reason'], 'recover-candidate-mismatch')
        self.assertEqual(self.scheduler.state.read_bytes(), previous)
        unused.assert_not_called()

    def test_interruption_on_either_side_of_atomic_publication_can_be_reconciled(self):
        _, receipt, entry = self.unresolved_with_receipt()
        proof = self.independent_receipt(receipt)
        inputs = (*self.store_proof(proof), Mock(return_value=receipt), Mock(return_value=[entry]),
                  Mock(return_value=proof['archiveSha256']))
        pending = self.scheduler.state.read_bytes()
        with patch('postgres_backup_catch_up.os.replace', side_effect=SystemExit):
            with self.assertRaises(SystemExit):
                self.restart().resolve(*inputs)
        self.assertEqual(self.scheduler.state.read_bytes(), pending)

        def stop_after_publish(path, value):
            replace_json(path, value)
            raise SystemExit

        with patch('postgres_backup_catch_up.replace_json', side_effect=stop_after_publish):
            with self.assertRaises(SystemExit):
                self.restart().resolve(*inputs)
        completed = self.scheduler.state.read_bytes()
        self.assertEqual(self.restart().resolve(*inputs)['resolution'], 'already-resolved')
        self.assertEqual(self.scheduler.state.read_bytes(), completed)

    def test_resolution_admits_only_catch_up_with_a_previously_verified_point(self):
        _, receipt, _ = self.unresolved_with_receipt()
        state = read_json(self.scheduler.state)
        pending = state['attempt']
        unused = Mock(side_effect=AssertionError('Out of scope for resolution'))
        for point, trigger in ((None, 'catch-up'), (state['lastVerified'], 'scheduled')):
            with self.subTest(point=point, trigger=trigger):
                replace_json(self.scheduler.state, {**state, 'lastVerified': point,
                                                    'attempt': {**pending, 'trigger': trigger}})
                before = self.scheduler.state.read_bytes()
                proof = self.independent_receipt(receipt)
                result = self.restart().resolve(*self.store_proof(proof), unused, unused, unused)
                self.assertEqual((result['resolution'], result['reason']),
                                 ('not-applied', 'not-catch-up-with-baseline'))
                self.assertEqual(self.scheduler.state.read_bytes(), before)


if __name__ == '__main__':
    unittest.main()
