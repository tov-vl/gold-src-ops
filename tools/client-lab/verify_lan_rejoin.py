"""Verify two owned launches and one complete reconnect on the same fixture."""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re

def read(path):
    return json.loads(path.read_bytes())


def utc(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def verify_receipts(root, job, expected_action, count):
    directory = root / "controller-results" / job
    claim = read(directory / "dispatch.json")
    result_path = directory / "result.json"
    result_bytes = result_path.read_bytes()
    result = json.loads(result_bytes)
    renderer = read(root / f"RENDERER-{job}.json")
    collection = read(directory / "collection.json")
    assert claim["id"] == result["id"] == renderer["jobId"] == collection["id"] == job
    assert claim["status"] == "dispatch_claimed"
    assert result["action"] == expected_action and result["language"] == "en"
    assert result["launchCommandDispatched"] is True and result["launchRequested"] is True
    assert result["cleanupVerified"] is True and result["configCleanupVerified"] is True
    assert result["cleanup"] == {"game": "verified", "config": "verified"}
    assert collection["evidenceComplete"] is True and len(collection["images"]) == count
    assert [image["number"] for image in result["images"]] == list(range(1, count + 1))
    menu_images = result["images"][:4]
    assert len(menu_images) == 4 and all(image["source"] == "owned-client-dc" for image in menu_images)
    for metadata, source in zip(collection["images"], result["images"]):
        assert all(metadata[key] == source[key] for key in ("number", "bytes", "sha256"))
        path = directory / ("image-%d.bmp" % metadata["number"])
        assert path.stat().st_size == metadata["bytes"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == metadata["sha256"].lower()
    assert renderer["rendererIdentified"] is True and renderer["identity"]["backend"] == "OpenGL"
    assert renderer["sourceResultSha256"].lower() == hashlib.sha256(result_bytes).hexdigest()
    assert utc(claim["utc"]) <= utc(result["gameStartedUtc"]) < utc(result["completedUtc"])
    return result, renderer["identity"]["version"], utc(claim["utc"])


def one_ordered_sequence(events, patterns):
    indexes = [[index for index, line in enumerate(events) if re.fullmatch(pattern, line)] for pattern in patterns]
    return (all(len(hits) == 1 for hits in indexes)
            and all(left[0] < right[0] for left, right in zip(indexes, indexes[1:])))


def verify(root):
    if not __debug__:
        raise RuntimeError("LAN rejoin verification requires Python assertions")
    plan = read(root / "plan.json")
    pair = read(root / "rejoin-plan.json")
    assert pair["schema"] == 1 and pair["runId"] == plan["id"] and plan["language"] == "en"
    first_id, second_id = pair["first"], pair["second"]
    for job in (first_id, second_id):
        assert re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", job)
    assert first_id != second_id
    first, first_gl, _ = verify_receipts(root, first_id, "verify-panel", 5)
    second, second_gl, second_dispatch = verify_receipts(root, second_id, "capture-guns", 4)
    assert utc(first["completedUtc"]) < second_dispatch
    assert first["gameStartedUtc"] != second["gameStartedUtc"] and first["gameSessionId"] == second["gameSessionId"]
    assert first["interaction"]["continuity"]["phase"] == "ready-for-owned-close"
    assert first["interaction"]["respawnSnapshotSha256"] == first["images"][4]["sha256"]
    assert second["readiness"]["status"] == "ready" and second["readiness"]["setupAcknowledged"] is True
    cleanup = read(root / "cleanup.json")
    assert cleanup["fixtureRemoved"] is True and cleanup["fixtureEvidenceComplete"] is True
    runtime = read(root / "evidence/runtime.json")
    assert runtime["runtimeVerified"] is True and runtime["vacantForRejoin"] is True
    assert runtime["pluginSha256"] == "71AEF4EC88C1F97528B749AB9D0E3111C28FE8DB981E0742B2313F06D9741848"
    checkpoints = first["interaction"]["checkpoints"]
    assert {row["name"] for row in checkpoints} == {"primary-exit", "primary-reopened", "pistol-selected-close", "pistol-exit"}
    assert len(checkpoints) == 4
    for metadata in checkpoints:
        path = root / "controller-results" / first_id / ("checkpoint-%s.bmp" % metadata["name"])
        assert path.stat().st_size == metadata["bytes"]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == metadata["sha256"].lower()

    log = (root / "evidence" / "server-final.log").read_text(encoding="utf-8", errors="replace")
    events = [line.split("FIXTURE_CONTINUITY_", 1)[1] for line in log.splitlines() if "FIXTURE_CONTINUITY_" in line]
    assert not any(event.startswith(("TIMEOUT ", "REFUSED ")) for event in events)
    assert one_ordered_sequence(events, (
        r"ARMED selected=1 bots=2",
        r"ROUND_END observed=1",
        r"NEXT_ROUND spawn=1 selected=1",
        r"REQUEST_SENT observed=1",
        r"CLIENT_RECEIVED observed=1",
        r"CLIENT_CLOSE_ARMED observed=1",
        r"DISCONNECT observed=1",
        r"RECONNECT new_connection=1",
        r"RECONNECTED_SPAWN lan_identity=1 same_identity=1 selected=0 default=1",
    ))
    assert read(root / "evidence/amxx-errors.json")["errorFiles"] == 0
    summary = {"accepted": True, "release": "player-storage-v0.40.0",
               "firstJob": first["id"], "secondJob": second["id"],
               "freshCsProcess": True, "sameWindowsSession": True,
               "firstProcessClosedBeforeSecondDispatch": True, "rendererBoth": [first_gl, second_gl],
               "selectedLoadoutAfterNextRound": True, "serverSawDisconnect": True,
               "serverSawNewConnectionAndSpawn": True, "lanIdentity": True,
               "menuFramesFromOwnedWindow": True, "usesF5OrF8": False,
               "defaultLoadoutAfterRejoin": True, "selectedLoadoutAfterRejoin": False,
               "steamPersistenceVerified": False, "fixtureRemoved": True}
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("--write", action="store_true")
    args = parser.parse_args()
    root = args.run.resolve(strict=True)
    summary = verify(root)
    report = root / "REJOIN-ACCEPTANCE.json"
    if args.write:
        with report.open("x", encoding="utf-8") as stream:
            json.dump(summary, stream, indent=2)
    elif report.exists():
        assert summary == read(report), "Recorded LAN rejoin evidence differs"
    print(json.dumps(summary))


if __name__ == "__main__":
    main()
