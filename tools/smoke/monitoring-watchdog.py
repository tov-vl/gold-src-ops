#!/usr/bin/env python3
"""Pinned Prometheus/Alertmanager heartbeat rehearsal; local receiver only."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("owner", Path(__file__).with_name("owner-notifications.py"))
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)
docker, mount, request, endpoint = owner.docker, owner.mount, owner.request, owner.endpoint
AM_SOURCE = ROOT / "ops/production/owner-notifications"
OBS = ROOT / "ops/production/observability"
EXPECTED = {"project": "goldsrcops", "environment": "production", "heartbeat": "monitoring-pipeline"}
LABELS = {"alertname": "MonitoringHeartbeat", "component": "monitoring-pipeline", "severity": "none"}
SERVER = '''
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
attempts=[]
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def do_GET(self):
  self.send_response(200); self.end_headers(); self.wfile.write(json.dumps(attempts).encode())
 def do_POST(self):
  length=int(self.headers.get("Content-Length","0"))
  if not 0 < length <= 32768:
   self.send_response(413); self.end_headers(); return
  data=json.loads(self.rfile.read(length))
  heartbeat=[a for a in attempts if a["path"]=="/heartbeat"]
  code=307 if self.path=="/redirect" else 503 if self.path=="/heartbeat" and not heartbeat else 200
  attempts.append({"path":self.path,"code":code,"payload":data})
  self.send_response(code)
  if code==307: self.send_header("Location","/forwarded")
  self.end_headers()
HTTPServer(("0.0.0.0",8080),Handler).serve_forever()
'''


def wait(check, seconds=40):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            result = check()
            if result:
                return result
        except (OSError, ValueError):
            pass
        time.sleep(0.3)
    raise AssertionError("Local watchdog boundary did not reach its expected state.")


def write(path, text):
    path.write_text(text, encoding="utf-8")
    path.chmod(0o644)


def main():
    original = (AM_SOURCE / "alertmanager.yml").read_text(encoding="utf-8")
    heartbeat = (OBS / "rules/monitoring-heartbeat.yml").read_text(encoding="utf-8")
    assert "group_interval: 1m\n      repeat_interval: 1m" in original
    assert 'send_resolved: false' in original and "expr: vector(1)" in heartbeat
    assert "keep_firing_for" not in heartbeat and "for:" not in heartbeat
    fixture_rules = heartbeat
    for key, value in (("component", "unrelated"), ("severity", "warning"),
                       ("alertname", "MonitoringHeartbeatOther")):
        labels = {**LABELS, key: value}
        fixture_rules += ("\n      - alert: " + labels.pop("alertname") + "\n"
                          "        expr: vector(1)\n        labels:\n" +
                          "".join("          " + k + ": " + v + "\n" for k, v in labels.items()))
    env = dict(line.split("=", 1) for line in (ROOT / "ops/production/deployment.env.example").read_text().splitlines()
               if "=" in line and not line.startswith("#"))
    prom_image = env["GOLDSRCOPS_PROMETHEUS_IMAGE"]
    prefix = "monitoring-watchdog-" + uuid.uuid4().hex[:10]
    containers, network, volume = [], False, False
    with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
        temp = Path(temporary)
        config, rules = temp / "alertmanager.yml", temp / "rules.yml"
        prod = (OBS / "prometheus.yml").read_text(encoding="utf-8")
        prom_text = ("global:\n  evaluation_interval: 1s\nrule_files: [/rules.yml]\n" +
                     prod[prod.index("alerting:"):prod.index("scrape_configs:")] + "scrape_configs: []\n")
        prom_text = prom_text.replace("owner-alertmanager:9093", prefix + "-am:9093")
        prom_config = temp / "prometheus.yml"
        fast = (original.replace("group_interval: 1m", "group_interval: 1s")
                .replace("repeat_interval: 1m", "repeat_interval: 2s")
                .replace("group_wait: 30s", "group_wait: 1s"))
        write(config, fast)
        write(rules, fixture_rules)
        write(prom_config, prom_text)
        receiver = temp / "receiver.py"
        write(receiver, SERVER)
        url, hb_url = temp / "owner-url", temp / "heartbeat-url"
        write(url, "http://" + prefix + "-sink:8080/owner\n")
        write(hb_url, "http://" + prefix + "-sink:8080/heartbeat\n")
        def rows():
            return request(endpoint(prefix + "-sink", 8080))
        def received():
            return [r for r in rows() if r["path"] == "/heartbeat" and r["code"] == 200]
        def start_am():
            docker("run", "-d", "--name", prefix + "-am", "--network", prefix, *owner.SAFE,
                   "--publish", "127.0.0.1::9093",
                   "--mount", "type=volume,source=" + prefix + ",target=/alertmanager",
                   "--mount", mount(config, "/config.yml"),
                   "--mount", mount(url, "/run/secrets/irm-webhook-url"),
                   "--mount", mount(hb_url, "/run/secrets/irm-heartbeat-url"),
                   owner.IMAGE, "--config.file=/config.yml", "--storage.path=/alertmanager",
                   "--cluster.listen-address=")
        def am_post(labels, resolved=False):
            now = dt.datetime.now(dt.timezone.utc)
            request(endpoint(prefix + "-am", 9093) + "/api/v2/alerts", [{
                "labels": labels, "annotations": {"private": "fixture-secret"},
                "generatorURL": "http://fixture-secret/private",
                "startsAt": (now - dt.timedelta(seconds=5)).isoformat(),
                "endsAt": (now + dt.timedelta(seconds=-1 if resolved else 40)).isoformat()}])
        try:
            for path in (AM_SOURCE / "alertmanager.yml", config):
                docker("run", "--rm", "--network", "none", *owner.SAFE,
                       "--mount", mount(path, "/config.yml"), "--entrypoint", "/bin/amtool",
                       owner.IMAGE, "check-config", "/config.yml")
            compose_env = temp / "fixture.env"
            write(compose_env, "GOLDSRCOPS_TELEMETRY_NETWORK=fixture-telemetry\n"
                  "GOLDSRCOPS_OWNER_ALERTMANAGER_IP=172.29.249.7\n"
                  "GOLDSRCOPS_IRM_WEBHOOK_FILE=" + url.as_posix() + "\n"
                  "GOLDSRCOPS_IRM_HEARTBEAT_FILE=" + hb_url.as_posix() + "\n")
            rendered = json.loads(docker("compose", "--env-file", compose_env, "-f", AM_SOURCE / "compose.yml",
                                         "config", "--format", "json"))
            service = rendered["services"]["alertmanager"]
            assert len(rendered["services"]) == 1 and not service.get("ports")
            assert {s["source"] for s in service["secrets"]} == {"irm-webhook-url", "irm-heartbeat-url"}
            assert service["image"] == owner.IMAGE and service["read_only"]
            docker("network", "create", prefix)
            network = True
            docker("volume", "create", prefix)
            volume = True
            containers.append(prefix + "-sink")
            docker("run", "-d", "--name", containers[-1], "--network", prefix, *owner.SAFE,
                   "--user", "65534:65534", "--publish", "127.0.0.1::8080",
                   "--mount", mount(receiver, "/receiver.py"), owner.PYTHON, "python", "-B", "/receiver.py")
            containers.append(prefix + "-am")
            start_am()
            wait(lambda: request(endpoint(prefix + "-am", 9093) + "/api/v2/status"))
            containers.append(prefix + "-prom")
            docker("run", "-d", "--name", containers[-1], "--network", prefix, *owner.SAFE,
                   "--publish", "127.0.0.1::9090",
                   "--tmpfs", "/prometheus:rw,nosuid,size=64m,uid=65534,gid=65534",
                   "--mount", mount(prom_config, "/config.yml"), "--mount", mount(rules, "/rules.yml"),
                   prom_image, "--config.file=/config.yml", "--storage.tsdb.path=/prometheus",
                   "--rules.alert.resend-delay=1s")
            prom_url = endpoint(prefix + "-prom", 9090)
            wait(lambda: len(received()) >= 3)
            assert any(r["code"] == 503 for r in rows())
            assert all(r["payload"] == EXPECTED for r in rows())
            assert all(r["path"] == "/heartbeat" for r in rows()), "Heartbeat entered owner warnings."
            alerts = request(prom_url + "/api/v1/alerts")["data"]["alerts"]
            assert len(alerts) == 4 and all(a["state"] == "firing" for a in alerts)
            forwarded = request(endpoint(prefix + "-am", 9093) + "/api/v2/alerts")
            assert len(forwarded) == 1 and forwarded[0]["labels"] == LABELS
            print("WATCHDOG=real_prometheus_repeat_retry_private_projection_passed", flush=True)
            # Wrong tuple and unknown names must not escape to either receiver.
            for key, value in (("component", "unrelated"), ("severity", "warning"),
                               ("alertname", "MonitoringHeartbeatOther")):
                am_post({**LABELS, key: value})
            time.sleep(3)
            assert all(r["path"] == "/heartbeat" and r["payload"] == EXPECTED for r in rows())
            warning = {"alertname": owner.ALERTS[0], "component": "game-event-agent", "severity": "warning"}
            am_post(warning)
            wait(lambda: any(r["path"] == "/owner" for r in rows()))
            owner.check_projection(next(r["payload"] for r in rows() if r["path"] == "/owner"), "firing")
            am_post(warning, True)
            print("WATCHDOG=exact_tuple_and_existing_owner_route_passed", flush=True)
            # Deleting only the fixture rule must end heartbeats, not send a resolved ping.
            write(rules, "groups: []\n")
            docker("kill", "--signal", "HUP", prefix + "-prom")
            wait(lambda: not request(prom_url + "/api/v1/alerts")["data"]["alerts"])
            time.sleep(5)
            count = len(received())
            time.sleep(5)
            assert len(received()) == count, "Resolved heartbeat falsely prolonged health."
            write(rules, fixture_rules)
            docker("kill", "--signal", "HUP", prefix + "-prom")
            wait(lambda: len(received()) > count)
            # The real Alertmanager must not follow an endpoint redirect.
            write(hb_url, "http://" + prefix + "-sink:8080/redirect\n")
            docker("kill", "--signal", "HUP", prefix + "-am")
            wait(lambda: any(r["path"] == "/redirect" for r in rows()))
            count = len(received())
            time.sleep(4)
            assert len(received()) == count and not any(r["path"] == "/forwarded" for r in rows())
            write(hb_url, "http://" + prefix + "-sink:8080/heartbeat\n")
            docker("kill", "--signal", "HUP", prefix + "-am")
            wait(lambda: len(received()) > count)
            print("WATCHDOG=rule_removal_recovery_and_redirect_refusal_passed", flush=True)
            # A stopped Alertmanager cannot be masked by a separate cron pinger.
            docker("stop", "--time", "5", prefix + "-am")
            count = len(received())
            time.sleep(5)
            assert len(received()) == count
            docker("start", prefix + "-am")
            wait(lambda: len(received()) > count)
            # Prometheus loss has a bounded retained-alert tail, not instantaneous loss.
            docker("stop", "--time", "5", prefix + "-prom")
            time.sleep(8)
            count = len(received())
            time.sleep(5)
            assert len(received()) == count, "Expired source kept sending healthy heartbeats."
            docker("start", prefix + "-prom")
            wait(lambda: len(received()) > count)
            assert all(r["payload"] == EXPECTED for r in rows() if r["path"] != "/owner")
            print("WATCHDOG=sender_loss_retained_expiry_and_recovery_passed", flush=True)
        finally:
            for name in reversed(containers):
                docker("rm", "-f", name)
            if network:
                docker("network", "rm", prefix)
            if volume:
                docker("volume", "rm", prefix)


if __name__ == "__main__":
    main()
