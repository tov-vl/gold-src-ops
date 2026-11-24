"""Verify live WAL metrics and alert delivery in disposable Docker containers."""

import argparse
import json
import subprocess
import time
import uuid
from pathlib import Path

LAB = Path(__file__).resolve().parent
LABEL = 'goldsrcops.live-wal-monitor'
IMAGES = {
    'postgres': 'postgres:16-alpine@sha256:cf78e76683b9ca8c5733cbbdce6c9262b45b6767934dd0a95e671f9a0fc20685',
    'python': 'python:3.12-slim@sha256:b18b261e1b4974b4f321451ad5251ded85abb22c2b8b262da527dda1d031ab30',
    'prometheus': 'prom/prometheus:v3.14.0@sha256:5ce7540c3c00ef4ab0c9d2c995c6a5b9c421f44b4a115d97a2c7af3b1c21cbb0',
    'alertmanager': 'prom/alertmanager:v0.34.1@sha256:e9733bafb1bdef9b00e25a21f8f99dc26a22224bf16641ad754d1649f4c3357a',
}
AGE_WARNING = 'PostgresWalArchiveAgeWarning'
AGE_CRITICAL = 'PostgresWalArchiveAgeCritical'
PREFIX = 'goldsrcops_postgres_wal_'
HTTP = """import sys, urllib.request
with urllib.request.urlopen(sys.argv[1], timeout=3) as response:
    print(response.read().decode())
"""
REFERENCE = r"""
WITH ready AS MATERIALIZED (
  SELECT name, (pg_stat_file('pg_wal/archive_status/' || name, true)).modification AS created
  FROM pg_ls_dir('pg_wal/archive_status') AS status(name)
  WHERE name ~ '^[0-9A-F]{24}\.ready$'
)
SELECT json_build_object('ready_segments', count(created),
     'oldest_ready_age_seconds', coalesce(extract(epoch FROM clock_timestamp() - min(created)), 0)) FROM ready
"""


def require(condition, reason):
    if not condition:
        raise RuntimeError(reason)


class Lab:
    def __init__(self, context, output):
        self.cli = ['docker', '--context', context] if context else ['docker']
        self.output = output
        self.token = uuid.uuid4().hex[:12]
        self.name = 'gso-wal-live-' + self.token
        self.network = self.name + '-net'
        self.wal = self.name + '-wal'
        self.archive = self.name + '-archive'
        self.containers = {}

    def docker(self, *arguments, check=True, timeout=35):
        result = subprocess.run([*self.cli, *arguments], capture_output=True, text=True, encoding='utf-8',
                                errors='replace', timeout=timeout)
        if check and result.returncode:
            raise RuntimeError(f'Docker {arguments[0]} failed: {result.stderr[-2000:]}')
        return result

    def start(self, alias, image, command, options=()):
        name = self.name + '-' + alias
        self.containers[alias] = name
        self.docker('run', '--pull=never', '-d', '--name', name, '--label', f'{LABEL}={self.token}',
                    '--network', self.network, '--network-alias', alias, '--memory', '512m', '--cpus', '1',
                    '--mount', f'type=bind,source={LAB},target=/lab,readonly', *options, IMAGES[image], *command)

    def exec(self, alias, *command, check=True):
        return self.docker('exec', self.containers[alias], *command, check=check)

    def wait(self, description, probe, seconds=25):
        limit = time.monotonic() + seconds
        while time.monotonic() < limit:
            result = probe()
            if result is not None:
                return result
            time.sleep(0.3)
        raise TimeoutError(description)

    def sql(self, sql):
        return self.exec('postgres', 'psql', '-XAt', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres',
                         '-d', 'postgres', '-c', sql).stdout.strip()

    def http(self, path):
        result = self.exec('observer', 'python', '-B', '-c', HTTP, path, check=False)
        return result.stdout.strip() if result.returncode == 0 else None

    def query(self, name):
        response = self.http('http://prometheus:9090/api/v1/query?query=' + name)
        return json.loads(response)['data']['result'] if response else None

    def active(self):
        response = self.http('http://prometheus:9090/api/v1/alerts')
        if response is None:
            return None
        return {row['labels']['alertname']: row['state'] for row in json.loads(response)['data']['alerts']}

    def events(self):
        response = self.http('http://observer:8182/events')
        return json.loads(response) if response is not None else None

    def event(self, name, state):
        return next((event for event in self.events() or [] if (event['name'], event['status']) == (name, state)), None)

    def metrics(self):
        response = self.http('http://collector:8181/metrics')
        return {name.removeprefix(PREFIX): float(value) for name, value in
                (line.split() for line in response.splitlines())} if response else None

    def stack(self):
        self.docker('network', 'create', '--internal', '--label', f'{LABEL}={self.token}', self.network)
        for volume in (self.wal, self.archive):
            self.docker('volume', 'create', '--label', f'{LABEL}={self.token}', volume)
        wal_mount = f'type=volume,source={self.wal},target=/wal'
        archive_mount = f'type=volume,source={self.archive},target=/archive'
        self.start('postgres', 'postgres', ['-c', 'archive_mode=on',
                   '-c', 'archive_command=sh /lab/archive.sh "%p" "%f"'], options=[
                       '--mount', wal_mount, '--mount', archive_mount,
                       '-e', 'POSTGRES_INITDB_WALDIR=/wal', '-e', 'POSTGRES_HOST_AUTH_METHOD=trust',
                   ])
        self.wait('PostgreSQL startup', lambda: True if self.exec(
            'postgres', 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres', check=False).returncode == 0 else None)
        self.exec('postgres', 'chown', 'postgres:postgres', '/archive')
        self.exec('postgres', 'touch', '/archive/online')
        uid = self.exec('postgres', 'id', '-u', 'postgres').stdout.strip()
        gid = self.exec('postgres', 'id', '-g', 'postgres').stdout.strip()
        require(uid.isdecimal() and uid != '0' and gid.isdecimal(), 'Invalid PostgreSQL identity')
        self.start('collector', 'python', ['python', '-B', '/lab/collector.py', '/wal'], options=[
            '--user', f'{uid}:{gid}', '--cap-drop', 'ALL', '--read-only', '--security-opt', 'no-new-privileges',
            '--mount', wal_mount + ',readonly',
        ])
        mounts = json.loads(self.docker('inspect', '--format', '{{json .Mounts}}', self.containers['collector']).stdout)
        require(any(m['Destination'] == '/wal' and m['Name'] == self.wal and not m['RW'] for m in mounts),
                'Collector did not mount the WAL filesystem read-only')
        self.start('observer', 'python', ['python', '-B', '/lab/observer.py'])
        self.start('alertmanager', 'alertmanager', ['--config.file=/lab/alertmanager.yml',
                   '--storage.path=/tmp/alertmanager', '--cluster.listen-address='])
        self.start('prometheus', 'prometheus', ['--config.file=/lab/prometheus.yml',
                   '--storage.tsdb.path=/tmp/prometheus', '--rules.alert.resend-delay=1s'])
        self.sql('CREATE TABLE lab_write(payload text NOT NULL)')
        self.sql("ALTER TABLE lab_write ALTER COLUMN payload SET STORAGE EXTERNAL")
        self.sql('SELECT pg_switch_wal()')
        self.wait('Healthy collector and empty queue', self.healthy)

    def healthy(self):
        values = self.metrics()
        up = self.query('up')
        if values and values['ready_segments'] == 0 and up and up[0]['value'][1] == '1' and self.active() == {}:
            return values
        return None

    def compare_sources(self):
        before = json.loads(self.sql(REFERENCE))
        metrics = self.metrics()
        after = json.loads(self.sql(REFERENCE))
        require(metrics and before['ready_segments'] <= metrics['ready_segments'] <= after['ready_segments'],
                'Live queue count differed from PostgreSQL inspection')
        require(abs(metrics['oldest_ready_age_seconds'] - after['oldest_ready_age_seconds']) < 3,
                'Live queue age differed from PostgreSQL inspection')
        fields = self.exec('postgres', 'df', '-Pk', '/wal').stdout.splitlines()[-1].split()
        df_bytes = int(fields[3]) * 1024
        require(abs(metrics['filesystem_available_bytes'] - df_bytes) <= 64 * 1024 * 1024,
                'Collector and PostgreSQL did not observe the same filesystem headroom')
        return dict(metrics=metrics, postgres_before=before, postgres_after=after, df_available_bytes=df_bytes)

    def run(self, critical):
        self.stack()
        with (self.output / 'writer.log').open('wb') as stream:
            writer = subprocess.Popen([*self.cli, 'exec', self.containers['postgres'], 'pgbench', '-n', '-h',
                '/var/run/postgresql', '-U', 'postgres', '-R', '120', '-T', '390' if critical else '270', '-P', '5',
                '-f', '/lab/writer.sql', 'postgres'], stdout=stream, stderr=subprocess.STDOUT)
        try:
            self.wait('Writer startup', lambda: True if self.writes() >= 300 else None)
            self.exec('postgres', 'rm', '/archive/online')
            start = time.monotonic()
            previous_writes = initial_writes = self.writes()
            previous_check = start

            def alert_state(name, minimum):
                nonlocal previous_writes, previous_check
                require(writer.poll() is None, 'PostgreSQL writer stopped')
                now = time.monotonic()
                if now - previous_check >= 5:
                    current = self.writes()
                    require(current > previous_writes, 'PostgreSQL writer stalled')
                    previous_writes, previous_check = current, now
                if now - start >= minimum and self.active() == {name: 'firing'} and self.event(name, 'firing'):
                    return round(now - start, 3)
                return None

            warning_at = self.wait('Live age warning did not fire', lambda: alert_state(AGE_WARNING, 0), 160)
            critical_at = None
            if critical:
                critical_at = self.wait('Live age critical did not fire', lambda: alert_state(AGE_CRITICAL, 0), 150)
                self.wait('Warning did not resolve at critical threshold', lambda: self.event(AGE_WARNING, 'resolved'))
            else:
                self.wait('Bounded outage and active warning', lambda: alert_state(AGE_WARNING, 150), 35)
            comparison = self.compare_sources()
            last_alert = AGE_CRITICAL if critical else AGE_WARNING
            before_resume = self.writes()
            self.exec('postgres', 'touch', '/archive/online')
            resumed = time.monotonic()
            self.wait('Live archive did not catch up', lambda: self.healthy() if self.event(last_alert, 'resolved') else None, 100)
            catchup = round(time.monotonic() - resumed, 3)
            after_resume = self.writes()
            require(after_resume > before_resume > initial_writes, 'Expected writes during outage and catch-up')
            self.docker('pause', self.containers['collector'])
            try:
                self.wait('Scrape timeout must surface up=0 and missing WAL metrics', self.timed_out_scrape)
            finally:
                self.docker('unpause', self.containers['collector'])
            self.wait('Resumed scrape must expose the healthy queue', self.healthy)
        finally:
            self.docker('stop', '-t', '5', self.containers['postgres'])
            writer.wait(timeout=12)

        events = self.events()
        names = [AGE_WARNING, AGE_CRITICAL] if critical else [AGE_WARNING]
        require(len(events) == 2 * len(names), 'Unexpected additional notification')
        for name in names:
            pair = [event for event in events if event['name'] == name]
            require([event['status'] for event in pair] == ['firing', 'resolved']
                    and pair[0]['fingerprint'] == pair[1]['fingerprint'], f'Unexpected lifecycle for {name}')
        require(self.docker('network', 'inspect', '--format', '{{.Internal}}', self.network).stdout.strip() == 'true',
                'Rehearsal network is not internal')
        for name in self.containers.values():
            require(not json.loads(self.docker('inspect', '--format', '{{json .HostConfig.PortBindings}}', name).stdout),
                    'Rehearsal container published a port')
        return dict(status='passed', images=IMAGES, warning_fired_seconds=warning_at, critical_fired_seconds=critical_at,
                    cross_check=comparison, catchup_seconds=catchup, writes_during_outage=before_resume - initial_writes,
                    writes_during_catchup=after_resume - before_resume, notifications=events,
                    timeout_observed_as_missing=True, terminal_active_alerts=self.active())

    def writes(self):
        return int(self.sql('SELECT count(*) FROM lab_write'))

    def timed_out_scrape(self):
        up = self.query('up')
        if up and up[0]['value'][1] == '0' and all(self.query(PREFIX + name) == [] for name in (
                'ready_segments', 'oldest_ready_age_seconds', 'filesystem_available_bytes')) and self.active() == {}:
            return True
        return None

    def cleanup(self):
        items = [('container', name, ('rm', '-f', '-v')) for name in reversed(list(self.containers.values()))]
        items.extend(('volume', name, ('volume', 'rm')) for name in (self.archive, self.wal))
        items.append(('network', self.network, ('network', 'rm')))
        for kind, name, remove in items:
            found = self.docker(kind, 'inspect', name, check=False)
            if found.returncode:
                continue
            data = json.loads(found.stdout)[0]
            labels = (data['Config'] if kind == 'container' else data).get('Labels') or {}
            require(labels.get(LABEL) == self.token, 'Unexpected resource ownership')
            self.docker(*remove, name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--context')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pull', action='store_true', help='Fetch the pinned images before starting the isolated network')
    parser.add_argument('--critical', action='store_true')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    lab = Lab(args.context, args.output)
    for image in IMAGES.values():
        if args.pull:
            lab.docker('pull', image, timeout=120)
        lab.docker('image', 'inspect', image)
    checked = lab.docker('run', '--pull=never', '--rm', '--network', 'none', '--mount',
                         f'type=bind,source={LAB},target=/lab,readonly', IMAGES['python'],
                         'python', '-B', '/lab/test_collector.py', check=False)
    (args.output / 'collector-tests.log').write_text(checked.stdout + checked.stderr, encoding='utf-8')
    require(checked.returncode == 0, 'Collector boundary tests failed')
    try:
        result = lab.run(args.critical)
    except Exception:
        for alias, name in lab.containers.items():
            logs = lab.docker('logs', '--tail', '40', name, check=False)
            (args.output / f'{alias}.log').write_text(logs.stdout + logs.stderr, encoding='utf-8')
        raise
    finally:
        lab.cleanup()
    result['temporary_resources_removed'] = True
    (args.output / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
