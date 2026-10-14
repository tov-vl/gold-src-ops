#!/usr/bin/env python3
"""Local RCON acceptance for explicitly isolated disposable engine fixtures only."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import re
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import player_data_schedule as policy

from addon_runtime_protocol import HEADER, COMMANDS, query, verify, read_password


def errors_absent(directory):
    policy.require(directory.is_dir() and not any(p.is_symlink() for p in (directory, *directory.parents)))
    logs = list(itertools.islice(directory.glob('error_*.log'), 129))
    policy.require(len(logs) <= 128)
    policy.require(all(p.is_file() and not p.is_symlink() and p.stat().st_size == 0 for p in logs))


def require_isolated_fixture(enabled, container=Path('/.dockerenv'), managed=Path('/etc/goldsrcops/gameserver')):
    # Refuse before reading stdin or opening a socket, even if the flag is supplied.
    policy.require(enabled and container.is_file() and not managed.exists() and not managed.is_symlink())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--isolated-fixture', action='store_true')
    parser.add_argument('--expectations', type=Path, required=True)
    parser.add_argument('--expectations-sha256', required=True)
    parser.add_argument('--error-directory', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('kind', choices=['weapon-selection'])
    parser.add_argument('manifest')
    args = parser.parse_args()
    require_isolated_fixture(args.isolated_fixture)
    policy.require(os.geteuid() == 0 and 1 <= args.port <= 65535)
    with policy.private_path(args.expectations).open('rb') as source:
        raw = source.read(4097)
    policy.require(len(raw) <= 4096 and hashlib.sha256(raw).hexdigest() == args.expectations_sha256)
    versions = json.loads(raw)
    policy.require(isinstance(versions, dict) and 1 <= len(versions) <= 2)
    policy.require(all(re.fullmatch('[a-f0-9]{64}', k) and re.fullmatch(r'\d+\.\d+\.\d+', v) for k, v in versions.items()))
    policy.require(args.manifest in versions)
    password = read_password(sys.stdin.buffer)
    errors_absent(args.error_directory)
    text = '\n'.join(query(('127.0.0.1', args.port), password, command) for command in COMMANDS)
    verify(text, versions[args.manifest])
    errors_absent(args.error_directory)
    print('ADDON_RUNTIME_ACCEPTANCE=passed')


if __name__ == '__main__':
    try:
        main()
    except Exception:
        print('ADDON_RUNTIME_ACCEPTANCE=failed', file=sys.stderr)
        sys.exit(1)
