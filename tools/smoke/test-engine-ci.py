#!/usr/bin/env python3
"""Exercise result rejection and, optionally, a real failing engine fixture."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('engine-ci.py')
spec = importlib.util.spec_from_file_location('engine_ci', SCRIPT)
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)


class ResultTests(unittest.TestCase):
    def test_success(self):
        engine.validate_log('SMOKE=passed failures=0 languages=2\n', 'SMOKE')

    def test_reject_invalid_result(self):
        for text in ('', 'SMOKE=passed\n', 'SMOKE=failed failures=1\n',
                     'SMOKE=passed failures=01\n', 'SMOKE=passed failures=0\n' * 2):
            with self.subTest(text=text), self.assertRaises(ValueError):
                engine.validate_log(text, 'SMOKE')

    def test_success_marker_cannot_hide_errors(self):
        for error in ('ASSERTION_FAILED', 'Run time error', 'Load fails', 'bad load',
                      'ML_NOTFOUND', 'OTHER=failed failures=7'):
            with self.subTest(error=error), self.assertRaises(ValueError):
                engine.validate_log('SMOKE=passed failures=0\n' + error, 'SMOKE')

    def test_asset_tampering_fails_without_download(self):
        with tempfile.TemporaryDirectory(prefix='gameplay-asset-test-') as folder:
            path = Path(folder) / 'asset.zip'
            path.write_bytes(b'tampered')
            with patch.object(engine.urllib.request, 'urlopen') as network, self.assertRaises(ValueError):
                engine.download({'name':path.name,'sha256':'0'*64,'url':'https://example.invalid/asset.zip'}, path.parent)
            network.assert_not_called()

    def test_native_error_log_is_fatal(self):
        with tempfile.TemporaryDirectory(prefix='gameplay-log-test-') as folder:
            evidence = Path(folder)
            (evidence/'case.log').write_text('SMOKE=passed failures=0\n')
            (evidence/'error_20261021.log').write_text('native failure')
            with self.assertRaises(ValueError):
                engine.validate_evidence(evidence,[{'command':'case','markers':['SMOKE']}])

    def test_mutable_image_rejected(self):
        with patch.object(engine.subprocess, 'run') as process, self.assertRaises(ValueError):
            engine.ensure_image('example/game:latest')
        process.assert_not_called()

    def test_compiled_manifest_binds_payload_and_checkout(self):
        with tempfile.TemporaryDirectory(prefix='gameplay-manifest-test-') as folder:
            root = Path(folder)
            compiled = root / 'compiled'
            compiled.mkdir()
            for name in engine.FIXTURES:
                (compiled / name).write_bytes(b'fixture-bytes')
            package = root / 'package'
            engine.package_fixtures(compiled, package)
            engine.verify_fixtures(package)
            with patch.object(engine, 'source_identity', return_value={'changed': '0'*64}), self.assertRaises(ValueError):
                engine.verify_fixtures(package)
            (package / next(iter(engine.FIXTURES))).write_bytes(b'changed')
            with self.assertRaises(ValueError):
                engine.verify_fixtures(package)

    def test_map_modes_require_separate_evidence(self):
        cases = [case for case in json.loads(engine.LOCK.read_bytes())['cases']
                 if case['plugin'] == 'map_menu_smoke']
        self.assertEqual(len(cases), 2)
        self.assertNotEqual(cases[0]['command'], cases[1]['command'])
        with tempfile.TemporaryDirectory(prefix='map-mode-test-') as folder:
            evidence = Path(folder)
            for case in cases:
                (evidence / (case['command'] + '.log')).write_text(
                    ''.join(marker + '=passed failures=0\n' for marker in case['markers']))
            engine.validate_evidence(evidence, cases)
            (evidence / (cases[1]['command'] + '.log')).write_text(
                (evidence / (cases[0]['command'] + '.log')).read_text())
            with self.assertRaises(ValueError):
                engine.validate_evidence(evidence, cases)

    def test_case_plan_rejects_log_collision_or_unknown_plugin(self):
        case = next(case for case in json.loads(engine.LOCK.read_bytes())['cases']
                    if case['command'] == 'goldsrcops_map_menu_provider_smoke')
        with self.assertRaises(ValueError):
            engine.cases_tsv([case, case])
        for plugins in (['missing'], [case['plugin']]):
            with self.subTest(plugins=plugins), self.assertRaises(ValueError):
                engine.cases_tsv([{**case, 'companionPlugins': plugins}])

    def test_combat_requires_each_result(self):
        case = next(case for case in json.loads(engine.LOCK.read_bytes())['cases']
                    if case['command'] == 'goldsrcops_map_stats_smoke')
        markers = ['MAP_STATS_SMOKE', 'KILL_HEAL_SMOKE', 'KILL_AMMO_SMOKE', 'KILL_STREAK_SMOKE']
        self.assertEqual(case['markers'], markers)
        with tempfile.TemporaryDirectory(prefix='combat-result-test-') as folder:
            evidence = Path(folder)
            log = evidence / (case['command'] + '.log')
            results = {marker: marker + '=passed failures=0\n' for marker in markers}
            log.write_text(''.join(results.values()))
            engine.validate_evidence(evidence, [case])
            for marker in markers:
                for invalid in ('', marker + '=failed failures=1\n', results[marker] * 2):
                    with self.subTest(marker=marker, invalid=invalid):
                        log.write_text(''.join(invalid if name == marker else line
                                               for name, line in results.items()))
                        with self.assertRaises(ValueError):
                            engine.validate_evidence(evidence, [case])

    def test_respawn_rules_result_is_required(self):
        case = next(case for case in json.loads(engine.LOCK.read_bytes())['cases']
                    if case['command'] == 'goldsrcops_player_menu_smoke')
        self.assertIn('PLAYER_RESPAWN_RULES_SMOKE', case['markers'])
        with tempfile.TemporaryDirectory(prefix='respawn-result-test-') as folder:
            evidence = Path(folder)
            log = evidence / (case['command'] + '.log')
            valid = ''.join(marker + '=passed failures=0\n' for marker in case['markers'])
            log.write_text(valid)
            engine.validate_evidence(evidence, [case])
            for replacement in ('', 'PLAYER_RESPAWN_RULES_SMOKE=failed failures=1\n',
                                'PLAYER_RESPAWN_RULES_SMOKE=passed failures=0\n' * 2):
                log.write_text(valid.replace('PLAYER_RESPAWN_RULES_SMOKE=passed failures=0\n', replacement))
                with self.assertRaises(ValueError):
                    engine.validate_evidence(evidence, [case])

    def test_missing_fixture_log_is_fatal(self):
        with tempfile.TemporaryDirectory(prefix='gameplay-missing-test-') as folder, self.assertRaises(FileNotFoundError):
            engine.validate_evidence(Path(folder),[{'command':'case','markers':['SMOKE']}])

    def test_multiplayer_requires_all_native_checkpoints(self):
        lock = json.loads(engine.LOCK.read_bytes())
        case = next(case for case in lock['cases'] if case['command'] == 'goldsrcops_multiplayer_smoke')
        markers = ['MULTIPLAYER_SMOKE', 'SPAWN_PROTECTION_SMOKE',
                   'AUTOMATIC_RESPAWN_SMOKE', 'MULTIPLAYER_ISOLATION_SMOKE']
        self.assertEqual(case['markers'], markers)
        negative = lock['missingSpawnProtectionCase']
        self.assertEqual(negative['plugin'], case['plugin'])
        self.assertEqual(negative['markers'], markers)
        self.assertNotEqual(negative['command'], case['command'])
        with tempfile.TemporaryDirectory(prefix='multiplayer-result-test-') as folder:
            evidence = Path(folder)
            log = evidence / (case['command'] + '.log')
            valid = {marker: marker + '=passed failures=0\n' for marker in markers}
            log.write_text(''.join(valid.values()))
            engine.validate_evidence(evidence, [case])
            for marker in markers:
                for invalid in ('', marker + '=failed failures=1\n', valid[marker] * 2):
                    with self.subTest(marker=marker, invalid=invalid):
                        log.write_text(''.join(invalid if name == marker else value
                                               for name, value in valid.items()))
                        with self.assertRaises(ValueError):
                            engine.validate_evidence(evidence, [case])

    def test_changed_native_inputs_reject_old_compiled_manifest(self):
        targets = ('tools/smoke/amxx-multiplayer.inc', 'ops/gameserver/fast-reentry-loadout-v1.cfg',
                   'tools/smoke/amxx-game-event-producer.sma',
                   'samples/amxmodx-game-event-producer/goldsrcops_game_events.sma')
        for changed in targets:
            with self.subTest(changed=changed), tempfile.TemporaryDirectory(prefix='multiplayer-source-test-') as folder:
                root = Path(folder)
                checkout = root / 'checkout'
                for name in set(engine.source_identity()) | set(targets):
                    destination = checkout / name
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes((engine.ROOT / name).read_bytes())
                compiled = root / 'compiled'
                compiled.mkdir()
                for name in engine.FIXTURES:
                    (compiled / name).write_bytes(b'fixture-bytes')
                with patch.object(engine, 'ROOT', checkout):
                    engine.package_fixtures(compiled, root / 'package')
                    engine.verify_fixtures(root / 'package')
                    target = checkout / changed
                    target.write_bytes(target.read_bytes() + b'\n// changed test input\n')
                    with self.assertRaises(ValueError):
                        engine.verify_fixtures(root / 'package')

    def test_producer_requires_each_native_checkpoint(self):
        lock = json.loads(engine.LOCK.read_bytes())
        case = next(c for c in lock['cases'] if c['command'] == 'goldsrcops_producer_smoke')
        markers = ['PRODUCER_SMOKE', 'PRODUCER_ROSTER_SMOKE',
                   'PRODUCER_LIFECYCLE_SMOKE', 'PRODUCER_SPOOL_SMOKE']
        self.assertEqual(case['markers'], markers)
        self.assertEqual(lock['disabledProducerCase']['markers'], markers)
        self.assertEqual(lock['disabledProducerCase']['plugin'], case['plugin'])
        self.assertNotEqual(lock['disabledProducerCase']['command'], case['command'])
        with tempfile.TemporaryDirectory(prefix='producer-markers-test-') as folder:
            path = Path(folder) / (case['command'] + '.log')
            valid = {name: name + '=passed failures=0\n' for name in markers}
            path.write_text(''.join(valid.values()))
            engine.validate_evidence(path.parent, [case])
            for marker in markers:
                for invalid in ('', marker + '=failed failures=1\n', valid[marker] * 2):
                    with self.subTest(marker=marker, invalid=invalid):
                        path.write_text(''.join(invalid if name == marker else value for name, value in valid.items()))
                        with self.assertRaises(ValueError):
                            engine.validate_evidence(path.parent, [case])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--integration', action='store_true')
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--compiled', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ResultTests))
    if not result.wasSuccessful():
        return 1
    if args.integration:
        if args.cache is None or args.output is None or args.compiled is None:
            parser.error('--integration requires --cache, --compiled and --output')
        attempt = subprocess.run([sys.executable,'-B',str(SCRIPT),'--cache',str(args.cache),
                                  '--output',str(args.output),'--compiled',str(args.compiled),'--negative-check'], timeout=720)
        evidence = args.output/'evidence'
        outcome = json.loads((evidence/'result.json').read_bytes())
        log = (evidence/'goldsrcops_engine_failure.log').read_text(errors='replace')
        if (attempt.returncode == 0 or outcome['result'] != 'failed' or outcome['cleanup'] != 'passed'
                or 'ENGINE_ASSERTION_FAILED intentional negative fixture' not in log):
            raise RuntimeError('Real engine assertion did not fail the runner')
        print('ENGINE_FAILURE_GATE=passed intentional_assertion=rejected')
        missing = args.output.with_name(args.output.name + '-map-provider')
        attempt = subprocess.run([sys.executable,'-B',str(SCRIPT),'--cache',str(args.cache),
                                  '--output',str(missing),'--compiled',str(args.compiled),
                                  '--missing-map-provider-check'], timeout=720)
        outcome = json.loads((missing/'evidence/result.json').read_bytes())
        log = (missing/'evidence/goldsrcops_map_menu_provider_smoke.log').read_text(errors='replace')
        if (attempt.returncode == 0 or outcome['result'] != 'failed' or outcome['cleanup'] != 'passed'
                or 'MAP_MENU_ASSERTION_FAILED expected language provider presence' not in log):
            raise RuntimeError('Missing map provider did not fail the runner')
        print('MAP_PROVIDER_FAILURE_GATE=passed missing_provider=rejected')
        unprotected = args.output.with_name(args.output.name + '-spawn-protection')
        attempt = subprocess.run([sys.executable, '-B', str(SCRIPT), '--cache', str(args.cache),
                                  '--output', str(unprotected), '--compiled', str(args.compiled),
                                  '--missing-spawn-protection-check'], timeout=720)
        outcome = json.loads((unprotected/'evidence/result.json').read_bytes())
        log = (unprotected/'evidence/goldsrcops_multiplayer_unprotected.log').read_text(errors='replace')
        if (attempt.returncode == 0 or outcome['result'] != 'failed' or outcome['cleanup'] != 'passed'
                or 'MULTIPLAYER_ASSERTION_FAILED spawn protection blocks native enemy damage' not in log):
            raise RuntimeError('Disabled spawn protection did not fail the native damage fixture')
        print('SPAWN_PROTECTION_FAILURE_GATE=passed disabled_protection=rejected')
        disabled = args.output.with_name(args.output.name + '-producer')
        attempt = subprocess.run([sys.executable, '-B', str(SCRIPT), '--cache', str(args.cache),
                                  '--output', str(disabled), '--compiled', str(args.compiled),
                                  '--disabled-producer-check'], timeout=720)
        outcome = json.loads((disabled/'evidence/result.json').read_bytes())
        log = (disabled/'evidence/goldsrcops_producer_disabled.log').read_text(errors='replace')
        if (attempt.returncode == 0 or outcome['result'] != 'failed' or outcome['cleanup'] != 'passed'
                or 'PRODUCER_ASSERTION_FAILED native round end commits bot-only record' not in log):
            raise RuntimeError('Disabled producer did not fail native round-event acceptance')
        print('PRODUCER_FAILURE_GATE=passed disabled_producer=rejected')
    return 0


if __name__ == '__main__':
    sys.exit(main())
