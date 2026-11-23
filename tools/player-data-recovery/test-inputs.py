"""Fast synthetic tests with no image, network, or Docker executable."""

from argparse import Namespace
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

from check import bundle, check
from fixtures import fixture, vault

with patch.dict(sys.modules, {"bundle": bundle}):
    from load import MAX_ROWS, VaultRejected, expectations


class RecoveryInputTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="player-recovery-input-")
        self.addCleanup(temporary.cleanup)
        self.archive, self.digest = fixture(Path(temporary.name))
        self.source = self.archive.parent / "vault"

    def test_wrong_hash_rejected_without_staging_or_docker(self):
        with patch("check.stage_inputs") as stage, patch("check.docker_command") as docker:
            report = check(Namespace(bundle=self.archive, expected_sha256="0" * 64))

        self.assertEqual("input_hash_mismatch", report["reason"])
        self.assertFalse(report["containerStarted"])
        stage.assert_not_called()
        docker.assert_not_called()
        for secret in (str(self.archive), self.digest, "STEAM_0"):
            self.assertNotIn(secret, json.dumps(report))

    def test_wrong_vault_hash_rejected_even_with_matching_tar_hash(self):
        output = io.BytesIO()
        with tarfile.open(self.archive, "r:") as original, tarfile.open(fileobj=output, mode="w") as altered:
            for entry in original:
                data = original.extractfile(entry).read()
                if entry.name == bundle.STEMS[0] + ".vault":
                    data = data[:-1] + bytes([data[-1] ^ 1])
                altered.addfile(entry, io.BytesIO(data))

        self.archive.write_bytes(output.getbuffer())
        with patch("check.stage_inputs") as stage:
            report = check(Namespace(bundle=self.archive, expected_sha256=bundle.digest(output.getvalue())))

        self.assertTrue(report["inputHashVerified"])
        self.assertFalse(report["vaultHashesVerified"])
        self.assertFalse(report["containerStarted"])
        stage.assert_not_called()

    def test_expected_rows_and_tamper_control(self):
        raw = self.archive.read_bytes()
        normal = expectations(raw, mismatch=False).splitlines()
        changed = expectations(raw, mismatch=True).splitlines()
        self.assertEqual(b"5", normal[0])
        self.assertEqual(6, len(normal))
        self.assertNotEqual(normal[1], changed[1])
        self.assertEqual(normal[2:], changed[2:])

    def test_empty_closed_vaults(self):
        for stem in bundle.STEMS:
            (self.source / (stem + ".vault")).write_bytes(vault([]))
        self.assertEqual(b"0\n", expectations(bundle.create(self.source), mismatch=False))

    def test_nonempty_journal_rejected(self):
        (self.source / (bundle.STEMS[1] + ".journal")).write_bytes(b"\x01")
        with self.assertRaisesRegex(VaultRejected, "^nonempty_journal$"):
            expectations(bundle.create(self.source), mismatch=False)

    def test_combined_record_limit(self):
        entries = [(f"v1:STEAM_0:1:{i}".encode(), b"1 25 2") for i in range(MAX_ROWS)]
        (self.source / (bundle.STEMS[0] + ".vault")).write_bytes(vault(entries))
        with self.assertRaisesRegex(VaultRejected, "^vault_record_limit_exceeded$"):
            expectations(bundle.create(self.source), mismatch=False)


if __name__ == "__main__":
    unittest.main()
