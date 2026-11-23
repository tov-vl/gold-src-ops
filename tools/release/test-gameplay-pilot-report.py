"""Synthetic template and preflight cases for the offline gameplay report checker."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
script = HERE / "check-gameplay-pilot-report.py"
spec = importlib.util.spec_from_file_location("check_gameplay_pilot_report", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
TEMPLATE = HERE.parents[1] / "docs/templates/gameplay-pilot-result.md"


def fixtures():
    prefix = "2026-11-22T"
    observed = prefix + "14:00:00+00:00"
    header = ("## Server Preflight\n\n- Observed UTC: " + observed +
              "\n- Baseline result: ready\n- Synthetic token: private-fixture-reference")
    preflight = {"schema": 1, "result": "ready", "checks": {"fixture": {"observed": 0, "expected": 0}},
                 "drift": {}, "unavailable": [], "collectionErrors": {},
                 "startedAtUtc": prefix + "13:59:00+00:00", "observedAtUtc": observed,
                 "pilotHeader": header}
    report = TEMPLATE.read_text(encoding="utf-8")
    report = report.replace("- UTC start/end and elapsed minutes: <UTC-start / UTC-end / elapsed minutes>",
                            "- UTC start/end and elapsed minutes: " + prefix + "14:01:00Z / " +
                            prefix + "14:36:00Z / 35")
    report = report.replace("- Result: <Not run / Core passed with limits / Partial / Failed>",
                            "- Result: Core passed with limits")
    report = report.replace("## Scenario Results", header + "\n\n## Scenario Results")
    for index in range(1, 18):
        scenario = f"P{index:02}"
        if scenario in module.CONDITIONAL:
            text = "Not tested | | | Map transition prerequisite unavailable"
        else:
            text = r"Passed | Actor A \| B, map and round | Observed response | fixture-evidence"
        report = report.replace(f"| {scenario} | Not tested | | | Not run |", f"| {scenario} | {text} |")
    return preflight, report


class PilotReportTests(unittest.TestCase):
    def setUp(self):
        self.preflight, self.report = fixtures()

    def validate_report(self, *, crlf=False):
        status = module.validate(json.dumps(self.preflight), self.report.replace("\n", "\r\n") if crlf else self.report)
        self.assertNotIn("private-fixture-reference", json.dumps(status))
        self.assertNotIn("fixture-evidence", json.dumps(status))
        return status

    def assert_error(self, error, scenario=None):
        result = self.validate_report()
        self.assertEqual("invalid_report", result["result"])
        expected = {"code": error}
        if scenario:
            expected["scenario"] = scenario
        self.assertIn(expected, result["errors"])

    def test_complete_with_conditional_reasons(self):
        result = self.validate_report()
        self.assertEqual("valid_report", result["result"])
        self.assertTrue(result["preflightMatched"])
        self.assertEqual(60, result["preflightLeadSeconds"])
        self.assertEqual({"passed": 13, "failed": 0, "notTested": 4, "expectedScenarios": 17},
                         result["counts"])

    def test_missing_scenario(self):
        self.report = "\n".join(line for line in self.report.splitlines() if not line.startswith("| P09 |"))
        self.assert_error("missing_scenario", "P09")

    def test_duplicate_scenario(self):
        self.report = self.report.replace("| P04 |", "| P03 |")
        self.assert_error("duplicate_scenario", "P03")

    def test_missing_untested_reason(self):
        self.report = self.report.replace("Map transition prerequisite unavailable", "Not run", 1)
        self.assert_error("untested_reason_missing", "P07")

    def test_tampered_preflight_header(self):
        self.report = self.report.replace("private-fixture-reference", "altered")
        self.assert_error("preflight_header_mismatch")

    def test_preflight_not_ready(self):
        self.preflight["result"] = "drift"
        self.assert_error("preflight_not_ready")

    def test_preflight_after_session_start(self):
        self.report = self.report.replace("14:01:00Z", "13:00:00Z")
        self.assert_error("session_chronology_invalid")

    def test_core_pass_requires_all_core_cases(self):
        self.report = self.report.replace("| P03 | Passed |", "| P03 | Failed |")
        self.assert_error("core_pass_inconsistent")

    def test_run_cli_success_and_failure(self):
        with tempfile.TemporaryDirectory(prefix="gameplay-pilot-report-") as directory:
            root = Path(directory)
            preflight_path, report_path = root / "preflight.json", root / "pilot.md"
            preflight_path.write_text(json.dumps(self.preflight), encoding="utf-8-sig")
            report_path.write_bytes(self.report.replace("\n", "\r\n").encode())
            command = [sys.executable, "-B", str(script), "--preflight", str(preflight_path),
                       "--report", str(report_path)]
            run = subprocess.run(command, capture_output=True, timeout=10, check=False)
            self.assertEqual(0, run.returncode)
            self.assertEqual("valid_report", json.loads(run.stdout)["result"])
            report_path.write_text(self.report.replace("| P17 |", "| P16 |"), encoding="utf-8")
            run = subprocess.run(command, capture_output=True, timeout=10, check=False)
            self.assertEqual(1, run.returncode)
            self.assertEqual("invalid_report", json.loads(run.stdout)["result"])
            self.assertNotIn(str(report_path), run.stdout.decode())


if __name__ == "__main__":
    unittest.main()
