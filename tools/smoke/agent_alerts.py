"""Independent synthetic warning expectations for the pinned Prometheus engine."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / 'ops/production/observability'
RULES = OBS / 'rules/game-event-agent.yml'
ALERTS = ['GameEventAgentMonitorUnavailable', 'GameEventAgentObservationInvalid',
          'GameEventAgentWorkerProblem', 'GameEventAgentQueueDelayed', 'GameEventAgentSpoolProblem']
PREFIX = 'goldsrcops_game_event_agent_'
LABELS = '{job="goldsrcops",service_name="goldsrcops-game-event-agent-monitor"}'


def fixtures():
    annotations, current = {}, None
    for line in RULES.read_text(encoding='utf-8').splitlines():
        if '      - alert: ' in line:
            current = line.split(': ', 1)[1]
            annotations[current] = {}
        elif current and line.strip().startswith(('summary:', 'description:', 'runbook_url:')):
            key, value = line.strip().split(': ', 1)
            annotations[current][key] = json.loads(value)
    tests = []
    defaults = {'observed_timestamp_seconds': '0+60x30', 'source_observed_timestamp_seconds': '0+60x30',
                'observation_success': '1+0x30', 'service_active': '1+0x30', 'queue_pending': '0+0x30',
                'queue_in_flight': '0+0x30', 'queue_dead_letter': '0+0x30', 'spool_available': '1+0x30',
                'spool_rejected': '0+0x30', 'oldest_waiting_age_seconds': '0+0x30',
                'loop_state:import': '3+0x30', 'loop_state:delivery': '3+0x30'}

    def case(name, changes, checks, empty=False):
        data = {} if empty else {**defaults, **changes}
        inputs = []
        for key, values in data.items():
            if values is None:
                continue
            metric, _, loop = key.partition(':')
            labels = LABELS if not loop else LABELS[:-1] + ',loop="' + loop + '"}'
            inputs.append({'series': PREFIX + metric + labels, 'values': values})
        rules, expressions = [], []
        for minute, expected in checks:
            for alert in ALERTS:
                firing = expected.get(alert) == 'firing'
                rules.append({'eval_time': str(minute) + 'm', 'alertname': alert, 'exp_alerts': [
                    {'exp_labels': {'severity': 'warning', 'component': 'game-event-agent'},
                     'exp_annotations': annotations[alert]}] if firing else []})
            expressions.append({'eval_time': str(minute) + 'm', 'expr': 'ALERTS{component="game-event-agent"}',
                'exp_samples': [{'labels': 'ALERTS{alertname="' + key + '",alertstate="' + state
                    + '",component="game-event-agent",severity="warning"}', 'value': 1} for key, state in expected.items()]})
        tests.append({'name': name, 'interval': '1m', 'input_series': inputs,
                      'alert_rule_test': rules, 'promql_expr_test': expressions})

    monitor, invalid, worker, delayed, spool = ALERTS
    case('empty-idle-loops-are-healthy', {}, [(20, {})])
    case('never-observed-hold-and-fire', {}, [(4, {monitor: 'pending'}), (5, {monitor: 'firing'})], empty=True)
    case('frozen-heartbeat-exact-boundary', {'observed_timestamp_seconds': '0+0x30', 'source_observed_timestamp_seconds': '0+0x30'},
         [(3, {}), (4, {monitor: 'pending'}), (8, {monitor: 'pending'}), (9, {monitor: 'firing'})])
    case('future-heartbeat', {'observed_timestamp_seconds': '99999+0x30'}, [(5, {monitor: 'firing'})])
    case('failed-observation-suppresses-retained-data', {'observation_success': '0+0x30', 'queue_dead_letter': '7+0x30',
         'loop_state:import': '4+0x30'}, [(4, {invalid: 'pending'}), (5, {invalid: 'firing'})])
    case('old-source-with-live-observer', {'source_observed_timestamp_seconds': '0+0x30'}, [(3, {}), (9, {invalid: 'firing'})])
    case('future-source', {'source_observed_timestamp_seconds': '99999+0x30'}, [(5, {invalid: 'firing'})])
    case('missing-success-gauge', {'observation_success': None}, [(5, {invalid: 'firing'})])
    case('disabled-loops-and-intentionally-waiting-delivery', {'loop_state:import': '0+0x30', 'loop_state:delivery': '0+0x30',
         'queue_pending': '2+0x30', 'oldest_waiting_age_seconds': '900+0x30'}, [(20, {})])
    for state in (1, 2, 4, 5, 6, 7, 8):
        case('enabled-loop-state-' + str(state), {'loop_state:import': str(state) + '+0x30'},
             [(4, {worker: 'pending'}), (5, {worker: 'firing'})])
    case('process-stopped-even-with-disabled-loops', {'service_active': '0+0x30', 'loop_state:import': '0+0x30',
         'loop_state:delivery': '0+0x30'}, [(5, {worker: 'firing'})])
    case('missing-loop-is-unknown', {'loop_state:import': None}, [(5, {worker: 'firing'})])
    case('missing-process-state-is-unknown', {'service_active': None}, [(5, {worker: 'firing'})])
    case('transient-worker-recovers-before-hold', {'loop_state:delivery': '5+0x3 3+0x26'}, [(3, {worker: 'pending'}), (4, {}), (10, {})])
    case('worker-fires-then-resolves', {'loop_state:import': '4+0x6 3+0x23'}, [(5, {worker: 'firing'}), (7, {})])
    case('waiting-age-below-threshold', {'queue_pending': '1+0x30', 'oldest_waiting_age_seconds': '299+0x30'}, [(20, {})])
    case('waiting-age-exact-threshold', {'queue_pending': '1+0x30', 'oldest_waiting_age_seconds': '300+0x30'},
         [(4, {delayed: 'pending'}), (5, {delayed: 'firing'})])
    case('waiting-age-grows-with-enqueue-time', {'queue_pending': '1+0x30', 'oldest_waiting_age_seconds': '290+60x30'},
         [(0, {}), (1, {delayed: 'pending'}), (6, {delayed: 'firing'})])
    case('in-flight-is-waiting', {'queue_in_flight': '1+0x30', 'oldest_waiting_age_seconds': '301+0x30'}, [(5, {delayed: 'firing'})])
    case('empty-waiting-queue-ignores-retained-age', {'oldest_waiting_age_seconds': '999+0x30'}, [(20, {})])
    case('queue-drained-resolves', {'queue_pending': '1+0x6 0+0x23', 'oldest_waiting_age_seconds': '900+0x30'},
         [(5, {delayed: 'firing'}), (7, {})])
    case('dead-letters', {'queue_dead_letter': '1+0x30'}, [(5, {spool: 'firing'})])
    case('rejected-files', {'spool_rejected': '1+0x30'}, [(5, {spool: 'firing'})])
    case('unreadable-spool-retained-rejected-is-not-required', {'spool_available': '0+0x30', 'spool_rejected': None}, [(5, {spool: 'firing'})])
    case('quarantine-resolves', {'queue_dead_letter': '1+0x6 0+0x23'}, [(5, {spool: 'firing'}), (7, {})])
    case('observer-loss-replaces-retained-data-alerts', {'queue_dead_letter': '1+0x30', 'observed_timestamp_seconds': '0+60x6 360+0x23',
         'source_observed_timestamp_seconds': '0+60x6 360+0x23'}, [(5, {spool: 'firing'}), (10, {monitor: 'pending'}), (15, {monitor: 'firing'})])
    case('monitor-recovery-removes-warning', {'observed_timestamp_seconds': '0+0x10 660+60x19',
         'source_observed_timestamp_seconds': '0+0x10 660+60x19'}, [(9, {monitor: 'firing'}), (11, {})])
    case('invalid-recovers', {'observation_success': '0+0x6 1+0x23'}, [(5, {invalid: 'firing'}), (7, {})])
    return {'rule_files': ['/rules/game-event-agent.yml'], 'evaluation_interval': '1m', 'tests': tests}


def validate_rules(docker, mount, image, temporary):
    value = fixtures()
    path = Path(temporary) / 'agent-alerts-tests.json'
    path.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
           '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(OBS / 'rules', '/rules'),
           '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('AGENT_ALERT_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)


def check_stack(collector_url, prom_url, graf_url, query, request, wait, docker, prometheus, config_path, rule_path):
    import base64
    import datetime as dt
    import urllib.request
    from unittest import mock
    import agent_monitor_stack
    fixture = agent_monitor_stack.load('alert_stack_fixture', 'tools/smoke/agent-monitor.py')
    monitor, contract = fixture.monitor, fixture.contract
    dashboard = json.loads((ROOT / 'ops/production/agent-alerts-dashboard.json').read_bytes())
    uid = dashboard['uid']
    previous = request(graf_url + '/api/dashboards/uid/goldsrcops-player-backup', True)
    headers = {'Authorization': 'Basic ' + base64.b64encode(b'admin:fixture-monitor-only').decode(), 'Content-Type': 'application/json'}
    def api(path, method, value=None):
        body = json.dumps(value).encode() if value is not None else None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(urllib.request.Request(graf_url + path, data=body, headers=headers, method=method), timeout=5) as response:
            return json.loads(response.read(262144))
    def definitions():
        return {g['name']: [{k: r.get(k) for k in ('name','query','duration','labels','annotations','type')} for r in g['rules']]
                for g in request(prom_url + '/api/v1/rules')['data']['groups']}
    reviewed = definitions()
    assert len(reviewed['goldsrcops-game-event-agent']) == 9
    for group in request(prom_url + '/api/v1/rules')['data']['groups']:
        assert all(x['health'] == 'ok' for x in group['rules'])
    original_rule, original_config = rule_path.read_text(encoding='utf-8'), config_path.read_text(encoding='utf-8')
    identity = json.loads(docker('inspect', prometheus))[0]
    created = False
    real_connection = monitor.http.client.HTTPConnection
    port = int(collector_url.rsplit(':', 1)[1])
    def publish(kind=None):
        fixture.NOW = dt.datetime.now(contract.UTC)
        raw = fixture.snapshot()
        if kind == ALERTS[2]: raw['health']['delivery']['cycle']['outcome'] = 'failed'
        if kind == ALERTS[3]: raw['queue']['pending'] = 1; raw['oldestWaitingAgeSeconds'] = 300
        if kind == ALERTS[4]: raw['queue']['deadLetter'] = 1
        value = {'schemaVersion': 1, 'available': False} if kind == ALERTS[1] else contract.project(raw, True, fixture.NOW)
        samples = monitor.observe(value, fixture.NOW)
        if kind == ALERTS[0]:
            samples = [(name, fixture.NOW.timestamp()-181 if name in ('observed_timestamp_seconds','source_observed_timestamp_seconds') else number, loop) for name,number,loop in samples]
        with mock.patch.object(monitor.http.client,'HTTPConnection', lambda address,target_port,timeout: real_connection('127.0.0.1',port,timeout=timeout)):
            monitor.publish('127.0.0.1',monitor.payload(samples,fixture.NOW))
    try:
        publish()
        wait(lambda: query('goldsrcops_agent:observation_valid') and not query('ALERTS{component="game-event-agent"}'))
        result = api('/api/dashboards/db','POST',{'dashboard':dashboard,'folderUid':previous['meta']['folderUid'],'overwrite':False})
        assert result['status']=='success' and result['uid']==uid
        created = True
        actual=request(graf_url+'/api/dashboards/uid/'+uid,True)
        assert not actual['meta']['provisioned'] and actual['dashboard']['panels']==dashboard['panels']
        panels={name:dashboard['panels'][i+1]['targets'][0]['expr'] for i,name in enumerate(ALERTS)}
        for expr in panels.values():
            assert float(query(expr)[0]['value'][1])==0
            assert not query(expr.replace('time()','(time() + 61)'))
        rule_path.write_text(original_rule.replace('for: 5m','for: 3s'),encoding='utf-8')
        docker('exec',prometheus,'/bin/promtool','check','rules','/etc/prometheus/rules/game-event-agent.yml')
        docker('kill','--signal','HUP',prometheus)
        wait(lambda: any(r.get('duration')==3 for r in definitions()['goldsrcops-game-event-agent'] if r['type']=='alerting'))
        for alert in ALERTS:
            publish(alert)
            wait(lambda: query('ALERTS{alertname="'+alert+'",alertstate="firing"}'))
            assert float(query(panels[alert])[0]['value'][1])==2
            current=query('ALERTS{component="game-event-agent"}')
            assert len(current)==1 and current[0]['metric']['alertname']==alert
            publish()
            wait(lambda: not query('ALERTS{component="game-event-agent"}'))
            assert all(float(query(expr)[0]['value'][1])==0 for expr in panels.values())
        stable=definitions()
        rule_path.write_text('groups: [',encoding='utf-8')
        docker('kill','--signal','HUP',prometheus)
        wait(lambda: not request(prom_url+'/api/v1/status/runtimeinfo')['data']['reloadConfigSuccess'])
        assert definitions()==stable
        rule_path.write_text(original_rule,encoding='utf-8')
        docker('kill','--signal','HUP',prometheus)
        wait(lambda: request(prom_url+'/api/v1/status/runtimeinfo')['data']['reloadConfigSuccess'] and definitions()==reviewed)
        config_path.write_text(original_config.replace('  - /etc/prometheus/rules/game-event-agent.yml\n',''),encoding='utf-8')
        docker('kill','--signal','HUP',prometheus)
        wait(lambda: 'goldsrcops-game-event-agent' not in definitions())
        assert definitions()=={k:v for k,v in reviewed.items() if k!='goldsrcops-game-event-agent'}
    finally:
        rule_path.write_text(original_rule,encoding='utf-8')
        config_path.write_text(original_config,encoding='utf-8')
        docker('kill','--signal','HUP',prometheus)
        wait(lambda: request(prom_url+'/api/v1/status/runtimeinfo')['data']['reloadConfigSuccess'] and definitions()==reviewed)
        if created: api('/api/dashboards/uid/'+uid,'DELETE')
    after=json.loads(docker('inspect',prometheus))[0]
    assert after['Id']==identity['Id'] and after['State']['StartedAt']==identity['State']['StartedAt']
    assert request(graf_url+'/api/dashboards/uid/goldsrcops-player-backup',True)['dashboard']==previous['dashboard']
    print('AGENT_ALERT_STACK=all_five_fire_resolve_dashboard_invalid_reload_rollback_passed',flush=True)
