#!/usr/bin/env python3
"""Read-only projection of the player backup ledger into private OTLP metrics."""
import datetime as dt
import http.client
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
from zoneinfo import ZoneInfo

ROOT = Path('/var/lib/goldsrcops/player-data-backup')
CONFIG = Path('/etc/goldsrcops/player-data-monitor.json')
UTC = dt.timezone.utc
MOSCOW = ZoneInfo('Europe/Moscow')
MAX_AGE = 8 * 86400
MAX_RUNNING = 65 * 60
MAX_JSON = 16384
TIMER = 'goldsrcops-player-data-backup.timer'
SERVICE = 'goldsrcops-player-data-backup.service'
PREFIX = 'goldsrcops_player_backup_'
# Stable public codes; filenames, identities, hashes and errors are never labels.
STATES = {'none': 0, 'verified': 1, 'skipped-busy': 2, 'skipped-window': 3,
          'running': 4, 'reconcile': 5, 'failed': 6, 'unknown': 7}
KEYS = ('observation_success', 'observed_timestamp_seconds',
        'last_success_timestamp_seconds', 'copy_present', 'fresh',
        'timer_enabled', 'requires_reconciliation', 'service_failed',
        'missed_cycle', 'in_progress', 'last_attempt_status',
        'last_attempt_timestamp_seconds')


def require(condition):
    if not condition:
        raise ValueError('Invalid player backup observation.')


def private(path, directory=False):
    path = Path(path)
    require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    require(info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0)
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode) and info.st_nlink == 1)
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def decode(raw):
    return json.loads(raw, object_pairs_hook=unique_object,
                      parse_constant=lambda _: require(False))


def read_json(path):
    path = private(path)
    # A concurrent atomic replacement may produce an unknown sample, never a lock
    # that could prevent the non-blocking backup runner from claiming its window.
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= MAX_JSON
                and before.st_uid == os.geteuid() and before.st_mode & 0o077 == 0)
        raw = stream.read(MAX_JSON + 1)
        after = os.fstat(stream.fileno())
    signature = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns)
    require(len(raw) <= MAX_JSON and signature(before) == signature(after) == signature(path.stat()))
    result = decode(raw.decode('utf-8-sig'))
    require(isinstance(result, dict))
    return result


def exists(path):
    # Broken links are invalid evidence, not missing evidence.
    return path.exists() or path.is_symlink()


def timestamp(value, moment):
    require(isinstance(value, str))
    parsed = dt.datetime.fromisoformat(value)
    require(parsed.tzinfo is not None and 0 < parsed.timestamp() <= moment.timestamp() + 300)
    return parsed


def slot(value):
    require(isinstance(value, str) and re.fullmatch(r'\d{4}-\d{2}-\d{2}', value) is not None)
    date = dt.date.fromisoformat(value)
    require(date.weekday() == 6)
    return dt.datetime.combine(date, dt.time(4), MOSCOW)


def last_due(moment):
    local = moment.astimezone(MOSCOW)
    date = local.date() - dt.timedelta(days=(local.weekday() + 1) % 7)
    due = dt.datetime.combine(date, dt.time(4), MOSCOW)
    return due if local >= due else due - dt.timedelta(days=7)


def success(record, moment):
    require(record['schema'] == 1 and record['state'] == 'verified'
            and record['workload'] == 'goldsrcops-player-data-v1')
    for key in ('bundleSha256', 'snapshotId'):
        require(isinstance(record[key], str) and re.fullmatch(r'[a-f0-9]{64}', record[key]) is not None)
    return timestamp(record['capturedUtc'], moment)


def properties(unit, names):
    # Fixed read-only systemctl verbs. A hard outer service timeout bounds failure.
    result = subprocess.run(['/usr/bin/systemctl', 'show', unit, '--property=' + ','.join(names)],
                            capture_output=True, timeout=5, check=False)
    require(result.returncode == 0 and len(result.stdout) <= MAX_JSON)
    pairs = [line.split('=', 1) for line in result.stdout.decode('utf-8').splitlines()]
    require(all(len(pair) == 2 for pair in pairs))
    values = unique_object(pairs)
    require(set(values) == set(names))
    return values


def system_state():
    return {
        'timer': properties(TIMER, ['LoadState', 'ActiveState', 'UnitFileState', 'Persistent', 'TimersCalendar']),
        'service': properties(SERVICE, ['LoadState', 'ActiveState', 'Result'])}


def observe(root, units, moment, enabled_since):
    require(moment.tzinfo is not None)
    enabled = timestamp(enabled_since, moment)
    metrics = {key: 0 for key in KEYS}
    metrics['observed_timestamp_seconds'] = moment.timestamp()
    metrics['last_attempt_status'] = STATES['unknown']
    try:
        root = private(root, directory=True)
        timer, service = units['timer'], units['service']
        require(timer['LoadState'] == 'loaded' and service['LoadState'] == 'loaded')
        metrics['timer_enabled'] = int(timer['ActiveState'] == 'active'
                                       and timer['UnitFileState'] == 'enabled'
                                       and timer['Persistent'] == 'no'
                                       and re.findall(r'OnCalendar=([^;]+);', timer['TimersCalendar'])
                                       == ['Sun *-*-* 04:00:00 Europe/Moscow '])
        running = service['ActiveState'] in ('activating', 'active')
        metrics['service_failed'] = int(not running and (service['ActiveState'] == 'failed' or service['Result'] != 'success'))
        if exists(root / 'last-success.json'):
            last_success = read_json(root / 'last-success.json')
            captured = success(last_success, moment)
            metrics['last_success_timestamp_seconds'] = captured.timestamp()
            metrics['copy_present'] = 1
            metrics['fresh'] = int((moment - captured).total_seconds() <= MAX_AGE)
        cycles = []
        with os.scandir(root) as entries:
            for index, entry in enumerate(entries):
                require(index < 256)
                if re.fullmatch(r'\d{4}-\d{2}-\d{2}', entry.name):
                    require(slot(entry.name) <= moment + dt.timedelta(minutes=5))
                    private(Path(entry.path), directory=True)
                    cycles.append(entry.name)
        require(len(cycles) <= 52)
        latest = max(cycles) if cycles else None
        record = read_json(root / latest / 'result.json') if latest else None
        started = None
        if record:
            require(record['schema'] == 1 and record['slot'] == latest)
            started = timestamp(record['startedUtc'], moment)
            require(slot(latest) - dt.timedelta(minutes=5) <= started
                    <= slot(latest) + dt.timedelta(minutes=10))
            metrics['last_attempt_timestamp_seconds'] = started.timestamp()
            require(record['state'] in ('started', 'stopping', 'archiving', 'verified', 'skipped-busy', 'skipped-window'))
            if record['state'] in ('verified', 'skipped-busy', 'skipped-window'):
                require(timestamp(record['completedUtc'], moment) >= started)
                metrics['last_attempt_status'] = STATES[record['state']]
            if record['state'] == 'verified':
                capture = success(record, moment)
                require(capture >= started - dt.timedelta(minutes=5))
                require(metrics['copy_present'] == 1 and metrics['last_success_timestamp_seconds'] == capture.timestamp())
                require(all(last_success[key] == record[key] for key in ('bundleSha256', 'snapshotId')))
                require(capture <= timestamp(record['completedUtc'], moment) + dt.timedelta(minutes=5))
        pending = read_json(root / 'pending.json') if exists(root / 'pending.json') else None
        if pending is not None:
            require(pending['schema'] == 1 and pending['state'] == 'started' and pending['slot'] == latest)
            require(timestamp(pending['startedUtc'], moment) == started)
        unfinished = record is not None and record['state'] in ('started', 'stopping', 'archiving')
        if pending is not None or unfinished:
            normal = running and started is not None and (moment - started).total_seconds() <= MAX_RUNNING
            metrics['in_progress'] = int(normal)
            metrics['requires_reconciliation'] = int(not normal)
            metrics['last_attempt_status'] = STATES['running' if normal else 'reconcile']
        elif running:
            # Atomic claim/finish boundaries are retried by the next read-only sample.
            require(False)
        elif metrics['service_failed']:
            metrics['last_attempt_status'] = STATES['failed']
        elif record is None:
            metrics['last_attempt_status'] = STATES['none']
        due = last_due(moment)
        metrics['missed_cycle'] = int(due >= enabled and moment > due + dt.timedelta(minutes=10)
                                      and due.date().isoformat() not in cycles and not metrics['in_progress'])
        metrics['observation_success'] = 1
    except (ValueError, KeyError, TypeError, OSError, OverflowError):
        # No partial values survive malformed/private/concurrently changing data.
        metrics = {key: 0 for key in KEYS}
        metrics.update(observed_timestamp_seconds=moment.timestamp(), last_attempt_status=STATES['unknown'])
    return metrics


def payload(metrics):
    require(set(metrics) == set(KEYS) and all(type(v) in (int, float) and math.isfinite(v) for v in metrics.values()))
    time_nano = str(int(metrics['observed_timestamp_seconds'] * 1_000_000_000))
    return json.dumps({'resourceMetrics': [{
        'resource': {'attributes': [{'key': 'service.name', 'value': {'stringValue': 'goldsrcops-player-backup-monitor'}}]},
        'scopeMetrics': [{'scope': {'name': 'goldsrcops.player-backup'}, 'metrics': [
            {'name': PREFIX + key, 'gauge': {'dataPoints': [{'timeUnixNano': time_nano, 'asDouble': value}]}}
            for key, value in metrics.items()]}]}]}, separators=(',', ':')).encode()


def configuration(moment):
    config = read_json(CONFIG)
    require(set(config) == {'schema', 'collectorAddress', 'enabledSinceUtc'} and config['schema'] == 1)
    address = ipaddress.IPv4Address(config['collectorAddress'])
    require(any(address in ipaddress.IPv4Network(network) for network in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')))
    timestamp(config['enabledSinceUtc'], moment)
    return config


def publish(address, body):
    require(len(body) <= MAX_JSON)
    # Direct numeric endpoint: no proxy environment, redirects, DNS or retries.
    connection = http.client.HTTPConnection(address, 4318, timeout=5)
    try:
        connection.request('POST', '/v1/metrics', body=body, headers={'Content-Type': 'application/json'})
        response = connection.getresponse()
        require(response.status == 200 and response.getheader('Content-Type', '').split(';')[0] == 'application/json')
        raw = response.read(4097)
        require(len(raw) <= 4096)
        result = decode(raw.decode('utf-8'))
        require(isinstance(result, dict) and set(result).issubset({'partialSuccess'}))
        partial = result.get('partialSuccess', {})
        require(isinstance(partial, dict) and str(partial.get('rejectedDataPoints', 0)) == '0'
                and not partial.get('errorMessage'))
    finally:
        connection.close()


def main():
    require(os.geteuid() == 0 and len(sys.argv) == 2 and sys.argv[1] in ('inspect', 'publish'))
    moment = dt.datetime.now(UTC)
    config = configuration(moment)
    try:
        units = system_state()
    except (ValueError, OSError, subprocess.SubprocessError):
        units = {}
    metrics = observe(ROOT, units, moment, config['enabledSinceUtc'])
    if sys.argv[1] == 'publish':
        publish(config['collectorAddress'], payload(metrics))
        print('PLAYER_BACKUP_MONITOR=published; observation=' + ('valid' if metrics['observation_success'] else 'unknown'))
    else:
        print(json.dumps(metrics, separators=(',', ':')))
    return 0 if metrics['observation_success'] else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('PLAYER_BACKUP_MONITOR=failed; inspect_monitor_configuration_and_transport', file=sys.stderr)
        sys.exit(1)
