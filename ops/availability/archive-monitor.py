#!/usr/bin/env python3
"""Bounded public GitHub schedule observation; never dispatches or reads secrets."""
import datetime as dt
import http.client
import importlib.util
import json
import math
from pathlib import Path
import re
import signal
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('archive_transport', ROOT / 'production/agent-monitor.py')
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
UTC = dt.timezone.utc
WORKFLOW = 350269402
BASE = '/repos/tov-vl/gold-src-ops/actions'
PREFIX = 'goldsrcops_archive_schedule_'
SERVICE = 'goldsrcops-archive-schedule-monitor'
METRICS = {'trigger_source', 'trigger_source_observed_timestamp_seconds', 'observation_success', 'observed_timestamp_seconds', 'has_success',
           'last_success_timestamp_seconds', 'latest_run_failed'}


def require(value):
    if not value:
        raise ValueError('Invalid bounded schedule observation')


def decode(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result)
            result[key] = value
        return result
    require(0 < len(raw) <= 524288)
    return json.loads(raw.decode('utf-8'), object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False))


def moment(value, now):
    require(type(value) is str and re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z', value))
    value = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(dt.datetime(2020, 1, 1, tzinfo=UTC) <= value <= now)
    return value


def request(path):
    connection = http.client.HTTPSConnection('api.github.com', timeout=8)
    try:
        connection.request('GET', path, headers={'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': SERVICE, 'Cache-Control': 'no-cache'})
        response = connection.getresponse()
        require(response.status == 200 and response.getheader('Content-Type', '').split(';')[0] == 'application/json')
        length = response.getheader('Content-Length')
        require(length is None or 0 < int(length) <= 524288)
        return decode(response.read(524289))
    finally:
        connection.close()


def observe(document, jobs, now, event='schedule'):
    require(type(document) is dict and type(document.get('total_count')) is int and document['total_count'] >= 0)
    runs = document.get('workflow_runs')
    require(type(runs) is list and len(runs) <= 20 and len(runs) <= document['total_count'])
    require(bool(runs) == bool(document['total_count']))
    seen = set()
    previous = now
    eligible = []
    for run in runs:
        require(type(run) is dict and type(run.get('id')) is int and run['id'] > 0 and run['id'] not in seen)
        seen.add(run['id'])
        require(run.get('workflow_id') == WORKFLOW and run.get('event') == event and run.get('head_branch') == 'main')
        require(type(run.get('head_sha')) is str and re.fullmatch('[a-f0-9]{40}', run['head_sha']))
        created = moment(run.get('created_at'), now)
        require(created <= previous)
        previous = created
        require(type(run.get('run_attempt')) is int and run['run_attempt'] > 0)
        status, conclusion = run.get('status'), run.get('conclusion')
        require(status in ('queued', 'in_progress', 'completed', 'waiting', 'pending', 'requested'))
        require((status == 'completed' and conclusion in ('success', 'failure', 'cancelled', 'skipped', 'timed_out', 'action_required', 'neutral', 'stale', 'startup_failure'))
                or (status != 'completed' and conclusion is None))
        if status == 'completed' and conclusion == 'success' and run['run_attempt'] == 1:
            eligible.append((run, created))
    samples = [('observation_success', 1), ('observed_timestamp_seconds', now.timestamp()),
               ('has_success', 0), ('last_success_timestamp_seconds', 0),
               ('latest_run_failed', int(bool(runs) and runs[0]['status'] == 'completed' and runs[0]['conclusion'] != 'success'))]
    if eligible:
        run, created = eligible[0]
        value = jobs(run['id'])
        require(type(value) is dict and type(value.get('total_count')) is int and value['total_count'] == 1
                and type(value.get('jobs')) is list and len(value['jobs']) == 1)
        job = value['jobs'][0]
        require(job.get('run_id') == run['id'] and type(job.get('run_attempt')) is int and job['run_attempt'] == 1 and job.get('name') == 'Archive Primary Evidence'
                and job.get('status') == 'completed' and job.get('conclusion') == 'success')
        started, completed = moment(job.get('started_at'), now), moment(job.get('completed_at'), now)
        require(created <= started <= completed and (completed - started).total_seconds() <= 900)
        steps = job.get('steps')
        require(type(steps) is list and len(steps) <= 32)
        for name in ('Verify pinned exporter revision', 'Export and archive primary evidence'):
            matches = [s for s in steps if type(s) is dict and s.get('name') == name]
            require(len(matches) == 1 and matches[0].get('status') == 'completed' and matches[0].get('conclusion') == 'success')
        # Creation time cannot be refreshed by a later manual rerun or metadata edit.
        samples[2:4] = [('has_success', 1), ('last_success_timestamp_seconds', created.timestamp())]
    samples.extend([('trigger_source', 1 if event == 'schedule' else 2),
                    ('trigger_source_observed_timestamp_seconds', now.timestamp())])
    return samples


def collect_external(mapping, generation, now, fetch=request):
    """Correlate a protected read-only claim projection with independent public jobs."""
    try:
        require(type(mapping) is dict and set(mapping) == {'generation', 'observed_at', 'claim', 'slot', 'run_id'})
        require(type(generation) is str and str(uuid.UUID(generation)) == generation
                and mapping['generation'] == generation)
        observed = moment(mapping['observed_at'], now)
        require((now - observed).total_seconds() <= 1800)
        require(type(mapping['claim']) is str and str(uuid.UUID(mapping['claim'])) == mapping['claim'])
        slot = moment(mapping['slot'], now)
        require(slot.minute == 53 and slot.second == 0)
        run_id = mapping['run_id']
        require(type(run_id) is int and 0 < run_id < 2**63)
        run = fetch(BASE + f'/runs/{run_id}/attempts/1')
        require(run.get('id') == run_id and run.get('run_attempt') == 1)
        created = moment(run.get('created_at'), now)
        require(slot <= created < slot + dt.timedelta(hours=1))
        details = fetch(BASE + f'/runs/{run_id}/attempts/1/jobs?per_page=10')
        # A completed receipt-validation step is necessary. The authenticated
        # adapter separately checks the exact stdout receipt and content hashes.
        require(type(details.get('jobs')) is list and len(details['jobs']) == 1)
        steps = details['jobs'][0].get('steps')
        require(type(steps) is list and len(steps) <= 32)
        for name in ('Validate Archive admission', 'Validate external Archive receipt'):
            found = [s for s in steps if type(s) is dict and s.get('name') == name]
            require(len(found) == 1 and found[0].get('conclusion') == 'success'
                    and found[0].get('status') == 'completed')
        return observe({'total_count': 1, 'workflow_runs': [run]}, lambda _: details,
                       now, event='workflow_dispatch')
    except (ValueError, TypeError, KeyError, OverflowError, OSError, http.client.HTTPException):
        return [('observation_success', 0), ('observed_timestamp_seconds', now.timestamp())]


def collect(now, fetch=request, clock=None):
    try:
        document = fetch(BASE + '/workflows/' + str(WORKFLOW) + '/runs?event=schedule&branch=main&per_page=20')
        require(type(document) is dict and type(document.get('workflow_runs')) is list and len(document['workflow_runs']) <= 20)
        candidate = next((run for run in document['workflow_runs'] if type(run) is dict
            and run.get('status') == 'completed' and run.get('conclusion') == 'success'
            and type(run.get('run_attempt')) is int and run['run_attempt'] == 1), None)
        details = None
        if candidate is not None:
            require(type(candidate.get('id')) is int and 0 < candidate['id'] < 2**63)
            details = fetch(BASE + '/runs/' + str(candidate['id']) + '/jobs?filter=latest&per_page=10')
        # Validate against time after both reads: a job may complete during I/O.
        # All identity, ordering, step and strict future-time checks still apply.
        return observe(document, lambda _: details, clock() if clock else now)
    except (ValueError, TypeError, KeyError, OverflowError, OSError, http.client.HTTPException):
        return [('observation_success', 0), ('observed_timestamp_seconds', (clock() if clock else now).timestamp())]


def payload(samples, now):
    require(len(samples) <= len(METRICS) and len({name for name, _ in samples}) == len(samples))
    metrics = []
    for name, value in samples:
        require(name in METRICS and type(value) in (int, float) and math.isfinite(value) and value >= 0)
        metrics.append({'name': PREFIX + name, 'gauge': {'dataPoints': [
            {'timeUnixNano': str(int(now.timestamp() * 1000000000)), 'asDouble': value}]}})
    return json.dumps({'resourceMetrics': [{'resource': {'attributes': [
        {'key': 'service.name', 'value': {'stringValue': SERVICE}}]}, 'scopeMetrics': [
        {'scope': {'name': 'goldsrcops.archive-schedule'}, 'metrics': metrics}]}]}, allow_nan=False).encode()


def main():
    external = len(sys.argv) == 4 and sys.argv[1] in ('inspect-external', 'publish-external')
    require(external or (len(sys.argv) == 2 and sys.argv[1] in ('inspect', 'publish')))
    def expired(*_):
        raise TimeoutError('Bounded observation expired')
    signal.signal(signal.SIGALRM, expired)
    signal.alarm(30)
    if external:
        path = Path(sys.argv[2])
        require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 16384)
        samples = collect_external(decode(path.read_bytes()), sys.argv[3], dt.datetime.now(UTC))
    else:
        samples = collect(dt.datetime.now(UTC), clock=lambda: dt.datetime.now(UTC))
    signal.alarm(0)
    now = dt.datetime.now(UTC)
    if sys.argv[1] in ('publish', 'publish-external'):
        config = transport.configuration()
        transport.publish(config['collectorAddress'], payload(samples, now))
    print('ARCHIVE_MONITOR=' + ('observed' if samples[0][1] else 'unavailable'))
    return 0 if samples[0][1] else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('ARCHIVE_MONITOR=failed; inspect_configuration_and_transport', file=sys.stderr)
        sys.exit(1)
