#!/usr/bin/env python3
"""Reject false crash evidence and optionally exercise real journal loss."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('engine_ci', Path(__file__).with_name('engine-ci.py'))
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)


class CrashEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix='player-data-crash-test-')
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        (self.root/'process-kill.tsv').write_text('9\t137\n')
        for case in engine.CRASH_CASES:
            clean = case['command'] != 'goldsrcops_crash_write'
            (self.root/(case['command']+'.log')).write_text(
                case['markers'][0]+'=passed failures=0\n'+('PLAYER_DATA_PLUGIN_END=called\n' if clean else ''))
        for directory in ('baseline','post-crash'):
            (self.root/directory).mkdir()
            for name in ('goldsrcops-player-stats-v1','goldsrcops-player-preferences-v1'):
                (self.root/directory/(name+'.vault')).write_bytes(b'synthetic-checkpoint')
                if directory == 'post-crash':
                    (self.root/directory/(name+'.journal')).write_bytes(b'synthetic-journal')

    def test_complete_crash_chain_is_required(self):
        engine.validate_evidence(self.root, engine.CRASH_CASES)

    def test_clean_or_unknown_exit_cannot_pass_as_kill(self):
        for receipt in ('15\t143\n','9\t0\n','9\t137\n9\t137\n',''):
            (self.root/'process-kill.tsv').write_text(receipt)
            with self.subTest(receipt=receipt), self.assertRaises(ValueError):
                engine.validate_evidence(self.root,engine.CRASH_CASES)

    def test_graceful_close_in_crash_log_is_rejected(self):
        path=self.root/'goldsrcops_crash_write.log'
        path.write_text(path.read_text()+'PLAYER_DATA_PLUGIN_END=called\n')
        with self.assertRaises(ValueError): engine.validate_evidence(self.root,engine.CRASH_CASES)

    def test_every_recovery_marker_is_required_exactly_once(self):
        for case in engine.CRASH_CASES:
            path=self.root/(case['command']+'.log'); original=path.read_text()
            for text in ('',original*2,original.replace('failures=0','failures=1')):
                path.write_text(text)
                with self.subTest(command=case['command']), self.assertRaises(ValueError):
                    engine.validate_evidence(self.root,engine.CRASH_CASES)
            path.write_text(original)

    def test_checkpoint_updates_cannot_masquerade_as_journal_recovery(self):
        path=self.root/'post-crash/goldsrcops-player-stats-v1.vault'
        path.write_bytes(b'already-checkpointed-updates')
        with self.assertRaises(ValueError): engine.validate_evidence(self.root,engine.CRASH_CASES)

    def test_both_journals_must_be_present_and_bounded(self):
        for name in ('goldsrcops-player-stats-v1','goldsrcops-player-preferences-v1'):
            path=self.root/'post-crash'/(name+'.journal');original=path.read_bytes()
            for raw in (b'',b'header00',b'x'*(1024*1024+1)):
                path.write_bytes(raw)
                with self.subTest(name=name,size=len(raw)),self.assertRaises(ValueError):
                    engine.validate_evidence(self.root,engine.CRASH_CASES)
            path.unlink()
            with self.assertRaises(FileNotFoundError): engine.validate_evidence(self.root,engine.CRASH_CASES)
            path.write_bytes(original)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--integration',action='store_true')
    parser.add_argument('--cache',type=Path)
    parser.add_argument('--compiled',type=Path)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--nvault-module',type=Path)
    args=parser.parse_args()
    if not unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(CrashEvidenceTests)).wasSuccessful():
        return 1
    if args.integration:
        if not all((args.cache,args.compiled,args.output)):parser.error('Integration requires cache, compiled and output')
        for mode,name in (('--player-data-crash','positive'),('--discard-crash-journals-check','negative-journals')):
            output=args.output/name
            command=[sys.executable,'-B',str(engine.ROOT/'tools/smoke/engine-ci.py'),
                                    '--cache',str(args.cache),'--compiled',str(args.compiled),
                                    '--output',str(output),mode]
            if args.nvault_module:command+=['--nvault-module',str(args.nvault_module)]
            attempt=subprocess.run(command,timeout=720)
            evidence=output/'evidence';result=json.loads((evidence/'result.json').read_bytes())
            if name=='positive':
                if attempt.returncode or result['result']!='passed' or result['cleanup']!='passed':
                    raise RuntimeError('Process-crash recovery failed; inspect retained diagnostics')
                engine.validate_evidence(evidence,engine.CRASH_CASES)
            else:
                log=(evidence/'goldsrcops_crash_recover.log').read_text(errors='replace')
                if (not attempt.returncode or result['result']!='failed' or result['cleanup']!='passed'
                        or 'PLAYER_DATA_CRASH_ASSERTION_FAILED saved counters belong to the same Steam identity' not in log
                        or 'PLAYER_DATA_CRASH_ASSERTION_FAILED confirmed language and weapon choices recovered per identity' not in log):
                    raise RuntimeError('Missing journals did not fail the exact native recovery assertions')
            print('PLAYER_DATA_CRASH_GATE='+name+' passed',flush=True)
    return 0


if __name__=='__main__':sys.exit(main())
