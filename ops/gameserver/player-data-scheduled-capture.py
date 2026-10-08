#!/usr/bin/env python3
"""Restricted game-host endpoint. No caller-supplied paths or shell commands."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import sys
import time

DIRECTORY = Path(__file__).resolve().parent
sys.path.insert(0, str(DIRECTORY))
import player_data_schedule as policy

spec = importlib.util.spec_from_file_location("bundle", DIRECTORY / "player-data-bundle.py")
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

ROOT = Path("/var/lib/goldsrcops-player-backup")
CONFIG = Path("/etc/goldsrcops/gameserver/player-data-backup.json")
GAME = "goldsrcops-gameserver.service"
AGENT = "goldsrcops-game-event-agent.service"


class Host:
    def __init__(self):
        self.config = policy.read_json(CONFIG)
        policy.require(set(self.config) == {"schema", "host", "port", "expectedName", "vaultDirectory"})
        policy.require(self.config["schema"] == 1 and type(self.config["port"]) is int
                       and 1 <= self.config["port"] <= 65535)
        policy.require(self.config["vaultDirectory"] == "/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/vault")
        self.vault = Path(self.config["vaultDirectory"])
        addresses = json.loads(policy.run(["ip", "-j", "address", "show"], maximum=65536))
        local = {entry["local"] for interface in addresses for entry in interface["addr_info"]
                 if entry["family"] == "inet"}
        expected_port = policy.run(["bash", str(DIRECTORY / "player-data-schedule-guard.sh"), "endpoint"])
        policy.require(self.config["host"] in local and int(expected_port) == self.config["port"],
                       "A2S must target the prepared local game endpoint.")

    def guard(self, state):
        policy.run(["bash", str(DIRECTORY / "player-data-schedule-guard.sh"), state])

    def state(self):
        return policy.run(["systemctl", "show", GAME, "-p", "ActiveState", "--value"]).strip()

    def agent(self):
        return policy.run(["systemctl", "show", AGENT, "-p", "InvocationID", "--value"]).strip()

    def empty(self):
        return policy.probe(self.config)

    def stop(self):
        policy.run(["systemctl", "stop", GAME], timeout=60)

    def start(self):
        policy.run(["systemctl", "start", GAME], timeout=45)

    def capture(self, output):
        self.guard("quiescent")
        data = bundle.create(self.vault)
        bundle.write_new(output, data)
        self.guard("quiescent")
        manifest, members = bundle.inspect(data)
        policy.require(bundle.inventory(self.vault) == {name: members.get(name) for name in bundle.NAMES})
        return bundle.digest(data), manifest["captured_utc"]

    def ready(self):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                # Occupancy after restart is allowed; identity and A2S must answer.
                self.empty()
                break
            except (ValueError, OSError):
                time.sleep(2)
        else:
            raise ValueError("Game postflight failed.")
        self.guard("postflight")


def capture(slot, ledger, host, clock=policy.now):
    policy.require(policy.weekly_slot(clock()) == slot, "Outside the Sunday maintenance window.")
    cycle, existing = ledger.claim(slot)
    if existing:
        return existing
    record = policy.read_json(cycle / "result.json")
    stopped = False
    start_attempted = False
    try:
        host.guard("preflight")
        agent = host.agent()
        if not host.empty():
            record["state"] = "skipped-busy"
            ledger.finish(cycle, record)
            return record
        # Recheck time and occupancy immediately before the only stop call.
        if policy.weekly_slot(clock()) != slot:
            record["state"] = "skipped-window"
            ledger.finish(cycle, record)
            return record
        if not host.empty():
            record["state"] = "skipped-busy"
            ledger.finish(cycle, record)
            return record
        record["state"] = "stopping"
        policy.write_json(cycle / "result.json", record, replace=True)
        stopped = True
        started = time.monotonic()
        host.stop()
        digest, captured = host.capture(cycle / "player-data.tar")
        host.guard("quiescent")
        start_attempted = True
        host.start()
        duration = round(time.monotonic() - started, 3)
        host.ready()
        policy.require(host.agent() == agent, "Game agent changed during capture.")
        record.update(state="captured", bundleSha256=digest, capturedUtc=captured,
                      stopStartSeconds=duration, completedUtc=clock().isoformat())
        ledger.finish(cycle, record)
        return record
    finally:
        # A known stopped service can be restarted once even if capture failed.
        # An unknown stop/start result is retained for operator reconciliation.
        if stopped and not start_attempted and host.state() == b"inactive":
            host.guard("quiescent")
            host.start()


def main():
    policy.require(os.geteuid() == 0 and len(sys.argv) == 3)
    action, slot = sys.argv[1:]
    policy.require(action in ("capture", "status", "bundle"))
    policy.valid_slot(slot)
    ledger = policy.Ledger(ROOT)
    with policy.lock(ROOT / "schedule.lock"):
        if action == "capture":
            with policy.lock(Path("/etc/goldsrcops/gameserver/player-data-backup.lock"), shared=True):
                with policy.lock(Path("/etc/goldsrcops/gameserver/game-event-pilot-install.lock"), shared=True):
                    result = capture(slot, ledger, Host())
            print(json.dumps(result, separators=(",", ":")))
        else:
            result = policy.read_json(ROOT / slot / "result.json")
            policy.require(result["slot"] == slot)
            if action == "status":
                print(json.dumps(result, separators=(",", ":")))
            else:
                policy.require(result["state"] == "captured" and not ledger.pending.exists())
                data = bundle.read_regular(ROOT / slot / "player-data.tar", bundle.MAX_ARCHIVE)
                policy.require(bundle.digest(data) == result["bundleSha256"])
                bundle.inspect(data)
                sys.stdout.buffer.write(data)


if __name__ == "__main__":
    os.umask(0o077)
    def interrupted(_signal, _frame):
        raise RuntimeError("Capture interrupted; reconcile before retry.")
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGHUP, interrupted)
    try:
        main()
    except Exception:
        print("PLAYER_DATA_SCHEDULE_CAPTURE=failed_reconciliation_required", file=sys.stderr)
        sys.exit(1)
