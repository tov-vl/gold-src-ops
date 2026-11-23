"""Measure the actual isolated runner against four fixed synthetic vault sizes."""

import argparse
import json
from pathlib import Path
import tempfile
import time

from check import add_runtime_arguments, bundle, check
from fixtures import vault


SIZES = (78, 1024, 4096, 4097)


def size_fixture(directory, rows):
    source = directory / "vault"
    source.mkdir()
    stats, settings = [], []
    players = (rows + 4) // 5
    for index in range(players):
        key = f"v1:STEAM_0:1:{900000000 + index}".encode("ascii")
        stats.append((key, f"1 {(index * 17) % 500} {(index * 11) % 120}".encode("ascii")))
        for prefix, value in (
            (b"", b"1 2 1 2"),
            (b"hud:", b"1 1"),
            (b"streak:", b"1 7"),
            (b"name:", f"1 Fixture{index}".encode("ascii")),
        ):
            if len(settings) < rows - players:
                settings.append((prefix + key, value))
    assert len(stats) + len(settings) == rows
    for stem, entries in zip(bundle.STEMS, (stats, settings), strict=True):
        (source / (stem + ".vault")).write_bytes(vault(entries))
    path = directory / "size-fixture.tar"
    raw = bundle.create(source)
    path.write_bytes(raw)
    return path, bundle.digest(raw), len(raw)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    add_runtime_arguments(parser)
    args = parser.parse_args()
    failures = 0
    with tempfile.TemporaryDirectory(prefix="player-recovery-sizes-") as temporary:
        root = Path(temporary)
        for rows in SIZES:
            directory = root / str(rows)
            directory.mkdir()
            start = time.monotonic()
            args.bundle, args.expected_sha256, size = size_fixture(directory, rows)
            prepare = round(time.monotonic() - start, 3)
            report = check(args)
            if rows <= 4096:
                complete = (report["result"] == "passed" and report["containerRemoved"]
                            and [epoch["rows"] for epoch in report["epochs"]] == [rows, rows])
            else:
                complete = (report.get("reason") == "vault_record_limit_exceeded"
                            and report["containerRemoved"] and not report["stockPairOpened"]
                            and not report["epochs"])
            print(json.dumps({"syntheticRows": rows, "archiveBytes": size, "fixtureSeconds": prepare,
                              "expectedOutcome": complete, **report}), flush=True)
            failures += not complete
    return int(failures > 0)


if __name__ == "__main__":
    raise SystemExit(main())
