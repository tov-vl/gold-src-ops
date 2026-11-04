#!/usr/bin/env python3
"""Fixed passive filesystem aggregates, independent of agent and SQLite health."""
import datetime as dt
import json
import os
from pathlib import Path
import signal
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import storage_diagnostics as contract

PATHS = {
    'queue': Path('/var/lib/goldsrc/game-event-agent/queue'),
    'spool': Path('/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/goldsrcops-spool'),
    'players': Path('/var/lib/goldsrc/server/cstrike/addons/amxmodx/data/vault'),
    'backup': Path('/var/lib/goldsrcops-player-backup'),
}


def observe(path):
    contract.require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    contract.require(path.is_dir())
    value = os.statvfs(path)
    contract.require(value.f_frsize > 0 and value.f_blocks > 0)
    # Available-to-unprivileged values exclude root-reserved capacity. Negative
    # availability is conservatively full, never a healthy unsigned wraparound.
    row = {'available': True, 'bytesTotal': value.f_blocks * value.f_frsize,
           'bytesAvailable': max(0, value.f_bavail) * value.f_frsize,
           'inodesTotal': value.f_files or None,
           'inodesAvailable': max(0, value.f_favail) if value.f_files else None}
    contract.validate_row(row)
    return row


def capture():
    roles = {}
    for role, path in PATHS.items():
        try:
            roles[role] = observe(path)
        except (ValueError, OSError, OverflowError):
            roles[role] = {'available': False}
    value = {'schemaVersion': 1, 'observedAtUtc': dt.datetime.now(contract.UTC).isoformat(), 'roles': roles}
    contract.validate(value, dt.datetime.now(contract.UTC))
    return value


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 1)
    print(json.dumps(capture(), separators=(',', ':'), allow_nan=False))


if __name__ == '__main__':
    def interrupted(_number, _frame):
        raise InterruptedError('Storage observation interrupted.')
    for number in (signal.SIGHUP, signal.SIGTERM):
        signal.signal(number, interrupted)
    try:
        main()
    except Exception:
        print('STORAGE_OBSERVATION=refused', file=sys.stderr)
        sys.exit(1)
