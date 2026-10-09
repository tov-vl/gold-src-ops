#!/usr/bin/env python3
"""Offline ledger, failure, privacy and transport tests; no remote backup calls."""
import copy
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("monitor", ROOT / "ops/production/player-data-monitor.py")
monitor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(monitor)
WHEN = dt.datetime(2026, 10, 11, 1, tzinfo=dt.timezone.utc)
ENABLED = (WHEN - dt.timedelta(days=2)).isoformat()
SLOT = "2026-10-11"
UNITS = {
    "timer": {"LoadState": "loaded", "ActiveState": "active", "UnitFileState": "enabled",
              "Persistent": "no", "TimersCalendar": "{ OnCalendar=Sun *-*-* 04:00:00 Europe/Moscow ; next_elapse=Sun 2026-10-18 01:00:00 UTC }"},
    "service": {"LoadState": "loaded", "ActiveState": "inactive", "Result": "success"}}


class MonitorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.root.chmod(0o700)
        self.units = copy.deepcopy(UNITS)
        self.success()

    def tearDown(self):
        self.temp.cleanup()

    def write(self, path, record):
        path = self.root / path
        path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        path.write_text(json.dumps(record))
        path.chmod(0o600)

    def success(self, captured=WHEN - dt.timedelta(days=3)):
        record = {"schema": 1, "state": "verified", "workload": "goldsrcops-player-data-v1",
                  "capturedUtc": captured.isoformat(), "bundleSha256": "a" * 64, "snapshotId": "b" * 64}
        self.write("last-success.json", record)
        return record

    def cycle(self, state="skipped-busy", pending=False):
        record = {"schema": 1, "state": state, "slot": SLOT, "startedUtc": WHEN.isoformat()}
        if state in ("verified", "skipped-busy", "skipped-window"):
            record["completedUtc"] = (WHEN + dt.timedelta(seconds=20)).isoformat()
        if state == "verified":
            record.update(self.success(WHEN + dt.timedelta(seconds=10)))
        self.write(SLOT + "/result.json", record)
        if pending:
            self.write("pending.json", {"schema": 1, "state": "started", "slot": SLOT, "startedUtc": WHEN.isoformat()})
        return record

    def observe(self, moment=WHEN + dt.timedelta(minutes=20), enabled=ENABLED):
        return monitor.observe(self.root, self.units, moment, enabled)

    def test_initial_baseline_has_no_historical_missed_cycle(self):
        data = self.observe(WHEN - dt.timedelta(days=1))
        self.assertEqual(data["observation_success"], 1)
        self.assertEqual(data["missed_cycle"], 0)
        self.assertEqual(data["fresh"], 1)
        self.assertEqual(data["last_attempt_status"], 0)

    def test_busy_and_skipped_window_do_not_renew_capture(self):
        for state in ("skipped-busy", "skipped-window"):
            self.cycle(state)
            data = self.observe()
            self.assertEqual(data["observation_success"], 1)
            self.assertEqual(data["last_attempt_status"], monitor.STATES[state])
            self.assertEqual(data["last_success_timestamp_seconds"], (WHEN - dt.timedelta(days=3)).timestamp())
            self.assertEqual(data["missed_cycle"], 0)
            self.assertEqual(data["requires_reconciliation"], 0)

    def test_verified_cycle_matches_last_success_identity(self):
        record = self.cycle("verified")
        self.assertEqual(self.observe()["last_attempt_status"], 1)
        record["snapshotId"] = "c" * 64
        self.write(SLOT + "/result.json", record)
        self.assert_unknown()

    def test_freshness_boundary_and_missing_copy(self):
        self.cycle()
        self.success(WHEN - dt.timedelta(days=8))
        self.assertEqual(self.observe(WHEN)["fresh"], 1)
        self.assertEqual(self.observe(WHEN + dt.timedelta(seconds=1))["fresh"], 0)
        (self.root / "last-success.json").unlink()
        data = self.observe()
        self.assertEqual(data["observation_success"], 1)
        self.assertEqual(data["copy_present"], 0)

    def test_running_claim_and_orphan_require_different_action(self):
        for state in ("started", "archiving"):
            self.cycle(state, pending=True)
            self.units["service"]["ActiveState"] = "activating"
            data = self.observe()
            self.assertEqual(data["observation_success"], 1)
            self.assertEqual(data["in_progress"], 1)
            self.assertEqual(data["requires_reconciliation"], 0)
            self.units["service"]["ActiveState"] = "failed"
            self.units["service"]["Result"] = "timeout"
            data = self.observe()
            self.assertEqual(data["requires_reconciliation"], 1)
            self.assertEqual(data["service_failed"], 1)
            self.assertEqual(data["last_attempt_status"], 5)

    def test_stuck_active_attempt_is_not_perpetually_healthy(self):
        self.cycle("started", pending=True)
        self.units["service"]["ActiveState"] = "activating"
        data = self.observe(WHEN + dt.timedelta(minutes=66))
        self.assertEqual(data["in_progress"], 0)
        self.assertEqual(data["requires_reconciliation"], 1)

    def test_pending_after_finish_is_reconciled_when_service_exits(self):
        self.cycle("verified", pending=True)
        self.assertEqual(self.observe()["requires_reconciliation"], 1)
        (self.root / "pending.json").unlink()
        self.assertEqual(self.observe()["requires_reconciliation"], 0)

    def test_failed_service_without_claim_is_visible(self):
        self.units["service"].update(ActiveState="failed", Result="exit-code")
        data = self.observe()
        self.assertEqual(data["service_failed"], 1)
        self.assertEqual(data["missed_cycle"], 1)
        self.assertEqual(data["last_attempt_status"], 6)

    def test_timer_disabled_stopped_catchup_or_extra_schedule(self):
        for key, value in (("ActiveState", "inactive"), ("UnitFileState", "disabled"), ("Persistent", "yes"),
                           ("TimersCalendar", "OnCalendar=Mon *-*-* 04:00:00 Europe/Moscow ;"),
                           ("TimersCalendar", UNITS["timer"]["TimersCalendar"] * 2)):
            self.units = copy.deepcopy(UNITS)
            self.units["timer"][key] = value
            data = self.observe()
            self.assertEqual(data["observation_success"], 1)
            self.assertEqual(data["timer_enabled"], 0)

    def test_missed_cycle_grace_moscow_boundary_and_enable_time(self):
        self.assertEqual(self.observe(WHEN + dt.timedelta(minutes=10))["missed_cycle"], 0)
        self.assertEqual(self.observe(WHEN + dt.timedelta(minutes=10, seconds=1))["missed_cycle"], 1)
        self.assertEqual(self.observe(enabled=(WHEN + dt.timedelta(minutes=1)).isoformat())["missed_cycle"], 0)
        self.assertEqual(monitor.last_due(WHEN - dt.timedelta(seconds=1)), WHEN - dt.timedelta(days=7))
        self.assertEqual(monitor.last_due(WHEN), WHEN)

    def assert_unknown(self):
        data = self.observe()
        self.assertEqual(data["observation_success"], 0)
        self.assertEqual(data["last_attempt_status"], 7)
        self.assertEqual(data["copy_present"], 0)
        self.assertEqual(data["observed_timestamp_seconds"], (WHEN + dt.timedelta(minutes=20)).timestamp())

    def test_invalid_or_future_records_never_publish_partial_green(self):
        for record in ({}, {"schema": 9}, self.success(WHEN + dt.timedelta(hours=1))):
            self.write("last-success.json", record)
            self.assert_unknown()
        self.success()
        self.cycle("started")
        self.write("pending.json", {"schema": 1, "state": "started", "slot": "2026-10-18", "startedUtc": WHEN.isoformat()})
        self.assert_unknown()

    def test_empty_pending_and_capacity_overflow_are_not_healthy(self):
        self.write("pending.json", {})
        self.assert_unknown()
        (self.root / "pending.json").unlink()
        self.cycle()
        for week in range(1, 53):
            date = (WHEN - dt.timedelta(weeks=week)).date().isoformat()
            (self.root / date).mkdir(mode=0o700)
        self.assert_unknown()

    def test_missing_units_and_running_without_claim_are_unknown(self):
        self.units = {}
        self.assert_unknown()
        self.units = copy.deepcopy(UNITS)
        self.units["service"]["ActiveState"] = "activating"
        self.assert_unknown()

    def test_private_permissions_links_duplicates_and_bounds(self):
        path = self.root / "last-success.json"
        path.chmod(0o644)
        self.assert_unknown()
        path.unlink()
        path.symlink_to(self.root / "missing")
        self.assert_unknown()
        path.unlink()
        for raw in ('{"schema":1,"schema":1}', '{"x":NaN}', "x" * (monitor.MAX_JSON + 1)):
            path.write_text(raw)
            path.chmod(0o600)
            self.assert_unknown()
        self.success()
        os.link(path, self.root / "linked")
        self.assert_unknown()

    def test_file_replacement_during_read_becomes_unknown(self):
        original = os.fstat
        calls = []
        def changed(fd):
            result = original(fd)
            calls.append(result)
            if len(calls) == 2:
                self.success(WHEN - dt.timedelta(days=1))
            return result
        with mock.patch.object(monitor.os, "fstat", side_effect=changed):
            self.assert_unknown()

    def test_observation_does_not_lock_or_write_ledger(self):
        self.cycle()
        def inventory():
            return {str(p.relative_to(self.root)): (p.read_bytes(), p.stat().st_mtime_ns)
                    for p in self.root.rglob("*") if p.is_file()}
        before = inventory()
        with mock.patch.object(monitor.subprocess, "run", side_effect=AssertionError("unexpected command")):
            self.assertEqual(self.observe()["observation_success"], 1)
        self.assertEqual(inventory(), before)

    def test_transport_contains_only_fixed_numeric_projection(self):
        self.cycle("verified")
        body = monitor.payload(self.observe())
        document = json.loads(body)
        self.assertLess(len(body), monitor.MAX_JSON)
        self.assertNotIn(b"a" * 64, body)
        self.assertNotIn(b"b" * 64, body)
        metrics = document["resourceMetrics"][0]["scopeMetrics"][0]["metrics"]
        self.assertEqual({m["name"] for m in metrics}, {monitor.PREFIX + k for k in monitor.KEYS})
        self.assertTrue(all("attributes" not in m["gauge"]["dataPoints"][0] for m in metrics))
        with self.assertRaises(ValueError):
            monitor.payload(dict(self.observe(), injected=1))

    def test_transport_rejects_partial_failure_redirect_and_large_reply_without_retry(self):
        for status, reply, content in ((200, b"{}", "application/json"),
                (200, b'{"partialSuccess":{"rejectedDataPoints":"1"}}', "application/json"),
                (200, b'{"partialSuccess":{"errorMessage":"dropped"}}', "application/json"),
                (200, b"x" * 4097, "application/json"), (302, b"{}", "application/json"),
                (200, b"{}", "text/html")):
            with self.subTest(status=status, reply=reply[:80]), mock.patch.object(monitor.http.client, "HTTPConnection") as client:
                response = client.return_value.getresponse.return_value
                response.status = status
                response.read.return_value = reply
                response.getheader.return_value = content
                if (status, reply, content) == (200, b"{}", "application/json"):
                    monitor.publish("172.28.0.2", b"{}")
                else:
                    with self.assertRaises(ValueError):
                        monitor.publish("172.28.0.2", b"{}")
                self.assertEqual(client.return_value.request.call_count, 1)
                client.return_value.close.assert_called_once()

    def test_config_rejects_public_dns_and_future_baseline(self):
        path = self.root / "config.json"
        with mock.patch.object(monitor, "CONFIG", path):
            for address in ("127.0.0.1", "8.8.8.8", "collector.local", "::1", "169.254.1.1"):
                self.write("config.json", {"schema": 1, "collectorAddress": address, "enabledSinceUtc": ENABLED})
                with self.assertRaises(ValueError):
                    monitor.configuration(WHEN)
            self.write("config.json", {"schema": 1, "collectorAddress": "172.28.0.2", "enabledSinceUtc": ENABLED})
            self.assertEqual(monitor.configuration(WHEN)["collectorAddress"], "172.28.0.2")
            self.write("config.json", {"schema": 1, "collectorAddress": "172.28.0.2",
                                       "enabledSinceUtc": (WHEN + dt.timedelta(hours=1)).isoformat()})
            with self.assertRaises(ValueError):
                monitor.configuration(WHEN)

    def test_only_bounded_read_only_systemctl_is_executed(self):
        with mock.patch.object(monitor.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, b"LoadState=loaded\n", b"")) as run:
            self.assertEqual(monitor.properties(monitor.TIMER, ["LoadState"]), {"LoadState": "loaded"})
            self.assertEqual(run.call_args.args[0], ["/usr/bin/systemctl", "show", monitor.TIMER, "--property=LoadState"])
            self.assertEqual(run.call_args.kwargs["timeout"], 5)


if __name__ == "__main__":
    unittest.main()
