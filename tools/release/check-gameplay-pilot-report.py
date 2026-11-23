#!/usr/bin/env python3
"""Check a filled gameplay-pilot Markdown record against its saved preflight."""

import argparse
from collections import Counter
import datetime as dt
import json
from pathlib import Path
import re


MAX_INPUT_BYTES = 256 * 1024
SCENARIOS = tuple(f"P{index:02}" for index in range(1, 18))
CONDITIONAL = {"P07", "P08", "P09", "P17"}
OUTCOMES = ("Core passed with limits", "Partial", "Failed")
COLUMNS = ("ID", "Result", "Actual steps / preconditions / actor",
           "Observed result and comparison", "Evidence or untested reason")
PLACEHOLDERS = {"-", "not run", "not tested", "not checked", "n/a", "tbd"}


class ReportInvalid(ValueError):
    """Report a fixed error code without echoing source text."""


def read_text(path):
    if not path.is_file():
        raise ReportInvalid("input_file_invalid")
    with path.open("rb") as source:
        raw = source.read(MAX_INPUT_BYTES + 1)
    if len(raw) > MAX_INPUT_BYTES:
        raise ReportInvalid("input_too_large")
    return raw.decode("utf-8-sig").replace("\r\n", "\n")


def utc_time(text):
    try:
        moment = dt.datetime.fromisoformat(text)
    except (TypeError, ValueError):
        raise ReportInvalid("utc_timestamp_invalid") from None
    if moment.utcoffset() != dt.timedelta(0):
        raise ReportInvalid("utc_timestamp_invalid")
    return moment


def unique_fields(pairs):
    value = dict(pairs)
    if len(value) != len(pairs):
        raise ReportInvalid("preflight_duplicate_field")
    return value


def as_preflight(text):
    try:
        data = json.loads(text, object_pairs_hook=unique_fields)
    except json.JSONDecodeError:
        raise ReportInvalid("preflight_json_invalid") from None
    if (not isinstance(data, dict) or type(data.get("schema")) is not int
            or data["schema"] != 1 or data.get("result") != "ready"):
        raise ReportInvalid("preflight_not_ready")

    checks = data.get("checks")
    if (not isinstance(checks, dict) or not checks
            or any(not isinstance(row, dict) or row.get("observed") is None
                   or row.get("observed") != row.get("expected") for row in checks.values())
            or data.get("drift") != {} or data.get("unavailable") != [] or data.get("collectionErrors") != {}):
        raise ReportInvalid("preflight_checks_incomplete")
    observed = utc_time(data.get("observedAtUtc"))
    if utc_time(data.get("startedAtUtc")) > observed:
        raise ReportInvalid("preflight_chronology_invalid")
    header = data.get("pilotHeader")
    if not isinstance(header, str):
        raise ReportInvalid("preflight_header_invalid")
    lines = header.replace("\r\n", "\n").strip().splitlines()
    if (not lines or lines[0] != "## Server Preflight"
            or "- Observed UTC: " + data["observedAtUtc"] not in lines
            or "- Baseline result: ready" not in lines):
        raise ReportInvalid("preflight_header_invalid")
    return observed, lines


def section(report, heading):
    lines = report.splitlines()
    candidates = [i for i, line in enumerate(lines) if line.strip() == heading]
    if len(candidates) != 1:
        raise ReportInvalid("report_section_missing_or_duplicate")
    start = candidates[0]
    end = next((i for i in range(start + 1, len(lines)) if re.match(r"^#{1,2} ", lines[i])), len(lines))
    return lines[start:end]


def field(lines, prefix):
    matches = [line[len(prefix):].strip() for line in lines if line.startswith(prefix)]
    if len(matches) != 1:
        raise ReportInvalid("session_field_missing_or_duplicate")
    return matches[0]


def meaningful(value):
    value = value.strip()
    return bool(value) and not (value.startswith("<") and value.endswith(">")) and value.casefold() not in PLACEHOLDERS


def cells(line):
    return tuple(cell.strip() for cell in re.split(r"(?<!\\)\|", line[1:-1]))


def validate_rows(lines, errors):
    table = [line.strip() for line in lines[1:] if line.lstrip().startswith("|")]
    if len(table) < 2 or not all(line.endswith("|") for line in table) or cells(table[0]) != COLUMNS:
        raise ReportInvalid("scenario_table_invalid")
    header_rule = cells(table[1])
    if len(header_rule) != len(COLUMNS) or any(not re.fullmatch(r":?-{3,}:?", cell) for cell in header_rule):
        raise ReportInvalid("scenario_table_invalid")
    observed = {}
    for line in table[2:]:
        item = cells(line)
        if len(item) != len(COLUMNS):
            errors.append({"code": "scenario_row_invalid"})
            continue
        scenario, status, actual, comparison, evidence = item
        if scenario not in SCENARIOS:
            errors.append({"code": "unknown_scenario"})
            continue
        if scenario in observed:
            errors.append({"code": "duplicate_scenario", "scenario": scenario})
            continue
        observed[scenario] = status
        if status not in ("Passed", "Failed", "Not tested"):
            errors.append({"code": "scenario_status_invalid", "scenario": scenario})
            continue
        if status == "Not tested":
            if not meaningful(evidence):
                errors.append({"code": "untested_reason_missing", "scenario": scenario})
        elif not all(meaningful(item) for item in (actual, comparison, evidence)):
            errors.append({"code": "scenario_evidence_missing", "scenario": scenario})
    for scenario in SCENARIOS:
        if scenario not in observed:
            errors.append({"code": "missing_scenario", "scenario": scenario})
    return observed


def validate(preflight_text, report_text):
    result = {"schema": 1, "result": "invalid_report", "preflightMatched": False, "errors": []}
    errors = result["errors"]
    try:
        observed, expected_header = as_preflight(preflight_text)
        copied = section(report_text, "## Server Preflight")
        if "\n".join(copied).strip() != "\n".join(expected_header):
            errors.append({"code": "preflight_header_mismatch"})
        else:
            result["preflightMatched"] = True

        session = section(report_text, "## Session And Baseline")
        times = field(session, "- UTC start/end and elapsed minutes: ").split(" / ")
        if len(times) != 3 or not meaningful(times[2]):
            raise ReportInvalid("session_time_format_invalid")
        started, ended = map(utc_time, times[:2])
        if not observed <= started < ended:
            errors.append({"code": "session_chronology_invalid"})
        else:
            result["preflightLeadSeconds"] = round((started - observed).total_seconds(), 3)
        outcome = field(session, "- Result: ")
        if outcome not in OUTCOMES:
            errors.append({"code": "session_outcome_invalid"})
        else:
            result["declaredOutcome"] = outcome

        statuses = validate_rows(section(report_text, "## Scenario Results"), errors)
        counts = Counter(statuses.values())
        result["counts"] = {"passed": counts["Passed"], "failed": counts["Failed"],
                            "notTested": counts["Not tested"], "expectedScenarios": len(SCENARIOS)}
        if not counts["Passed"] and not counts["Failed"]:
            errors.append({"code": "no_scenarios_executed"})
        if outcome == "Core passed with limits" and any(statuses.get(key) != "Passed"
                                                       for key in SCENARIOS if key not in CONDITIONAL):
            errors.append({"code": "core_pass_inconsistent"})
    except ReportInvalid as error:
        errors.append({"code": str(error)})
    if not errors:
        result["result"] = "valid_report"
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    try:
        result = validate(read_text(args.preflight), read_text(args.report))
    except ReportInvalid as error:
        result = {"schema": 1, "result": "invalid_report", "preflightMatched": False,
                  "errors": [{"code": str(error)}]}
    except (OSError, UnicodeError):
        result = {"schema": 1, "result": "invalid_report", "preflightMatched": False,
                  "errors": [{"code": "input_file_invalid"}]}
    print(json.dumps(result, indent=2))
    return int(result["result"] != "valid_report")


if __name__ == "__main__":
    raise SystemExit(main())
