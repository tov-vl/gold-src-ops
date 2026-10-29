#!/usr/bin/env python3
"""Restricted passive agent observation into the existing private OTLP pipeline."""
import datetime as dt
import http.client
import ipaddress
import json
import math
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / 'gameserver'))
import agent_diagnostics as contract

CONFIG = Path('/etc/goldsrcops/agent-monitor.json')
SSH_CONFIG = Path('/etc/goldsrcops/player-data-backup/ssh_config')
PREFIX = 'goldsrcops_game_event_agent_'
SERVICE = 'goldsrcops-game-event-agent-monitor'
SCALARS = {'queueCapacity': 'queue_capacity', 'pending': 'queue_pending', 'inFlight': 'queue_in_flight',
           'deadLetter': 'queue_dead_letter', 'spoolAvailable': 'spool_available',
           'spoolReady': 'spool_ready', 'spoolProcessing': 'spool_processing', 'spoolRejected': 'spool_rejected'}


def private(path):
    contract.require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    contract.require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and info.st_nlink == 1
                     and stat.S_IMODE(info.st_mode) == 0o600 and info.st_size <= contract.MAX_JSON)
    return path


def configuration():
    value = contract.decode(private(CONFIG).read_bytes())
    contract.require(set(value) == {'schemaVersion', 'collectorAddress'}
                     and type(value['schemaVersion']) is int and value['schemaVersion'] == 1)
    address = ipaddress.IPv4Address(value['collectorAddress'])
    contract.require(any(address in ipaddress.IPv4Network(network)
                         for network in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')))
    return value


def remote():
    private(SSH_CONFIG)
    # Use the existing key and host binding. No shell, forwarding, caller-supplied
    # destination, backup action or retry can be selected by this collector.
    arguments = ['/usr/bin/ssh', '-F', str(SSH_CONFIG), '-T', '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes', '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none',
        '-o', 'ConnectionAttempts=1', '-o', 'ConnectTimeout=5', '-o', 'ServerAliveInterval=5',
        '-o', 'ServerAliveCountMax=1', '-o', 'ClearAllForwardings=yes', '-o', 'ControlMaster=no',
        'goldsrcops-player-backup', 'agent-status']
    with subprocess.Popen(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True,
                          env={'PATH': '/usr/bin:/bin', 'LC_ALL': 'C'}) as process:
        try:
            raw, _ = process.communicate(timeout=25)
        except BaseException:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise
        contract.require(process.returncode == 0 and len(raw) <= contract.MAX_JSON)
        return contract.decode(raw)


def observe(value, now):
    samples = [('observation_success', 0, None), ('observed_timestamp_seconds', now.timestamp(), None)]
    try:
        contract.validate(value, now)
        contract.require(value['available'])
        observed = contract.moment(value['observedAtUtc'], now)
        samples[0] = ('observation_success', 1, None)
        samples.extend([('source_observed_timestamp_seconds', observed.timestamp(), None),
                        ('service_active', int(value['serviceActive']), None),
                        ('oldest_waiting_age_seconds', value['oldestWaitingAgeSeconds'] or 0, None)])
        samples.extend((metric, int(value[key]), None) for key, metric in SCALARS.items())
        for name, loop in value['loops'].items():
            state = contract.loop_state(loop, value['serviceActive'], now)
            samples.extend([('loop_state', contract.STATES[state], name),
                            ('loop_stale_after_seconds', loop['staleAfterSeconds'], name)])
            for key, metric in (('lastCompletedAtUtc', 'loop_last_completed_timestamp_seconds'),
                                ('lastSuccessfulAtUtc', 'loop_last_success_timestamp_seconds')):
                timestamp = contract.moment(loop[key], now).timestamp() if loop[key] is not None else 0
                samples.append((metric, timestamp, name))
    except (ValueError, TypeError, KeyError, OverflowError):
        # Do not publish fake zero queue counts. Previously exported values are
        # explicitly masked by observation_success and freshness in every panel.
        samples = samples[:2]
        samples[0] = ('observation_success', 0, None)
    return samples


def payload(samples, now):
    points = {}
    allowed = {'observation_success', 'observed_timestamp_seconds', 'source_observed_timestamp_seconds',
               'service_active', 'oldest_waiting_age_seconds', *SCALARS.values(), 'loop_state',
               'loop_stale_after_seconds', 'loop_last_completed_timestamp_seconds', 'loop_last_success_timestamp_seconds'}
    for name, value, loop in samples:
        contract.require(name in allowed and type(value) in (float, int) and math.isfinite(value)
                         and value >= 0 and loop in (None, 'import', 'delivery'))
        point = {'timeUnixNano': str(int(now.timestamp() * 1000000000)), 'asDouble': value}
        if loop is not None:
            point['attributes'] = [{'key': 'loop', 'value': {'stringValue': loop}}]
        points.setdefault(name, []).append(point)
    body = json.dumps({'resourceMetrics': [{'resource': {'attributes': [
        {'key': 'service.name', 'value': {'stringValue': SERVICE}}]},
        'scopeMetrics': [{'scope': {'name': 'goldsrcops.game-event-agent'}, 'metrics': [
            {'name': PREFIX + name, 'gauge': {'dataPoints': values}} for name, values in points.items()]}]}]},
        separators=(',', ':'), allow_nan=False).encode()
    contract.require(len(body) <= contract.MAX_JSON)
    return body


def publish(address, body):
    # Numeric private endpoint, no environment proxy, DNS, redirect or retry.
    connection = http.client.HTTPConnection(address, 4318, timeout=5)
    try:
        connection.request('POST', '/v1/metrics', body=body, headers={'Content-Type': 'application/json'})
        response = connection.getresponse()
        contract.require(response.status == 200 and response.getheader('Content-Type', '').split(';')[0] == 'application/json')
        raw = response.read(4097)
        contract.require(len(raw) <= 4096)
        value = contract.decode(raw)
        contract.require(type(value) is dict and set(value).issubset({'partialSuccess'}))
        partial = value.get('partialSuccess', {})
        contract.require(type(partial) is dict and str(partial.get('rejectedDataPoints', 0)) == '0'
                         and not partial.get('errorMessage'))
    finally:
        connection.close()


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 2 and sys.argv[1] in ('inspect', 'publish'))
    config = configuration()
    try:
        value = remote()
    except (ValueError, OSError, subprocess.SubprocessError):
        value = {'schemaVersion': 1, 'available': False}
    now = dt.datetime.now(contract.UTC)
    samples = observe(value, now)
    if sys.argv[1] == 'publish':
        publish(config['collectorAddress'], payload(samples, now))
        print('AGENT_MONITOR=published; observation=' + ('valid' if samples[0][1] else 'unavailable'))
    else:
        print(json.dumps([{'metric': PREFIX + name, 'value': value, 'loop': loop}
                          for name, value, loop in samples], separators=(',', ':')))
    return 0 if samples[0][1] else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('AGENT_MONITOR=failed; inspect_configuration_and_transport', file=sys.stderr)
        sys.exit(1)
