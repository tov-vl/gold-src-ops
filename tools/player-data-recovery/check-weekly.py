"""Check the local restic readback bound to a selected weekly player-data slot."""

import argparse
import datetime as dt
import json
from pathlib import Path
import re

from check import CheckError, add_runtime_arguments, bundle, check, require


HEX_SHA256 = re.compile(r"[0-9a-f]{64}")
MAX_RECEIPT_BYTES = 16 * 1024


def load_receipt(path):
    raw = bundle.read_regular(path, MAX_RECEIPT_BYTES)

    def reject_constant(_):
        raise ValueError("Invalid JSON numeric constant.")

    value = json.loads(raw, object_pairs_hook=bundle.unique_object, parse_constant=reject_constant)
    require(isinstance(value, dict), "receipt_contract_invalid")
    return value


def utc_time(value):
    moment = dt.datetime.fromisoformat(value)
    require(moment.utcoffset() == dt.timedelta(0), "receipt_time_invalid")
    return moment


def bind_receipts(slot, weekly, recover):
    require(re.fullmatch(r"[0-9]{4}-[0-9]{2}-[0-9]{2}", slot), "weekly_slot_invalid")
    requested = dt.date.fromisoformat(slot)
    require(requested.weekday() == 6, "weekly_slot_invalid")
    require(weekly.get("slot") == slot, "weekly_slot_mismatch")
    require(type(weekly.get("schema")) is int and weekly["schema"] == 1
            and weekly.get("workload") == "goldsrcops-player-data-v1"
            and weekly.get("state") == "verified", "weekly_receipt_unverified")
    require(type(recover.get("schemaVersion")) is int and recover["schemaVersion"] == 1
            and recover.get("action") == "Recover" and recover.get("state") == "verified",
            "recover_receipt_unverified")

    snapshot, digest = weekly["snapshotId"], weekly["bundleSha256"]
    require(HEX_SHA256.fullmatch(snapshot) and recover.get("snapshotId") == snapshot, "snapshot_mismatch")
    require(HEX_SHA256.fullmatch(digest) and recover.get("bundleSha256") == digest, "receipt_sha256_mismatch")

    capture = utc_time(weekly["capturedUtc"])
    require(capture.date() == requested, "weekly_capture_date_mismatch")
    require(utc_time(weekly["startedUtc"]) <= capture <= utc_time(weekly["completedUtc"])
            <= utc_time(recover["startedUtc"]) <= utc_time(recover["completedUtc"]),
            "receipt_chronology_invalid")
    return digest, capture


def check_weekly(args):
    try:
        digest, capture = bind_receipts(args.slot, load_receipt(args.weekly_result),
                                        load_receipt(args.recover_receipt))
    except CheckError as error:
        return {"schema": 1, "result": "failed", "reason": str(error), "receiptBindingVerified": False}
    except (OSError, ValueError, TypeError, KeyError):
        return {"schema": 1, "result": "failed", "reason": "receipt_contract_invalid",
                "receiptBindingVerified": False}

    capture_matches = False

    def match_capture(manifest):
        nonlocal capture_matches
        require(utc_time(manifest["captured_utc"]) == capture, "capture_timestamp_mismatch")
        capture_matches = True

    args.bundle = args.recover_receipt.parent / "readback" / "player-data.tar"
    args.expected_sha256 = digest
    result = check(args, match_manifest=match_capture)
    result["receiptBindingVerified"] = capture_matches
    result["weeklySlot"] = args.slot
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--slot", required=True, help="The selected weekly slot as YYYY-MM-DD.")
    parser.add_argument("--weekly-result", type=Path, required=True)
    parser.add_argument("--recover-receipt", type=Path, required=True)
    add_runtime_arguments(parser)
    result = check_weekly(parser.parse_args())
    print(json.dumps(result, indent=2))
    return int(result["result"] != "passed")


if __name__ == "__main__":
    raise SystemExit(main())
