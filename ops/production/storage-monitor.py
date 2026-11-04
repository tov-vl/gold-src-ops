#!/usr/bin/env python3
"""Fixed storage observation through the existing SSH and private OTLP pipeline."""
import datetime as dt
import importlib.util
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent / 'gameserver'))
import storage_diagnostics as contract

spec = importlib.util.spec_from_file_location('storage_transport', ROOT / 'agent-monitor.py')
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
PREFIX = 'goldsrcops_game_host_storage_'
SERVICE = 'goldsrcops-game-host-storage-monitor'
METRICS = ('role_available', 'bytes_total', 'bytes_available', 'inodes_supported', 'inodes_total', 'inodes_available')


def remote():
    transport.private(transport.SSH_CONFIG)
    arguments = ['/usr/bin/ssh', '-F', str(transport.SSH_CONFIG), '-T', '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes', '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none',
        '-o', 'ConnectionAttempts=1', '-o', 'ConnectTimeout=5', '-o', 'ServerAliveInterval=5',
        '-o', 'ServerAliveCountMax=1', '-o', 'ClearAllForwardings=yes', '-o', 'ControlMaster=no',
        'goldsrcops-player-backup', 'storage-status']
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
        contract.require(process.returncode == 0)
        return contract.decode(raw)


def observe(value, now):
    samples = [('observation_success', 0, None), ('observed_timestamp_seconds', now.timestamp(), None)]
    try:
        observed = contract.validate(value, now)
        samples[0] = ('observation_success', 1, None)
        samples.append(('source_observed_timestamp_seconds', observed.timestamp(), None))
        for role, row in value['roles'].items():
            samples.append(('role_available', int(row['available']), role))
            if not row['available']:
                continue
            samples.extend([('bytes_total', row['bytesTotal'], role), ('bytes_available', row['bytesAvailable'], role),
                            ('inodes_supported', int(row['inodesTotal'] is not None), role)])
            if row['inodesTotal'] is not None:
                samples.extend([('inodes_total', row['inodesTotal'], role), ('inodes_available', row['inodesAvailable'], role)])
    except (ValueError, TypeError, KeyError, OverflowError):
        samples = samples[:2]
        samples[0] = ('observation_success', 0, None)
    return samples


def payload(samples, now):
    points = {}
    for name, value, role in samples:
        contract.require(type(value) in (float, int) and math.isfinite(value) and value >= 0)
        contract.require((role is None and name in ('observation_success','observed_timestamp_seconds','source_observed_timestamp_seconds'))
                         or role in contract.ROLES and name in METRICS)
        point = {'timeUnixNano': str(int(now.timestamp() * 1000000000)), 'asDouble': value}
        if role is not None:
            point['attributes'] = [{'key': 'role', 'value': {'stringValue': role}}]
        points.setdefault(name, []).append(point)
    body = json.dumps({'resourceMetrics': [{'resource': {'attributes': [
        {'key':'service.name','value':{'stringValue':SERVICE}}]}, 'scopeMetrics': [
        {'scope':{'name':'goldsrcops.game-host-storage'},'metrics': [
            {'name':PREFIX+name,'gauge':{'dataPoints':values}} for name,values in points.items()]}]}]},
        separators=(',', ':'), allow_nan=False).encode()
    contract.require(len(body) <= contract.MAX_JSON)
    return body


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 2 and sys.argv[1] in ('inspect','publish'))
    config = transport.configuration()
    try:
        value = remote()
    except (ValueError, OSError, subprocess.SubprocessError):
        value = None
    now = dt.datetime.now(contract.UTC)
    samples = observe(value, now)
    if sys.argv[1] == 'publish':
        transport.publish(config['collectorAddress'], payload(samples, now))
        print('STORAGE_MONITOR=published; observation=' + ('valid' if samples[0][1] else 'unavailable'))
    else:
        print(json.dumps([{'metric':PREFIX+name,'value':value,'role':role} for name,value,role in samples]))
    return 0 if samples[0][1] else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('STORAGE_MONITOR=failed; inspect_configuration_and_transport', file=sys.stderr)
        sys.exit(1)
