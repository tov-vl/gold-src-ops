#!/usr/bin/env python3
"""Check native module packaging and isolated addon commit/timing evidence."""
import argparse
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


engine = load('engine_ci', ROOT / 'tools/smoke/engine-ci.py')
build = load('nvault_build', ROOT / 'native/nvault-durability/build.py')


class StorageTests(unittest.TestCase):
    def test_handler_wall_time_is_required_and_bounded(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder)
            path = evidence / 'goldsrcops_storage_benchmark.log'
            good = 'PROCESS_STORAGE_TIMING samples=10000 identities=32 handler_p99_us=40 max_us=400\n'
            path.write_text(good)
            engine.validate_storage_timing(evidence)
            for invalid in ('', good*2, good.replace('40 ', '5001 '), good.replace('10000', '100'),
                            good.replace('max_us=400', 'max_us=20'), good.replace('identities=32', 'identities=2'),
                            good.replace('max_us=400', 'max_us=' + str(engine.ENGINE_TIMEOUT_SECONDS * 1000000 + 1))):
                path.write_text(invalid)
                with self.subTest(value=invalid), self.assertRaises(ValueError):
                    engine.validate_storage_timing(evidence)

    def test_single_tail_outlier_is_retained_without_replacing_p99_gate(self):
        with tempfile.TemporaryDirectory() as folder:
            evidence = Path(folder)
            path = evidence / 'goldsrcops_storage_benchmark.log'
            observed = 'PROCESS_STORAGE_TIMING samples=10000 identities=32 handler_p99_us=8 max_us=1595399\n'
            path.write_text(observed)
            self.assertEqual(engine.validate_storage_timing(evidence),
                             dict(samples=10000, identities=32, handlerP99Us=8, handlerMaxUs=1595399))
            path.write_text(observed.replace('handler_p99_us=8', 'handler_p99_us=5001'))
            with self.assertRaises(ValueError):
                engine.validate_storage_timing(evidence)

    def test_each_helper_has_separate_case(self):
        self.assertEqual(len(engine.PROCESS_CASES), 6)
        plan = engine.cases_tsv(engine.PROCESS_CASES)
        self.assertEqual(len(plan.splitlines()), 6)
        self.assertEqual(len({case['command'] for case in engine.PROCESS_CASES}), 6)

    def test_runtime_snapshot_survives_checkout_changes(self):
        from unittest.mock import patch
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'source'; source.mkdir()
            inputs = root / 'inputs'; inputs.mkdir()
            for name in engine.RUNTIME_PATHS:
                path = source / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_text('original')
            with patch.object(engine, 'ROOT', source):
                snapshot, hashes = engine.freeze_runtime(inputs)
            for name in engine.RUNTIME_PATHS:
                (source / name).write_text('modified while running')
                self.assertEqual((snapshot / name).read_text(), 'original')
                self.assertEqual(engine.digest(snapshot / name), hashes[name])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--integration', action='store_true')
    parser.add_argument('--module-package', type=Path)
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--compiled', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if not unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(StorageTests)).wasSuccessful():
        return 1
    if args.integration:
        if not all((args.module_package, args.cache, args.compiled, args.output)):
            parser.error('Integration requires module-package/cache/compiled/output')
        build.verify(args.module_package.resolve())
        engine.execute(args.cache.resolve(), args.output.resolve(), args.compiled.resolve(),
                       nvault_module=args.module_package.resolve() / 'testing/nvault_amxx_i386.so', process_storage=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
