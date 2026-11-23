#!/usr/bin/env python3
"""Exercise the operator command with synthetic closed vaults and failure controls."""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

from check import add_runtime_arguments, bundle, check
from fixtures import fixture


def cli(args, digest):
    command = [sys.executable, "-B", str(Path(__file__).with_name("check.py")),
               "--bundle", str(args.bundle), "--expected-sha256", digest]
    for name in ("stable_zip", "assets", "steam_client", "engine_image_id", "docker_context"):
        command.extend(("--" + name.replace("_", "-"), str(getattr(args, name))))
    proc = subprocess.run(command, capture_output=True, timeout=260, check=False)
    return proc.returncode, json.loads(proc.stdout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    add_runtime_arguments(parser)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="player-recovery-fixture-") as directory:
        args.bundle, args.expected_sha256 = fixture(Path(directory))
        exit_code, invalid_hash = cli(args, "0" * 64)
        print(json.dumps({"case": "wrong_sha256", **invalid_hash}), flush=True)
        assert exit_code == 1 and invalid_hash["reason"] == "input_hash_mismatch"
        assert not invalid_hash["containerStarted"] and not invalid_hash["isolationVerified"]

        exit_code, positive = cli(args, args.expected_sha256)
        print(json.dumps({"case": "valid_fixture", **positive}), flush=True)
        assert exit_code == 0 and positive["result"] == "passed"
        assert positive["stockPairOpened"] and positive["containerRemoved"]
        assert [row["rows"] for row in positive["epochs"]] == [5, 5]

        negative = check(args, mismatch=True)
        print(json.dumps({"case": "engine_mismatch", **negative}), flush=True)
        assert negative["result"] == "failed" and negative["reason"] == "engine_mismatch"
        assert negative["stockPairOpened"] and negative["containerRemoved"]
        assert len(negative["epochs"]) == 1 and negative["epochs"][0]["failures"] > 0
        assert bundle.digest(args.bundle.read_bytes()) == args.expected_sha256
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
