#!/usr/bin/env python3
"""One claimed dashboard install/rollback through the existing private Grafana."""
import argparse
import base64
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import stat
import subprocess
import sys

UID = 'goldsrcops-game-event-agent'
CONTAINER = 'goldsrcops-grafana-1'
IMAGE = 'grafana/grafana:13.2.1@sha256:f772d434e8fab0049deb2b1b30abd43342bcfca1537614aa8d36080232cf4283'
PASSWORD = Path('/etc/goldsrcops/secrets/grafana-admin-password')
STATE = Path('/var/lib/goldsrcops/agent-monitor-rollout')
DASHBOARD = Path(__file__).resolve().parent / 'agent-monitor-dashboard.json'
MAXIMUM = 262144


def require(value, message='Dashboard boundary rejected.'):
    if not value:
        raise ValueError(message)


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def encode(value):
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False) + '\n').encode()


def trusted(path, owner=0, mode=None, maximum=MAXIMUM):
    require(path.is_absolute() and not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == owner and info.st_nlink == 1
            and info.st_mode & 0o022 == 0 and info.st_size <= maximum)
    if mode is not None:
        require(stat.S_IMODE(info.st_mode) == mode)
    return path.read_bytes()


def write_new(path, value):
    with path.open('xb') as stream:
        os.fchmod(stream.fileno(), 0o600)
        stream.write(encode(value))
        stream.flush()
        os.fsync(stream.fileno())
    descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def context():
    # Resolve only the existing pinned container, before reading its password.
    template = '{"image":{{json .Config.Image}},"running":{{json .State.Running}},"networks":{{json .NetworkSettings.Networks}}}'
    result = subprocess.run(['docker', 'inspect', '--format', template, CONTAINER],
                            capture_output=True, timeout=10, check=False)
    require(result.returncode == 0 and len(result.stdout) <= 16384)
    container = json.loads(result.stdout)
    require(container['image'] == IMAGE and container['running'] is True
            and set(container['networks']) == {'goldsrcops_telemetry'})
    address = container['networks']['goldsrcops_telemetry']['IPAddress']
    require(any(ipaddress.IPv4Address(address) in ipaddress.IPv4Network(network)
                for network in ('10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16')))
    password = trusted(PASSWORD, owner=472, mode=0o400, maximum=256)
    require(16 <= len(password) <= 256 and b'\n' not in password and b'\r' not in password)
    return address, {'Authorization': 'Basic ' + base64.b64encode(b'admin:' + password).decode(),
                     'Content-Type': 'application/json'}


def api(connection, method, path, value=None):
    address, headers = connection
    require(path in ('/api/dashboards/db', '/api/dashboards/uid/' + UID,
                     '/api/dashboards/uid/goldsrcops-operations') and method in ('GET', 'POST', 'DELETE'))
    client = http.client.HTTPConnection(address, 3000, timeout=5)
    try:
        client.request(method, path, body=encode(value) if value is not None else None, headers=headers)
        response = client.getresponse()
        raw = response.read(MAXIMUM + 1)
        require(len(raw) <= MAXIMUM)
        require(response.status in (200, 404) and response.getheader('Content-Type', '').split(';')[0] == 'application/json')
        return response.status, json.loads(raw)
    finally:
        client.close()


def state_directory():
    require(not any(p.is_symlink() for p in (STATE, *STATE.parents)))
    if not STATE.exists():
        STATE.mkdir(mode=0o700)
    info = STATE.stat()
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o700)


def check_body(actual, expected):
    require(actual.get('uid') == UID and actual.get('editable') is False)
    require(all(actual.get(key) == value for key, value in expected.items() if key not in ('id', 'version')))


def execute(action, expected_hash):
    raw = trusted(DASHBOARD)
    require(digest(raw) == expected_hash)
    expected = json.loads(raw)
    require(expected['uid'] == UID and expected['editable'] is False)
    state_directory()
    pending, accepted = STATE / 'pending.json', STATE / 'accepted.json'
    require(not pending.exists() and not pending.is_symlink(), 'A dashboard request requires reconciliation.')
    connection = context()
    status, current = api(connection, 'GET', '/api/dashboards/uid/' + UID)
    if action == 'install':
        require(status == 404 and not accepted.exists() and not accepted.is_symlink())
        code, folder = api(connection, 'GET', '/api/dashboards/uid/goldsrcops-operations')
        require(code == 200 and folder['meta']['folderUid'])
        write_new(pending, {'schemaVersion': 1, 'action': 'install', 'dashboardSha256': expected_hash})
        code, result = api(connection, 'POST', '/api/dashboards/db', {
            'dashboard': expected, 'folderUid': folder['meta']['folderUid'], 'overwrite': False})
        require(code == 200 and result['status'] == 'success' and result['uid'] == UID)
        code, result = api(connection, 'GET', '/api/dashboards/uid/' + UID)
        require(code == 200 and result['meta']['provisioned'] is False)
        check_body(result['dashboard'], expected)
        write_new(accepted, {'schemaVersion': 1, 'dashboardSha256': expected_hash,
                            'acceptedSha256': digest(encode(result['dashboard'])),
                            'version': result['dashboard']['version'], 'uid': UID})
    else:
        receipt = json.loads(trusted(accepted, mode=0o600))
        require(status == 200 and current['meta']['provisioned'] is False
                and receipt['schemaVersion'] == 1 and receipt['dashboardSha256'] == expected_hash
                and receipt['uid'] == UID and receipt['version'] == current['dashboard']['version']
                and receipt['acceptedSha256'] == digest(encode(current['dashboard'])))
        require(not (STATE / 'rolled-back.json').exists())
        write_new(pending, {'schemaVersion': 1, 'action': 'rollback', 'acceptedSha256': receipt['acceptedSha256']})
        code, _ = api(connection, 'DELETE', '/api/dashboards/uid/' + UID)
        require(code == 200)
        code, _ = api(connection, 'GET', '/api/dashboards/uid/' + UID)
        require(code == 404)
        write_new(STATE / 'rolled-back.json', {'schemaVersion': 1, 'uid': UID, 'state': 'rolled-back'})
    pending.unlink()
    descriptor = os.open(STATE, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('install', 'rollback'))
    parser.add_argument('--dashboard-sha256', required=True)
    args = parser.parse_args()
    require(os.geteuid() == 0)
    execute(args.action, args.dashboard_sha256)
    print('AGENT_MONITOR_DASHBOARD=' + ('installed' if args.action == 'install' else 'rolled-back'))


if __name__ == '__main__':
    os.umask(0o077)
    try:
        main()
    except Exception:
        print('AGENT_MONITOR_DASHBOARD=failed; reconcile_before_retry', file=sys.stderr)
        sys.exit(1)
