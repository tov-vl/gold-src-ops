import json
from pathlib import Path
import tempfile
import unittest

from commit_coverage import FRESHNESS_SECONDS, MAX_SNAPSHOT_BYTES, collect, evaluate


def observation(through=100, commits=(), latest=None):
    rows = [dict(sequence=index, committed_epoch=committed, wal_upper_bound=offset)
            for index, committed, offset in commits]
    return dict(schema=1, observed_epoch=1000, verified_through=through,
                latest_sequence=latest if latest is not None else (rows[-1]['sequence'] if rows else 0),
                witnesses=rows)


class CommitCoverageTests(unittest.TestCase):
    def test_idle_is_known_with_no_pending_writes(self):
        self.assertEqual(evaluate(observation(), 1000), dict(known=1, pending_witnesses=0,
                                                             oldest_uncovered_age_seconds=0))

    def test_closed_chain_not_queue_size_sets_age(self):
        rows = [(1, 670, 70), (2, 760, 110), (3, 990, 190)]
        self.assertEqual(evaluate(observation(commits=rows), 1000)['oldest_uncovered_age_seconds'], 240)
        self.assertEqual(evaluate(observation(110, rows), 1000)['oldest_uncovered_age_seconds'], 10)
        self.assertEqual(evaluate(observation(190, rows), 1000)['pending_witnesses'], 0)

    def test_freshness_boundary_and_future_rejection(self):
        evaluate(observation(), 1000 + FRESHNESS_SECONDS)
        with self.assertRaises(ValueError):
            evaluate(observation(), 1000 + FRESHNESS_SECONDS + 0.001)
        with self.assertRaises(ValueError):
            evaluate(observation(), 997)

    def test_missing_or_bad_input_exports_only_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'snapshot.json'
            self.assertEqual(collect(path, 1000), {'known': 0})
            for data in (b'{', b'\xff', b' ' * (MAX_SNAPSHOT_BYTES + 1),
                         json.dumps(observation(latest=1)).encode(),
                         json.dumps(observation(commits=[(2, 980, 110), (1, 990, 120)])).encode(),
                         json.dumps(observation(commits=[(1, float('nan'), 110)])).encode(),
                         json.dumps(observation(commits=[(1, 1001, 110)])).encode(),
                         json.dumps(observation(commits=[(1, 950, 110), (2, 940, 120)])).encode(),
                         json.dumps(observation(commits=[(1, 950, 110), (2, 960, 100)])).encode()):
                with self.subTest(data=data[:100]):
                    path.write_bytes(data)
                    self.assertEqual(collect(path, 1000), {'known': 0})
            path.write_text(json.dumps(observation(commits=[(9, 800, 180)])), encoding='utf-8')
            self.assertEqual(collect(path, 1000)['oldest_uncovered_age_seconds'], 200)
            self.assertEqual(collect(path, 1026), {'known': 0})


if __name__ == '__main__':
    unittest.main()
