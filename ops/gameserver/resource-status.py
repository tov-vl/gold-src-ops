#!/usr/bin/env python3
"""Read only fixed aggregate proc files; retain no counter state or identities."""
import datetime as dt
import os
from pathlib import Path
import signal
import sys
import time
import json

sys.path.insert(0, str(Path(__file__).resolve().parent))
import resource_diagnostics as contract

MEMORY = Path('/proc/meminfo')
CPU = Path('/proc/stat')


def read(path, maximum):
    contract.require(not any(p.is_symlink() for p in (path, *path.parents)))
    with path.open('rb') as stream:
        raw = stream.read(maximum + 1)
    contract.require(len(raw) <= maximum)
    return raw.decode('ascii')


def memory():
    fields = {}
    wanted = {'MemTotal', 'MemAvailable', 'SwapTotal', 'SwapFree'}
    for line in read(MEMORY, 65536).splitlines():
        name, separator, value = line.partition(':')
        if name not in wanted:
            continue
        parts = value.split()
        contract.require(separator and name not in fields and len(parts) == 2
                         and parts[0].isascii() and parts[0].isdecimal() and parts[1] == 'kB')
        fields[name] = int(parts[0]) * 1024
    contract.require(set(fields) == wanted)
    row = {'available': True, 'totalBytes': fields['MemTotal'], 'availableBytes': fields['MemAvailable'],
           'swapTotalBytes': fields['SwapTotal'], 'swapFreeBytes': fields['SwapFree']}
    contract.validate_group('memory', row)
    return row


def counters():
    # Read just the first bounded line, never per-process or per-CPU inventories.
    contract.require(not any(p.is_symlink() for p in (CPU, *CPU.parents)))
    with CPU.open('rb') as stream:
        raw = stream.readline(1025)
    contract.require(len(raw) <= 1024 and raw.endswith(b'\n'))
    parts = raw.decode('ascii').split()
    contract.require(parts[0] == 'cpu' and 9 <= len(parts) <= 11)
    contract.require(all(p.isascii() and p.isdecimal() for p in parts[1:]))
    # Guest/guest_nice are already in user/nice; exclude them from the sum.
    return [contract.number(int(p)) for p in parts[1:9]]


def cpu():
    before, start = counters(), time.monotonic()
    time.sleep(1)
    after, end = counters(), time.monotonic()
    delta = [b - a for a, b in zip(before, after)]
    contract.require(all(d >= 0 for d in delta) and sum(delta) > 0)
    total = sum(delta)
    # Executed work excludes idle, iowait and stolen hypervisor time.
    busy = sum(delta[i] for i in (0, 1, 2, 5, 6)) / total
    row = {'available': True, 'busyRatio': busy, 'stealRatio': delta[7] / total, 'sampleSeconds': end - start}
    contract.validate_group('cpu', row)
    return row


def capture():
    groups = {}
    for name, observe in (('memory', memory), ('cpu', cpu)):
        try:
            groups[name] = observe()
        except (ValueError, OSError, OverflowError, IndexError):
            groups[name] = {'available': False}
    now = dt.datetime.now(contract.UTC)
    value = {'schemaVersion': 1, 'observedAtUtc': now.isoformat(), **groups}
    contract.validate(value, now)
    return value


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 1)
    signal.alarm(8)
    print(json.dumps(capture(), separators=(',', ':'), allow_nan=False))


if __name__ == '__main__':
    try:
        main()
    except Exception:
        print('RESOURCE_OBSERVATION=refused', file=sys.stderr)
        sys.exit(1)
