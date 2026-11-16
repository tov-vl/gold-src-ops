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


if __name__ == '__main__':
    unittest.main(verbosity=2)
