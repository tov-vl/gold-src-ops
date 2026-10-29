"""Synthetic agent telemetry and exact dashboard API lifecycle on the pinned stack."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import time
import urllib.request
from unittest import mock

ROOT = Path(__file__).resolve().parents[2]
PREFIX = 'goldsrcops_game_event_agent_'
LABELS = '{job="goldsrcops",service_name="goldsrcops-game-event-agent-monitor"}'


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def check_stack(collector_url, graf_url, query, request, wait):
    fixtures = load('agent_fixture', 'tools/smoke/agent-monitor.py')
    monitor = fixtures.monitor
    contract = fixtures.contract
    dashboard = json.loads((ROOT / 'ops/production/agent-monitor-dashboard.json').read_bytes())
    uid = dashboard['uid']
    previous = request(graf_url + '/api/dashboards/uid/goldsrcops-player-backup', True)
    import base64
    headers = {'Authorization': 'Basic ' + base64.b64encode(b'admin:fixture-monitor-only').decode(),
               'Content-Type': 'application/json'}
    def api(path, method, value=None):
        body = json.dumps(value).encode() if value is not None else None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(urllib.request.Request(graf_url + path, data=body, headers=headers, method=method), timeout=5) as response:
            return json.loads(response.read(1024 * 1024))
    created = False
    try:
        result = api('/api/dashboards/db', 'POST', {'dashboard': dashboard, 'folderUid': previous['meta']['folderUid'], 'overwrite': False})
        assert result['status'] == 'success' and result['uid'] == uid
        created = True
        current = request(graf_url + '/api/dashboards/uid/' + uid, True)
        assert not current['meta']['provisioned'] and not current['dashboard']['editable']
        assert current['dashboard']['panels'] == dashboard['panels']
        status_expr = next(p for p in dashboard['panels'] if p['title'] == 'Наблюдение')['targets'][0]['expr']
        pending_expr = next(p for p in dashboard['panels'] if p['title'] == 'Pending')['targets'][0]['expr']
        loop_expr = next(p for p in dashboard['panels'] if p['title'] == 'Импорт')['targets'][0]['expr']
        active_expr = next(p for p in dashboard['panels'] if p['title'] == 'Процесс агента')['targets'][0]['expr']
        real_connection = monitor.http.client.HTTPConnection
        port = int(collector_url.rsplit(':', 1)[1])
        def publish(samples):
            with mock.patch.object(monitor.http.client, 'HTTPConnection',
                    lambda address, target_port, timeout: real_connection('127.0.0.1', port, timeout=timeout)):
                monitor.publish('127.0.0.1', monitor.payload(samples, dt.datetime.now(contract.UTC)))
        def value(expr):
            values = query(expr)
            assert len(values) == 1, values
            return float(values[0]['value'][1])
        def observation(change=None, active=True):
            fixtures.NOW = dt.datetime.now(contract.UTC)
            raw = fixtures.snapshot()
            if change:
                change(raw)
            return monitor.observe(contract.project(raw, active, fixtures.NOW), fixtures.NOW)
        publish(observation())
        wait(lambda: value(status_expr) == 1 and value(loop_expr) == 3)
        assert value(pending_expr) == 0
        for panel in dashboard['panels']:
            for target in panel.get('targets', []):
                query(target['expr'])  # All exact production PromQL must parse.
        def backlog(raw):
            raw['queue']['pending'] = 3
            raw['queue']['inFlight'] = 1
            raw['queue']['deadLetter'] = 2
            raw['spool'].update(ready=5, processing=4, rejected=6)
            raw['oldestWaitingAgeSeconds'] = 10
            raw['health']['delivery']['cycle']['outcome'] = 'retrying'
        publish(observation(backlog))
        wait(lambda: value(pending_expr) == 3)
        expected = {'Pending': 3, 'In-flight': 1, 'Dead-letter': 2, 'Ready': 5, 'Processing': 4, 'Rejected': 6}
        for panel in dashboard['panels']:
            if panel['type'] == 'timeseries':
                for target in panel['targets']:
                    assert value(target['expr']) == expected[target['legendFormat']]
        assert value(PREFIX + 'loop_state' + LABELS[:-1] + ',loop="delivery"}') == 6
        spool_expr = next(p for p in dashboard['panels'] if p['id'] == 12)['targets'][0]['expr']
        def missing_spool(raw):
            backlog(raw)
            raw['spoolAvailable'] = False
        publish(observation(missing_spool))
        wait(lambda: value(pending_expr) == 3 and not query(spool_expr))
        assert value(PREFIX + 'spool_ready' + LABELS) == 5
        def stuck(raw):
            cycle = raw['health']['import']['cycle']
            cycle['lastStartedAtUtc'] = (fixtures.NOW - dt.timedelta(seconds=40)).isoformat()
            cycle['lastCompletedAtUtc'] = (fixtures.NOW - dt.timedelta(seconds=39)).isoformat()
            cycle['lastSuccessfulAtUtc'] = cycle['lastCompletedAtUtc']
        publish(observation(stuck))
        wait(lambda: value(loop_expr) == 4)
        publish(observation(active=False))
        wait(lambda: value(active_expr) == 0 and value(loop_expr) == 8)
        # Exporter still holds the previous queue gauge after failed observation.
        publish(observation(backlog))
        wait(lambda: value(pending_expr) == 3)
        publish(monitor.observe({'schemaVersion': 1, 'available': False}, dt.datetime.now(contract.UTC)))
        wait(lambda: value(status_expr) == 0 and not query(pending_expr))
        assert value(PREFIX + 'queue_pending' + LABELS) == 3
        # Simulate a frozen collector heartbeat without waiting three minutes.
        old = time.time() - 181
        frozen = [(name, old if name in ('observed_timestamp_seconds', 'source_observed_timestamp_seconds') else number, loop)
                  for name, number, loop in observation(backlog)]
        publish(frozen)
        wait(lambda: value(status_expr) == 0 and not query(pending_expr))
        future = [(name, time.time() + 3600 if name == 'source_observed_timestamp_seconds' else number, loop)
                  for name, number, loop in observation(backlog)]
        publish(future)
        wait(lambda: value(status_expr) == 0 and not query(pending_expr))
        publish(observation())
        wait(lambda: value(status_expr) == 1 and value(pending_expr) == 0 and value(loop_expr) == 3)
        assert request(graf_url + '/api/dashboards/uid/goldsrcops-player-backup', True)['dashboard'] == previous['dashboard']
        print('AGENT_MONITOR_STACK=idle_backlog_stale_stopped_unavailable_clock_recovery_dashboard_passed', flush=True)
    finally:
        if created:
            # Only the fixture's new API-managed dashboard is removed. Existing
            # provisioned dashboards and the provider deletion policy are untouched.
            result = api('/api/dashboards/uid/' + uid, 'DELETE')
            assert result['title'] == dashboard['title']
