#!/usr/bin/env python3
"""Local Alertmanager wire/retry/dedup/privacy rehearsal. Never uses a real IRM URL."""
import datetime
import json
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.request
import uuid
import credential_expiry_alerts
import archive_schedule_alerts
import polling_liveness_alerts
import snapshot_retention_liveness_alerts
import command_dispatch_liveness_alerts

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'ops/production/owner-notifications'
IMAGE = 'prom/alertmanager:v0.34.1@sha256:e9733bafb1bdef9b00e25a21f8f99dc26a22224bf16641ad754d1649f4c3357a'
PYTHON = 'python:3.12-alpine@sha256:5789aa194f757888f41fa3d48e14b6feaa1b57c0af0b56fcb1372d6474d3bff0'
ALERTS = ['GameEventAgentMonitorUnavailable', 'GameEventAgentObservationInvalid',
          'GameEventAgentWorkerProblem', 'GameEventAgentQueueDelayed', 'GameEventAgentSpoolProblem',
          'PlayerBackupMonitorUnavailable', 'PlayerBackupObservationInvalid',
          'PlayerBackupInterventionRequired', 'PlayerBackupScheduleProblem', 'PlayerBackupCopyUnavailable',
          'GameHostStorageMonitorUnavailable','GameHostStorageObservationInvalid',
          'GameHostStorageBytesLow','GameHostStorageInodesLow',
          'GameHostResourceMonitorUnavailable', 'GameHostResourceObservationInvalid', 'GameHostMemoryLow']
ALERTS += ['CredentialExpiryMonitorUnavailable'] + [
    'CredentialExpiry' + purpose + state
    for purpose in ('GrafanaMetrics', 'GrafanaLogs', 'ArchiveReader', 'ArchiveWriter')
    for state in ('Soon', 'Critical', 'Expired', 'Unknown')]
ALERTS += archive_schedule_alerts.alert_names() + polling_liveness_alerts.alert_names() + snapshot_retention_liveness_alerts.alert_names() + command_dispatch_liveness_alerts.alert_names()
COMPONENTS = {name: 'command-dispatch-liveness' if name.startswith('CommandDispatch') else 'snapshot-retention-liveness' if name.startswith('SnapshotRetention') else 'polling-liveness' if name.startswith('Polling') else 'archive-schedule' if name.startswith('ArchiveSchedule') else 'credential-expiry' if name.startswith('CredentialExpiry') else 'game-host-resources' if name.startswith(('GameHostResource', 'GameHostMemory')) else
              'game-host-storage' if name.startswith('GameHostStorage') else
              'player-backup' if name.startswith('PlayerBackup') else 'game-event-agent' for name in ALERTS}
SAFE = ['--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--pids-limit', '64']
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))
SERVER = '''
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
attempts=[]
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def do_GET(self):
  self.send_response(200); self.end_headers(); self.wfile.write(json.dumps(attempts).encode())
 def do_POST(self):
  if int(self.headers.get('Content-Length','0'))>32768:
   self.send_response(413); self.end_headers(); return
  payload=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
  code=500 if not attempts else 200
  attempts.append({'code':code,'payload':payload})
  self.send_response(code); self.end_headers()
HTTPServer(('0.0.0.0',8080),Handler).serve_forever()
'''


def docker(*args, timeout=120):
    result = subprocess.run(['docker', *map(str, args)], capture_output=True, text=True,
                            encoding='utf-8', timeout=timeout)
    if result.returncode:
        raise RuntimeError('Local notification check failed: ' + result.stderr[-2000:])
    return result.stdout.strip()


def mount(source, target):
    return 'type=bind,source=' + str(source) + ',target=' + target + ',readonly'


def request(url, value=None):
    data = json.dumps(value).encode() if value is not None else None
    with OPENER.open(urllib.request.Request(url, data, {'Content-Type': 'application/json'}), timeout=5) as response:
        body = response.read(1024 * 1024)
        return json.loads(body) if body else None


def wait(check, seconds=35):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            value = check()
            if value:
                return value
        except (OSError, ValueError):
            pass
        time.sleep(0.5)
    raise AssertionError('Notification fixture did not reach expected state.')


def endpoint(name, port):
    value = json.loads(docker('inspect', name))[0]['NetworkSettings']['Ports'][str(port) + '/tcp'][0]
    assert value['HostIp'] == '127.0.0.1'
    return 'http://127.0.0.1:' + value['HostPort']


def check_projection(payload, state):
    name = payload['groupLabels']['alertname']
    assert name in ALERTS and payload['status'] == state
    assert payload['groupKey'] == 'goldsrcops.production.' + name
    assert set(payload) == {'groupKey', 'status', 'receiver', 'groupLabels', 'commonLabels',
                            'commonAnnotations', 'externalURL', 'alerts'}
    assert payload['groupLabels'] == {'alertname': name, 'project': 'goldsrcops', 'environment': 'production'}
    labels = {'alertname': name, 'component': COMPONENTS[name], 'severity': 'warning'}
    assert payload['commonLabels'] == labels
    assert isinstance(payload['alerts'], list) and len(payload['alerts']) == 1
    row = payload['alerts'][0]
    assert set(row) == {'status', 'labels', 'annotations', 'startsAt', 'endsAt', 'fingerprint'}
    assert row['status'] == state and row['labels'] == labels
    runbook = 'v2.130-command-dispatch-liveness.md' if COMPONENTS[name] == 'command-dispatch-liveness' else 'v2.127-retention-liveness.md' if COMPONENTS[name] == 'snapshot-retention-liveness' else 'v2.121-polling-liveness.md' if COMPONENTS[name] == 'polling-liveness' else 'v2.117-archive-reliability.md' if COMPONENTS[name] == 'archive-schedule' else 'v2.116-credential-notifications.md' if COMPONENTS[name] == 'credential-expiry' else 'v2.105-game-resource-monitor.md' if COMPONENTS[name] == 'game-host-resources' else 'v2.104-game-storage-monitor.md' if COMPONENTS[name] == 'game-host-storage' else 'game-player-data-monitoring.md' if COMPONENTS[name] == 'player-backup' else 'v2.97-agent-alerts.md'
    annotations = {'summary': 'GoldSrcOps - ' + name,
                   'runbook_url': 'https://github.com/tov-vl/gold-src-ops/blob/main/docs/' + runbook}
    assert payload['commonAnnotations'] == annotations and row['annotations'] == annotations
    assert 'fixture-secret' not in json.dumps(payload) and 'generatorURL' not in json.dumps(payload)


def check_prometheus(temporary, prefix, containers):
    """Exercise the production relabel rules in the real pinned Prometheus process."""
    sink = prefix + '-prom-sink'
    receiver = temporary / 'prom-receiver.py'
    receiver.write_text(SERVER.replace('500 if not attempts else 200', '200'), encoding='utf-8')
    receiver.chmod(0o644)
    containers.append(sink)
    docker('run', '-d', '--name', sink, '--network', prefix, *SAFE,
           '--user', '65534:65534', '--publish', '127.0.0.1::8080',
           '--mount', mount(receiver, '/receiver.py'), PYTHON, 'python', '-B', '/receiver.py')
    rules = []
    for name in ALERTS:
        component = COMPONENTS[name]
        for candidate_component, severity in ((component, 'warning'), ('unrelated', 'warning'),
                *[(other,'warning') for other in set(COMPONENTS.values()) if other != component],
                (component, 'critical')):
            rules.append({'alert': name, 'expr': 'vector(1)',
                          'labels': {'component': candidate_component, 'severity': severity,
                                     'private_host': 'fixture-secret-host'}})
    for component in sorted(set(COMPONENTS.values())):
        rules.append({'alert': 'UnrelatedWarning', 'expr': 'vector(1)',
                      'labels': {'component': component, 'severity': 'warning'}})
    path = temporary / 'routing-rules.json'
    path.write_text(json.dumps({'groups': [{'name': 'routing-matrix', 'rules': rules}]}), encoding='utf-8')
    path.chmod(0o644)
    original = (ROOT / 'ops/production/observability/prometheus.yml').read_text(encoding='utf-8')
    config = original[:original.index('rule_files:')] + 'rule_files:\n  - /rules.json\n\n' + original[original.index('alerting:'):original.index('scrape_configs:')] + 'scrape_configs: []\n'
    config = config.replace('evaluation_interval: 15s', 'evaluation_interval: 1s')
    config = config.replace('owner-alertmanager:9093', sink + ':8080')
    config_path = temporary / 'routing-prometheus.yml'
    config_path.write_text(config, encoding='utf-8')
    config_path.chmod(0o644)
    env = dict(line.split('=', 1) for line in (ROOT / 'ops/production/deployment.env.example').read_text().splitlines()
               if '=' in line and not line.startswith('#'))
    image = env['GOLDSRCOPS_PROMETHEUS_IMAGE']
    credential_expiry_alerts.validate_rules(docker, mount, image, temporary)
    archive_schedule_alerts.validate_rules(docker, mount, image, temporary)
    polling_liveness_alerts.validate_rules(docker, mount, image, temporary)
    snapshot_retention_liveness_alerts.validate_rules(docker, mount, image, temporary)
    command_dispatch_liveness_alerts.validate_rules(docker, mount, image, temporary)
    name = prefix + '-prom'
    containers.append(name)
    docker('run', '-d', '--name', name, '--network', prefix, *SAFE, '--publish', '127.0.0.1::9090',
           '--tmpfs', '/prometheus:rw,nosuid,size=64m,uid=65534,gid=65534',
           '--mount', mount(config_path, '/config.yml'), '--mount', mount(path, '/rules.json'),
           image, '--config.file=/config.yml', '--storage.tsdb.path=/prometheus', '--rules.alert.resend-delay=1s')
    prom_url, sink_url = endpoint(name, 9090), endpoint(sink, 8080)
    wait(lambda: request(prom_url + '/api/v1/status/runtimeinfo'))
    wait(lambda: request(prom_url + '/api/v1/alertmanagers')['data']['activeAlertmanagers'])
    wait(lambda: len(request(prom_url + '/api/v1/alerts')['data']['alerts']) == len(rules))
    def complete_batch():
        value = request(sink_url)
        names = {r['labels']['alertname'] for batch in value for r in batch['payload']}
        return value if len(names) == len(ALERTS) else None
    try:
        sent = wait(complete_batch)
    except AssertionError:
        print(json.dumps(request(prom_url + '/api/v1/alerts')), flush=True)
        print(json.dumps(request(prom_url + '/api/v1/alertmanagers')), flush=True)
        raise
    rows = [alert for batch in sent for alert in batch['payload']]
    assert {(r['labels']['alertname'], r['labels']['component']) for r in rows} == set(COMPONENTS.items())
    assert all(set(r['labels']) == {'alertname', 'component', 'severity'} and r['labels']['severity'] == 'warning'
               for r in rows)
    print('OWNER_PROMETHEUS_ROUTING=' + str(len(rules)) + '_inputs_' + str(len(ALERTS)) + '_allowed_exact_component_pairs_no_private_labels_passed', flush=True)


def main():
    prefix = 'owner-notifications-' + uuid.uuid4().hex[:10]
    containers = []
    network = False
    volume = False
    with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
        temporary = Path(temporary)
        config = temporary / 'alertmanager.yml'
        original = (SOURCE / 'alertmanager.yml').read_text(encoding='utf-8')
        # Short timers are fixture-only; the production policy stays 30s/5m/6h.
        config.write_text(original.replace('group_wait: 30s', 'group_wait: 1s')
                          .replace('group_interval: 5m', 'group_interval: 2s')
                          .replace('repeat_interval: 6h', 'repeat_interval: 2m'), encoding='utf-8')
        secret = temporary / 'fixture-url'
        secret.write_text('http://' + prefix + '-sink:8080/\n', encoding='utf-8')
        receiver = temporary / 'receiver.py'
        receiver.write_text(SERVER, encoding='utf-8')
        for file in (config, secret, receiver):
            file.chmod(0o644)
        try:
            env = temporary / 'fixture.env'
            env.write_text('GOLDSRCOPS_TELEMETRY_NETWORK=fixture-telemetry\n'
                           'GOLDSRCOPS_OWNER_ALERTMANAGER_IP=172.29.249.7\n'
                           'GOLDSRCOPS_IRM_HEARTBEAT_FILE=' + secret.as_posix() + '\n'
                           'GOLDSRCOPS_IRM_WEBHOOK_FILE=' + secret.as_posix() + '\n', encoding='utf-8')
            rendered = json.loads(docker('compose', '--env-file', env, '-f', SOURCE / 'compose.yml',
                                         'config', '--format', 'json'))
            assert set(rendered['services']) == {'alertmanager'}
            service = rendered['services']['alertmanager']
            assert service['image'] == IMAGE and service['user'] == '65534:65534'
            assert not service.get('ports') and service['read_only'] and service['cap_drop'] == ['ALL']
            assert rendered['networks']['telemetry']['external'] is True
            assert service['networks']['outbound']['gw_priority'] == 1
            assert 'group_wait: 30s' in original and 'group_interval: 5m' in original and 'repeat_interval: 6h' in original
            for file in (SOURCE / 'alertmanager.yml', config):
                docker('run', '--rm', '--network', 'none', *SAFE, '--mount', mount(file, '/config.yml'),
                       '--entrypoint', '/bin/amtool', IMAGE, 'check-config', '/config.yml')
            docker('network', 'create', prefix)
            network = True
            docker('volume', 'create', prefix)
            volume = True
            sink = prefix + '-sink'
            containers.append(sink)
            docker('run', '-d', '--name', sink, '--network', prefix, *SAFE,
                   '--user', '65534:65534', '--publish', '127.0.0.1::8080',
                   '--mount', mount(receiver, '/receiver.py'), PYTHON, 'python', '-B', '/receiver.py')
            am = prefix + '-am'
            containers.append(am)
            docker('run', '-d', '--name', am, '--network', prefix, *SAFE,
                   '--publish', '127.0.0.1::9093', '--mount', 'type=volume,source=' + prefix + ',target=/alertmanager',
                   '--mount', mount(config, '/config.yml'), '--mount', mount(secret, '/run/secrets/irm-webhook-url'),
                   '--mount', mount(secret, '/run/secrets/irm-heartbeat-url'),
                   IMAGE, '--config.file=/config.yml', '--storage.path=/alertmanager',
                   '--cluster.listen-address=', '--data.maintenance-interval=1s')
            sink_url, am_url = endpoint(sink, 8080), endpoint(am, 9093)
            wait(lambda: request(am_url + '/api/v2/status'))
            starts = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(seconds=5)

            def alert(name, resolved=False):
                ends = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=-1 if resolved else 300)
                return {'labels': {'alertname': name, 'component': COMPONENTS.get(name, 'game-event-agent'), 'severity': 'warning',
                                   'private_host': 'fixture-secret-host'},
                        'annotations': {'summary': 'fixture-secret-annotation', 'private': 'fixture-secret-value'},
                        'generatorURL': 'http://fixture-secret-host:9090/private-query',
                        'startsAt': starts.isoformat(), 'endsAt': ends.isoformat()}

            request(am_url + '/api/v2/alerts', [alert(name) for name in ALERTS])
            request(am_url + '/api/v2/alerts', [alert(name) for name in ALERTS])
            # Unknown alert names/components must not leave the local Alertmanager.
            invalid = [alert('UnrelatedWarning')]
            for name in ALERTS:
                for component, severity in (('unrelated', 'warning'),
                        *[(other,'warning') for other in set(COMPONENTS.values()) if other != COMPONENTS[name]],
                        (COMPONENTS[name], 'critical')):
                    invalid.append(dict(alert(name), labels={'alertname': name, 'component': component, 'severity': severity}))
            request(am_url + '/api/v2/alerts', invalid)
            sent = wait(lambda: (v if len([x for x in v if x['code'] == 200]) >= len(ALERTS) else None)
                        if (v := request(sink_url)) else None)
            assert len([x for x in sent if x['code'] == 500]) == 1
            assert len([x for x in sent if x['code'] == 200]) == len(ALERTS)
            for item in sent:
                check_projection(item['payload'], 'firing')
            firing_keys = {x['payload']['groupKey'] for x in sent}
            assert len(firing_keys) == len(ALERTS)
            # Invalid reload must retain the accepted route and secret reference.
            config.write_text('route: [invalid\n', encoding='utf-8')
            docker('kill', '--signal', 'HUP', am)
            def reload_status(expected):
                with OPENER.open(am_url + '/metrics', timeout=5) as response:
                    text = response.read(1024 * 1024).decode()
                return 'alertmanager_config_last_reload_successful ' + str(expected) + '\n' in text
            wait(lambda: reload_status(0))
            config.write_text(original.replace('group_wait: 30s', 'group_wait: 1s')
                              .replace('group_interval: 5m', 'group_interval: 2s')
                              .replace('repeat_interval: 6h', 'repeat_interval: 2m'), encoding='utf-8')
            docker('kill', '--signal', 'HUP', am)
            wait(lambda: reload_status(1))
            time.sleep(3)
            assert len(request(sink_url)) == len(sent), 'Duplicate firing reached receiver.'
            # A graceful restart must retain the successful notification log.
            docker('restart', am)
            am_url = endpoint(am, 9093)
            wait(lambda: request(am_url + '/api/v2/status'))
            request(am_url + '/api/v2/alerts', [alert(name) for name in ALERTS])
            time.sleep(4)
            assert len(request(sink_url)) == len(sent), 'Restart lost notification deduplication.'
            request(am_url + '/api/v2/alerts', [alert(name, True) for name in ALERTS])
            resolved = wait(lambda: (v if len([x for x in v if x['payload']['status'] == 'resolved']) == len(ALERTS) else None)
                            if (v := request(sink_url)) else None)
            recoveries = [x['payload'] for x in resolved if x['payload']['status'] == 'resolved']
            assert {x['groupKey'] for x in recoveries} == firing_keys
            assert all(x['alerts'][0]['status'] == 'resolved' for x in recoveries)
            for payload in recoveries:
                check_projection(payload, 'resolved')
            time.sleep(3)
            assert len(request(sink_url)) == len(resolved)
            check_prometheus(temporary, prefix, containers)
            print('OWNER_NOTIFICATIONS=compose_' + str(len(ALERTS)) + '_firing_resolved_retry_dedup_reload_restart_privacy_passed', flush=True)
        except Exception:
            for name in containers:
                result = subprocess.run(['docker', 'logs', '--tail', '12', name], capture_output=True,
                                        text=True, encoding='utf-8', timeout=10)
                print((result.stdout + result.stderr)[-2500:])
            raise
        finally:
            for name in reversed(containers):
                docker('rm', '-f', name)
            if network:
                docker('network', 'rm', prefix)
            if volume:
                docker('volume', 'rm', prefix)


if __name__ == '__main__':
    main()
