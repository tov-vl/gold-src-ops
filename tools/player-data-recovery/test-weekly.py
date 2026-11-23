"""Tests for the receipt-driven weekly player recovery check."""

import argparse
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

from check import add_runtime_arguments, bundle
from fixtures import fixture


HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("check_weekly", HERE / "check-weekly.py")
weekly_check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(weekly_check)
SLOT = "2026-11-22"
CAPTURE = SLOT + "T01:00:18+00:00"
SNAPSHOT = "a" * 64


def fixture_receipts(directory, capture=CAPTURE):
    synthetic, _ = fixture(directory)
    recovered = directory / "recover" / "readback"
    recovered.mkdir(parents=True)
    output = io.BytesIO()
    with tarfile.open(synthetic, "r:") as original, tarfile.open(fileobj=output, mode="w") as modified:
        for entry in original:
            raw = original.extractfile(entry).read()
            if entry.name == "manifest.json":
                manifest = json.loads(raw)
                manifest["captured_utc"] = capture
                raw = json.dumps(manifest, separators=(",", ":")).encode()
                entry.size = len(raw)
            modified.addfile(entry, io.BytesIO(raw))
    data = output.getvalue()
    (recovered / "player-data.tar").write_bytes(data)
    digest = bundle.digest(data)
    weekly = {"schema": 1, "slot": SLOT, "workload": "goldsrcops-player-data-v1", "state": "verified",
              "snapshotId": SNAPSHOT, "bundleSha256": digest, "startedUtc": SLOT + "T01:00:00+00:00",
              "capturedUtc": CAPTURE, "completedUtc": SLOT + "T01:01:00+00:00"}
    recover = {"schemaVersion": 1, "action": "Recover", "state": "verified", "snapshotId": SNAPSHOT,
               "bundleSha256": digest, "startedUtc": SLOT + "T03:03:00.0000000+00:00",
               "completedUtc": SLOT + "T03:03:05.0421836+00:00"}
    slot_file = directory / "weekly.json"
    operation = directory / "recover" / "operation.json"
    save(slot_file, weekly)
    save(operation, recover)
    return slot_file, operation, weekly, recover


def save(path, value):
    path.write_text(json.dumps(value), encoding="utf-8")


class WeeklyReceiptTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="weekly-recovery-tests-")
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        slot_file, operation, self.weekly, self.recover = fixture_receipts(self.root)
        self.args = argparse.Namespace(slot=SLOT, weekly_result=slot_file, recover_receipt=operation,
                                       engine_image_id="sha256:" + "b" * 64)

    def check(self):
        with patch("check.stage_inputs") as stage, patch("check.run_engine") as engine:
            result = weekly_check.check_weekly(self.args)
        if result["result"] != "passed":
            stage.assert_not_called()
            engine.assert_not_called()
        else:
            stage.assert_called_once()
            engine.assert_called_once()
        safe = json.dumps(result)
        for value in (str(self.root), SNAPSHOT, self.weekly["bundleSha256"], "STEAM_0"):
            self.assertNotIn(value, safe)
        return result

    def rejected(self, reason):
        report = self.check()
        self.assertEqual("failed", report["result"])
        self.assertEqual(reason, report["reason"])
        self.assertFalse(report["receiptBindingVerified"])

    def test_exact_receipts_allow_engine(self):
        self.assertTrue(self.check()["receiptBindingVerified"])

    def test_selecting_neighboring_slot_is_rejected(self):
        self.args.slot = "2026-11-29"
        self.rejected("weekly_slot_mismatch")

    def test_readback_capture_matches_receipt_and_not_only_its_hash(self):
        other = self.root / "other"
        other.mkdir()
        slot_file, operation, _, _ = fixture_receipts(other, "2026-11-15T01:00:18+00:00")
        self.args.weekly_result = slot_file
        self.args.recover_receipt = operation
        self.rejected("capture_timestamp_mismatch")

    def test_snapshot_disagreement(self):
        self.recover["snapshotId"] = "c" * 64
        save(self.args.recover_receipt, self.recover)
        self.rejected("snapshot_mismatch")

    def test_incomplete_recover(self):
        self.recover["state"] = "started"
        save(self.args.recover_receipt, self.recover)
        self.rejected("recover_receipt_unverified")

    def test_receipt_sha_disagreement(self):
        self.recover["bundleSha256"] = "d" * 64
        save(self.args.recover_receipt, self.recover)
        self.rejected("receipt_sha256_mismatch")

    def test_duplicate_keys(self):
        data = self.args.recover_receipt.read_text(encoding="utf-8")
        self.args.recover_receipt.write_text('{"state":"started",' + data[1:], encoding="utf-8")
        self.rejected("receipt_contract_invalid")

    def test_replaced_tar(self):
        tar = self.args.recover_receipt.parent / "readback" / "player-data.tar"
        tar.write_bytes(tar.read_bytes() + b"x")
        self.rejected("input_hash_mismatch")

    def test_invalid_chronology(self):
        self.recover["startedUtc"] = SLOT + "T01:00:00+00:00"
        save(self.args.recover_receipt, self.recover)
        self.rejected("receipt_chronology_invalid")


def integration(args):
    with tempfile.TemporaryDirectory(prefix="weekly-recovery-smoke-") as tmp:
        slot_file, operation, _, _ = fixture_receipts(Path(tmp))
        command = [sys.executable, "-B", str(HERE / "check-weekly.py"), "--slot", SLOT,
                   "--weekly-result", str(slot_file), "--recover-receipt", str(operation)]
        for name in ("stable_zip", "assets", "steam_client", "engine_image_id", "docker_context"):
            command.extend(("--" + name.replace("_", "-"), str(getattr(args, name))))
        proc = subprocess.run(command, capture_output=True, timeout=260, check=False)
        report = json.loads(proc.stdout)
        print(json.dumps(report, indent=2))
        assert proc.returncode == 0 and report["receiptBindingVerified"] and report["containerRemoved"]
        assert [epoch["rows"] for epoch in report["epochs"]] == [5, 5]


if __name__ == "__main__":
    if "--integration" in sys.argv:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("--integration", action="store_true", required=True)
        add_runtime_arguments(parser)
        integration(parser.parse_args())
    else:
        unittest.main()
