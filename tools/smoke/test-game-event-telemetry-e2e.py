#!/usr/bin/env python3
"""Admission controls only; generated unit inputs are never runtime evidence."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import uuid

spec = importlib.util.spec_from_file_location('telemetry_e2e', Path(__file__).with_name('game-event-telemetry-e2e.py'))
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)


class TelemetryAdmissionTests(unittest.TestCase):
    def prepare(self, root):
        evidence, compiled = root / 'evidence', root / 'compiled'
        evidence.mkdir()
        compiled.mkdir()
        engine = acceptance.engine
        lock = json.loads(engine.LOCK.read_bytes())
        for filename in engine.FIXTURES:
            (compiled / filename).write_bytes(b'unit-admission-control-only')
        manifest = dict(schema=1, toolchain=dict(amxx='1.10.0.5481', reapi='5.24.0.300'),
                        sources=engine.source_identity(), payload={name: engine.digest(compiled / name) for name in engine.FIXTURES})
        (compiled / 'manifest.json').write_text(json.dumps(manifest))
        for index, case in enumerate(lock['cases']):
            (evidence / (case['command'] + '.log')).write_text(''.join(marker + '=passed failures=0\n' for marker in case['markers']))
            if case['command'] not in engine.OBJECTIVES:
                continue
            identity = str(uuid.UUID(int=index + 1, version=4))
            directory = evidence / 'round-records' / case['command']
            directory.mkdir(parents=True)
            raw = json.dumps(dict(spoolVersion=1, recordId=identity,
                                  event=dict(type='round.ended', occurredAtUtc='2026-11-16T09:00:00Z',
                                             map=case['map'], players=3, bots=3))).encode()
            filename = identity + '.json'
            (directory / filename).write_bytes(raw)
            directory.with_suffix('.tsv').write_text('\t'.join((filename, str(len(raw)), hashlib.sha256(raw).hexdigest())) + '\n')
        result = dict(result='passed', mode='gameplay', network='none', publishedPorts=0, cleanup='passed', containerExitCode=0,
                      lockSha256=engine.digest(engine.LOCK), fixtureManifestSha256=engine.digest(compiled / 'manifest.json'),
                      cases=len(lock['cases']), compiledFixtures=len(engine.FIXTURES))
        (evidence / 'result.json').write_text(json.dumps(result))
        return evidence, compiled

    def test_complete_admission_and_distinct_native_inputs(self):
        with tempfile.TemporaryDirectory(prefix='telemetry-admission-') as temporary:
            evidence, compiled = self.prepare(Path(temporary))
            records, binding = acceptance.read_records(evidence, compiled)
            self.assertEqual(set(records), set(acceptance.SCENARIOS))
            self.assertEqual(len({hashlib.sha256(raw).digest() for raw in records.values()}), 3)
            self.assertEqual(binding['cases'], 26)

    def test_missing_failed_substituted_or_incomplete_evidence_is_rejected(self):
        for corruption in ('missing', 'failed', 'cleanup', 'lock', 'fixture', 'source', 'bytes', 'marker', 'error', 'missing-round'):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory(prefix='telemetry-rejection-') as temporary:
                evidence, compiled = self.prepare(Path(temporary))
                result_path = evidence / 'result.json'
                result = json.loads(result_path.read_bytes())
                if corruption == 'missing':
                    result_path.unlink()
                elif corruption in ('failed', 'cleanup', 'lock', 'fixture'):
                    key = dict(failed='result', cleanup='cleanup', lock='lockSha256', fixture='fixtureManifestSha256')[corruption]
                    result[key] = 'invalid'
                    result_path.write_text(json.dumps(result))
                elif corruption == 'source':
                    manifest_path = compiled / 'manifest.json'
                    manifest = json.loads(manifest_path.read_bytes())
                    manifest['sources'] = {}
                    manifest_path.write_text(json.dumps(manifest))
                elif corruption == 'bytes':
                    path = next((evidence / 'round-records').rglob('*.json'))
                    path.write_bytes(path.read_bytes() + b' ')
                elif corruption == 'marker':
                    (evidence / 'goldsrcops_bomb_explode.log').write_text('incomplete')
                elif corruption == 'error':
                    (evidence / 'error_unit.log').write_text('runtime error')
                else:
                    (evidence / 'round-records/goldsrcops_bomb_defuse.tsv').unlink()
                with self.assertRaises((ValueError, OSError)):
                    acceptance.read_records(evidence, compiled)


class QueueReceiptTests(unittest.TestCase):
    def prepare(self):
        def identity(number):
            return str(uuid.UUID(int=number, version=4))

        records = {name: json.dumps(dict(spoolVersion=1, recordId=identity(index + 1), event=dict(
            type='round.ended', occurredAtUtc=f'2026-11-16T09:0{index}:00Z', map='de_dust2', players=3, bots=3))).encode()
                   for index, name in enumerate(acceptance.SCENARIOS)}
        source, other = identity(11), identity(12)
        server, second_server = identity(21), identity(22)

        def observations(offset, source_id, server_id):
            result = []
            for index, raw in enumerate(records.values()):
                event = json.loads(raw)['event']
                request = dict(event, eventId=identity(offset + index), sourceInstanceId=source_id,
                               sequenceNumber=index + 1, contractVersion=1)
                receipt = {key: request[key] for key in ('eventId', 'sourceInstanceId', 'sequenceNumber')}
                receipt.update(serverId=server_id, duplicate=False)
                result.append(dict(payloadSha256=hashlib.sha256(json.dumps(request).encode()).hexdigest().upper(),
                                   status=202, request=request, receipt=receipt))
            return result

        delivered, second = observations(100, source, server), observations(200, other, second_server)

        def entry(observation, next_sequence, status=0, attempts=1, failure=None):
            request = observation['request']
            return dict(identity=dict(source=request['sourceInstanceId'], nextSequence=next_sequence, eventId=request['eventId'],
                                      sequence=request['sequenceNumber'], payloadSha256=observation['payloadSha256']),
                        status=status, attempts=attempts, failure=failure)

        def cycles(started, outcome='idle'):
            return dict(startedAtUtc=started, importOutcome=outcome, deliveryOutcome='idle', bothCompleted=True,
                        importCompletedAtUtc=started, deliveryCompletedAtUtc=started)

        rejected = dict(second[0], status=409, receipt=None)
        full = dict(scenario='bounded-capacity', status='passed', serverId=server, capacity=2, maximumObservedDepth=2, queued=2,
                    deferred=1, deferredSha256=hashlib.sha256(list(records.values())[2]).hexdigest(), persistedRows=0,
                    cycles=cycles('2026-11-16T10:00:00Z', 'blocked'),
                    snapshot=dict(source=source, nextSequence=3, entries=[entry(item, 3) for item in delivered[:2]]))
        drained = dict(scenario='restart-and-drain', status='passed', serverId=server, maximumObservedDepth=2, nextSequence=4,
                       persistedRows=3, queueDrained=True, spoolDrained=True, source=source, readerProjection='matched',
                       accepted=delivered, restartedCycles=cycles('2026-11-16T10:01:00Z'))
        dead = dict(scenario='dead-letter-isolation', status='passed', serverId=second_server, capacity=3, maximumObservedDepth=3,
                    persistedRows=2, deadLetterRetainedAcrossRestart=True, automaticReplay=False, readerProjection='matched',
                    snapshot=dict(source=other, nextSequence=4, entries=[entry(second[0], 4, status=2, failure='conflict')]),
                    previousCycles=cycles('2026-11-16T10:02:00Z'), restartedCycles=cycles('2026-11-16T10:03:00Z'),
                    accepted=second[1:], rejected=rejected)
        return records, dict(status='native-telemetry-queue-recovery-accepted', persistedRows=5,
                             nativeInputHashes={name: hashlib.sha256(raw).hexdigest() for name, raw in records.items()},
                             scenarios=[full, drained, dead])

    def test_queue_receipt_requires_its_explicit_mode(self):
        records, result = self.prepare()
        acceptance.validate_result(result, records, queue_recovery=True)
        with self.assertRaises(ValueError):
            acceptance.validate_result(result, records)

    def test_corrupted_queue_claims_fail_closed(self):
        controls = [
            (('persistedRows',), 4),
            (('nativeInputHashes', 'ordinary'), '0' * 64),
            (('scenarios', 0, 'maximumObservedDepth'), 3),
            (('scenarios', 0, 'snapshot', 'nextSequence'), 4),
            (('scenarios', 0, 'deferredSha256'), '0' * 64),
            (('scenarios', 0, 'cycles', 'importOutcome'), 'idle'),
            (('scenarios', 1, 'queueDrained'), False),
            (('scenarios', 1, 'restartedCycles', 'startedAtUtc'), '2026-11-16T10:00:00Z'),
            (('scenarios', 1, 'restartedCycles', 'deliveryCompletedAtUtc'), '2026-11-16T09:59:00Z'),
            (('scenarios', 1, 'accepted', 0, 'payloadSha256'), '0' * 64),
            (('scenarios', 2, 'snapshot', 'entries', 0, 'status'), 0),
            (('scenarios', 2, 'snapshot', 'entries', 0, 'attempts'), 2),
            (('scenarios', 2, 'automaticReplay'), True),
            (('scenarios', 2, 'restartedCycles', 'bothCompleted'), False),
            (('scenarios', 2, 'rejected', 'payloadSha256'), '0' * 64),
            (('scenarios', 2, 'accepted', 0, 'receipt', 'sourceInstanceId'), 'wrong-source'),
            (('scenarios', 2, 'accepted', 0, 'request', 'players'), 0),
        ]
        for path, replacement in controls:
            with self.subTest(path=path):
                records, original = self.prepare()
                result = copy.deepcopy(original)
                target = result
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = replacement
                with self.assertRaises(ValueError):
                    acceptance.validate_result(result, records, queue_recovery=True)


class ServerIsolationReceiptTests(unittest.TestCase):
    def prepare(self):
        records, queue = QueueReceiptTests().prepare()
        original = queue['scenarios']
        first = copy.deepcopy(original[1]['accepted'])
        second = [copy.deepcopy(original[2]['rejected']), *copy.deepcopy(original[2]['accepted'])]
        first_id, second_id = original[1]['serverId'], original[2]['serverId']
        source, other = original[1]['source'], original[2]['snapshot']['source']
        native = [json.loads(raw)['event'] for raw in records.values()]
        for index, item in enumerate(second):
            item['status'] = 202
            item['request'].update(native[(index + 1) % 3])
            item['receipt'] = {key: item['request'][key] for key in ('eventId', 'sourceInstanceId', 'sequenceNumber', 'occurredAtUtc')}
            item['receipt'].update(serverId=second_id, duplicate=False)
            item['payloadSha256'] = hashlib.sha256(json.dumps(item['request']).encode()).hexdigest().upper()

        def snapshot(items, source_id, next_sequence):
            return dict(source=source_id, nextSequence=next_sequence, spoolReceipts=0, entries=[dict(
                identity=dict(source=source_id, nextSequence=next_sequence, eventId=item['request']['eventId'],
                              sequence=item['request']['sequenceNumber'], payloadSha256=item['payloadSha256']),
                status=0, attempts=1, failure=None) for item in items])

        cycles = dict(startedAtUtc='2026-11-16T10:00:00Z', bothCompleted=True, importOutcome='idle', deliveryOutcome='idle',
                      importCompletedAtUtc='2026-11-16T10:01:00Z', deliveryCompletedAtUtc='2026-11-16T10:01:00Z')
        common = dict(status='passed', bothAgentsRunning=True, firstServerId=first_id, secondServerId=second_id)
        rejected = dict(first[1], status=503, receipt=None)
        parallel = dict(common, scenario='parallel-delivery', firstInitial=snapshot(first[:1], source, 2),
                        secondInitial=snapshot(second[:1], other, 2), firstAccepted=first[:1], secondAccepted=second[:1],
                        firstCycles=cycles, secondCycles=cycles, persistedRows=2)
        outage = dict(common, scenario='one-server-unavailable', firstSnapshot=snapshot(first[1:2], source, 3),
                      rejectedDelivery=rejected, recoveryReleased=False, firstPersistedRows=1, secondPersistedRows=2,
                      secondAccepted=second[:2])
        forbidden = dict(status='passed', bothAgentsRunning=True, scenario='cross-server-forbidden', credentialServerId=first_id,
                         targetServerId=second_id, httpStatus=403, beforeRows=3, afterRows=3, attempted=rejected,
                         secondPersistedRowsAfterDenial=3, recoveryReleased=False, beforeRecovery=snapshot(first[1:], source, 4),
                         secondAcceptedAfterDenial=second)
        recovered = dict(common, scenario='recover-and-isolate', firstFinal=snapshot([], source, 4), secondFinal=snapshot([], other, 4),
                         firstAccepted=first, secondAccepted=second, firstCycles=cycles, secondCycles=cycles, persistedRows=6,
                         readerProjection='matched-per-server')
        return records, json.loads(json.dumps(dict(status='native-telemetry-server-isolation-accepted', persistedRows=6,
                             nativeInputHashes={name: hashlib.sha256(raw).hexdigest() for name, raw in records.items()},
                             scenarios=[parallel, outage, forbidden, recovered])))

    def test_two_server_receipt_requires_its_explicit_mode(self):
        records, receipt = self.prepare()
        acceptance.validate_result(receipt, records, server_isolation=True)
        for kwargs in ({}, {'queue_recovery': True}, {'queue_recovery': True, 'server_isolation': True}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                acceptance.validate_result(receipt, records, **kwargs)

    def test_corrupted_isolation_receipts_fail_closed(self):
        controls = [
            (('persistedRows',), 5),
            (('nativeInputHashes', 'ordinary'), '0' * 64),
            (('scenarios', 0, 'bothAgentsRunning'), False),
            (('scenarios', 0, 'persistedRows'), 3),
            (('scenarios', 0, 'firstInitial', 'nextSequence'), 3),
            (('scenarios', 1, 'secondPersistedRows'), 1),
            (('scenarios', 1, 'recoveryReleased'), True),
            (('scenarios', 1, 'firstSnapshot', 'entries', 0, 'identity', 'payloadSha256'), '0' * 64),
            (('scenarios', 1, 'rejectedDelivery', 'status'), 202),
            (('scenarios', 2, 'httpStatus'), 202),
            (('scenarios', 2, 'afterRows'), 4),
            (('scenarios', 2, 'targetServerId'), 'wrong-server'),
            (('scenarios', 2, 'credentialServerId'), 'wrong-credential'),
            (('scenarios', 2, 'secondPersistedRowsAfterDenial'), 2),
            (('scenarios', 2, 'recoveryReleased'), True),
            (('scenarios', 2, 'beforeRecovery', 'nextSequence'), 5),
            (('scenarios', 2, 'beforeRecovery', 'entries', 0, 'status'), 2),
            (('scenarios', 3, 'firstFinal', 'nextSequence'), 5),
            (('scenarios', 3, 'firstFinal', 'spoolReceipts'), 1),
            (('scenarios', 3, 'readerProjection'), 'unverified'),
            (('scenarios', 3, 'firstCycles', 'bothCompleted'), False),
            (('scenarios', 3, 'secondCycles', 'startedAtUtc'), '2026-11-16T10:05:00Z'),
            (('scenarios', 3, 'firstAccepted', 0, 'receipt', 'duplicate'), True),
            (('scenarios', 3, 'secondAccepted', 0, 'receipt', 'serverId'), 'wrong-server'),
            (('scenarios', 3, 'secondAccepted', 0, 'request', 'players'), 0),
            (('scenarios', 3, 'secondAccepted', 0, 'request', 'sourceInstanceId'), 'wrong-source'),
        ]
        for path, replacement in controls:
            with self.subTest(path=path):
                records, receipt = self.prepare()
                receipt = copy.deepcopy(receipt)
                target = receipt
                for key in path[:-1]:
                    target = target[key]
                target[path[-1]] = replacement
                with self.assertRaises(ValueError):
                    acceptance.validate_result(receipt, records, server_isolation=True)


class PostgresRecoveryReceiptTests(unittest.TestCase):
    def prepare(self):
        records = {name: name.encode() for name in acceptance.SCENARIOS}
        source = str(uuid.UUID(int=10, version=4))
        events = []
        for sequence, (position, record, visible, replayed) in enumerate((
                ('in-snapshot', 'ordinary', True, False),
                ('acknowledged-after-snapshot', 'lost-ack', False, False),
                ('pending-at-cutover', 'unavailable-restart', True, True)), start=1):
            events.append(dict(position=position, nativeSha256=hashlib.sha256(records[record]).hexdigest(),
                               eventId=str(uuid.UUID(int=sequence, version=4)), source=source,
                               sequence=sequence, database=visible, reader=visible, replayed=replayed))
        receipt = dict(status='native-telemetry-postgres-recovery-accepted', queueDrained=True,
                       rowsBeforeCutover=2, rowsAfterRestore=1, rowsAfterDelivery=2, events=events,
                       snapshot=dict(freshContainer=True, bytes=1024, sha256='a' * 64))
        return records, receipt

    def test_recognizes_the_three_distinct_recovery_outcomes(self):
        records, receipt = self.prepare()
        acceptance.validate_postgres_recovery(receipt, records)

    def test_rejects_incorrect_recovery_outcomes(self):
        for path, replacement in (
                (('snapshot', 'freshContainer'), False), (('snapshot', 'sha256'), 'g' * 64),
                (('rowsAfterRestore',), 2), (('rowsAfterDelivery',), 3), (('queueDrained',), False),
                (('events', 1, 'database'), True), (('events', 1, 'reader'), True),
                (('events', 2, 'replayed'), False), (('events', 2, 'nativeSha256'), '0' * 64),
                (('events', 2, 'eventId'), str(uuid.UUID(int=1, version=4))),
                (('events', 2, 'source'), str(uuid.UUID(int=11, version=4)))):
            with self.subTest(path=path):
                records, receipt = self.prepare()
                section = receipt
                for key in path[:-1]:
                    section = section[key]
                section[path[-1]] = replacement
                with self.assertRaises(ValueError):
                    acceptance.validate_postgres_recovery(receipt, records)


if __name__ == '__main__':
    unittest.main(verbosity=2)
