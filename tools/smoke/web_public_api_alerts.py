"""Synthetic public-read warning policy for the pinned Prometheus stack."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / "ops/production/observability"
RULES = OBS / "rules/web-public-api.yml"
ALERT = "PublicWebApiReadFailures"
METRIC = "goldsrcops_web_public_api_duration_seconds_count"


def series(outcome, values, operation="status", resource="current", service="GoldSrcOps.Web"):
    return {"series": METRIC + '{job="goldsrcops",service_name="' + service
            + '",service_instance_id="' + resource + '",operation="' + operation
            + '",outcome="' + outcome + '"}', "values": values}


def fixtures():
    # Reuse annotation prose only; expectations do not derive from expressions.
    annotations = {}
    for line in RULES.read_text(encoding="utf-8").splitlines():
        if re.match(r"          (summary|description|runbook_url): ", line):
            key, value = line.strip().split(": ", 1)
            annotations[key] = json.loads(value)
    tests = []
    def case(name, data, checks):
        alerts, expressions = [], []
        for minute, state, operations in checks:
            expected = [{"exp_labels": {"severity": "warning", "component": "web-public-api", "operation": op},
                         "exp_annotations": annotations} for op in operations] if state == "firing" else []
            alerts.append({"eval_time": str(minute) + "m", "alertname": ALERT, "exp_alerts": expected})
            expressions.append({"eval_time": str(minute) + "m", "expr": 'ALERTS{alertname="' + ALERT + '"}',
                                "exp_samples": [{"labels": 'ALERTS{alertname="' + ALERT + '",alertstate="' + state
                                   + '",component="web-public-api",operation="' + op + '",severity="warning"}', "value": 1}
                                   for op in operations]})
        tests.append({"name": name, "interval": "1m", "input_series": data,
                      "alert_rule_test": alerts, "promql_expr_test": expressions})
    healthy = series("success", "0+2x40")
    failed = [series("success", "0+1x40"), series("error", "0+1x40")]
    case("healthy-with-no-failure-series", [healthy], [(20, "", [])])
    case("sustained-error-hold-and-fire", failed, [(9, "pending", ["status"]), (10, "firing", ["status"])])
    case("timeout-only", [series("timeout", "0+2x40")], [(10, "firing", ["status"])])
    case("mixed-failures", [series("success", "0+2x40"), series("error", "0+1x40"), series("timeout", "0+1x40")], [(10, "firing", ["status"])])
    case("optional-404", [series("not_found", "0+2x40", "leaderboard")], [(20, "", [])])
    case("canceled-only", [series("canceled", "0+50x40")], [(20, "", [])])
    case("cancellation-does-not-dilute", failed + [series("canceled", "0+100x40")], [(10, "firing", ["status"])])
    case("cancellation-does-not-fill-minimum", [series("error", "0 5+0x39"), series("canceled", "0+100x40")], [(10, "", [])])
    case("too-few-failures", [healthy, series("error", "0 4+0x39")], [(10, "", [])])
    case("exact-minimum-and-window-expiry", [series("success", "0 5+0x39"), series("error", "0 5+0x39")], [(5, "pending", ["status"]), (6, "firing", ["status"]), (20, "", [])])
    case("below-ratio", [series("success", "0+81x40"), series("error", "0+19x40")], [(20, "", [])])
    case("exact-ratio", [series("success", "0+4x40"), series("error", "0+1x40")], [(10, "firing", ["status"])])
    case("successful-reads-resolve", [series("success", "0+1x10 110+100x30"), series("error", "0+1x10 10+0x30")], [(10, "firing", ["status"]), (12, "", [])])
    case("no-observations", [], [(20, "", [])])
    case("idle-counters", [series("error", "100+0x40"), series("success", "100+0x40")], [(20, "", [])])
    case("missing-scrapes-age-out-not-recovery", [series("error", "0+2x10 stale _x30")], [(10, "firing", ["status"]), (30, "", [])])
    case("counter-reset-is-not-new-failure", [healthy, series("error", "100+0x10 0+0x30")], [(20, "", [])])
    case("different-operation-does-not-dilute", [series("timeout", "0+2x40", "history"), healthy], [(10, "firing", ["history"])])
    case("retained-old-instance-has-no-increase", [healthy, series("error", "100+0x40", resource="previous")], [(20, "", [])])
    case("foreign-service-ignored", [healthy, series("error", "0+50x40", service="other")], [(20, "", [])])
    case("unknown-labels-ignored", [healthy, series("error", "0+50x40", "unknown"), series("unknown", "0+50x40")], [(20, "", [])])
    case("all-four-operations", [series("timeout", "0+2x40", op) for op in ("status", "history", "leaderboard", "server")], [(10, "firing", ["history", "leaderboard", "server", "status"])])
    return {"rule_files": ["/rules/web-public-api.yml"], "evaluation_interval": "1m", "tests": tests}


def validate_rules(docker, mount, image, temporary):
    fixture = fixtures()
    path = Path(temporary) / "web-public-api-tests.json"
    path.write_text(json.dumps(fixture, ensure_ascii=False), encoding="utf-8")
    path.chmod(0o644)
    docker("run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
           "--security-opt", "no-new-privileges", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m",
           "--mount", mount(OBS / "rules", "/rules"), "--mount", mount(path, "/fixtures.json"),
           "--entrypoint", "/bin/promtool", image, "test", "rules", "/fixtures.json")
    print("WEB_PUBLIC_API_ALERTS=" + str(len(fixture["tests"])) + "_scenarios_passed", flush=True)


def check_stack(prom_url, graf_url, query, request, wait, docker, prometheus, config_path):
    rules = request(prom_url + "/api/v1/rules")["data"]["groups"]
    group = next(g for g in rules if g["name"] == "goldsrcops-web-public-api")
    assert len(group["rules"]) == 3 and all(r["health"] == "ok" for r in group["rules"])
    assert not query('ALERTS{component="web-public-api"}')
    assert not query("goldsrcops_web_public_api:reads_15m")
    dashboard = wait(lambda: request(graf_url + "/api/dashboards/uid/goldsrcops-web-public-api", True))
    assert dashboard["meta"]["provisioned"] and not dashboard["dashboard"]["editable"]
    expected = json.loads((OBS / "grafana/dashboards/goldsrcops-web-public-api.json").read_text(encoding="utf-8"))
    assert dashboard["dashboard"]["panels"] == expected["panels"]
    for panel in dashboard["dashboard"]["panels"]:
        for target in panel.get("targets", []):
            assert not query(target["expr"])
    before = request(prom_url + "/api/v1/status/runtimeinfo")["data"]["lastConfigTime"]
    identity = json.loads(docker("inspect", prometheus))[0]
    docker("kill", "--signal", "HUP", prometheus)
    wait(lambda: request(prom_url + "/api/v1/status/runtimeinfo")["data"]["lastConfigTime"] != before)
    assert request(prom_url + "/api/v1/status/runtimeinfo")["data"]["reloadConfigSuccess"]
    after = json.loads(docker("inspect", prometheus))[0]
    assert after["Id"] == identity["Id"] and after["State"]["StartedAt"] == identity["State"]["StartedAt"]
    # Exercise the same in-place write + SIGHUP recovery boundary as production.
    original = config_path.read_text(encoding="utf-8")
    try:
        config_path.write_text(original.replace("  - /etc/prometheus/rules/web-public-api.yml\n", ""), encoding="utf-8")
        docker("kill", "--signal", "HUP", prometheus)
        wait(lambda: not any(g["name"] == "goldsrcops-web-public-api" for g in request(prom_url + "/api/v1/rules")["data"]["groups"]))
        config_path.write_text("invalid: [", encoding="utf-8")
        docker("kill", "--signal", "HUP", prometheus)
        wait(lambda: not request(prom_url + "/api/v1/status/runtimeinfo")["data"]["reloadConfigSuccess"])
        assert request(prom_url + "/api/v1/rules")["data"]["groups"]
    finally:
        config_path.write_text(original, encoding="utf-8")
        docker("kill", "--signal", "HUP", prometheus)
    wait(lambda: request(prom_url + "/api/v1/status/runtimeinfo")["data"]["reloadConfigSuccess"] and
         any(g["name"] == "goldsrcops-web-public-api" for g in request(prom_url + "/api/v1/rules")["data"]["groups"]))
    after = json.loads(docker("inspect", prometheus))[0]
    assert after["Id"] == identity["Id"] and after["State"]["StartedAt"] == identity["State"]["StartedAt"]
    print("WEB_PUBLIC_API_STACK=rules_empty_data_dashboard_reload_rollback_invalid_recovery_passed", flush=True)
