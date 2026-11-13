"""Offline command-dispatch lifecycle policy fixtures; no live game or IRM access."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUMMARIES = {
    'CommandDispatchMonitorUnavailable': 'Active command dispatch observation is unavailable',
    'CommandDispatchCycleStalled': 'Command dispatch cycle has no recent completion',
    'CommandDispatchCycleFailed': 'Command dispatch cycles are failing'}


def alert_names():
    return list(SUMMARIES)


def fixtures():
    base = {'enabled': '1+0x20', 'started_timestamp_seconds': '1+0x20',
            'last_started_timestamp_seconds': '0+60x20', 'last_finished_timestamp_seconds': '0+60x20',
            'observed_timestamp_seconds': '0+60x20', 'cycle_budget_seconds': '660+0x20', 'state': '1+0x20', 'last_successful_cycle_timestamp_seconds': '0+60x20', 'last_failure_timestamp_seconds': '0+0x20', 'in_progress': '0+0x20', 'consecutive_failures': '0+0x20'}
    tests = []
    def case(name, updates, checks, extra=()):
        series = dict(base, **updates)
        inputs = [{'series': 'goldsrcops_command_dispatch_worker_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="active"}', 'values': value}
                  for key, value in series.items() if value is not None]
        inputs += list(extra)
        tests.append({'name': name, 'interval': '1m', 'input_series': inputs,
            'alert_rule_test': [{'eval_time': str(minute) + 'm', 'alertname': alert,
                'exp_alerts': [{'exp_labels': {'component': 'command-dispatch-liveness', 'severity': 'warning'},
                    'exp_annotations': {'summary': SUMMARIES[alert], 'runbook_url': 'https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.130-command-dispatch-liveness.md'}}]
                    if alert in expected else []} for minute, expected in checks for alert in alert_names()]})

    case('running-including-empty-and-failed-server-responses', {}, [(10, [])])
    case('startup-unknown-is-not-healthy-or-a-failure', {'state': '0+0x7 4+0x13', 'last_finished_timestamp_seconds': '0+0x20'}, [(10, []), (14, ['CommandDispatchCycleStalled'])])
    case('stall-held-even-if-state-is-retained-running', {'last_finished_timestamp_seconds': '1+0x20'}, [(12, []), (14, ['CommandDispatchCycleStalled'])])
    case('failed-held', {'state': '2+0x20', 'consecutive_failures': '2+0x20'}, [(2, []), (3, ['CommandDispatchCycleFailed'])])
    case('single-failure-recovers-before-hold', {'state': '1 2 1+0x18'}, [(10, [])])
    case('failure-resolves', {'state': '2+0x5 1+0x14', 'consecutive_failures': '2+0x5 0+0x14'}, [(4, ['CommandDispatchCycleFailed']), (7, [])])
    case('stall-resolves', {'last_finished_timestamp_seconds': '1+0x15 960+60x4'}, [(14, ['CommandDispatchCycleStalled']), (18, [])])
    case('missing-observer', {'observed_timestamp_seconds': None}, [(4, []), (5, ['CommandDispatchMonitorUnavailable'])])
    case('retained-collector-cannot-refresh-worker', {'observed_timestamp_seconds': '1+0x20'}, [(6, []), (9, ['CommandDispatchMonitorUnavailable'])])
    case('future-clock', {'observed_timestamp_seconds': '99999+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('invalid-state', {'state': '1.5+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('invalid-budget', {'cycle_budget_seconds': '299+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('unbounded-budget', {'cycle_budget_seconds': '4295149+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('missing-progress', {'last_finished_timestamp_seconds': None}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('future-progress', {'last_finished_timestamp_seconds': '99999+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('incoherent-enabled-disabled-state', {'state': '3+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('disabled-is-not-an-active-cleaner', {'enabled': '0+0x20', 'state': '3+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    duplicate = [{'series': 'goldsrcops_command_dispatch_worker_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="duplicate"}', 'values': value} for key, value in base.items()]
    case('multiple-enabled-sources-are-not-merged', {}, [(6, ['CommandDispatchMonitorUnavailable'])], duplicate)
    disabled = [{'series': 'goldsrcops_command_dispatch_worker_' + key + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="http-only"}', 'values': '0+0x20' if key == 'enabled' else '3+0x20' if key == 'state' else value} for key, value in base.items()]
    case('disabled-replica-does-not-hide-active-failure', {'state': '2+0x20', 'consecutive_failures': '2+0x20'}, [(4, ['CommandDispatchCycleFailed'])], disabled)
    case('failed-cycle-then-stall-is-not-a-double-alert', {'state': '2+0x20', 'consecutive_failures': '2+0x20', 'last_finished_timestamp_seconds': '1+0x20'}, [(4, ['CommandDispatchCycleFailed']), (12, []), (14, ['CommandDispatchCycleStalled'])])
    retained = [dict(item, values='1+0x20') if 'observed_timestamp' in item['series'] else item for item in duplicate]
    case('old-instance-overlap-expires-before-hold', {}, [(6, []), (10, [])], retained)
    case('one-failure-is-not-repeated', {'state': '2+0x20', 'consecutive_failures': '1+0x20'}, [(10, [])])
    case('future-success', {'last_successful_cycle_timestamp_seconds': '99999+0x20'}, [(5, ['CommandDispatchMonitorUnavailable'])])
    case('missing-failure-count', {'consecutive_failures': None}, [(5, ['CommandDispatchMonitorUnavailable'])])
    return {'rule_files': ['/rules/command-dispatch-liveness.yml'], 'evaluation_interval': '1m', 'tests': tests}


def validate_rules(docker, mount, image, temporary):
    path = Path(temporary) / 'command-dispatch-rule-tests.json'
    value = fixtures()
    path.write_text(json.dumps(value), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
           '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(ROOT / 'ops/production/observability/rules', '/rules'),
           '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('COMMAND_DISPATCH_LIVENESS_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)
