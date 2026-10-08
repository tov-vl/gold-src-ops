#!/usr/bin/env python3
"""Offline failure injection for weekly scheduling, host recovery and uncertain delivery."""
import datetime as dt
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "ops/gameserver"))
import player_data_schedule as policy


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


game = load("game", "ops/gameserver/player-data-scheduled-capture.py")
control = load("control", "ops/production/player-data-scheduled.py")
SLOT = "2026-10-11"
WHEN = dt.datetime(2026, 10, 11, 1, tzinfo=dt.timezone.utc)


class FakeHost:
    def __init__(self, failure=None, occupancy=(True, True)):
        self.failure = failure
        self.occupancy = iter(occupancy)
        self.calls = []
        self.active = True
        self.agent_calls = 0

    def guard(self, stage):
        self.calls.append(stage)
        if self.failure == stage:
            raise ValueError("fixture failure")
        if stage == "quiescent":
            assert not self.active

    def state(self):
        return b"active" if self.active else b"inactive"

    def agent(self):
        self.agent_calls += 1
        return b"changed" if self.failure == "agent" and self.agent_calls > 1 else b"same"

    def empty(self):
        return next(self.occupancy)

    def stop(self):
        self.calls.append("stop")
        if self.failure == "stop-unknown":
            raise ValueError("fixture uncertain stop")
        self.active = False
        if self.failure == "stop-inactive":
            raise ValueError("fixture known inactive")

    def start(self):
        self.calls.append("start")
        if self.failure == "start":
            raise ValueError("fixture uncertain start")
        self.active = True

    def capture(self, path):
        self.calls.append("capture")
        assert not self.active
        if self.failure == "capture":
            raise ValueError("fixture failed capture")
        path.write_bytes(b"synthetic")
        return "a" * 64, WHEN.isoformat()

    def ready(self):
        self.calls.append("ready")
        if self.failure == "ready":
            raise ValueError("fixture failed readiness")


class FakeTransport:
    def __init__(self, data, state="captured", failure=None):
        self.data, self.state, self.failure = data, state, failure
        self.calls = []

    def remote(self, action, slot, maximum=16384):
        self.calls.append(action)
        if self.failure == "lost-reply":
            raise TimeoutError("fixture lost reply")
        if action == "bundle":
            return self.data if self.failure != "corrupt" else self.data[:-1]
        record = {"schema": 1, "slot": slot, "state": self.state,
                  "bundleSha256": control.bundle.digest(self.data),
                  "capturedUtc": WHEN.isoformat()}
        if self.failure == "wrong-slot":
            record["slot"] = "2026-10-18"
        return json.dumps(record).encode()

    def archive(self, directory, digest):
        self.calls.append("archive")
        if self.failure == "archive-unknown":
            raise TimeoutError("fixture uncertain upload")
        return {"action": "Archive", "state": "verified", "bundleSha256": digest,
                "snapshotId": "b" * 64}


class ScheduleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)
        self.ledger = policy.Ledger(self.root)
        self.clock = lambda: WHEN

    def tearDown(self):
        self.temp.cleanup()

    def archive_bytes(self):
        vault = self.root / "synthetic-vault"
        vault.mkdir(mode=0o700)
        for name in control.bundle.NAMES:
            if name.endswith(".vault"):
                (vault / name).write_bytes(b"synthetic")
        manifest, members = control.bundle.inspect(control.bundle.create(vault))
        manifest["captured_utc"] = WHEN.isoformat()
        members["manifest.json"] = json.dumps(manifest).encode()
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data in members.items():
                item = tarfile.TarInfo(name)
                item.size, item.mode = len(data), 0o600
                archive.addfile(item, io.BytesIO(data))
        return output.getvalue()

    def success_record(self, captured=WHEN):
        return {"schema": 1, "state": "verified", "workload": "goldsrcops-player-data-v1",
                "capturedUtc": captured.isoformat(), "bundleSha256": "a" * 64,
                "snapshotId": "b" * 64}

    def test_moscow_week_and_window_edges(self):
        self.assertEqual(policy.weekly_slot(WHEN), SLOT)
        self.assertEqual(policy.weekly_slot(WHEN + dt.timedelta(minutes=4, seconds=59)), SLOT)
        for moment in (WHEN - dt.timedelta(seconds=1), WHEN + dt.timedelta(minutes=5),
                       WHEN + dt.timedelta(days=1), WHEN - dt.timedelta(days=1)):
            self.assertIsNone(policy.weekly_slot(moment))
        self.assertEqual(policy.weekly_slot(WHEN + dt.timedelta(days=7)), "2026-10-18")

    def test_no_catch_up_or_outside_window_stop(self):
        host = FakeHost()
        with self.assertRaises(ValueError):
            game.capture(SLOT, self.ledger, host, lambda: WHEN + dt.timedelta(hours=1))
        self.assertEqual(host.calls, [])
        self.assertFalse(self.ledger.pending.exists())
        transport = FakeTransport(b"")
        self.assertEqual(control.cycle(self.ledger, transport,
                                      lambda: WHEN + dt.timedelta(hours=1))["state"], "skipped-window")
        self.assertEqual(transport.calls, [])

    def test_busy_and_last_moment_join_never_stop(self):
        for occupancy in ((False,), (True, False)):
            with self.subTest(occupancy=occupancy), tempfile.TemporaryDirectory() as directory:
                ledger = policy.Ledger(Path(directory))
                host = FakeHost(occupancy=occupancy)
                result = game.capture(SLOT, ledger, host, self.clock)
                self.assertEqual(result["state"], "skipped-busy")
                self.assertNotIn("stop", host.calls)
                self.assertFalse(ledger.pending.exists())

    def test_window_closes_during_preflight(self):
        ticks = iter((WHEN, WHEN + dt.timedelta(minutes=5)))
        host = FakeHost()
        result = game.capture(SLOT, self.ledger, host, lambda: next(ticks))
        self.assertEqual(result["state"], "skipped-window")
        self.assertNotIn("stop", host.calls)

    def test_one_capture_one_restart_duplicate_is_read_only(self):
        host = FakeHost()
        first = game.capture(SLOT, self.ledger, host, self.clock)
        self.assertEqual(first["state"], "captured")
        before = list(host.calls)
        self.assertEqual(game.capture(SLOT, self.ledger, host, self.clock), first)
        self.assertEqual(host.calls, before)
        self.assertEqual(host.calls.count("stop"), 1)
        self.assertEqual(host.calls.count("start"), 1)
        self.assertTrue(host.active)

    def test_failure_matrix_never_retries_uncertain_effect(self):
        for failure, starts in (("preflight", 0), ("stop-unknown", 0),
                                ("stop-inactive", 1), ("capture", 1),
                                ("start", 1), ("ready", 1), ("agent", 1)):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                ledger = policy.Ledger(Path(directory))
                host = FakeHost(failure)
                with self.assertRaises(ValueError):
                    game.capture(SLOT, ledger, host, self.clock)
                self.assertTrue(ledger.pending.exists())
                self.assertEqual(host.calls.count("start"), starts)
                before = list(host.calls)
                with self.assertRaises(ValueError):
                    game.capture("2026-10-18", ledger, host, lambda: WHEN + dt.timedelta(days=7))
                self.assertEqual(host.calls, before)

    def test_control_verified_bytes_and_no_duplicate_upload(self):
        transport = FakeTransport(self.archive_bytes())
        result = control.cycle(self.ledger, transport, self.clock)
        self.assertEqual(result["state"], "verified")
        self.assertEqual(result["engineAcceptance"], "pending")
        self.assertEqual(transport.calls, ["capture", "bundle", "archive"])
        self.assertEqual(control.cycle(self.ledger, transport, self.clock), result)
        self.assertEqual(transport.calls.count("archive"), 1)
        self.assertEqual(policy.freshness(self.root, WHEN)["state"], "fresh")

    def test_control_failure_is_persistent_across_weeks(self):
        data = self.archive_bytes()
        for failure in ("lost-reply", "wrong-slot", "corrupt", "archive-unknown"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                ledger = policy.Ledger(Path(directory))
                transport = FakeTransport(data, failure=failure)
                with self.assertRaises((ValueError, TimeoutError)):
                    control.cycle(ledger, transport, self.clock)
                before = list(transport.calls)
                with self.assertRaises(ValueError):
                    control.cycle(ledger, transport, lambda: WHEN + dt.timedelta(days=7))
                self.assertEqual(transport.calls, before)
                self.assertFalse((Path(directory) / "last-success.json").exists())

    def test_busy_does_not_renew_last_success(self):
        policy.write_json(self.root / "last-success.json", self.success_record(WHEN - dt.timedelta(days=7)))
        original = (self.root / "last-success.json").read_bytes()
        transport = FakeTransport(b"", state="skipped-busy")
        result = control.cycle(self.ledger, transport, self.clock)
        self.assertEqual(result["state"], "skipped-busy")
        self.assertEqual(transport.calls, ["capture"])
        self.assertEqual((self.root / "last-success.json").read_bytes(), original)

    def test_freshness_uses_capture_time_not_readback_time(self):
        policy.write_json(self.root / "last-success.json", self.success_record())
        self.assertEqual(policy.freshness(self.root, WHEN + dt.timedelta(days=8))["ageHours"], 192)
        for moment in (WHEN + dt.timedelta(days=8, seconds=1), WHEN - dt.timedelta(minutes=6)):
            with self.assertRaises(ValueError):
                policy.freshness(self.root, moment)
        policy.write_json(self.ledger.pending, {"state": "started"})
        with self.assertRaises(ValueError):
            policy.freshness(self.root, WHEN)

    def test_private_files_and_symlinks_are_enforced(self):
        target = self.root / "target.json"
        policy.write_json(target, {"schema": 1})
        link = self.root / "link.json"
        link.symlink_to(target)
        with self.assertRaises(ValueError):
            policy.read_json(link)
        target.chmod(0o644)
        with self.assertRaises(ValueError):
            policy.read_json(target)

    def test_serialization_and_existing_transition_metadata(self):
        shared = self.root / "shared"
        shared.mkdir(mode=0o750)
        path = shared / "transition.lock"
        path.touch(mode=0o640)
        with policy.lock(path, shared=True):
            with self.assertRaises(BlockingIOError):
                with policy.lock(path, shared=True):
                    pass
        path.chmod(0o660)
        with self.assertRaises(ValueError):
            with policy.lock(path, shared=True):
                pass

    def test_capacity_is_bounded_without_pruning(self):
        for index in range(policy.MAX_CYCLES):
            (self.root / ("2000-00-%02d" % index)).mkdir()
        with self.assertRaises(ValueError):
            self.ledger.claim(SLOT)

    def test_a2s_occupancy_and_identity_parser(self):
        prefix = b"\xff\xff\xff\xffI\x30Fixture\0de_dust2\0cstrike\0Counter-Strike\0\x0a\0"
        self.assertEqual(policy.parse_info(prefix + bytes((0, 16, 0)))[2:], (0, 0))
        self.assertEqual(policy.parse_info(prefix + bytes((1, 16, 0)))[2:], (1, 0))
        for packet in (b"", b"\xff\xff\xff\xff", prefix, prefix + bytes((17, 16, 0)),
                       prefix + bytes((0, 16, 1)), b"\xfe\xff\xff\xff" + b"x" * 40):
            with self.assertRaises(ValueError):
                policy.parse_info(packet)

    def test_a2s_endpoint_must_be_local_and_use_the_prepared_port(self):
        config = {"schema": 1, "host": "127.0.0.1", "port": 27015,
                  "expectedName": "Fixture", "vaultDirectory": "/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/vault"}
        addresses = json.dumps([{"addr_info": [{"family": "inet", "local": "127.0.0.1"}]}]).encode()
        with mock.patch.object(policy, "read_json", return_value=config), mock.patch.object(
                policy, "run", side_effect=[addresses, b"27015"]):
            self.assertEqual(game.Host().config["port"], 27015)
        for host, port in (("192.0.2.100", 27015), ("127.0.0.1", 27016)):
            config.update(host=host, port=port)
            with mock.patch.object(policy, "read_json", return_value=config), mock.patch.object(
                    policy, "run", side_effect=[addresses, b"27015"]), self.assertRaises(ValueError):
                game.Host()

    def test_remote_input_contract(self):
        for slot in ("2026-10-12", "../2026-10-11", SLOT + "; id", SLOT + "\n"):
            with self.assertRaises(ValueError):
                policy.valid_slot(slot)

    def test_manual_baseline_requires_verified_bound_bytes_and_is_create_only(self):
        data = self.archive_bytes()
        path = self.root / "manual.tar"
        control.bundle.write_new(path, data)
        operation_path = self.root / "manual.json"
        operation = {"action": "Archive", "state": "verified",
                     "bundleSha256": control.bundle.digest(data), "snapshotId": "c" * 64}
        policy.write_json(operation_path, operation)
        result = control.bootstrap(self.root, operation_path, path, self.clock)
        self.assertEqual(result["source"], "reviewed-manual-baseline")
        self.assertEqual(policy.freshness(self.root, WHEN)["state"], "fresh")
        with self.assertRaises(ValueError):
            control.bootstrap(self.root, operation_path, path, self.clock)

    def test_manual_baseline_rejects_unverified_or_corrupt_archive(self):
        data = self.archive_bytes()
        path = self.root / "manual.tar"
        control.bundle.write_new(path, data)
        operation_path = self.root / "manual.json"
        operation = {"action": "Archive", "state": "uploaded",
                     "bundleSha256": control.bundle.digest(data), "snapshotId": "c" * 64}
        policy.write_json(operation_path, operation)
        with self.assertRaises(ValueError):
            control.bootstrap(self.root, operation_path, path, self.clock)
        operation["state"] = "verified"
        operation["bundleSha256"] = "0" * 64
        policy.write_json(operation_path, operation, replace=True)
        with self.assertRaises(ValueError):
            control.bootstrap(self.root, operation_path, path, self.clock)
        self.assertFalse((self.root / "last-success.json").exists())

    def test_timer_has_exact_policy_and_no_automatic_retry(self):
        timer = (ROOT / "ops/production/systemd/goldsrcops-player-data-backup.timer").read_text()
        service = (ROOT / "ops/production/systemd/goldsrcops-player-data-backup.service").read_text()
        for line in ("OnCalendar=Sun *-*-* 04:00:00 Europe/Moscow", "Persistent=false",
                     "RandomizedDelaySec=0", "AccuracySec=1s"):
            self.assertIn(line, timer)
        self.assertIn("Restart=no", service)
        self.assertIn("player-data-scheduled.py status", service)
        self.assertNotIn("prune", service)
        self.assertNotIn("OnBootSec", timer)


if __name__ == "__main__":
    unittest.main(verbosity=2)
