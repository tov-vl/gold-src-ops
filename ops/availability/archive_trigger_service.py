"""Linux oneshot orchestration; persistent claims survive service termination."""
import contextlib
import datetime as dt
import errno
import importlib.util
import json
import os
from pathlib import Path
import signal
import stat
import struct
import sys
import tempfile

SPEC = importlib.util.spec_from_file_location('trigger_github', Path(__file__).with_name('archive_trigger_github.py'))
github = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(github)
state, workflow = github.state, github.workflow
CONFIG = Path('/etc/goldsrcops/archive-trigger/config.json')
STORE = Path('/var/lib/goldsrcops-archive-trigger')
OBSERVATION = Path('/var/lib/goldsrcops-archive-trigger-observation/latest.json')
SYSTEMD_CREDENTIALS = Path('/run/credentials/goldsrcops-archive-trigger.service')


def utcnow():
    return dt.datetime.now(state.UTC).replace(microsecond=0)


def protected_read(path, limit, owner=None, private=False):
    path = Path(path)
    state.require(os.name == 'posix', 'linux-required')
    # Reject symlink parents as well as the final file; installation owns these directories.
    state.require(not any(p.is_symlink() for p in (path, *path.parents)), 'symlink-path')
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        info = os.fstat(descriptor)
        state.require(stat.S_ISREG(info.st_mode) and 0 < info.st_size <= limit
            and info.st_uid == (os.geteuid() if owner is None else owner)
            and info.st_nlink == 1 and not info.st_mode & (0o077 if private else 0o022), 'protected-file')
        with os.fdopen(descriptor, 'rb', closefd=False) as stream:
            raw = stream.read(limit+1)
        state.require(0 < len(raw) <= limit)
        return raw
    finally:
        os.close(descriptor)


def load_token(directory):
    raw = protected_read(Path(directory)/'github-token', 513, private=True)
    token = raw.decode('ascii').removesuffix('\n')
    state.require(1 <= len(token) <= 512 and all(c.isalnum() or c == '_' for c in token), 'credential-format')
    return token


def _systemd_access(descriptor, uid, permission):
    info = os.fstat(descriptor)
    directory = permission == 5
    state.require(uid > 0 and permission in (4, 5), 'credential-identity')
    state.require(stat.S_ISDIR(info.st_mode) if directory else
                  stat.S_ISREG(info.st_mode) and info.st_nlink == 1 and 0 < info.st_size <= 513,
                  'credential-file')
    state.require(os.fstatvfs(descriptor).f_flag & os.ST_RDONLY, 'credential-mount')
    try:
        acl = os.getxattr(descriptor, 'system.posix_acl_access')
    except OSError as error:
        if error.errno not in (errno.ENODATA, errno.ENOTSUP):
            raise ValueError('credential-acl') from None
        acl = None
    mode = stat.S_IMODE(info.st_mode)
    if info.st_uid == uid:
        state.require(mode == permission << 6 and acl is None, 'credential-private-owner')
    else:
        # systemd normally retains root ownership and grants only the service UID
        # access through an ACL. The group-class mode bits then represent its mask.
        expected = struct.pack('<I', 2) + b''.join(struct.pack('<HHI', tag, access, identity)
            for tag, access, identity in ((1, permission, 0xffffffff), (2, permission, uid),
                (4, 0, 0xffffffff), (16, permission, 0xffffffff), (32, 0, 0xffffffff)))
        state.require(info.st_uid == 0 and info.st_gid == 0
            and mode == (permission << 6 | permission << 3) and acl == expected, 'credential-root-acl')


def load_systemd_token(directory):
    directory = Path(directory)
    state.require(os.name == 'posix' and directory == SYSTEMD_CREDENTIALS, 'credential-directory')
    state.require(not any(p.is_symlink() for p in (directory, *directory.parents)), 'symlink-path')
    uid = os.geteuid()
    parent = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        _systemd_access(parent, uid, 5)
        descriptor = os.open('github-token', os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            _systemd_access(descriptor, uid, 4)
            with os.fdopen(descriptor, 'rb', closefd=False) as stream:
                raw = stream.read(514)
            state.require(0 < len(raw) <= 513, 'credential-bound')
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)
    token = raw.decode('ascii').removesuffix('\n')
    state.require(1 <= len(token) <= 512 and all(c.isalnum() or c == '_' for c in token), 'credential-format')
    return token


def configuration(raw):
    value = workflow.decode(raw)
    state.require(type(value) is dict and set(value) == {'schema', 'mode', 'generation',
        'exporter', 'manifest', 'activation', 'coverage_end', 'assessment_closed'})
    state.require(type(value['schema']) is int and value['schema'] == 1
        and value['mode'] in ('paused', 'external') and type(value['assessment_closed']) is bool)
    state.identifier(value['generation'])
    state.digest(value['exporter'], 40)
    state.manifest(value['manifest'])
    state.require(set(value['manifest']) == set(workflow.FILES))
    state.require(state.moment(value['coverage_end']) <= state.moment(value['activation']))
    return value


@contextlib.contextmanager
def locked(directory):
    import fcntl
    directory = Path(directory)
    state.require(directory.is_dir() and not directory.is_symlink())
    fd = os.open(directory/'service.lock', os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        info = os.fstat(fd)
        state.require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid()
                      and info.st_nlink == 1 and not info.st_mode & 0o077)
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(fd)


def publish(path, value):
    path = Path(path)
    raw = json.dumps(value, sort_keys=True, separators=(',', ':')).encode()
    state.require(len(raw) <= 16384 and path.parent.is_dir() and not path.is_symlink()
                  and not any(p.is_symlink() for p in path.parents))
    fd, temporary = tempfile.mkstemp(prefix='.projection-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            os.fchmod(stream.fileno(), 0o640)
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(parent)
        finally:
            os.close(parent)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def safe_projection(ledger, config, path, now):
    try:
        result = github.projection(ledger, config['generation'], now)
    except ValueError:
        result = {}  # The observer publishes ObservationInvalid; no stale success is fabricated.
    publish(path, result)


def run_pass(ledger, config, path, client_factory, clock=utcnow):
    now = clock()
    state.require(state.moment(config['activation']) <= now, 'future-activation')
    with ledger.transaction(now):
        pass  # Validate the durable clock even when there is no work.
    rows = ledger.connection.execute(
        "SELECT * FROM claims WHERE state IN ('Prepared','Attempting','Accepted','Unknown') LIMIT 2").fetchall()
    state.require(len(rows) <= 1, 'competing-claims')
    if rows:
        row = dict(rows[0])
        if row['state'] in ('Unknown', 'Attempting'):
            safe_projection(ledger, config, path, now)
            return 'blocked-uncertain'
        if row['state'] == 'Accepted':
            github.reconcile(ledger, row['claim'], client_factory(), now, clock=clock)
            safe_projection(ledger, config, path, clock())
            return 'reconciled'
        # Prepared has never reached POST. Stop rather than dispatch a stale or wrong-generation claim.
        if (config['mode'] != 'external' or row['generation'] != config['generation']
                or row['slot'] != state.stamp(state.newest_slot(now))
                or row['exporter'] != config['exporter']
                or row['manifest'] != state.manifest(config['manifest'])):
            ledger.transition(row['claim'], 'Failed-known', now)
            safe_projection(ledger, config, path, now)
            return 'prepared-retired'
    else:
        if config['mode'] == 'paused':
            safe_projection(ledger, config, path, now)
            return 'paused'
        slot = state.stamp(state.newest_slot(now))
        if ledger.connection.execute('SELECT 1 FROM claims WHERE generation=? AND slot=?',
                                    (config['generation'], slot)).fetchone():
            safe_projection(ledger, config, path, now)
            return 'slot-recorded'
        latest = ledger.connection.execute(
            "SELECT slot FROM claims WHERE state='Verified' ORDER BY slot DESC LIMIT 1").fetchone()
        covered = state.moment(state.window(latest['slot'])[1] if latest else config['coverage_end'])
        _, end = state.window(slot)
        if state.moment(end) <= covered:
            safe_projection(ledger, config, path, now)
            return 'slot-covered'
        state.require(state.moment(end)-covered <= dt.timedelta(hours=24), 'catchup-bound-exceeded')
        # Read the credential before reserving a new claim; missing credentials never imply a POST.
        client = client_factory()
        row = ledger.prepare(config['generation'], slot, config['exporter'], config['manifest'], now)
    if rows:
        client = client_factory()
    # Service lock spans this whole pass; ledger Attempting is committed before sending.
    try:
        state.attempt_once(ledger, row['claim'], now, client.dispatch)
    finally:
        safe_projection(ledger, config, path, clock())
    return 'dispatched'


def main():
    state.require(len(sys.argv) == 2 and sys.argv[1] in ('initialize', 'run', 'status', 'check-credential'))
    if sys.argv[1] == 'check-credential':
        load_systemd_token(os.environ['CREDENTIALS_DIRECTORY'])
        print('ARCHIVE_TRIGGER_CREDENTIAL=validated')
        return
    config = configuration(protected_read(CONFIG, 16384, owner=0))
    state.require(config['manifest'] == workflow.content_manifest(Path(__file__).resolve().parents[2]),
                  'installed-content-changed')
    with locked(STORE):
        if sys.argv[1] == 'initialize':
            state.require(config['mode'] == 'paused')
            ledger = state.Ledger.create(STORE/'ledger.db', utcnow())
        else:
            info=(STORE/'ledger.db').lstat()
            state.require(stat.S_ISREG(info.st_mode) and info.st_uid==os.geteuid()
                          and info.st_nlink==1 and info.st_mode & 0o777 == 0o600, 'protected-ledger')
            ledger = state.Ledger(STORE/'ledger.db')
        try:
            if sys.argv[1] == 'run':
                def client():
                    return github.GitHub(load_systemd_token(os.environ['CREDENTIALS_DIRECTORY']))
                result = run_pass(ledger, config, OBSERVATION, client)
                protected = utcnow()-dt.timedelta(days=7) if config['assessment_closed'] else state.moment(config['activation'])
                ledger.retire(utcnow(), protected)
            else:
                # Status is read-only and never loads a credential or changes projection/clock.
                result = 'outstanding' if ledger.connection.execute(
                    "SELECT 1 FROM claims WHERE state IN ('Prepared','Attempting','Accepted','Unknown') LIMIT 1"
                ).fetchone() else 'idle'
            print('ARCHIVE_TRIGGER=' + result)
        finally:
            ledger.close()


if __name__ == '__main__':
    signal.signal(signal.SIGALRM, lambda *_: (_ for _ in ()).throw(TimeoutError('pass-expired')))
    signal.alarm(85)
    os.umask(0o077)
    try:
        main()
    except Exception:
        print('ARCHIVE_TRIGGER=stopped', file=sys.stderr)
        sys.exit(1)
