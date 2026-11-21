"""Exercise LAN rejoin verification against a complete disposable evidence set."""

import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


VERIFIER = Path(__file__).resolve().parents[1] / "verify_lan_rejoin.py"
spec = importlib.util.spec_from_file_location("verify_lan_rejoin", VERIFIER)
rejoin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rejoin)

RUN_ID = "20000000-0000-4000-8000-000000000001"
FIRST_JOB = "20000000-0000-4000-8000-000000000002"
SECOND_JOB = "20000000-0000-4000-8000-000000000003"
EVENTS = (
    "ARMED selected=1 bots=2",
    "ROUND_END observed=1",
    "NEXT_ROUND spawn=1 selected=1",
    "REQUEST_SENT observed=1",
    "CLIENT_RECEIVED observed=1",
    "CLIENT_CLOSE_ARMED observed=1",
    "DISCONNECT observed=1",
    "RECONNECT new_connection=1",
    "RECONNECTED_SPAWN lan_identity=1 same_identity=1 selected=0 default=1",
)


class LanRejoinEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="lan-rejoin-evidence-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.write_json("plan.json", {"id": RUN_ID, "language": "en"})
        self.write_json("rejoin-plan.json", {"schema": 1, "runId": RUN_ID, "first": FIRST_JOB, "second": SECOND_JOB})
        self.write_json("cleanup.json", {"fixtureRemoved": True, "fixtureEvidenceComplete": True})
        self.write_json("evidence/runtime.json", {
            "runtimeVerified": True,
            "vacantForRejoin": True,
            "pluginSha256": "71AEF4EC88C1F97528B749AB9D0E3111C28FE8DB981E0742B2313F06D9741848",
        })
        self.write_json("evidence/amxx-errors.json", {"errorFiles": 0})
        self.write_events(EVENTS)

        first = self.write_job(FIRST_JOB, "verify-panel", 5, "00:00", "00:10", "01:00")
        checkpoints = []
        for name in ("primary-exit", "primary-reopened", "pistol-selected-close", "pistol-exit"):
            path = self.job_file(FIRST_JOB, f"checkpoint-{name}.bmp")
            checkpoints.append({"name": name, **self.write_frame(path, name.encode("ascii"))})
        first["interaction"] = {
            "continuity": {"phase": "ready-for-owned-close"},
            "respawnSnapshotSha256": first["images"][-1]["sha256"],
            "checkpoints": checkpoints,
        }
        self.write_result(FIRST_JOB, first)

        second = self.write_job(SECOND_JOB, "capture-guns", 4, "01:10", "01:20", "02:00")
        second["readiness"] = {"status": "ready", "setupAcknowledged": True}
        self.write_result(SECOND_JOB, second)

    def write_json(self, path, data):
        path = self.root / path
        path.parent.mkdir(parents=True, exist_ok=True)
        content = json.dumps(data, sort_keys=True).encode("utf-8")
        path.write_bytes(content)
        return hashlib.sha256(content).hexdigest()

    def write_frame(self, path, content):
        # Small synthetic payloads exercise byte-count and hash binding.
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
        return {"bytes": len(content), "sha256": hashlib.sha256(content).hexdigest().upper()}

    def write_events(self, events):
        log = self.root / "evidence/server-final.log"
        log.write_text("\n".join(f"FIXTURE_CONTINUITY_{event}" for event in events) + "\n", encoding="utf-8")

    def job_file(self, job, name):
        return self.root / "controller-results" / job / name

    def write_job(self, job, action, count, dispatched, started, completed):
        self.write_json(self.job_file(job, "dispatch.json"), {
            "id": job, "status": "dispatch_claimed", "utc": f"2026-01-01T10:{dispatched}Z",
        })
        images = []
        for number in range(1, count + 1):
            frame = self.write_frame(self.job_file(job, f"image-{number}.bmp"), f"{job}/{number}".encode("ascii"))
            images.append({"number": number, "source": "owned-client-dc" if number <= 4 else "engine-tga", **frame})
        self.write_json(self.job_file(job, "collection.json"), {"id": job, "evidenceComplete": True, "images": images})
        return {
            "id": job, "action": action, "language": "en", "images": images,
            "launchCommandDispatched": True, "launchRequested": True, "gameSessionId": 8,
            "gameStartedUtc": f"2026-01-01T10:{started}Z", "completedUtc": f"2026-01-01T10:{completed}Z",
            "cleanupVerified": True, "configCleanupVerified": True, "cleanup": {"game": "verified", "config": "verified"},
        }

    def write_result(self, job, result):
        digest = self.write_json(self.job_file(job, "result.json"), result)
        self.write_json(f"RENDERER-{job}.json", {
            "jobId": job, "rendererIdentified": True, "identity": {"backend": "OpenGL", "version": "4.2 synthetic"},
            "sourceResultSha256": digest,
        })

    def test_complete_evidence_accepts_both_launches_and_the_lan_spawn(self):
        summary = rejoin.verify(self.root)
        self.assertTrue(summary["accepted"])
        self.assertEqual((summary["firstJob"], summary["secondJob"]), (FIRST_JOB, SECOND_JOB))
        self.assertTrue(summary["firstProcessClosedBeforeSecondDispatch"])
        self.assertTrue(summary["defaultLoadoutAfterRejoin"])
        self.assertFalse(summary["steamPersistenceVerified"])

    def test_same_size_mutated_frame_is_rejected(self):
        frame = self.job_file(SECOND_JOB, "image-2.bmp")
        original = frame.read_bytes()
        frame.write_bytes(original[:-1] + b"!")
        with self.assertRaises(AssertionError):
            rejoin.verify(self.root)

    def test_mutated_checkpoint_is_rejected(self):
        checkpoint = self.job_file(FIRST_JOB, "checkpoint-primary-exit.bmp")
        original = checkpoint.read_bytes()
        checkpoint.write_bytes(original[:-1] + b"!")
        with self.assertRaises(AssertionError):
            rejoin.verify(self.root)

    def test_renderer_receipt_cannot_be_reused_for_a_modified_report(self):
        path = self.job_file(SECOND_JOB, "result.json")
        result = rejoin.read(path)
        result["completedUtc"] = "2026-01-01T10:02:01Z"
        self.write_json(path, result)
        with self.assertRaises(AssertionError):
            rejoin.verify(self.root)

    def test_renderer_receipt_must_identify_opengl_on_both_launches(self):
        for job in (FIRST_JOB, SECOND_JOB):
            path = self.root / f"RENDERER-{job}.json"
            original = path.read_bytes()
            renderer = rejoin.read(path)
            for identified, backend in ((False, "OpenGL"), (True, "Software")):
                with self.subTest(job=job, identified=identified, backend=backend):
                    renderer["rendererIdentified"] = identified
                    renderer["identity"]["backend"] = backend
                    self.write_json(path, renderer)
                    with self.assertRaises(AssertionError):
                        rejoin.verify(self.root)
            path.write_bytes(original)

    def test_second_dispatch_must_follow_first_process_cleanup(self):
        path = self.job_file(SECOND_JOB, "dispatch.json")
        claim = rejoin.read(path)
        for timestamp in ("2026-01-01T10:00:59Z", "2026-01-01T10:01:00Z"):
            with self.subTest(timestamp=timestamp):
                claim["utc"] = timestamp
                self.write_json(path, claim)
                with self.assertRaises(AssertionError):
                    rejoin.verify(self.root)

    def test_reconnect_requires_one_ordered_connection_and_matching_spawn(self):
        for events in (
            (*EVENTS[:7], EVENTS[-1]),
            (*EVENTS[:6], EVENTS[7], EVENTS[6], EVENTS[-1]),
            (*EVENTS[:8], EVENTS[7], EVENTS[-1]),
            (*EVENTS[:8], EVENTS[-1].replace("same_identity=1", "same_identity=0")),
        ):
            with self.subTest(events=events):
                self.write_events(events)
                with self.assertRaises(AssertionError):
                    rejoin.verify(self.root)

    def test_cli_rejects_existing_acceptance_report_that_differs_from_evidence(self):
        args = (sys.executable, "-B", str(VERIFIER), str(self.root))
        written = subprocess.run((*args, "--write"), capture_output=True, check=True, timeout=15)
        accepted = subprocess.run(args, capture_output=True, check=True, timeout=15)
        self.assertEqual(json.loads(written.stdout), json.loads(accepted.stdout))
        report = self.root / "REJOIN-ACCEPTANCE.json"
        summary = rejoin.read(report)
        summary["steamPersistenceVerified"] = True
        self.write_json(report, summary)
        rejected = subprocess.run(args, capture_output=True, timeout=15)
        self.assertNotEqual(rejected.returncode, 0)
        self.assertIn(b"Recorded LAN rejoin evidence differs", rejected.stderr)
