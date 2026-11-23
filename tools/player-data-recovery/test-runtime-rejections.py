"""Exercise the live isolated runner's failure reports and container removal."""

import argparse
import json
from pathlib import Path
import tempfile
from unittest.mock import patch

import check as recovery
from fixtures import fixture, vault


def pack_source(args, source):
    raw = recovery.bundle.create(source)
    args.bundle.write_bytes(raw)
    args.expected_sha256 = recovery.bundle.digest(raw)


def invalid_sources(args, root):
    for name in ("nonempty_journal", "vault_record_limit_exceeded"):
        directory = root / name
        directory.mkdir()
        args.bundle, _ = fixture(directory)
        source = directory / "vault"
        if name == "nonempty_journal":
            (source / (recovery.bundle.STEMS[1] + ".journal")).write_bytes(b"\x01")
        else:
            entries = [(f"v1:STEAM_0:1:{index}".encode(), b"1 25 2") for index in range(4096)]
            (source / (recovery.bundle.STEMS[0] + ".vault")).write_bytes(vault(entries))
        pack_source(args, source)
        report = recovery.check(args)
        print(json.dumps({"case": name, **report}), flush=True)
        assert report["result"] == "failed" and report["reason"] == name
        assert not report["stockPairOpened"] and not report["epochs"] and report["containerRemoved"]


def timeout_case(args, root):
    directory = root / "engine-timeout"
    directory.mkdir()
    args.bundle, args.expected_sha256 = fixture(directory)
    stage = recovery.stage_inputs
    start_command = recovery.subprocess.run
    stalled = False

    def stage_with_stall(options, kit):
        stage(options, kit)
        runner = kit / "run.sh"
        source = runner.read_text(encoding="utf-8")
        original = "    sleep 5\n    printf"
        stall = """    sleep 5
    if [[ "$expected" == ACTUAL_COPY_ENGINE= ]]; then
        kill -0 "$pid"
        echo FIXTURE_ENGINE_STALLED=1
        sleep 180
    fi
    printf"""
        assert source.count(original) == 1
        runner.write_text(source.replace(original, stall), encoding="utf-8", newline="\n")

    def observe_command(command, **kwargs):
        nonlocal stalled
        result = start_command(command, **kwargs)
        if command[3:5] == ["start", "-ai"]:
            stalled = b"FIXTURE_ENGINE_STALLED=1" in result.stdout.splitlines()
        return result

    with patch.object(recovery, "RUN_TIMEOUT", 120), \
         patch.object(recovery, "stage_inputs", stage_with_stall), \
         patch.object(recovery.subprocess, "run", observe_command):
        report = recovery.check(args)

    print(json.dumps({"case": "engine_timeout", "fixtureReachedStall": stalled, **report}), flush=True)
    assert stalled and report["result"] == "failed" and report["reason"] == "engine_timed_out"
    assert report["containerStarted"] and report["containerRemoved"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    recovery.add_runtime_arguments(parser)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="player-recovery-rejections-") as temporary:
        root = Path(temporary)
        invalid_sources(args, root)
        timeout_case(args, root)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
