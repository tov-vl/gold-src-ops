"""Exercise scheduling decisions for a disposable PostgreSQL backup repository."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
from uuid import uuid4

if os.name == 'nt':
    import msvcrt
else:
    import fcntl

BACKUP = Path(__file__).resolve().parents[2] / 'ops/production/postgres-backup.ps1'
DEFAULT_POLICY = (1800, 2700, 3000, 3600)
ACCELERATED_POLICY = (8, 12, 50, 60)
LAB_LABEL = 'goldsrcops.logical-backup-rpo'
KEY = re.compile(r'gso-logical-([0-9a-f]{12})')
SNAPSHOT = re.compile(r'[0-9a-f]{64}')
STAMP = re.compile(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?(?:Z|[+-]\d{2}:\d{2})')
ARCHIVE_NAME = 'goldsrcops-postgresql.dump'
RECOVERABLE_TAG = 'goldsrcops-postgresql-recoverable'


def utc_now():
    return datetime.now(timezone.utc)


def instant(value):
    if not isinstance(value, str) or not STAMP.fullmatch(value):
        raise ValueError('Expected an offset-qualified timestamp')
    return datetime.fromisoformat(value)


def read_json(path):
    with path.open('rb') as stream:
        data = stream.read(65537)
    if len(data) > 65536:
        raise ValueError('Oversized receipt')
    return json.loads(data)


def replace_json(path, value):
    temporary = path.with_name(f'.{path.name}.{uuid4().hex}.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as stream:
            json.dump(value, stream, indent=2)
            stream.write('\n')
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


@contextmanager
def decision_lock(path, *, read_only=False):
    with path.open('rb' if read_only else 'a+b') as stream:
        stream.seek(0)
        try:
            if os.name == 'nt':
                mode = msvcrt.LK_NBRLCK if read_only else msvcrt.LK_NBLCK
                msvcrt.locking(stream.fileno(), mode, 1)
            else:
                mode = fcntl.LOCK_SH if read_only else fcntl.LOCK_EX
                fcntl.flock(stream.fileno(), mode | fcntl.LOCK_NB)
        except BlockingIOError:
            yield False
        except OSError as error:
            if os.name != 'nt' or error.errno != 13:
                raise
            yield False
        else:
            yield True


class Scheduler:
    def __init__(self, root, identity, policy=DEFAULT_POLICY, clock=utc_now):
        self.identity = identity
        self.policy = policy
        self.clock = clock
        self.state = root / 'catch-up-state.json'
        self.initialized = root / 'catch-up-initialized'
        self.lock = root / 'catch-up-decision.lock'
        self.identity_fields = dict(version=1, backupHost=identity, policySeconds=list(policy))

    def initialize(self):
        with decision_lock(self.lock) as acquired:
            if not acquired:
                raise ValueError('Another scheduler invocation owns the decision lock')
            if self.state.exists():
                raise ValueError('Scheduler already initialized')
            with self.initialized.open('x', encoding='ascii') as stream:
                stream.write(self.identity + '\n')
                stream.flush()
                os.fsync(stream.fileno())
            replace_json(self.state, {**self.identity_fields, 'lastVerified': None, 'attempt': None})

    def load(self):
        state = read_json(self.state)
        if self.initialized.read_text(encoding='ascii') != self.identity + '\n' or any(
                state[key] != value for key, value in self.identity_fields.items()):
            raise ValueError('Scheduler state does not belong to this lab and policy')
        point, attempt = state['lastVerified'], state['attempt']
        if point is not None:
            if not SNAPSHOT.fullmatch(point['snapshotId']):
                raise ValueError('Invalid verified snapshot ID')
            if not instant(point['coverageFromUtc']) <= instant(point['snapshotAtUtc']) <= instant(point['verifiedAtUtc']):
                raise ValueError('Invalid verified snapshot chronology')
        if attempt is not None:
            claimed = instant(attempt['claimedAtUtc'])
            if (not re.fullmatch(r'[0-9a-f]{32}', attempt['id'])
                    or attempt['trigger'] not in ('scheduled', 'catch-up')
                    or (point is not None and claimed < instant(point['verifiedAtUtc']))):
                raise ValueError('Invalid backup claim')
        return state

    def inspect(self, trigger):
        try:
            state = self.load()
        except (OSError, ValueError, KeyError, AttributeError, TypeError):
            return None, dict(decision='state-unavailable', rpo='unknown')
        now = self.clock()
        point = state['lastVerified']
        attempt = state['attempt']
        if attempt and instant(attempt['claimedAtUtc']) > now:
            return None, dict(decision='state-unavailable', rpo='unknown')
        pending = {'claimId': attempt['id']} if attempt else {}
        if point is None:
            decision = 'due' if trigger == 'scheduled' else 'no-verified-point'
            return state, dict(decision='unresolved-attempt' if attempt else decision, rpo='unknown', **pending)
        verified_age = (now - instant(point['verifiedAtUtc'])).total_seconds()
        point_age = (now - instant(point['coverageFromUtc'])).total_seconds()
        if verified_age < 0:
            return None, dict(decision='state-unavailable', rpo='unknown')
        scheduled, catch_up, warning, limit = self.policy
        threshold = scheduled if trigger == 'scheduled' else catch_up
        rpo = 'breached' if point_age > limit else 'warning' if point_age >= warning else 'within'
        decision = 'due' if verified_age >= threshold else 'fresh'
        return state, dict(decision='unresolved-attempt' if attempt else decision, rpo=rpo,
                           verifiedAgeSeconds=verified_age, recoveryPointAgeUpperBoundSeconds=point_age,
                           snapshotId=point['snapshotId'], **pending)

    def plan(self, trigger):
        with decision_lock(self.lock) as acquired:
            return self.inspect(trigger)[1] if acquired else dict(decision='busy', rpo='unknown')

    def validate_candidate(self, receipt, state, now):
        snapshot = receipt['SnapshotId']
        captured, completed = (instant(receipt[field]) for field in ('SnapshotTime', 'CompletedAtUtc'))
        previous = state['lastVerified']
        began = instant(state['attempt']['claimedAtUtc'])
        if (receipt['Action'] != 'PostgreSQLBackup' or receipt['Workload'] != 'ControlPlane'
                or receipt['BackupHost'] != self.identity or receipt['ArchiveName'] != ARCHIVE_NAME
                or not SNAPSHOT.fullmatch(snapshot) or not began <= captured <= completed <= now
                or (previous is not None and snapshot == previous['snapshotId'])):
            raise ValueError('Candidate receipt differs from the owned backup attempt')
        return snapshot, captured, completed

    def reconcile(self, read_candidate, list_snapshots):
        if not self.lock.is_file() or self.lock.resolve(strict=True) != self.lock:
            return dict(decision='state-unavailable', rpo='unknown', reconciliation='not-assessed')
        with decision_lock(self.lock, read_only=True) as acquired:
            if not acquired:
                return dict(decision='busy', rpo='unknown', reconciliation='not-assessed')
            state, report = self.inspect('catch-up')
            if state is None or state['attempt'] is None:
                reason = 'not-assessed' if state is None else 'no-unresolved-attempt'
                return {**report, 'reconciliation': reason}
            attempt = state['attempt']
            began, now = instant(attempt['claimedAtUtc']), self.clock()
            try:
                observed = {}
                for entry in list_snapshots():
                    if entry['hostname'] != self.identity or entry['paths'] != [f'/{ARCHIVE_NAME}']:
                        continue
                    captured = instant(entry['time'])
                    snapshot = entry['id']
                    if captured > now or not SNAPSHOT.fullmatch(snapshot) or snapshot in observed:
                        raise ValueError('Invalid local snapshot inventory')
                    if captured >= began:
                        observed[snapshot] = entry
            except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
                return {**report, 'reconciliation': 'uncertain', 'reason': 'inventory-unavailable'}

            report = {**report, 'candidateCount': len(observed)}
            if len(observed) > 1:
                return {**report, 'reconciliation': 'uncertain', 'reason': 'multiple-candidates'}
            try:
                receipt = read_candidate(attempt['id'])
                snapshot, captured, _ = self.validate_candidate(receipt, state, now)
            except FileNotFoundError:
                if observed:
                    return {**report, 'reconciliation': 'uncertain', 'reason': 'missing-receipt'}
                return {**report, 'reconciliation': 'no-candidate-observed', 'reason': 'missing-receipt'}
            except (OSError, ValueError, KeyError, TypeError):
                return {**report, 'reconciliation': 'uncertain', 'reason': 'invalid-receipt'}
            entry = observed.get(snapshot)
            if entry is None or entry.get('tags') != [RECOVERABLE_TAG] or instant(entry['time']) != captured:
                return {**report, 'reconciliation': 'uncertain', 'reason': 'receipt-snapshot-mismatch'}
            return {**report, 'reconciliation': 'recover-candidate', 'candidateSnapshotId': snapshot}

    def execute(self, trigger, backup, verify):
        with decision_lock(self.lock) as acquired:
            if not acquired:
                return dict(decision='busy', rpo='unknown')
            state, plan = self.inspect(trigger)
            if plan['decision'] != 'due':
                return plan
            began = self.clock()
            claim = dict(id=uuid4().hex, trigger=trigger, claimedAtUtc=began.isoformat())
            replace_json(self.state, {**state, 'attempt': claim})
            phase = 'write'
            try:
                receipt = backup(claim['id'])
                phase = 'receipt'
                snapshot, captured, completed = self.validate_candidate(
                    receipt, {**state, 'attempt': claim}, self.clock())
                phase = 'verification'
                verify(receipt)
                verified = self.clock()
                if verified < completed:
                    raise ValueError('Clock moved during backup verification')
                phase = 'publication'
                point = dict(snapshotId=snapshot, coverageFromUtc=began.isoformat(),
                             snapshotAtUtc=captured.isoformat(), verifiedAtUtc=verified.isoformat())
                replace_json(self.state, {**state, 'lastVerified': point, 'attempt': None})
            except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.SubprocessError):
                return {**self.inspect(trigger)[1], 'decision': 'failed', 'phase': phase, 'claimId': claim['id']}
            return {**self.inspect(trigger)[1], 'decision': 'verified', 'claimId': claim['id']}


class LocalBackup:
    def __init__(self, root):
        temp = Path(tempfile.gettempdir()).resolve()
        if root.parent != temp or not root.name.startswith('gso-logical-rpo-'):
            raise ValueError('Use a task-owned logical-backup directory in the system temporary directory')
        self.root = root
        self.config = root / 'deployment.env'
        if self.config.resolve(strict=True) != self.config:
            raise ValueError('Backup configuration must belong to the local lab')
        with self.config.open(encoding='ascii') as stream:
            settings = dict(line.strip().split('=', 1) for line in stream if line.strip())
        self.identity = settings['GOLDSRCOPS_BACKUP_HOST']
        match = KEY.fullmatch(self.identity)
        self.repository = root / 'repository'
        if (not match or settings['GOLDSRCOPS_BACKUP_REPOSITORY'] != '/repository'
                or self.repository.resolve(strict=True) != self.repository or not self.repository.is_dir()):
            raise ValueError('A disposable local backup repository is required')
        for key, image in (('GOLDSRCOPS_RESTIC_IMAGE', 'restic/restic'),
                           ('GOLDSRCOPS_POSTGRES_IMAGE', 'postgres')):
            if not re.fullmatch(re.escape(image) + r'@sha256:[a-f0-9]{64}', settings[key]):
                raise ValueError('A pinned disposable image is required')
        for key, filename in (('GOLDSRCOPS_RESTIC_PASSWORD_FILE', 'restic-password'),
                              ('GOLDSRCOPS_RESTIC_ENVIRONMENT_FILE', 'restic.env')):
            file = root / filename
            if Path(settings[key]) != file or file.resolve(strict=True) != file:
                raise ValueError('Backup secret files must belong to the local lab')
        self.source = self.identity + '-source'
        self.label = match[1]
        self.restic_image = settings['GOLDSRCOPS_RESTIC_IMAGE']

    def invoke(self, action, *arguments):
        result = subprocess.run(
            ['pwsh', '-NoLogo', '-NoProfile', '-File', str(BACKUP), '-Action', action,
             '-AllowLocalTestResources', '-EnvironmentFile', str(self.config), '-LocalRepositoryPath',
             str(self.repository), '-LockFile', str(self.root / 'backup-operation.lock'), *arguments],
            capture_output=True, timeout=120)
        if result.returncode:
            raise RuntimeError(f'Isolated {action} failed (exit code {result.returncode})')

    def assert_local_engine(self):
        if os.environ.get('DOCKER_HOST'):
            raise ValueError('Explicit Docker hosts are outside the isolated rehearsal')
        endpoint = subprocess.run(['docker', 'context', 'inspect', '--format', '{{.Endpoints.docker.Host}}'],
                                  capture_output=True, timeout=10, check=True).stdout.decode('utf-8').strip()
        if not endpoint.startswith(('unix:///', 'npipe:////./pipe/')):
            raise ValueError('The isolated rehearsal requires a local Docker engine')

    def read_candidate(self, claim_id):
        if not re.fullmatch(r'[0-9a-f]{32}', claim_id):
            raise ValueError('Invalid candidate receipt identity')
        path = self.root / f'candidate-{claim_id}.json'
        if path.resolve(strict=True) != path:
            raise ValueError('Candidate receipt must belong to the local lab')
        return read_json(path)

    def create(self, claim_id):
        self.assert_local_engine()
        source = subprocess.run(
            ['docker', 'inspect', '--format', '{{json .Config.Labels}}', self.source],
            capture_output=True, timeout=10, check=True)
        if json.loads(source.stdout).get(LAB_LABEL) != self.label:
            raise ValueError('Source container does not belong to the local lab')
        path = self.root / f'candidate-{claim_id}.json'
        self.invoke('Create', '-SourceContainer', self.source, '-EvidenceFile', str(path))
        try:
            return self.read_candidate(claim_id)
        except (OSError, ValueError):
            return None

    def list_snapshots(self, snapshot_id=None):
        self.assert_local_engine()
        filter_args = ['--host', self.identity]
        if snapshot_id is not None:
            if not SNAPSHOT.fullmatch(snapshot_id):
                raise ValueError('Expected a complete snapshot ID')
            filter_args.append(snapshot_id)
        check = subprocess.run(
            ['docker', 'run', '--rm', '--pull', 'never', '--read-only', '--network', 'none',
             '--mount', f'type=bind,source={self.repository},target=/repository,readonly',
             '--mount', f'type=bind,source={self.root / "restic-password"},target=/password,readonly',
             self.restic_image, '--no-cache', '--no-lock', '-r', '/repository', '--password-file',
             '/password', '--json', 'snapshots', *filter_args],
            capture_output=True, timeout=60, check=True)
        matches = json.loads(check.stdout)
        if not isinstance(matches, list):
            raise ValueError('Unexpected restic snapshot inventory')
        return matches

    def verify(self, receipt):
        self.invoke('Check', '-ReadDataSubset', '100%')
        snapshot_id = receipt['SnapshotId']
        matches = self.list_snapshots(snapshot_id)
        if (len(matches) != 1 or matches[0]['id'] != snapshot_id or matches[0]['hostname'] != self.identity
                or matches[0]['tags'] != [RECOVERABLE_TAG]
                or matches[0]['paths'] != [f'/{ARCHIVE_NAME}']):
            raise ValueError('Candidate was not present in the fully checked repository')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('initialize', 'plan', 'execute', 'reconcile'))
    parser.add_argument('--lab-root', type=Path, required=True)
    parser.add_argument('--trigger', choices=('scheduled', 'catch-up'), default='catch-up')
    parser.add_argument('--accelerated', action='store_true', help='Use short intervals for fixture acceptance')
    args = parser.parse_args()
    lab = LocalBackup(args.lab_root.resolve(strict=True))
    policy = ACCELERATED_POLICY if args.accelerated else DEFAULT_POLICY
    scheduler = Scheduler(lab.root, lab.identity, policy)
    if args.mode == 'initialize':
        scheduler.initialize()
        result = dict(decision='initialized', rpo='unknown')
    elif args.mode == 'plan':
        result = scheduler.plan(args.trigger)
    elif args.mode == 'reconcile':
        result = scheduler.reconcile(lab.read_candidate, lab.list_snapshots)
    else:
        result = scheduler.execute(args.trigger, lab.create, lab.verify)
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
