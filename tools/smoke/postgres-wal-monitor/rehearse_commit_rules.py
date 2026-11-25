"""Verify the isolated Prometheus commit coverage rules on fixed timelines."""

import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile

IMAGE = 'prom/prometheus@sha256:5ce7540c3c00ef4ab0c9d2c995c6a5b9c421f44b4a115d97a2c7af3b1c21cbb0'
HERE = Path(__file__).resolve().parent
TARGET = {'job': 'wal-commit-coverage', 'instance': 'fixture'}
ALERTS = {
    'PostgresWalCommitCoverageUnknown': ('warning', 'PostgreSQL commit coverage is unknown'),
    'PostgresWalCommitCoverageWarning': ('warning', 'Observed PostgreSQL commits are waiting for verified WAL'),
    'PostgresWalCommitCoverageCritical': ('critical', 'Observed PostgreSQL commits approach the five-minute recovery objective'),
}


def timeline(name, steps):
    labels = '{job="wal-commit-coverage",instance="fixture"}'
    names = ('up', 'goldsrcops_postgres_wal_commit_known',
             'goldsrcops_postgres_wal_commit_oldest_uncovered_age_seconds')
    series = [dict(series=metric + labels, values=' '.join(str(row[index]) for row in steps))
              for index, metric in enumerate(names)]
    tests = []
    for index, (_, _, _, expected) in enumerate(steps):
        firing = {'unknown': 'Unknown', 'warning': 'Warning', 'critical': 'Critical'}
        active = {'PostgresWalCommitCoverage' + firing[key] for key in expected}
        for alert, (severity, summary) in ALERTS.items():
            want = [dict(exp_labels={**TARGET, 'severity': severity},
                         exp_annotations={'summary': summary})] if alert in active else []
            tests.append(dict(eval_time=f'{2 * index}s', alertname=alert, exp_alerts=want))
    return dict(name=name, interval='2s', input_series=series, alert_rule_test=tests)


def fixtures():
    return [
        timeline('recovery-and-observer-restart', [
            (1, 1, 0, ()),
            (1, 1, 179, ()),
            (1, 1, 180, ('warning',)),
            (1, 1, 240, ('critical',)),
            (0, 'stale', 'stale', ('unknown', 'critical')),
            (1, 0, 'stale', ('unknown', 'critical')),
            (1, 1, 247, ('critical',)),
            (1, 1, 0, ()),
            (1, 'stale', 'stale', ('unknown',)),
        ]),
        timeline('stale-critical-is-bounded', [
            (1, 1, 245, ('critical',)),
            *[(1, 0, 'stale', ('unknown', 'critical')) for _ in range(14)],
            (1, 0, 'stale', ('unknown',)),
            (1, 1, 0, ()),
        ]),
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--context')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    path = args.output.resolve()
    path.mkdir(parents=True, exist_ok=False)
    cli = ['docker', '--context', args.context] if args.context else ['docker']
    logic = subprocess.run([sys.executable, '-B', str(HERE/'test_commit_coverage.py')], check=True,
                           capture_output=True, text=True, timeout=30)
    (path/'logic.log').write_text(logic.stdout + logic.stderr, encoding='utf-8')
    cases = dict(rule_files=['/lab/commit-coverage.rules.yml'], evaluation_interval='2s', tests=fixtures())
    with tempfile.TemporaryDirectory(prefix='wal-commit-rule-input-') as name:
        (Path(name)/'cases.yml').write_text(json.dumps(cases), encoding='utf-8')
        run = subprocess.run([*cli, 'run', '--rm', '--pull', 'never', '--network', 'none',
                              '--mount', f'type=bind,source={HERE},target=/lab,readonly',
                              '--mount', f'type=bind,source={name},target=/tests,readonly',
                              '--entrypoint', '/bin/promtool', IMAGE, 'test', 'rules', '/tests/cases.yml'],
                             capture_output=True, text=True, timeout=90)
    (path/'promtool.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    run.check_returncode()
    receipt = dict(status='passed', fixtures=[test['name'] for test in cases['tests']],
                   rule_checks=sum(len(test['alert_rule_test']) for test in cases['tests']),
                   source_snapshot_states=['known-covered', 'known-pending', 'unknown'])
    text = json.dumps(receipt, indent=2) + '\n'
    (path/'result.json').write_text(text, encoding='utf-8')
    print(text)


if __name__ == '__main__':
    main()
