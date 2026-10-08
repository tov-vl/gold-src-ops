#!/usr/bin/env python3
"""Offline adversarial checks for the private player-data archive boundary."""
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("bundle", ROOT / "ops/gameserver/player-data-bundle.py")
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)


class BundleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.vault = self.root / "source"
        self.vault.mkdir()
        for name in bundle.NAMES:
            (self.vault / name).write_bytes(b"\x00\xff\r\n" + name.encode())
        (self.vault / "unrelated.vault").write_bytes(b"excluded")

    def tearDown(self):
        self.temp.cleanup()

    def archive(self, edit):
        data = bundle.create(self.vault)
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            entries = [(x, archive.extractfile(x).read()) for x in archive]
        entries = edit(entries)
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for info, content in entries:
                info.size = len(content)
                archive.addfile(info, io.BytesIO(content))
        return output.getvalue()

    def test_exact_roundtrip_including_nonempty_journals(self):
        before = bundle.inventory(self.vault)
        data = bundle.create(self.vault)
        bundle.restore(data, self.root / "restored")
        self.assertEqual(before, bundle.inventory(self.root / "restored"))
        self.assertEqual(before, bundle.inventory(self.vault))
        self.assertEqual(set(x.name for x in (self.root / "restored").iterdir()), set(bundle.NAMES))
        if os.name != "nt":
            self.assertEqual((self.root / "restored").stat().st_mode & 0o777, 0o700)
            self.assertTrue(all(x.stat().st_mode & 0o777 == 0o600 for x in (self.root / "restored").iterdir()))

    def test_absent_journal_is_not_an_empty_journal(self):
        (self.vault / bundle.NAMES[1]).unlink()
        (self.vault / bundle.NAMES[3]).write_bytes(b"")
        data = bundle.create(self.vault)
        bundle.restore(data, self.root / "restored")
        self.assertFalse((self.root / "restored" / bundle.NAMES[1]).exists())
        self.assertEqual((self.root / "restored" / bundle.NAMES[3]).read_bytes(), b"")

    def test_missing_vault(self):
        (self.vault / bundle.NAMES[2]).unlink()
        with self.assertRaises(ValueError):
            bundle.create(self.vault)

    def test_changed_set(self):
        original = bundle.inventory(self.vault)
        changed = dict(original)
        changed[bundle.NAMES[1]] += b"new"
        with patch.object(bundle, "inventory", side_effect=[original, changed]):
            with self.assertRaises(ValueError):
                bundle.create(self.vault)

    def test_existing_destination_and_source_refused(self):
        data = bundle.create(self.vault)
        for target in (self.vault, self.root):
            with self.assertRaises(FileExistsError):
                bundle.restore(data, target)

    def test_no_overwrite_archive(self):
        path = self.root / "archive.tar"
        bundle.write_new(path, b"first")
        with self.assertRaises(FileExistsError):
            bundle.write_new(path, b"second")
        self.assertEqual(path.read_bytes(), b"first")

    def test_hardlink_refused(self):
        os.link(self.vault / bundle.NAMES[0], self.root / "linked")
        with self.assertRaises(ValueError):
            bundle.create(self.vault)

    @unittest.skipIf(os.name == "nt", "Symlink privilege varies on Windows; required in Linux CI")
    def test_symlink_source_parent_and_target_refused(self):
        data = bundle.create(self.vault)
        (self.root / "alias").symlink_to(self.vault, target_is_directory=True)
        with self.assertRaises(ValueError):
            bundle.create(self.root / "alias")
        with self.assertRaises(ValueError):
            bundle.restore(data, self.root / "alias" / "new")
        (self.vault / bundle.NAMES[0]).unlink()
        (self.vault / bundle.NAMES[0]).symlink_to(self.root / "absent")
        with self.assertRaises(ValueError):
            bundle.create(self.vault)

    def test_corrupt_payload(self):
        data = self.archive(lambda e: e[:1] + [(e[1][0], b"corrupt")] + e[2:])
        with self.assertRaises(ValueError):
            bundle.restore(data, self.root / "new")
        self.assertFalse((self.root / "new").exists())

    def test_missing_member(self):
        with self.assertRaises(ValueError):
            bundle.inspect(self.archive(lambda e: e[:-1]))

    def test_duplicate_member(self):
        with self.assertRaises(ValueError):
            bundle.inspect(self.archive(lambda e: e + [e[1]]))

    def test_unknown_schema(self):
        def change(entries):
            manifest = json.loads(entries[0][1])
            manifest["schema"] = 2
            return [(entries[0][0], json.dumps(manifest).encode())] + entries[1:]
        with self.assertRaises(ValueError):
            bundle.inspect(self.archive(change))

    def test_paths_and_links_refused(self):
        for name, kind in (("../escaped", tarfile.REGTYPE), ("/absolute", tarfile.REGTYPE),
                           ("unknown", tarfile.REGTYPE), (bundle.NAMES[0], tarfile.SYMTYPE),
                           (bundle.NAMES[0], tarfile.LNKTYPE), (bundle.NAMES[0], tarfile.FIFOTYPE)):
            def change(entries):
                entries[1][0].name = name
                entries[1][0].type = kind
                return entries
            with self.subTest(name=name, kind=kind), self.assertRaises(ValueError):
                bundle.inspect(self.archive(change))

    def test_bounds_and_truncation(self):
        with patch.object(bundle, "MAX_FILE", 1), self.assertRaises(ValueError):
            bundle.create(self.vault)
        data = bundle.create(self.vault)
        with patch.object(bundle, "MAX_ARCHIVE", 1), self.assertRaises(ValueError):
            bundle.inspect(data)
        with self.assertRaises((tarfile.TarError, ValueError)):
            bundle.inspect(data[:700])


if __name__ == "__main__":
    unittest.main()
