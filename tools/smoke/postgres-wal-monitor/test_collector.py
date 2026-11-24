"""Boundary checks for counts, missing observations and the collector deadline."""

import os
import tempfile
import time
import unittest
from pathlib import Path

from collector import collect, read_with_deadline


class CollectorTests(unittest.TestCase):
    def test_observation_uses_only_regular_ready_files_on_the_wal_filesystem(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            status = root / 'archive_status'
            status.mkdir()
            for name, ts in (('000000010000000000000001.ready', 10), ('000000010000000000000002.ready', 30),
                             ('000000010000000000000003.done', 1), ('not-wal.ready', 1)):
                path = status / name
                path.touch()
                os.utime(path, (ts, ts))
            (status / '000000010000000000000004.ready').symlink_to(status / 'not-wal.ready')
            values = collect(root, 135)
            disk = os.statvfs(root)
            self.assertEqual(values, dict(ready_segments=2, oldest_ready_age_seconds=125,
                                          filesystem_available_bytes=disk.f_bavail * disk.f_frsize))

    def test_missing_directory_fails_and_empty_directory_reports_empty(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(FileNotFoundError):
                collect(root, 135)
            (root / 'archive_status').mkdir()
            values = collect(root, 135)
            self.assertEqual((values['ready_segments'], values['oldest_ready_age_seconds']), (0, 0))

    def test_deadline_and_next_observation(self):
        with self.assertRaises(TimeoutError):
            read_with_deadline(lambda: time.sleep(1), 0.01)
        self.assertEqual(read_with_deadline(lambda: 'fresh', 0.5), 'fresh')


if __name__ == '__main__':
    unittest.main(verbosity=2)
