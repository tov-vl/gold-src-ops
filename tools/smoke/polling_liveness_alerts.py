"""Offline polling lifecycle policy fixtures; no live game or IRM access."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUMMARIES = {
    'PollingMonitorUnavailable': 'Active polling observation is unavailable',
    'PollingCycleStalled': 'Polling cycle has no recent completion',
    'PollingCycleFailed': 'Polling cycles are failing'}


def alert_names():
    return list(SUMMARIES)


def fixtures():
    base = {'enabled': '1+0x20', 'started_timestamp_seconds': '1+0x20',
            'last_started_timestamp_seconds': '0+60x20', 'last_finished_timestamp_seconds': '0+60x20',
            'observed_timestamp_seconds': '0+60x20', 'cycle_budget_seconds': '370+0x20', 'state': '1+0x20'}
    tests = []
    def case(name, updates, checks, extra=()):
        series = dict(base, **updates)
        inputs = [{'series': 'goldsrcops_polling_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="active"}', 'values': value}
                  for key, value in series.items() if value is not None]
        inputs += list(extra)
        tests.append({'name': name, 'interval': '1m', 'input_series': inputs,
            'alert_rule_test': [{'eval_time': str(minute) + 'm', 'alertname': alert,
                'exp_alerts': [{'exp_labels': {'component': 'polling-liveness', 'severity': 'warning'},
                    'exp_annotations': {'summary': SUMMARIES[alert], 'runbook_url': 'https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.121-polling-liveness.md'}}]
                    if alert in expected else []} for minute, expected in checks for alert in alert_names()]})

    case('running-including-empty-and-failed-server-responses', {}, [(10, [])])
    case('startup-unknown-is-not-healthy-or-a-failure', {'state': '0+0x7 4+0x13', 'last_finished_timestamp_seconds': '0+0x20'}, [(6, []), (8, []), (10, ['PollingCycleStalled'])])
    case('stall-held-even-if-state-is-retained-running', {'last_finished_timestamp_seconds': '1+0x20'}, [(8, []), (9, ['PollingCycleStalled'])])
    case('failed-held', {'state': '2+0x20'}, [(2, []), (3, ['PollingCycleFailed'])])
    case('single-failure-recovers-before-hold', {'state': '1 2 1+0x18'}, [(10, [])])
    case('failure-resolves', {'state': '2+0x5 1+0x14'}, [(4, ['PollingCycleFailed']), (7, [])])
    case('stall-resolves', {'last_finished_timestamp_seconds': '1+0x10 660+60x9'}, [(9, ['PollingCycleStalled']), (12, [])])
    case('missing-observer', {'observed_timestamp_seconds': None}, [(4, []), (5, ['PollingMonitorUnavailable'])])
    case('retained-collector-cannot-refresh-worker', {'observed_timestamp_seconds': '1+0x20'}, [(6, []), (9, ['PollingMonitorUnavailable'])])
    case('future-clock', {'observed_timestamp_seconds': '99999+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('invalid-state', {'state': '1.5+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('invalid-budget', {'cycle_budget_seconds': '299+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('unbounded-budget', {'cycle_budget_seconds': '86401+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('missing-progress', {'last_finished_timestamp_seconds': None}, [(5, ['PollingMonitorUnavailable'])])
    case('future-progress', {'last_finished_timestamp_seconds': '99999+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('incoherent-enabled-disabled-state', {'state': '3+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    case('disabled-is-not-an-active-poller', {'enabled': '0+0x20', 'state': '3+0x20'}, [(5, ['PollingMonitorUnavailable'])])
    duplicate = [{'series': 'goldsrcops_polling_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="duplicate"}', 'values': value} for key, value in base.items()]
    case('multiple-enabled-sources-are-not-merged', {}, [(6, ['PollingMonitorUnavailable'])], duplicate)
    disabled = [{'series': 'goldsrcops_polling_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="http-only"}', 'values': '0+0x20' if key == 'enabled' else '3+0x20' if key == 'state' else value} for key, value in base.items()]
    case('disabled-replica-does-not-hide-active-failure', {'state': '2+0x20'}, [(4, ['PollingCycleFailed'])], disabled)
    case('failed-cycle-then-stall-is-not-a-double-alert', {'state': '2+0x20', 'last_finished_timestamp_seconds': '1+0x20'}, [(4, ['PollingCycleFailed']), (8, []), (9, ['PollingCycleStalled'])])
    retained = [dict(item, values='1+0x20') if 'observed_timestamp' in item['series'] else item for item in duplicate]
    case('old-instance-overlap-expires-before-hold', {}, [(6, []), (10, [])], retained)
    return {'rule_files': ['/rules/polling-liveness.yml'], 'evaluation_interval': '1m', 'tests': tests}


def validate_rules(docker, mount, image, temporary):
    path = Path(temporary) / 'polling-rule-tests.json'
    value = fixtures()
    path.write_text(json.dumps(value), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
           '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(ROOT / 'ops/production/observability/rules', '/rules'),
           '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('POLLING_LIVENESS_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)
