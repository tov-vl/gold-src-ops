"""Independent resource policy and real private telemetry/Grafana checks."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / 'ops/production/observability'
PREFIX = 'goldsrcops_game_host_resource_'
LABELS = 'job="goldsrcops",service_name="goldsrcops-game-host-resource-monitor"'
ALERTS = ('GameHostResourceMonitorUnavailable', 'GameHostResourceObservationInvalid', 'GameHostMemoryLow')


def fixtures():
    annotations, current = {}, None
    for line in (OBS / 'rules/game-host-resources.yml').read_text(encoding='utf-8').splitlines():
        if '      - alert: ' in line:
            current = line.split(': ', 1)[1]
            annotations[current] = {}
        elif current and line.strip().startswith(('summary:', 'description:', 'runbook_url:')):
            key, value = line.strip().split(': ', 1)
            annotations[current][key] = json.loads(value)
    defaults = {name: '0+60x30' for name in ('observed_timestamp_seconds', 'source_observed_timestamp_seconds')}
    for name, value in {'observation_success': 1, 'memory_available': 1, 'cpu_available': 1,
                        'memory_total_bytes': 4 * 1024**3, 'memory_available_bytes': 2 * 1024**3,
                        'swap_total_bytes': 1024**3, 'swap_free_bytes': 512 * 1024**2,
                        'cpu_busy_ratio': 0.2, 'cpu_steal_ratio': 0.05, 'cpu_sample_seconds': 1}.items():
        defaults[name] = str(value) + '+0x30'
    tests = []
    def case(name, changes, checks, empty=False):
        inputs = [{'series': PREFIX + key + '{' + LABELS + '}', 'values': value}
                  for key, value in ({} if empty else {**defaults, **changes}).items() if value is not None]
        rules = []
        for minute, expected in checks:
            for alert in ALERTS:
                rules.append({'eval_time': str(minute) + 'm', 'alertname': alert, 'exp_alerts': [
                    {'exp_labels': {'severity': 'warning', 'component': 'game-host-resources'},
                     'exp_annotations': annotations[alert]}] if alert in expected else []})
        tests.append({'name': name, 'interval': '1m', 'input_series': inputs, 'alert_rule_test': rules})
    unavailable, invalid, low = ALERTS
    case('healthy', {}, [(20, [])])
    case('missing-monitor-hold', {}, [(4, []), (5, [unavailable])], empty=True)
    case('stopped-heartbeat', {'observed_timestamp_seconds': '0+0x30'}, [(3, []), (8, []), (9, [unavailable])])
    case('future-heartbeat', {'observed_timestamp_seconds': '99999+0x30'}, [(5, [unavailable])])
    case('invalid-envelope-masks-old-low-memory', {'observation_success': '0+0x30', 'memory_available_bytes': '0+0x30'}, [(5, [invalid])])
    case('stale-source', {'source_observed_timestamp_seconds': '0+0x30'}, [(9, [invalid])])
    case('future-source', {'source_observed_timestamp_seconds': '99999+0x30'}, [(5, [invalid])])
    case('memory-unavailable-masks-retained-zero', {'memory_available': '0+0x30', 'memory_available_bytes': '0+0x30'}, [(5, [invalid])])
    case('missing-memory-availability', {'memory_available': None}, [(5, [invalid])])
    case('cpu-unavailable', {'cpu_available': '0+0x30'}, [(5, [invalid])])
    case('missing-cpu-availability', {'cpu_available': None}, [(5, [invalid])])
    case('cpu-failure-does-not-hide-valid-low-memory', {'cpu_available': '0+0x30', 'memory_available_bytes': '0+0x30'}, [(5, [invalid, low])])
    case('relative-memory-low', {'memory_available_bytes': str(400 * 1024**2) + '+0x30'}, [(4, []), (5, [low])])
    case('absolute-memory-low', {'memory_total_bytes': str(1024**3) + '+0x30', 'memory_available_bytes': str(200 * 1024**2) + '+0x30'}, [(5, [low])])
    case('memory-zero', {'memory_available_bytes': '0+0x30'}, [(5, [low])])
    case('exact-thresholds-healthy', {'memory_total_bytes': str(2560 * 1024**2) + '+0x30', 'memory_available_bytes': str(256 * 1024**2) + '+0x30'}, [(20, [])])
    case('no-swap-is-not-an-error', {'swap_total_bytes': '0+0x30', 'swap_free_bytes': '0+0x30'}, [(20, [])])
    case('full-swap-alone-is-not-an-alert', {'swap_free_bytes': '0+0x30'}, [(20, [])])
    case('high-cpu-is-observational-only', {'cpu_busy_ratio': '1+0x30', 'cpu_steal_ratio': '0+0x30'}, [(20, [])])
    case('high-steal-is-observational-only', {'cpu_busy_ratio': '0+0x30', 'cpu_steal_ratio': '1+0x30'}, [(20, [])])
    case('transient-recovers-before-hold', {'memory_available_bytes': '0+0x3 ' + str(2 * 1024**3) + '+0x26'}, [(3, []), (4, []), (10, [])])
    case('low-memory-fires-and-resolves', {'memory_available_bytes': '0+0x6 ' + str(2 * 1024**3) + '+0x23'}, [(5, [low]), (7, [])])
    case('invalid-observation-resolves', {'observation_success': '0+0x6 1+0x23'}, [(5, [invalid]), (7, [])])
    return {'rule_files': ['/rules/game-host-resources.yml'], 'evaluation_interval': '1m', 'tests': tests}


def validate_rules(docker, mount, image, temporary):
    value = fixtures()
    path = Path(temporary) / 'resource-rule-tests.json'
    path.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
           '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(OBS / 'rules', '/rules'),
           '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('RESOURCE_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)


def check_stack(collector_url, graf_url, query, request, wait):
    import base64
    import urllib.request
    spec = importlib.util.spec_from_file_location('resource_stack_fixture', ROOT / 'tools/smoke/resource-monitor.py')
    fixture = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fixture)
    monitor, contract = fixture.monitor, fixture.contract
    dashboard = json.loads((ROOT / 'ops/production/resource-dashboard.json').read_bytes())
    uid = dashboard['uid']
    headers = {'Authorization': 'Basic ' + base64.b64encode(b'admin:fixture-monitor-only').decode(),
               'Content-Type': 'application/json'}
    def api(method, path, value=None):
        body = json.dumps(value).encode() if value is not None else None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(urllib.request.Request(graf_url + path, data=body, headers=headers, method=method), timeout=5) as response:
            return json.loads(response.read(262144))
    port = int(collector_url.rsplit(':', 1)[1])
    real = monitor.transport.http.client.HTTPConnection
    def publish(kind=None):
        fixture.NOW = dt.datetime.now(contract.UTC)
        value = fixture.snapshot()
        if kind == 'invalid':
            value = None
        elif kind == 'memory':
            value['memory'] = {'available': False}
        elif kind == 'cpu-low':
            value['cpu'] = {'available': False}
            value['memory']['availableBytes'] = 0
        elif kind == 'low':
            value['memory']['availableBytes'] = 0
        elif kind == 'no-swap':
            value['memory'].update(swapTotalBytes=0, swapFreeBytes=0)
        with mock.patch.object(monitor.transport.http.client, 'HTTPConnection',
                               lambda address, target_port, timeout: real('127.0.0.1', port, timeout=timeout)):
            monitor.transport.publish('127.0.0.1', monitor.payload(monitor.observe(value, fixture.NOW), fixture.NOW))
    api('POST', '/api/dashboards/db', {'dashboard': dashboard, 'overwrite': False})
    pending = lambda name: query('ALERTS{alertname="' + name + '",alertstate="pending"}')
    try:
        publish()
        wait(lambda: query('goldsrcops_resources:memory_valid') and query('goldsrcops_resources:cpu_valid'))
        actual = request(graf_url + '/api/dashboards/uid/' + uid, True)
        assert actual['dashboard']['uid'] == uid and not actual['meta']['provisioned']
        for panel in dashboard['panels']:
            for target in panel.get('targets', []):
                assert query(target['expr'])
        publish('low')
        wait(lambda: pending('GameHostMemoryLow'))
        publish('cpu-low')
        wait(lambda: pending('GameHostResourceObservationInvalid') and pending('GameHostMemoryLow'))
        cpu_panel = next(p for p in dashboard['panels'] if p['id'] == 8)['targets'][0]['expr']
        assert not query(cpu_panel)
        publish('memory')
        wait(lambda: not query('ALERTS{alertname="GameHostMemoryLow"}'))
        memory_panel = next(p for p in dashboard['panels'] if p['id'] == 3)['targets'][0]['expr']
        assert not query(memory_panel)
        assert query(cpu_panel)
        publish('invalid')
        wait(lambda: not query('goldsrcops_resources:observation_valid'))
        for panel in dashboard['panels'][2:]:
            assert not query(panel['targets'][0]['expr'])
        publish('no-swap')
        wait(lambda: query('goldsrcops_resources:observation_valid') and
             not query('ALERTS{component="game-host-resources"}'))
        swap_panel = next(p for p in dashboard['panels'] if p['id'] == 6)['targets'][0]['expr']
        assert query(swap_panel)[0]['value'][1] == '0'
        print('RESOURCE_STACK=OTLP_memory_CPU_independent_failure_stale_masking_Grafana_recovery_passed', flush=True)
    finally:
        api('DELETE', '/api/dashboards/uid/' + uid)
