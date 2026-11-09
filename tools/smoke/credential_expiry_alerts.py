"""Synthetic bounded expiry rules; no provider or credential access."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PURPOSES = {'grafana-metrics': 'GrafanaMetrics', 'grafana-logs': 'GrafanaLogs',
            'archive-reader': 'ArchiveReader', 'archive-writer': 'ArchiveWriter'}
STATES = ('Unknown', 'Recorded', 'Soon', 'Critical', 'Expired')
STAMP = 'goldsrcops_credential_expiry_observed_timestamp_seconds'
STATE = 'goldsrcops_credential_expiry_state'


def fixtures():
    tests = []
    base = {STAMP: '0+60x20'}
    for purpose in PURPOSES:
        for state in STATES:
            base[STATE + '{purpose="' + purpose + '",state="' + state + '"}'] = ('1' if state == 'Recorded' else '0') + '+0x20'

    def case(name, overrides, checks):
        series = dict(base, **overrides)
        tests.append({'name': name, 'interval': '1m', 'input_series': [
            {'series': key[:-1] + ',job="goldsrcops",service_name="GoldSrcOps"}' if key.endswith('}') else
             key + '{job="goldsrcops",service_name="GoldSrcOps"}', 'values': value}
            for key, value in series.items() if value is not None],
            'alert_rule_test': [{'eval_time': str(minute) + 'm', 'alertname': alert,
                                 'exp_alerts': [{'exp_labels': {'component': 'credential-expiry', 'severity': 'warning'},
                                                 'exp_annotations': annotations(alert)}] if alert in expected else []}
                                for minute, expected in checks for alert in alert_names()]})

    case('recorded-is-not-a-warning', {}, [(10, [])])
    for purpose, label in PURPOSES.items():
        record = STATE + '{purpose="' + purpose + '",state="Recorded"}'
        for state in ('Soon', 'Critical', 'Expired', 'Unknown'):
            key = STATE + '{purpose="' + purpose + '",state="' + state + '"}'
            alert = 'CredentialExpiry' + label + state
            case(purpose + '-' + state, {record: '0+0x20', key: '1+0x20'}, [(4, []), (5, [alert])])
            case(purpose + '-' + state + '-resolves', {record: '0+0x6 1+0x13', key: '1+0x6 0+0x13'}, [(5, [alert]), (7, [])])
        unknown = 'CredentialExpiry' + label + 'Unknown'
        case(purpose + '-missing-state', {record: None}, [(5, [unknown])])
        case(purpose + '-invalid-fraction', {record: '0.5+0x20'}, [(5, [unknown])])
        case(purpose + '-two-active-states', {STATE + '{purpose="' + purpose + '",state="Soon"}': '1+0x20'}, [(5, [unknown])])
        soon = 'CredentialExpiry' + label + 'Soon'
        critical = 'CredentialExpiry' + label + 'Critical'
        case(purpose + '-progresses-without-overlap', {
            record: '0+0x20', STATE + '{purpose="' + purpose + '",state="Soon"}': '1+0x6 0+0x13',
            STATE + '{purpose="' + purpose + '",state="Critical"}': '0+0x6 1+0x13'},
            [(5, [soon]), (7, []), (12, [critical])])
    for previous_state in ('Recorded', 'Expired'):
        before = len(tests)
        case('retained-process-' + previous_state + '-must-not-create-unknown', {}, [(10, [])])
        retained = tests[before]['input_series']
        retained.append({'series': STAMP + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="retained"}', 'values': '-60+0x20'})
        for purpose in PURPOSES:
            for state in STATES:
                retained.append({'series': STATE + '{job="goldsrcops",service_name="GoldSrcOps",service_instance_id="retained",purpose="' + purpose + '",state="' + state + '"}',
                                 'values': ('1' if state == previous_state else '0') + '+0x20'})
    unavailable = 'CredentialExpiryMonitorUnavailable'
    case('missing-observation-masks-all-states', {STAMP: None}, [(4, []), (5, [unavailable])])
    case('retained-observation-expires', {STAMP: '0+0x20'}, [(3, []), (8, []), (9, [unavailable])])
    case('future-clock-is-unavailable', {STAMP: '99999+0x20'}, [(5, [unavailable])])
    case('observation-recovers', {STAMP: '99999+0x6 420+60x13'}, [(5, [unavailable]), (7, [])])
    return {'rule_files': ['/rules/credential-expiry.yml'], 'evaluation_interval': '1m', 'tests': tests}


def alert_names():
    return ['CredentialExpiryMonitorUnavailable'] + ['CredentialExpiry' + name + state
        for name in PURPOSES.values() for state in ('Soon', 'Critical', 'Expired', 'Unknown')]


def annotations(alert):
    if alert == 'CredentialExpiryMonitorUnavailable':
        summary = 'Recorded credential expiry observation unavailable'
    else:
        purpose = next(value for value in PURPOSES.values() if alert.startswith('CredentialExpiry' + value))
        state = alert.removeprefix('CredentialExpiry' + purpose)
        summary = purpose + ': ' + {'Soon': 'Recorded deadline within 30 days', 'Critical': 'Recorded deadline within 7 days',
            'Expired': 'Recorded deadline reached; access failure is not established',
            'Unknown': 'Recorded expiry metadata unknown or observation invalid'}[state]
    return {'summary': summary, 'runbook_url': 'https://github.com/tov-vl/gold-src-ops/blob/main/docs/v2.116-credential-notifications.md'}


def validate_rules(docker, mount, image, temporary):
    value = fixtures()
    path = Path(temporary) / 'credential-rule-tests.json'
    path.write_text(json.dumps(value), encoding='utf-8')
    path.chmod(0o644)
    docker('run', '--rm', '--network', 'none', '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
           '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--mount', mount(ROOT / 'ops/production/observability/rules', '/rules'),
           '--mount', mount(path, '/fixtures.json'), '--entrypoint', '/bin/promtool', image, 'test', 'rules', '/fixtures.json')
    print('CREDENTIAL_EXPIRY_RULES=' + str(len(value['tests'])) + '_scenarios_passed', flush=True)
