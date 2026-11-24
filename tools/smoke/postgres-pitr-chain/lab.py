"""Owned Docker resources for the isolated PostgreSQL PITR rehearsal."""

import json
import re
import subprocess
import time
import uuid
from pathlib import Path

KIT = Path(__file__).resolve().parent
LABEL = 'goldsrcops.pitr-chain'
IMAGE_REFS = {
    'postgres': 'postgres:16-alpine@sha256:cf78e76683b9ca8c5733cbbdce6c9262b45b6767934dd0a95e671f9a0fc20685',
    'archive': 'python:3.12-slim@sha256:b18b261e1b4974b4f321451ad5251ded85abb22c2b8b262da527dda1d031ab30',
}


def docker_cli(context):
    return ['docker', '--context', context] if context else ['docker']


def resolve_images(context, pull):
    cli = docker_cli(context)
    resolved = {}
    for name, qualified in IMAGE_REFS.items():
        tag, digest = qualified.split('@')
        expected = f'{tag.split(":", 1)[0]}@{digest}'
        if pull:
            subprocess.run([*cli, 'pull', qualified], check=True, timeout=100)
        for reference in (qualified, expected, tag):
            result = subprocess.run([*cli, 'image', 'inspect', reference], capture_output=True, timeout=20)
            if result.returncode == 0:
                image, = json.loads(result.stdout)
                matching = {expected, f'docker.io/library/{expected}'}.intersection(image['RepoDigests'] or [])
                if matching and image['Architecture'] == 'amd64' and image['Os'] == 'linux':
                    resolved[name] = image['Id']
                    break
        require(name in resolved, f'Pinned Linux amd64 image is missing: {expected}')
    return resolved


RESTORE = r"""
set -eu
export LC_ALL=C
sh /lab/wal_io.sh fetch "$1" /base /tmp/base.tar.gz
mkdir /tmp/recovery-data
tar -xzf /tmp/base.tar.gz -C /tmp/recovery-data
chmod 0700 /tmp/recovery-data
chown -R postgres:postgres /tmp/recovery-data
touch /tmp/recovery-data/recovery.signal
exec gosu postgres postgres -D /tmp/recovery-data \
    -c listen_addresses=127.0.0.1 -c archive_mode=off -c hot_standby=off \
    -c "restore_command=sh /lab/wal_io.sh fetch $1 /wal/%f %p" \
    -c "recovery_target_name=$2" -c recovery_target_action=promote
"""


def require(ok, detail):
    if not ok:
        raise RuntimeError(detail)


def wait_for(description, probe, timeout):
    limit = time.monotonic() + timeout
    while time.monotonic() < limit:
        result = probe()
        if result:
            return result
        time.sleep(0.3)
    raise TimeoutError(description)


class Lab:
    def __init__(self, context, output, images):
        self.context = context
        self.cli = docker_cli(context)
        self.images = images
        self.output = output
        self.token = uuid.uuid4().hex[:12]
        self.name = 'gso-pitr-chain-' + self.token
        self.network = self.name + '-net'
        self.primary_archive = self.name + '-archive'
        self.primary = self.name + '-source'
        self.volumes = [self.name + '-store']
        self.containers = []
        self.started = time.monotonic()


    def docker(self, *args, timeout=25, check=True):
        result = subprocess.run([*self.cli, *args], capture_output=True,
                                encoding='utf-8', errors='replace', timeout=timeout)
        if check and result.returncode:
            raise RuntimeError(f'Docker {args[0]} failed ({result.returncode}): {result.stderr.strip()[-1800:]}')
        return result

    def stamp(self):
        return round(time.monotonic() - self.started, 3)

    def create(self, name, image, options, command):
        self.containers.append(name)
        self.docker('run', '--pull', 'never', '-d', '--name', name, '--label', f'{LABEL}={self.token}',
                    '--network', self.network, '--memory', '384m', '--cpus', '1',
                    '--mount', f'type=bind,source={KIT},target=/lab,readonly', *options, image, *command)

    def storage(self, name, volume, readonly=False):
        mount = f'type=volume,source={volume},target=/archive' + (',readonly' if readonly else '')
        self.create(name, self.images['archive'], ['--mount', mount, '-e', f'LAB_READ_ONLY={int(readonly)}'],
                    ['python', '-B', '/lab/archive_server.py'])
        self.wait_for_storage(name)

    def wait_for_storage(self, name):
        wait_for('archive HTTP listener did not become healthy',
                 lambda: self.archive_read(name, '/health') == 'ok', 20)

    def archive_read(self, name, key):
        result = self.docker('exec', name, 'python', '-c',
                             'import sys, urllib.request as u; print(u.urlopen("http://127.0.0.1:8080"+sys.argv[1], timeout=2).read().decode().strip())',
                             key, timeout=5, check=False)
        return result.stdout.strip() if result.returncode == 0 else None

    def sql(self, name, sql):
        return self.docker('exec', name, 'psql', '-XAt', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres',
                           '-d', 'postgres', '-c', sql).stdout.strip()

    def ready(self, name):
        return self.docker('exec', name, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres',
                           check=False, timeout=5).returncode == 0

    def ids(self, name):
        return json.loads(self.sql(name, 'SELECT json_agg(id ORDER BY id) FROM lab_receipt'))

    def mark(self, label):
        segment = self.sql(self.primary, f"SELECT pg_walfile_name(pg_create_restore_point('{label}'))")
        require(bool(re.fullmatch('[0-9A-F]{24}', segment)), 'No exact WAL segment for target')
        self.sql(self.primary, 'SELECT pg_switch_wal()')
        return segment

    def is_running(self, name):
        return self.docker('inspect', '--format', '{{.State.Running}}', name).stdout.strip() == 'true'

    def restore(self, suffix, archive, target, expected):
        name = self.name + '-' + suffix
        began = self.stamp()
        self.create(name, self.images['postgres'], ['--entrypoint', '/bin/sh'], ['-ec', RESTORE, 'restore', archive, target])
        status = wait_for('restore neither started nor exited',
                          lambda: 'ready' if self.ready(name) else ('exited' if not self.is_running(name) else None), 35)
        logs = self.docker('logs', name)
        log = logs.stdout + logs.stderr
        (self.output / f'{suffix}.log').write_text(log, encoding='utf-8')
        require(status == 'ready' and f'recovery stopping at restore point "{target}"' in log,
                f'Restore did not reach {target}: {log[-1600:]}')
        rows = self.ids(name)
        require(self.sql(name, 'SELECT pg_is_in_recovery()') == 'f' and rows == expected,
                f'Restore {target} did not return the exact writable state')
        return dict(target=target, reached=True, receipts=rows, seconds=round(self.stamp() - began, 3))

    def cleanup(self):
        problems = []
        entries = [('container', name, ['rm', '-f', '-v']) for name in reversed(self.containers)]
        entries += [('volume', volume, ['volume', 'rm']) for volume in self.volumes]
        entries.append(('network', self.network, ['network', 'rm']))
        for kind, name, removal in entries:
            inspected = self.docker(kind, 'inspect', name, check=False)
            if inspected.returncode:
                continue
            data = json.loads(inspected.stdout)[0]
            labels = (data.get('Config', {}) if kind == 'container' else data).get('Labels') or {}
            if labels.get(LABEL) != self.token:
                problems.append(f'Unexpected ownership for {kind} {name}')
            elif self.docker(*removal, name, check=False).returncode:
                problems.append(f'Could not remove {kind} {name}')
        return problems

    def initialize(self, archive_timeout):
        self.docker('network', 'create', '--internal', '--label', f'{LABEL}={self.token}', self.network)
        for volume in self.volumes:
            self.docker('volume', 'create', '--label', f'{LABEL}={self.token}', volume)
        self.storage(self.primary_archive, self.volumes[0])
        archive_command = f'archive_command=sh /lab/wal_io.sh send {self.primary_archive} "/wal/%f" "%p"'
        self.create(self.primary, self.images['postgres'], ['-e', 'POSTGRES_HOST_AUTH_METHOD=trust'],
                    ['-c', 'listen_addresses=127.0.0.1', '-c', 'archive_mode=on', '-c', archive_command,
                     '-c', f'archive_timeout={archive_timeout}', '-c', 'track_commit_timestamp=on',
                     '-c', 'wal_keep_size=64MB'])
        wait_for('source PostgreSQL did not start', lambda: self.ready(self.primary), 25)
        self.sql(self.primary, 'CREATE TABLE lab_receipt(id int PRIMARY KEY)')
        self.sql(self.primary, 'INSERT INTO lab_receipt VALUES (1)')
        self.docker('exec', self.primary, 'pg_basebackup', '-h', '/var/run/postgresql', '-U', 'postgres',
                    '-D', '/tmp/lab-backup', '--format=tar', '--gzip', '--wal-method=none', '--checkpoint=fast',
                    timeout=90)
        self.docker('exec', self.primary, 'sh', '/lab/wal_io.sh', 'send', self.primary_archive, '/base',
                    '/tmp/lab-backup/base.tar.gz')
        base = self.archive_read(self.primary_archive, '/sha/base')
        expected = self.docker('exec', self.primary, 'sha256sum', '/tmp/lab-backup/base.tar.gz').stdout.split()[0]
        require(base == expected, 'Base backup archive checksum differs')

    def stop_source(self):
        self.docker('stop', '-t', '5', self.primary)
        require(not self.is_running(self.primary), 'Source remained active during restore')

    def verify_network(self):
        info = json.loads(self.docker('network', 'inspect', self.network).stdout)[0]
        require(info['Internal'], 'Rehearsal network is not internal')
        for name in self.containers:
            bindings = self.docker('inspect', '--format', '{{json .HostConfig.PortBindings}}', name)
            require(not json.loads(bindings.stdout), 'Rehearsal container published a port')
