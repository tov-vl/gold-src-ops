"""Archive freshness policy fixtures with no GitHub, B2 or IRM access."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SUMMARIES = {
    'ArchiveScheduleMonitorUnavailable': 'Archive schedule observer is unavailable',
    'ArchiveScheduleObservationInvalid': 'Archive schedule observation is invalid',
    'ArchiveScheduleDelayed': 'No recent successful scheduled Archive',
    'ArchiveScheduleCatchupAtRisk': 'Archive delay approaches the 24-hour catch-up bound'}


def alert_names():
    return list(SUMMARIES)


def fixtures():
    base = {'observed_timestamp_seconds': '0+60x1500', 'observation_success': '1+0x1500',
            'has_success': '1+0x1500', 'last_success_timestamp_seconds': '0+0x1500'}
    tests = []
    def case(name, updates, checks):
        values = dict(base, **updates)
        tests.append({'name': name, 'interval': '1m', 'input_series': [
            {'series': 'goldsrcops_archive_schedule_' + key + '{job="goldsrcops",service_name="goldsrcops-archive-schedule-monitor"}',
             'values': value} for key, value in values.items() if value is not None],
            'alert_rule_test': [{'eval_time': str(minute) + 'm', 'alertname': alert,
                'exp_alerts': [{'exp_labels': {'component': 'archive-schedule', 'severity': 'warning'},
                    'exp_annotations': {'summary': SUMMARIES[alert], 'runbook_url':
                        'https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.117-archive-reliability.md'}}] if alert in expected else []}
                for minute, expected in checks for alert in alert_names()]})
    case('fresh-and-three-hour-boundary', {}, [(10, []), (180, []), (185, []), (186, ['ArchiveScheduleDelayed'])])
    case('eighteen-hour-boundary', {}, [(1080, ['ArchiveScheduleDelayed']),
        (1086, ['ArchiveScheduleDelayed', 'ArchiveScheduleCatchupAtRisk'])])
    case('no-success-in-bounded-history', {'has_success': '0+0x1500'}, [(4, []), (5, ['ArchiveScheduleDelayed'])])
    case('failed-observation-masks-retained-success', {'observation_success': '0+0x1500'}, [(200, ['ArchiveScheduleObservationInvalid'])])
    case('missing-observer', {'observed_timestamp_seconds': None}, [(5, ['ArchiveScheduleMonitorUnavailable'])])
    case('future-observer', {'observed_timestamp_seconds': '999999+0x1500'}, [(5, ['ArchiveScheduleMonitorUnavailable'])])
    case('stopped-observer-expiry', {'observed_timestamp_seconds': '0+0x1500'}, [(30, []), (36, ['ArchiveScheduleMonitorUnavailable'])])
    case('age-recovers-after-new-scheduled-success', {'last_success_timestamp_seconds': '0+0x200 12060+60x1299'},
        [(200, ['ArchiveScheduleDelayed']), (202, [])])
    return {'rule_files': ['/rules/archive-schedule.yml'], 'evaluation_interval': '1m', 'tests': tests}


def validate_rules(docker, mount, image, temporary):
    value = fixtures()
    path = Path(temporary) / 'archive-rule-tests.json'
    path.write_text(json.dumps(value), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
        '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(ROOT / 'ops/production/observability/rules', '/rules'),
        '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('ARCHIVE_SCHEDULE_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)
