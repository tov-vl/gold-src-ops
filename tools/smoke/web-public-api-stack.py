#!/usr/bin/env python3
"""Disposable Web -> OTLP -> Prometheus/Grafana rehearsal; synthetic API only."""
import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import urllib.parse
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
OBS = ROOT / 'ops/production/observability'
spec = importlib.util.spec_from_file_location('stack', ROOT / 'tools/smoke/player-data-monitor-stack.py')
stack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stack)
docker, mount, wait = stack.docker, stack.mount, stack.wait
METRIC = 'goldsrcops_web_public_api_duration_seconds'
LABELS = '{job="goldsrcops",service_name="GoldSrcOps.Web"}'

FIXTURE = r'''
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var mode = "success";
app.MapGet("/fixture/{next}", (string next) => { mode = next; return Results.Ok(); });
app.MapGet("/api/public/{operation}", async (string operation, HttpContext context) =>
{
    var current = mode;
    if (current == "error") { context.Response.StatusCode = 503; return; }
    if (current == "not_found") { context.Response.StatusCode = 404; return; }
    var now = DateTimeOffset.UtcNow;
    object body = operation switch
    {
        "status" => new { state = "operational", monitoredServers = 1, onlineServers = 1, serversRequiringAttention = 0, openIncidents = 0, lastObservedAtUtc = now },
        "server" => new { name = "Fixture", host = "play.example.test", port = 27015, state = "online", map = "fixture", players = 0, maxPlayers = 16, lastObservedAtUtc = now },
        "leaderboard" => new { state = "fresh", capturedAtUtc = now, entries = Array.Empty<object>() },
        _ => new { window = "24h", fromUtc = now, toUtc = now, bucketMinutes = 60, observedBuckets = 0, totalBuckets = 24, buckets = Array.Empty<object>() }
    };
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsync("{");
    await context.Response.Body.FlushAsync();
    if (current is "slow" or "timeout") await Task.Delay(current == "slow" ? 1200 : 7000, context.RequestAborted);
    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(body)[1..]);
});
app.Run();
'''


def raw(url):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(url, timeout=10) as response:
        return response.status, response.read(2 * 1024 * 1024)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--web-image', required=True, help='Locally built image from the reviewed Web tree.')
    args = parser.parse_args()
    environment = dict(line.split('=', 1) for line in (ROOT / 'ops/production/deployment.env.example').read_text().splitlines() if '=' in line and not line.startswith('#'))
    images = {key: environment['GOLDSRCOPS_' + key + '_IMAGE'] for key in ('OTEL_COLLECTOR', 'PROMETHEUS', 'GRAFANA')}
    prefix = 'public-api-' + uuid.uuid4().hex[:10]
    containers, networks = [], []
    safe = ['--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges']
    with tempfile.TemporaryDirectory(prefix='public-api-') as temporary:
        temporary = Path(temporary)
        fixture = temporary / 'fixture'
        fixture.mkdir()
        (fixture / 'Fixture.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
        (fixture / 'Program.cs').write_text(FIXTURE)
        result = subprocess.run(['dotnet', 'publish', str(fixture / 'Fixture.csproj'), '-o', str(fixture / 'publish'), '--verbosity', 'quiet'], capture_output=True, text=True, timeout=120)
        assert result.returncode == 0, result.stdout + result.stderr
        config = temporary / 'prometheus.yml'
        config.write_text('global:\n  scrape_interval: 1s\nscrape_configs:\n  - job_name: goldsrcops\n    static_configs:\n      - targets: ["otel-collector:9464"]\n')
        dashboards = temporary / 'dashboards'
        dashboards.mkdir()
        dashboard_file = dashboards / 'public-api.json'
        dashboard_file.write_bytes((OBS / 'grafana/dashboards/goldsrcops-web-public-api.json').read_bytes())
        ratio = next(p for p in json.loads(dashboard_file.read_text(encoding='utf-8'))['panels'] if p['id'] == 5)['targets'][0]['expr']
        def series(outcome, values):
            return {'series': METRIC + '_count{job="goldsrcops",service_name="GoldSrcOps.Web",operation="status",outcome="' + outcome + '"}', 'values': values}
        tests = []
        for name, inputs, expected in [
            ('healthy', [series('success', '0+60x15')], [{'labels': '{operation="status"}', 'value': 0}]),
            ('failures-exclude-cancellation', [series('success', '0+30x15'), series('not_found', '0+30x15'), series('timeout', '0+60x15'), series('canceled', '0+600x15')], [{'labels': '{operation="status"}', 'value': 0.5}]),
            ('no-observations', [], [])]:
            tests.append({'name': name, 'interval': '1m', 'input_series': inputs, 'promql_expr_test': [{'expr': ratio, 'eval_time': '15m', 'exp_samples': expected}]})
        prom_tests = temporary / 'promql-tests.json'
        prom_tests.write_text(json.dumps({'rule_files': [], 'evaluation_interval': '1m', 'tests': tests}))
        docker('run', '--rm', '--network', 'none', *safe, '--tmpfs', '/tmp:rw,noexec,nosuid,size=32m', '--mount', mount(prom_tests, '/tests.json'), '--entrypoint', '/bin/promtool', images['PROMETHEUS'], 'test', 'rules', '/tests.json')
        try:
            edge, telemetry = prefix + '-edge', prefix + '-telemetry'
            docker('network', 'create', edge); networks.append(edge)
            docker('network', 'create', '--internal', telemetry); networks.append(telemetry)

            def start(suffix, options, internal=False):
                name = prefix + '-' + suffix
                containers.append(name)
                network = ['--network', telemetry, '--network-alias', suffix] if internal else ['--network', 'name=' + edge + ',gw-priority=1', '--network', 'name=' + telemetry + ',alias=' + suffix]
                docker('run', '-d', '--name', name, *network, *safe, *options)
                return name

            def endpoint(name, port):
                address = docker('port', name, str(port) + '/tcp')
                assert address.startswith('127.0.0.1:')
                return 'http://' + address

            collector = start('otel-collector', ['--mount', mount(OBS / 'otel-collector.yml', '/config.yml'), images['OTEL_COLLECTOR'], '--config=/config.yml'], internal=True)
            api = start('fixture', ['--publish', '127.0.0.1::8080', '--tmpfs', '/tmp', '--mount', mount(fixture / 'publish', '/fixture'), '--entrypoint', 'dotnet', args.web_image, '/fixture/Fixture.dll'])
            api_url = endpoint(api, 8080)
            # The API fixture belongs to edge only, as does the real Web -> API path.
            docker('network', 'disconnect', telemetry, api)
            api_ip = json.loads(docker('inspect', api))[0]['NetworkSettings']['Networks'][edge]['IPAddress']
            prometheus = start('prometheus', ['--publish', '127.0.0.1::9090', '--tmpfs', '/prometheus:uid=65534,gid=65534,mode=0700', '--mount', mount(config, '/etc/prometheus/prometheus.yml'), images['PROMETHEUS']])
            grafana = start('grafana', ['--publish', '127.0.0.1::3000', '--tmpfs', '/var/lib/grafana:uid=472,gid=0,mode=0700', '--tmpfs', '/var/log/grafana:uid=472,gid=0,mode=0700', '--tmpfs', '/tmp', '--env', 'GF_SECURITY_ADMIN_PASSWORD=fixture-monitor-only', '--env', 'GF_PLUGINS_PREINSTALL_DISABLED=true', '--env', 'GF_ANALYTICS_REPORTING_ENABLED=false', '--mount', mount(OBS / 'grafana/provisioning', '/etc/grafana/provisioning'), '--mount', mount(dashboards, '/etc/grafana/dashboards'), images['GRAFANA']])
            web_options = ['--publish', '127.0.0.1::8080', '--tmpfs', '/tmp', '--env', 'Authentication__Enabled=false', '--env', 'DataProtection__PersistKeys=false', '--env', 'GoldSrcOpsApi__BaseUrl=http://' + api_ip + ':8080/', '--env', 'Telemetry__Otlp__Enabled=true', '--env', 'Telemetry__Otlp__Endpoint=http://otel-collector:4317', args.web_image]
            web = start('web', web_options)
            web_url, prom_url, graf_url = endpoint(web, 8080), endpoint(prometheus, 9090), endpoint(grafana, 3000)
            wait(lambda: raw(web_url + '/health/live')[0] == 200)
            wait(lambda: raw(api_url + '/fixture/success')[0] == 200)

            def query(expression):
                result = stack.request(prom_url + '/api/v1/query?' + urllib.parse.urlencode({'query': expression}))
                assert result['status'] == 'success'
                return result['data']['result']

            wait(lambda: stack.request(graf_url + '/api/health')['database'] == 'ok')
            dashboard = wait(lambda: stack.request(graf_url + '/api/dashboards/uid/goldsrcops-web-public-api', True))
            assert dashboard['meta']['provisioned'] and not dashboard['dashboard']['editable']
            # A fresh empty deployment must not synthesize healthy zeroes.
            for panel in dashboard['dashboard']['panels']:
                for target in panel.get('targets', []):
                    assert query(target['expr']) == []
            for route in ('/', '/play', '/leaderboard'):
                assert raw(web_url + route)[0] == 200
            for mode in ('slow', 'not_found', 'error', 'timeout'):
                raw(api_url + '/fixture/' + mode)
                assert raw(web_url + '/leaderboard')[0] == 200
            raw(api_url + '/fixture/success')
            values = wait(lambda: query(METRIC + '_count' + LABELS), seconds=100)
            observed = {(x['metric']['operation'], x['metric']['outcome']): float(x['value'][1]) for x in values}
            assert all(observed.get((operation, 'success'), 0) >= 1 for operation in ('status', 'history', 'leaderboard', 'server')), observed
            assert all(observed.get(('leaderboard', outcome), 0) >= 1 for outcome in ('not_found', 'error', 'timeout')), observed
            sums = query(METRIC + '_sum{job="goldsrcops",service_name="GoldSrcOps.Web",operation="leaderboard",outcome="timeout"}')
            assert 4.8 <= float(sums[0]['value'][1]) < 6.5
            successful = query(METRIC + '_sum{job="goldsrcops",service_name="GoldSrcOps.Web",operation="leaderboard",outcome="success"}')
            assert float(successful[0]['value'][1]) >= 1.2
            for value in values:
                assert set(value['metric']) <= {'__name__', 'instance', 'job', 'exported_job', 'exported_instance', 'service_name', 'service_instance_id', 'telemetry_sdk_name', 'telemetry_sdk_language', 'telemetry_sdk_version', 'otel_scope_name', 'otel_scope_version', 'otel_scope_schema_url', 'operation', 'outcome'}, set(value['metric'])
            try:
                raw(web_url + '/metrics')
                raise AssertionError('Web unexpectedly exposes metrics')
            except urllib.error.HTTPError as error:
                assert error.code == 404
            for panel in dashboard['dashboard']['panels']:
                for target in panel.get('targets', []):
                    query(target['expr'])
            # Dedicated continuity and network rollback rehearsal, only this disposable stack.
            docker('stop', collector)
            assert raw(web_url + '/leaderboard')[0] == 200
            docker('network', 'disconnect', telemetry, web)
            assert raw(web_url + '/play')[0] == 200
            docker('network', 'connect', telemetry, web)
            docker('start', collector)
            assert raw(web_url + '/')[0] == 200
            wait(lambda: any(float(value['value'][1]) > observed[('status', 'success')] for value in query(METRIC + '_count{job="goldsrcops",service_name="GoldSrcOps.Web",operation="status",outcome="success"}')), seconds=100)
            snapshot = json.loads(docker('inspect', web))[0]
            assert snapshot['RestartCount'] == 0 and snapshot['State']['Running']
            assert json.loads(docker('network', 'inspect', telemetry))[0]['Internal']
            assert not json.loads(docker('inspect', collector))[0]['HostConfig']['PortBindings']
            print(json.dumps({'passed': True, 'operations': 4, 'runtime_outcomes': ['success', 'not_found', 'error', 'timeout'], 'dashboard_queries': 6, 'empty_state': True, 'collector_outage': True, 'network_rollback': True, 'public_metrics': False}))
        except Exception:
            if 'web' in locals():
                print(docker('logs', web)[-6000:])
            if 'api' in locals():
                print(docker('logs', api)[-6000:])
            raise
        finally:
            for container in reversed(containers):
                docker('rm', '-f', container)
            for network in reversed(networks):
                docker('network', 'rm', network)


if __name__ == '__main__':
    main()
