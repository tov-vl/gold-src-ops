#!/usr/bin/env python3
"""Admission controls only; generated unit inputs are never runtime evidence."""
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


if __name__ == '__main__':
    unittest.main(verbosity=2)
