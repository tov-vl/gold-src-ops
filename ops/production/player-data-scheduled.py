#!/usr/bin/env python3
"""Weekly control-plane cycle using the existing encrypted restic implementation."""
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import re
import sys

DIRECTORY = Path(__file__).resolve().parent
GAME_DIRECTORY = DIRECTORY.parent / "gameserver"
sys.path.insert(0, str(GAME_DIRECTORY))
import player_data_schedule as policy

spec = importlib.util.spec_from_file_location("bundle", GAME_DIRECTORY / "player-data-bundle.py")
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

ROOT = Path("/var/lib/goldsrcops/player-data-backup")
SSH_CONFIG = Path("/etc/goldsrcops/player-data-backup/ssh_config")


class Transport:
    def remote(self, action, slot, maximum=16384):
        policy.require(action in ("capture", "status", "bundle"))
        policy.valid_slot(slot)
        policy.private_path(SSH_CONFIG)
        return policy.run(["ssh", "-F", str(SSH_CONFIG), "-T",
                           "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
                           "-o", "IdentitiesOnly=yes", "-o", "ConnectionAttempts=1",
                           "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15",
                           "-o", "ServerAliveCountMax=3", "-o", "ClearAllForwardings=yes",
                           "goldsrcops-player-backup", action + " " + slot],
                          timeout=300, maximum=maximum)

    def archive(self, cycle, digest):
        policy.run(["pwsh", "-NoLogo", "-NoProfile", "-File",
                    str(DIRECTORY / "player-data-backup.ps1"), "-Action", "Archive",
                    "-EnvironmentFile", "/etc/goldsrcops/deployment.env",
                    "-BundlePath", str(cycle / "player-data.tar"),
                    "-ExpectedSha256", digest,
                    "-EvidenceDirectory", str(cycle / "archive-evidence")],
                   timeout=3000)
        return policy.read_json(cycle / "archive-evidence" / "operation.json")


def cycle(ledger, transport, clock=policy.now):
    slot = policy.weekly_slot(clock())
    if slot is None:
        return {"schema": 1, "state": "skipped-window"}
    directory, existing = ledger.claim(slot)
    if existing:
        return existing
    record = policy.read_json(directory / "result.json")
    # Claim precedes SSH: a lost reply must never repeat a stop or backup.
    remote = json.loads(transport.remote("capture", slot))
    policy.require(remote["schema"] == 1 and remote["slot"] == slot)
    if remote["state"] in ("skipped-busy", "skipped-window"):
        record.update(state=remote["state"], completedUtc=clock().isoformat())
        ledger.finish(directory, record)
        return record
    policy.require(remote["state"] == "captured")
    digest = remote["bundleSha256"]
    policy.require(re.fullmatch(r"[a-f0-9]{64}", digest) is not None)
    data = transport.remote("bundle", slot, maximum=policy.MAX_BUNDLE)
    policy.require(bundle.digest(data) == digest)
    manifest, _ = bundle.inspect(data)
    policy.require(manifest["captured_utc"] == remote["capturedUtc"])
    captured = dt.datetime.fromisoformat(manifest["captured_utc"])
    policy.require(policy.weekly_slot(captured) == slot)
    age = (clock() - captured).total_seconds()
    policy.require(-300 <= age <= 900)
    bundle.write_new(directory / "player-data.tar", data)
    record.update(state="archiving", bundleSha256=digest, capturedUtc=remote["capturedUtc"])
    policy.write_json(directory / "result.json", record, replace=True)
    operation = transport.archive(directory, digest)
    policy.require(operation["action"] == "Archive" and operation["state"] == "verified"
                   and operation["bundleSha256"] == digest
                   and re.fullmatch(r"[a-f0-9]{64}", operation["snapshotId"]) is not None)
    record.update(state="verified", workload="goldsrcops-player-data-v1",
                  snapshotId=operation["snapshotId"], completedUtc=clock().isoformat(),
                  engineAcceptance="pending")
    # Freshness refers to capture time, not a newer retry/readback timestamp.
    policy.write_json(ledger.root / "last-success.json", record, replace=True)
    ledger.finish(directory, record)
    return record


def bootstrap(root, operation_path, bundle_path, clock=policy.now):
    policy.require(not (root / "last-success.json").exists()
                   and not (root / "last-success.json").is_symlink())
    policy.require(not (root / "pending.json").exists())
    operation = policy.read_json(operation_path)
    policy.require(operation["action"] == "Archive" and operation["state"] == "verified")
    private_bundle = policy.private_path(bundle_path)
    data = bundle.read_regular(private_bundle, bundle.MAX_ARCHIVE)
    digest = bundle.digest(data)
    policy.require(digest == operation["bundleSha256"])
    policy.require(re.fullmatch(r"[a-f0-9]{64}", operation["snapshotId"]) is not None)
    manifest, _ = bundle.inspect(data)
    captured = dt.datetime.fromisoformat(manifest["captured_utc"])
    policy.require(-300 <= (clock() - captured).total_seconds() <= 8 * 86400)
    record = {"schema": 1, "state": "verified", "workload": "goldsrcops-player-data-v1",
              "capturedUtc": manifest["captured_utc"], "bundleSha256": digest,
              "snapshotId": operation["snapshotId"], "engineAcceptance": "pending",
              "source": "reviewed-manual-baseline"}
    policy.write_json(root / "last-success.json", record)
    return record


def main():
    policy.require(os.geteuid() == 0 and len(sys.argv) in (2, 4))
    action = sys.argv[1]
    policy.require((action in ("run", "status") and len(sys.argv) == 2)
                   or (action == "baseline" and len(sys.argv) == 4))
    with policy.lock(ROOT / "schedule.lock"):
        if action == "status":
            print(json.dumps(policy.freshness(ROOT), separators=(",", ":")))
        elif action == "baseline":
            bootstrap(ROOT, Path(sys.argv[2]), Path(sys.argv[3]))
            print("PLAYER_DATA_MANUAL_BASELINE=imported_verified_bytes")
        else:
            result = cycle(policy.Ledger(ROOT), Transport())
            # Never publish a snapshot identifier or player data to the journal.
            print("PLAYER_DATA_WEEKLY=" + result["state"])


if __name__ == "__main__":
    os.umask(0o077)
    try:
        main()
    except Exception:
        print("PLAYER_DATA_WEEKLY=failed_inspect_private_record_no_retry", file=sys.stderr)
        sys.exit(1)
