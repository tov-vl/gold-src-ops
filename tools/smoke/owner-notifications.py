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

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'ops/production/owner-notifications'
IMAGE = 'prom/alertmanager:v0.34.1@sha256:e9733bafb1bdef9b00e25a21f8f99dc26a22224bf16641ad754d1649f4c3357a'
PYTHON = 'python:3.12-alpine@sha256:5789aa194f757888f41fa3d48e14b6feaa1b57c0af0b56fcb1372d6474d3bff0'
ALERTS = ['GameEventAgentMonitorUnavailable', 'GameEventAgentObservationInvalid',
          'GameEventAgentWorkerProblem', 'GameEventAgentQueueDelayed', 'GameEventAgentSpoolProblem']
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
                   IMAGE, '--config.file=/config.yml', '--storage.path=/alertmanager',
                   '--cluster.listen-address=', '--data.maintenance-interval=1s')
            sink_url, am_url = endpoint(sink, 8080), endpoint(am, 9093)
            wait(lambda: request(am_url + '/api/v2/status'))
            starts = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(seconds=5)

            def alert(name, resolved=False):
                ends = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=-1 if resolved else 300)
                return {'labels': {'alertname': name, 'component': 'game-event-agent', 'severity': 'warning',
                                   'private_host': 'fixture-secret-host'},
                        'annotations': {'summary': 'fixture-secret-annotation', 'private': 'fixture-secret-value'},
                        'generatorURL': 'http://fixture-secret-host:9090/private-query',
                        'startsAt': starts.isoformat(), 'endsAt': ends.isoformat()}

            request(am_url + '/api/v2/alerts', [alert(name) for name in ALERTS])
            request(am_url + '/api/v2/alerts', [alert(name) for name in ALERTS])
            # Unknown alert names/components must not leave the local Alertmanager.
            invalid = alert('UnrelatedWarning')
            request(am_url + '/api/v2/alerts', [invalid, dict(alert(ALERTS[0]), labels={
                'alertname': ALERTS[0], 'component': 'unrelated', 'severity': 'warning'})])
            sent = wait(lambda: (v if len([x for x in v if x['code'] == 200]) >= 5 else None)
                        if (v := request(sink_url)) else None)
            assert len([x for x in sent if x['code'] == 500]) == 1
            assert len([x for x in sent if x['code'] == 200]) == 5
            for item in sent:
                payload = item['payload']
                assert payload['status'] == 'firing'
                assert payload['groupKey'] == 'goldsrcops.production.' + payload['groupLabels']['alertname']
                assert isinstance(payload['alerts'], list) and len(payload['alerts']) == 1
                assert payload['alerts'][0]['status'] == 'firing'
                expected_summary = 'GoldSrcOps - ' + payload['groupLabels']['alertname']
                assert payload['commonAnnotations']['summary'] == expected_summary
                assert payload['alerts'][0]['annotations']['summary'] == expected_summary
                assert 'fixture-secret' not in json.dumps(payload)
                assert 'generatorURL' not in json.dumps(payload)
            firing_keys = {x['payload']['groupKey'] for x in sent}
            assert len(firing_keys) == 5
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
            resolved = wait(lambda: (v if len([x for x in v if x['payload']['status'] == 'resolved']) == 5 else None)
                            if (v := request(sink_url)) else None)
            recoveries = [x['payload'] for x in resolved if x['payload']['status'] == 'resolved']
            assert {x['groupKey'] for x in recoveries} == firing_keys
            assert all(x['alerts'][0]['status'] == 'resolved' for x in recoveries)
            time.sleep(3)
            assert len(request(sink_url)) == len(resolved)
            print('OWNER_NOTIFICATIONS=compose_five_firing_five_resolved_retry_dedup_reload_restart_privacy_passed', flush=True)
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
