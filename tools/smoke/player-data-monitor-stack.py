#!/usr/bin/env python3
"""Pinned local Collector/Prometheus/Grafana rehearsal; synthetic data only."""
import base64
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.parse
import urllib.request
import uuid
from unittest import mock
import public_leaderboard_monitor as leaderboard

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / "ops/production/observability"
RULES = OBS / "rules/player-data-backup.yml"
PREFIX = "goldsrcops_player_backup_"
LABELS = '{job="goldsrcops",service_name="goldsrcops-player-backup-monitor"}'
ALERTS = ["PlayerBackupMonitorUnavailable", "PlayerBackupObservationInvalid",
          "PlayerBackupInterventionRequired", "PlayerBackupScheduleProblem", "PlayerBackupCopyUnavailable"]


def docker(*args, timeout=120):
    result = subprocess.run(["docker", *map(str, args)], capture_output=True, text=True, encoding="utf-8", timeout=timeout)
    if result.returncode:
        raise RuntimeError("Local Docker check failed: " + result.stderr[-3000:] + result.stdout[-3000:])
    return result.stdout.strip()


def mount(source, target):
    return "type=bind,source=" + str(source) + ",target=" + target + ",readonly"


def request(url, auth=False):
    headers = {}
    if auth:
        headers["Authorization"] = "Basic " + base64.b64encode(b"admin:fixture-monitor-only").decode()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(urllib.request.Request(url, headers=headers), timeout=5) as response:
        return json.loads(response.read(1024 * 1024))


def wait(check, seconds=90):
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            value = check()
            if value:
                return value
        except Exception as error:
            last = error
        time.sleep(1)
    raise AssertionError("Local stack did not reach the expected state.") from last


def fixtures():
    # Expected alert identities are independent of the production expressions.
    # Reuse prose only so wording edits do not weaken the behavior checks.
    annotations = {}
    current = None
    for line in RULES.read_text(encoding="utf-8").splitlines():
        if "      - alert: " in line:
            current = line.split(": ", 1)[1]
            annotations[current] = {}
        elif current and re.match(r"          (summary|description): ", line):
            key, value = line.strip().split(": ", 1)
            annotations[current][key] = json.loads(value)
    healthy = {
        "observed_timestamp_seconds": "0+60x30", "observation_success": "1+0x30",
        "last_success_timestamp_seconds": "-86400+0x30", "copy_present": "1+0x30",
        "timer_enabled": "1+0x30", "requires_reconciliation": "0+0x30",
        "service_failed": "0+0x30", "missed_cycle": "0+0x30",
        "in_progress": "0+0x30", "last_attempt_status": "1+0x30"}
    scenarios = [
        ("healthy", {}, [], "10m"),
        ("busy", {"last_attempt_status": "2+0x30"}, [], "10m"),
        ("busy-stale", {"last_attempt_status": "2+0x30", "last_success_timestamp_seconds": "-691201+0x30"}, [4], "10m"),
        ("in-progress", {"last_attempt_status": "4+0x30", "in_progress": "1+0x30"}, [], "10m"),
        ("orphan", {"requires_reconciliation": "1+0x30"}, [2], "10m"),
        ("service-failure", {"service_failed": "1+0x30"}, [2], "10m"),
        ("timer-disabled", {"timer_enabled": "0+0x30"}, [3], "10m"),
        ("missed-cycle", {"missed_cycle": "1+0x30"}, [3], "10m"),
        ("no-copy", {"copy_present": "0+0x30", "last_success_timestamp_seconds": "0+0x30"}, [4], "10m"),
        ("age-crosses-threshold", {"last_success_timestamp_seconds": "-690840+0x30"}, [4], "12m"),
        ("invalid-projection", {"observation_success": "0+0x30", "timer_enabled": "0+0x30", "copy_present": "0+0x30"}, [1], "10m"),
        ("frozen-heartbeat", {"observed_timestamp_seconds": "0+60x5 300+0x25"}, [0], "14m"),
        ("disappeared-heartbeat", {"observed_timestamp_seconds": "0+60x5 stale _x25"}, [0], "14m"),
        ("future-heartbeat", {"observed_timestamp_seconds": "5000+60x30"}, [0], "10m"),
        ("brief-error", {"service_failed": "1+0x2 0+0x28"}, [], "10m"),
        ("sustained-error", {"service_failed": "1+0x6 0+0x24"}, [2], "6m"),
        ("recovered-error", {"service_failed": "1+0x6 0+0x24"}, [], "10m"),
        ("not-yet-fired", {"timer_enabled": "0+0x30"}, [], "4m"),
        ("no-observations", None, [0], "10m")]
    tests = []
    for name, changes, expected, evaluation in scenarios:
        values = dict(healthy, **changes) if changes is not None else {}
        tests.append({"name": name, "interval": "1m",
                      "input_series": [{"series": PREFIX + key + LABELS, "values": value} for key, value in values.items()],
                      "alert_rule_test": [
                          {"eval_time": evaluation, "alertname": alert, "exp_alerts":
                           [{"exp_labels": {"severity": "warning", "component": "player-backup"},
                             "exp_annotations": annotations[alert]}] if i in expected else []}
                          for i, alert in enumerate(ALERTS)]})
    return {"rule_files": ["/rules/player-data-backup.yml"], "evaluation_interval": "1m", "tests": tests}


def main():
    environment = {}
    for line in (ROOT / "ops/production/deployment.env.example").read_text().splitlines():
        if "=" in line and not line.startswith("#"):
            key, value = line.split("=", 1)
            environment[key] = value
    images = {key: environment["GOLDSRCOPS_" + key + "_IMAGE"] for key in ("OTEL_COLLECTOR", "PROMETHEUS", "GRAFANA")}
    assert all(re.search(r"@sha256:[a-f0-9]{64}$", value) for value in images.values())
    safe = ["--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges"]
    with tempfile.TemporaryDirectory(prefix="player-monitor-") as temporary:
        leaderboard.validate_rules(docker, mount, images["PROMETHEUS"], temporary)
        path = Path(temporary) / "rules-test.json"
        path.write_text(json.dumps(fixtures(), ensure_ascii=False), encoding="utf-8")
        path.chmod(0o644)
        Path(temporary).chmod(0o755)
        docker("run", "--rm", "--network", "none", *safe,
               "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--mount", mount(OBS / "rules", "/rules"), "--mount", mount(path, "/fixtures.json"),
               "--entrypoint", "/bin/promtool", images["PROMETHEUS"], "test", "rules", "/fixtures.json")
        print("PLAYER_BACKUP_RULES=19_scenarios_passed", flush=True)
    docker("run", "--rm", "--network", "none", *safe, "--mount", mount(OBS / "otel-collector.yml", "/config.yml"),
           images["OTEL_COLLECTOR"], "validate", "--config=/config.yml")
    docker("run", "--rm", "--network", "none", *safe,
           "--mount", mount(OBS / "prometheus.yml", "/etc/prometheus/prometheus.yml"),
           "--mount", mount(OBS / "rules", "/etc/prometheus/rules"),
           "--entrypoint", "/bin/promtool", images["PROMETHEUS"], "check", "config", "/etc/prometheus/prometheus.yml")
    # Keep the production config validation above; accelerate only this disposable stack.
    with tempfile.TemporaryDirectory(prefix="player-monitor-config-") as temporary:
        path = Path(temporary) / "prometheus.yml"
        production = (OBS / "prometheus.yml").read_text(encoding="utf-8")
        timing = "global:\n  scrape_interval: 15s\n  scrape_timeout: 10s\n  evaluation_interval: 15s\n"
        assert production.startswith(timing), "Review smoke timing overrides after production config changes."
        fast_timing = "global:\n  scrape_interval: 1s\n  scrape_timeout: 1s\n  evaluation_interval: 1s\n"
        path.write_text(fast_timing + production[len(timing):], encoding="utf-8")
        path.chmod(0o644)
        Path(temporary).chmod(0o755)
        docker("run", "--rm", "--network", "none", *safe,
               "--mount", mount(path, "/etc/prometheus/prometheus.yml"),
               "--mount", mount(OBS / "rules", "/etc/prometheus/rules"),
               "--entrypoint", "/bin/promtool", images["PROMETHEUS"], "check", "config", "/etc/prometheus/prometheus.yml")
        run_stack(images, safe, path)


def run_stack(images, safe, prometheus_config):
    prefix = "player-monitor-" + uuid.uuid4().hex[:10]
    containers = []
    network_created = False
    try:
        # Docker internal networks suppress loopback publications on some engines.
        # The disposable test bridge publishes only 127.0.0.1; production has no ports.
        docker("network", "create", prefix)
        network_created = True
        def start(suffix, args):
            name = prefix + "-" + suffix
            containers.append(name)
            docker("run", "-d", "--name", name, "--network", prefix, "--network-alias", suffix, *safe, *args)
            return name
        collector = start("otel-collector", ["--publish", "127.0.0.1::4318", "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                          "--mount", mount(OBS / "otel-collector.yml", "/config.yml"),
                          images["OTEL_COLLECTOR"], "--config=/config.yml"])
        prometheus = start("prometheus", ["--publish", "127.0.0.1::9090",
                           "--tmpfs", "/prometheus:rw,noexec,nosuid,uid=65534,gid=65534,mode=0700,size=64m",
                           "--mount", mount(prometheus_config, "/etc/prometheus/prometheus.yml"),
                           "--mount", mount(OBS / "rules", "/etc/prometheus/rules"),
                           images["PROMETHEUS"], "--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.path=/prometheus"])
        grafana = start("grafana", ["--publish", "127.0.0.1::3000",
                        "--tmpfs", "/var/lib/grafana:rw,nosuid,uid=472,gid=0,mode=0700,size=64m",
                        "--tmpfs", "/var/log/grafana:rw,nosuid,uid=472,gid=0,mode=0700,size=16m",
                        "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
                        "--env", "GF_SECURITY_ADMIN_PASSWORD=fixture-monitor-only",
                        "--env", "GF_AUTH_ANONYMOUS_ENABLED=false",
                        "--env", "GF_USERS_ALLOW_SIGN_UP=false",
                        "--env", "GF_ANALYTICS_REPORTING_ENABLED=false",
                        "--env", "GF_ANALYTICS_CHECK_FOR_UPDATES=false",
                        "--env", "GF_ANALYTICS_CHECK_FOR_PLUGIN_UPDATES=false",
                        "--env", "GF_PLUGINS_PREINSTALL_DISABLED=true",
                        "--mount", mount(OBS / "grafana/provisioning", "/etc/grafana/provisioning"),
                        "--mount", mount(OBS / "grafana/dashboards", "/etc/grafana/dashboards"), images["GRAFANA"]])
        def endpoint(name, port):
            address = docker("port", name, str(port) + "/tcp")
            assert re.fullmatch(r"127\.0\.0\.1:\d+", address)
            return "http://" + address
        prom_url, graf_url, collector_url = endpoint(prometheus, 9090), endpoint(grafana, 3000), endpoint(collector, 4318)
        spec = importlib.util.spec_from_file_location("monitor", ROOT / "ops/production/player-data-monitor.py")
        monitor = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(monitor)
        metrics = {key: 0 for key in monitor.KEYS}
        metrics.update(observation_success=1, observed_timestamp_seconds=time.time(),
                       last_success_timestamp_seconds=time.time() - 86400, copy_present=1, fresh=1,
                       timer_enabled=1, last_attempt_status=2)
        actual_connection = monitor.http.client.HTTPConnection
        fixture_port = urllib.parse.urlsplit(collector_url).port
        def publish():
            # Only the test destination is remapped to an ephemeral loopback port.
            with mock.patch.object(monitor.http.client, "HTTPConnection",
                                   lambda address, port, timeout: actual_connection("127.0.0.1", fixture_port, timeout=timeout)):
                monitor.publish("127.0.0.1", monitor.payload(metrics))
            return True
        wait(publish)
        def query(expression):
            result = request(prom_url + "/api/v1/query?" + urllib.parse.urlencode({"query": expression}))
            assert result["status"] == "success"
            return result["data"]["result"]
        wait(lambda: query("goldsrcops_player_backup:observation_valid"))
        result = query(PREFIX + "last_attempt_status" + LABELS)
        assert len(result) == 1 and result[0]["value"][1] == "2", result
        assert not query('ALERTS{component="player-backup"}')
        rules = request(prom_url + "/api/v1/rules")["data"]["groups"]
        backup_rules = next(group for group in rules if group["name"] == "goldsrcops-player-backup")["rules"]
        assert len(backup_rules) == 7 and all(rule["health"] == "ok" for rule in backup_rules)
        wait(lambda: request(graf_url + "/api/health")["database"] == "ok")
        dashboard = wait(lambda: request(graf_url + "/api/dashboards/uid/goldsrcops-player-backup", True))
        assert dashboard["meta"]["provisioned"] and not dashboard["dashboard"]["editable"]
        for panel in dashboard["dashboard"]["panels"]:
            for target in panel.get("targets", []):
                query(target["expr"])
        assert request(graf_url + "/api/datasources/uid/goldsrcops-prometheus/health", True)["status"] == "OK"
        leaderboard.check_stack(collector_url, graf_url, query, request, wait)
        metrics.update(observation_success=0, observed_timestamp_seconds=time.time())
        publish()
        wait(lambda: query('ALERTS{alertname="PlayerBackupObservationInvalid",alertstate="pending"}'))
        assert not query("goldsrcops_player_backup:observation_valid")
        assert not query('ALERTS{alertname="PlayerBackupCopyUnavailable"}')
        metrics.update(observation_success=1, observed_timestamp_seconds=time.time())
        publish()
        wait(lambda: query("goldsrcops_player_backup:observation_valid") and not query('ALERTS{component="player-backup"}'))
        print("PLAYER_BACKUP_STACK=OTLP_metrics_rules_invalid_recovery_Grafana_passed", flush=True)
    except Exception:
        for name in containers:
            print(docker("inspect", "--format", "{{json .State}} {{json .NetworkSettings.Ports}}", name))
            result = subprocess.run(["docker", "logs", "--tail", "12", name], capture_output=True, text=True, encoding="utf-8", timeout=15)
            print((result.stdout + result.stderr)[-3000:])
        raise
    finally:
        for name in reversed(containers):
            docker("rm", "-f", name)
        if network_created:
            docker("network", "rm", prefix)


if __name__ == "__main__":
    main()
