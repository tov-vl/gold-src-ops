#!/usr/bin/env python3
"""Passive host resource samples through the fixed SSH/private OTLP pipeline."""
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
import resource_diagnostics as contract

spec = importlib.util.spec_from_file_location('resource_transport', ROOT / 'agent-monitor.py')
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
PREFIX = 'goldsrcops_game_host_resource_'
SERVICE = 'goldsrcops-game-host-resource-monitor'
METRICS = ('observation_success', 'observed_timestamp_seconds', 'source_observed_timestamp_seconds',
           'memory_available', 'memory_total_bytes', 'memory_available_bytes', 'swap_total_bytes', 'swap_free_bytes',
           'cpu_available', 'cpu_busy_ratio', 'cpu_steal_ratio', 'cpu_sample_seconds')


def remote():
    transport.private(transport.SSH_CONFIG)
    arguments = ['/usr/bin/ssh', '-F', str(transport.SSH_CONFIG), '-T', '-o', 'BatchMode=yes',
        '-o', 'StrictHostKeyChecking=yes', '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none',
        '-o', 'ConnectionAttempts=1', '-o', 'ConnectTimeout=5', '-o', 'ServerAliveInterval=5',
        '-o', 'ServerAliveCountMax=1', '-o', 'ClearAllForwardings=yes', '-o', 'ControlMaster=no',
        'goldsrcops-player-backup', 'resource-status']
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
    samples = [('observation_success', 0), ('observed_timestamp_seconds', now.timestamp())]
    try:
        observed = contract.validate(value, now)
        samples[0] = ('observation_success', 1)
        samples.append(('source_observed_timestamp_seconds', observed.timestamp()))
        memory, cpu = value['memory'], value['cpu']
        samples.extend([('memory_available', int(memory['available'])), ('cpu_available', int(cpu['available']))])
        if memory['available']:
            samples.extend([('memory_total_bytes', memory['totalBytes']), ('memory_available_bytes', memory['availableBytes']),
                            ('swap_total_bytes', memory['swapTotalBytes']), ('swap_free_bytes', memory['swapFreeBytes'])])
        if cpu['available']:
            samples.extend([('cpu_busy_ratio', cpu['busyRatio']), ('cpu_steal_ratio', cpu['stealRatio']),
                            ('cpu_sample_seconds', cpu['sampleSeconds'])])
    except (ValueError, TypeError, KeyError, OverflowError):
        samples = [('observation_success', 0), ('observed_timestamp_seconds', now.timestamp())]
    return samples


def payload(samples, now):
    metrics = []
    seen = set()
    for name, value in samples:
        contract.require(name in METRICS and name not in seen and type(value) in (float, int)
                         and math.isfinite(value) and value >= 0)
        seen.add(name)
        metrics.append({'name': PREFIX + name, 'gauge': {'dataPoints': [
            {'timeUnixNano': str(int(now.timestamp() * 1000000000)), 'asDouble': value}]}})
    body = json.dumps({'resourceMetrics': [{'resource': {'attributes': [
        {'key': 'service.name', 'value': {'stringValue': SERVICE}}]}, 'scopeMetrics': [
        {'scope': {'name': 'goldsrcops.game-host-resource'}, 'metrics': metrics}]}]},
        separators=(',', ':'), allow_nan=False).encode()
    contract.require(len(body) <= contract.MAX_JSON)
    return body


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 2 and sys.argv[1] in ('inspect', 'publish'))
    config = transport.configuration()
    try:
        value = remote()
    except (ValueError, OSError, subprocess.SubprocessError):
        value = None
    now = dt.datetime.now(contract.UTC)
    samples = observe(value, now)
    if sys.argv[1] == 'publish':
        transport.publish(config['collectorAddress'], payload(samples, now))
        print('RESOURCE_MONITOR=published; observation=' + ('valid' if samples[0][1] else 'unavailable'))
    else:
        print(json.dumps([{'metric': PREFIX + name, 'value': value} for name, value in samples]))
    return 0 if samples[0][1] else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        print('RESOURCE_MONITOR=failed; inspect_configuration_and_transport', file=sys.stderr)
        sys.exit(1)
