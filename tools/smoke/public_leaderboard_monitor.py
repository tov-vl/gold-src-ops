"""Synthetic leaderboard policy and OTLP/provisioning checks for the pinned stack."""
import json
from pathlib import Path
import re
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / "ops/production/observability"
RULES = OBS / "rules/public-leaderboard.yml"
PREFIX = "goldsrcops_leaderboard_"
LABELS = '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="current"}'
ALERTS = ["PublicLeaderboardMonitorUnavailable", "PublicLeaderboardRefreshDelayed",
          "PublicLeaderboardDataUnavailable", "PublicLeaderboardSnapshotStale"]


def fixtures():
    assert 'resource.AddService("GoldSrcOps")' in (ROOT / "src/GoldSrcOps.Api/Program.cs").read_text(encoding="utf-8")
    annotations, current = {}, None
    for line in RULES.read_text(encoding="utf-8").splitlines():
        if "      - alert: " in line:
            current = line.split(": ", 1)[1]
            annotations[current] = {}
        elif current and re.match(r"          (summary|description): ", line):
            key, value = line.strip().split(": ", 1)
            annotations[current][key] = json.loads(value)
    healthy = {"observed_timestamp_seconds": "0+60x40", "enabled": "1+0x40",
               "started_timestamp_seconds": "0+0x40", "last_attempt_timestamp_seconds": "0+60x40",
               "last_success_timestamp_seconds": "0+60x40", "snapshot_available": "1+0x40",
               "snapshot_age_seconds": "0+0x40", 'last_result{result="operator_busy"}': "0+0x40"}
    cases = [
        ("fresh-or-valid-empty", {}, [], "10m"),
        ("disabled-without-snapshot", {"enabled": "0+0x40", "snapshot_available": "0+0x40",
                                      "last_attempt_timestamp_seconds": "0+0x40", "last_success_timestamp_seconds": "0+0x40"}, [], "30m"),
        ("unavailable-not-yet-fired", {"snapshot_available": "0+0x40"}, [], "4m"),
        ("source-or-credential-unavailable", {"snapshot_available": "0+0x40"}, [2], "6m"),
        ("failed-read-stale", {"snapshot_age_seconds": "240+60x40"}, [3], "6m"),
        ("short-busy-stale", {"snapshot_age_seconds": "240+60x40", 'last_result{result="operator_busy"}': "1+0x40"}, [], "10m"),
        ("long-busy-stale", {"snapshot_age_seconds": "240+60x40", 'last_result{result="operator_busy"}': "1+0x40"}, [3], "18m"),
        ("initial-busy", {"snapshot_available": "0+0x40", 'last_result{result="operator_busy"}': "1+0x40"}, [], "19m"),
        ("long-initial-busy", {"snapshot_available": "0+0x40", 'last_result{result="operator_busy"}': "1+0x40"}, [2], "20m"),
        ("brief-unavailable", {"snapshot_available": "0+0x2 1+0x38"}, [], "10m"),
        ("recovered-unavailable", {"snapshot_available": "0+0x6 1+0x34"}, [], "10m"),
        ("recovered-stale", {"snapshot_age_seconds": "240+0x6 0+0x34"}, [], "10m"),
        ("worker-stalled", {"last_attempt_timestamp_seconds": "0+0x40"}, [1], "10m"),
        ("frozen-export", {"observed_timestamp_seconds": "0+60x5 300+0x35"}, [0], "14m"),
        ("disappeared-export", {"observed_timestamp_seconds": "0+60x5 stale _x35"}, [0], "14m"),
        ("future-export", {"observed_timestamp_seconds": "5000+60x40"}, [0], "10m"),
        ("missing-attempt", {"last_attempt_timestamp_seconds": None}, [0], "10m"),
        ("invalid-enabled", {"enabled": "2+0x40"}, [0], "10m"),
        ("invalid-age", {"snapshot_age_seconds": "-1+0x40"}, [0], "10m"),
        ("future-success", {"last_success_timestamp_seconds": "5000+60x40"}, [0], "10m"),
        ("no-observations", None, [0], "10m")]
    tests = []
    for name, changes, expected, evaluation in cases:
        values = dict(healthy, **changes) if changes is not None else {}
        series = []
        for key, value in values.items():
            if value is None:
                continue
            metric, _, tags = key.partition("{")
            labels = LABELS[:-1] + "," + tags if tags else LABELS
            series.append({"series": PREFIX + metric + labels, "values": value})
        tests.append({"name": name, "interval": "1m", "input_series": series,
                      "alert_rule_test": [{"eval_time": evaluation, "alertname": alert,
                      "exp_alerts": [{"exp_labels": {"severity": "warning", "component": "public-leaderboard"},
                                      "exp_annotations": annotations[alert]}] if i in expected else []}
                      for i, alert in enumerate(ALERTS)]})
    # A retained older API resource must not supply fields or busy status for
    # the current resource. Alert identities stay independent of expressions.
    retained = dict(healthy, observed_timestamp_seconds="0+0x40",
                    last_attempt_timestamp_seconds="0+0x40",
                    last_success_timestamp_seconds="0+0x40")
    for name, changes, expected in [
        ("restart-unavailable-old-fresh", {"snapshot_available": "0+0x40"}, [2]),
        ("restart-disabled-old-enabled", {"enabled": "0+0x40", "snapshot_available": "0+0x40",
                                           "last_attempt_timestamp_seconds": "0+0x40"}, []),
        ("restart-failed-old-busy", {"snapshot_age_seconds": "240+60x40"}, [3]),
        ("restart-missing-gauge-old-valid", {"snapshot_available": None}, [0])]:
        values = dict(healthy, **changes)
        series = []
        for resource, data in [("current", values), ("previous", retained)]:
            data = dict(data)
            if name == "restart-failed-old-busy" and resource == "previous":
                data['last_result{result="operator_busy"}'] = "1+0x40"
            for key, value in data.items():
                if value is None:
                    continue
                metric, _, tags = key.partition("{")
                labels = LABELS.replace('service_instance_id="current"', 'service_instance_id="' + resource + '"')
                labels = labels[:-1] + "," + tags if tags else labels
                series.append({"series": PREFIX + metric + labels, "values": value})
        tests.append({"name": name, "interval": "1m", "input_series": series,
                      "alert_rule_test": [{"eval_time": "10m", "alertname": alert,
                      "exp_alerts": [{"exp_labels": {"severity": "warning", "component": "public-leaderboard"},
                                      "exp_annotations": annotations[alert]}] if i in expected else []}
                      for i, alert in enumerate(ALERTS)]})
    return {"rule_files": ["/rules/public-leaderboard.yml"], "evaluation_interval": "1m", "tests": tests}


def validate_rules(docker, mount, image, temporary):
    path = Path(temporary) / "leaderboard-test.json"
    path.write_text(json.dumps(fixtures(), ensure_ascii=False), encoding="utf-8")
    path.chmod(0o644)
    docker("run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
           "--security-opt", "no-new-privileges", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--mount", mount(OBS / "rules", "/rules"),
           "--mount", mount(path, "/fixtures.json"), "--entrypoint", "/bin/promtool", image,
           "test", "rules", "/fixtures.json")
    print("PUBLIC_LEADERBOARD_RULES=25_scenarios_passed", flush=True)


def check_stack(collector_url, graf_url, query, request, wait):
    def publish(available=1, enabled=1):
        now = time.time()
        values = {"enabled": enabled, "observed_timestamp": now, "started_timestamp": now - 60,
                  "last_attempt_timestamp": now, "last_success_timestamp": now,
                  "snapshot_available": available, "snapshot_age": 0, "rows": 0}
        metrics = []
        for key, value in values.items():
            metrics.append({"name": "goldsrcops.leaderboard." + key,
                            "unit": "s" if key.endswith("timestamp") or key == "snapshot_age" else "",
                            "gauge": {"dataPoints": [{"asDouble": value,
                                       "timeUnixNano": str(time.time_ns())}]}})
        metrics.append({"name": "goldsrcops.leaderboard.last_result", "gauge": {"dataPoints": [
            {"asInt": "1", "timeUnixNano": str(time.time_ns()), "attributes": [
                {"key": "result", "value": {"stringValue": "success"}}]}]}})
        payload = {"resourceMetrics": [{"resource": {"attributes": [
            {"key": "service.name", "value": {"stringValue": "GoldSrcOps"}},
            {"key": "service.instance.id", "value": {"stringValue": "current"}}]},
            "scopeMetrics": [{"scope": {"name": "GoldSrcOps"}, "metrics": metrics}]}]}
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(urllib.request.Request(collector_url + "/v1/metrics", data=json.dumps(payload).encode(),
                                                headers={"Content-Type": "application/json"}), timeout=5) as response:
            assert response.status == 200
        return True
    wait(publish)
    wait(lambda: query("goldsrcops_leaderboard:enabled"))
    assert not query('ALERTS{component="public-leaderboard"}')
    dashboard = wait(lambda: request(graf_url + "/api/dashboards/uid/goldsrcops-public-leaderboard", True))
    assert dashboard["meta"]["provisioned"] and not dashboard["dashboard"]["editable"]
    for panel in dashboard["dashboard"]["panels"]:
        for target in panel.get("targets", []):
            query(target["expr"])
    # Configuration reload keeps the existing provider's immutable dashboard.
    # API-only recovery keeps this read-only panel while its data are unknown.
    publish(available=0)
    wait(lambda: query('ALERTS{alertname="PublicLeaderboardDataUnavailable",alertstate="pending"}'))
    publish()
    wait(lambda: query("goldsrcops_leaderboard:enabled") and not query('ALERTS{component="public-leaderboard"}'))
    publish(available=0, enabled=0)
    wait(lambda: not query("goldsrcops_leaderboard:enabled") and not query('ALERTS{component="public-leaderboard"}'))
    publish()
    wait(lambda: query("goldsrcops_leaderboard:enabled"))
    print("PUBLIC_LEADERBOARD_STACK=OTLP_Grafana_empty_invalid_recovery_disabled_passed", flush=True)
