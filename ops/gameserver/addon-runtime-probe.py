#!/usr/bin/env python3
"""Bounded read-only local RCON acceptance for a frozen weapon addon version."""
import argparse
import hashlib
import itertools
import json
import os
from pathlib import Path
import re
import socket
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import player_data_schedule as policy

HEADER = b'\xff' * 4
COMMANDS = ('goldsrcops_weapons_status', 'goldsrcops_stats_status')


def query(address, password, command):
    policy.require(command in COMMANDS)
    deadline = time.monotonic() + 4
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.connect(address)
        client.settimeout(2)
        client.send(HEADER + b'challenge rcon\n')
        challenge = client.recv(4096)
        match = re.fullmatch(rb'\xff{4}challenge rcon (-?\d+)\s*\x00?', challenge)
        policy.require(match is not None)
        client.send(HEADER + b'rcon ' + match[1] + b' "' + password + b'" ' + command.encode('ascii') + b'\n')
        output = bytearray()
        while True:
            remaining = deadline - time.monotonic()
            policy.require(remaining > 0)
            client.settimeout(min(0.3 if output else 2, remaining))
            try:
                packet = client.recv(8193)
            except socket.timeout:
                policy.require(bool(output))
                break
            policy.require(len(packet) <= 8192 and packet.startswith(HEADER + b'l'))
            output.extend(packet[5:].rstrip(b'\x00'))
            policy.require(len(output) <= 16384)
        return output.decode('utf-8')


def verify(text, version):
    policy.require(re.fullmatch(r'\d+\.\d+\.\d+', version) is not None)
    policy.require(not re.search(r'Run time error|Load fails|bad load|ML_NOTFOUND|Bad rcon_password', text, re.I))
    required = {
        'WEAPON_SELECTION_STATUS': {'enabled': '1', 'persistent_stats': '1'},
        'PLAYER_PREFERENCES_STATUS': {'storage': 'nvault', 'errors': '0'},
        'PLAYER_STATS_STATUS': {'storage': 'nvault', 'errors': '0'},
        'PLAYER_LEADERBOARD_STATUS': {'ready': '1', 'errors': '0'},
    }
    for prefix, expected in required.items():
        lines = [line for line in text.splitlines() if line.startswith(prefix + ' ')]
        policy.require(len(lines) == 1)
        pairs = [token.split('=', 1) for token in lines[0].split()[1:]]
        policy.require(all(len(pair) == 2 for pair in pairs) and len(dict(pairs)) == len(pairs))
        fields = dict(pairs)
        policy.require(fields.get('version') == version and all(fields.get(k) == v for k, v in expected.items()))


def errors_absent(directory):
    policy.require(directory.is_dir() and not any(p.is_symlink() for p in (directory, *directory.parents)))
    logs = list(itertools.islice(directory.glob('error_*.log'), 129))
    policy.require(len(logs) <= 128)
    policy.require(all(p.is_file() and not p.is_symlink() and p.stat().st_size == 0 for p in logs))


def read_password(source):
    raw = source.read(130)
    policy.require(len(raw) <= 129)
    password = raw.rstrip(b'\r\n')
    policy.require(1 <= len(password) <= 128 and all(32 < c < 127 and c not in (34, 59, 92) for c in password))
    return password


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--expectations', type=Path, required=True)
    parser.add_argument('--expectations-sha256', required=True)
    parser.add_argument('--error-directory', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('kind', choices=['weapon-selection'])
    parser.add_argument('manifest')
    args = parser.parse_args()
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
