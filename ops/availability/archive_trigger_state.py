"""Offline durable admission primitives; no network, credentials or CLI entrypoint."""
import contextlib
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import uuid

UTC = dt.timezone.utc
LIMIT_BYTES = 16 * 1024 * 1024
LIMIT_CLAIMS = 1000
LIVE = ('Prepared', 'Attempting', 'Accepted', 'Unknown')
SCHEMA = """
CREATE TABLE metadata (id INTEGER PRIMARY KEY CHECK(id=1), last_clock TEXT NOT NULL);
CREATE TABLE claims (
 claim TEXT PRIMARY KEY, generation TEXT NOT NULL, slot TEXT NOT NULL,
 exporter TEXT NOT NULL, manifest TEXT NOT NULL, state TEXT NOT NULL,
 run_id INTEGER UNIQUE, created TEXT NOT NULL, updated TEXT NOT NULL,
 receipt_hash TEXT, UNIQUE(generation,slot),
 CHECK(state IN ('Prepared','Attempting','Accepted','Verified','Failed-known','Unknown')));
CREATE UNIQUE INDEX one_live_claim ON claims((1))
 WHERE state IN ('Prepared','Attempting','Accepted','Unknown');
CREATE TABLE transitions (
 sequence INTEGER PRIMARY KEY, claim TEXT NOT NULL REFERENCES claims(claim),
 state TEXT NOT NULL, at TEXT NOT NULL);
CREATE TABLE retirement (day TEXT PRIMARY KEY, claims INTEGER NOT NULL);
PRAGMA user_version=1;
"""


def require(value, code='invalid-input'):
    if not value:
        raise ValueError(code)


def moment(value):
    require(type(value) is str and re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z', value))
    result = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(result.year >= 2020)
    return result


def stamp(value):
    require(type(value) is dt.datetime and value.tzinfo == UTC and value.microsecond == 0)
    return value.strftime('%Y-%m-%dT%H:%M:%SZ')


def identifier(value):
    require(type(value) is str and str(uuid.UUID(value)) == value)
    return value


def digest(value, size=64):
    require(type(value) is str and re.fullmatch('[0-9a-f]{' + str(size) + '}', value))
    return value


def manifest(value):
    require(type(value) is dict and 1 <= len(value) <= 8)
    for path, sha in value.items():
        require(type(path) is str and len(path) <= 180 and
                re.fullmatch(r'[a-zA-Z0-9_./-]+', path) and
                not path.startswith('/') and all(x not in ('', '.', '..') for x in path.split('/')))
        digest(sha)
    return json.dumps(value, sort_keys=True, separators=(',', ':'))


def newest_slot(now):
    stamp(now)
    slot = now.replace(minute=53, second=0)
    return slot if slot <= now else slot - dt.timedelta(hours=1)


def window(slot):
    value = moment(slot)
    require(value.minute == 53 and value.second == 0)
    end = value - dt.timedelta(minutes=5)
    return stamp(end - dt.timedelta(hours=24)), stamp(end)


def attempt_once(ledger, claim, now, send):
    """Commit before the injected transport; no retry on any ambiguous response."""
    row = ledger.begin(claim, now)
    start, end = window(row['slot'])
    request = {'claim': row['claim'], 'generation': row['generation'],
               'slot': row['slot'], 'exporter': row['exporter'],
               'manifest': json.loads(row['manifest']),
               'window_start': start, 'window_end': end}
    try:
        response = send(request)
        require(type(response) is dict and set(response) == {'status', 'workflow_run_id'}
                and type(response['status']) is int and response['status'] == 200,
                'dispatch-response')
        return ledger.accepted(claim, response['workflow_run_id'], now)
    except Exception:
        # Persist Unknown when possible; a failed persistence leaves Attempting,
        # which also blocks admission after restart. Never expose transport text.
        ledger.unknown(claim, now)
        raise ValueError('dispatch-uncertain') from None


class Ledger:
    """Existing stores must be opened explicitly; corruption never initializes state."""

    @classmethod
    def create(cls, path, now):
        path = Path(path)
        stamp(now)
        require(not path.is_symlink() and not path.exists(), 'state-exists')
        # Exclusive creation avoids truncating another process's ledger.
        with path.open('xb'):
            pass
        connection = sqlite3.connect(path)
        try:
            connection.executescript(SCHEMA)
            connection.execute('INSERT INTO metadata VALUES(1,?)', (stamp(now),))
            connection.commit()
        finally:
            connection.close()
        return cls(path)

    def __init__(self, path):
        self.path = Path(path).absolute()
        require(self.path.is_file() and not self.path.is_symlink(), 'state-unavailable')
        require(self.path.stat().st_size <= LIMIT_BYTES, 'state-capacity')
        self.connection = sqlite3.connect(self.path.as_uri() + '?mode=rw', uri=True,
                                          timeout=0, isolation_level=None)
        self.connection.row_factory = sqlite3.Row
        try:
            self.connection.execute('PRAGMA foreign_keys=ON')
            self.connection.execute('PRAGMA synchronous=FULL')
            require(self.connection.execute('PRAGMA user_version').fetchone()[0] == 1,
                    'unsupported-schema')
            require(self.connection.execute('PRAGMA quick_check').fetchone()[0] == 'ok',
                    'state-corrupt')
            schema_query = ("SELECT type,name,tbl_name,sql FROM sqlite_master "
                            "WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name")
            expected = sqlite3.connect(':memory:')
            try:
                expected.executescript(SCHEMA)
                require([tuple(r) for r in self.connection.execute(schema_query)] ==
                        expected.execute(schema_query).fetchall(), 'state-schema')
            finally:
                expected.close()
            require(self.connection.execute('SELECT count(*) FROM metadata').fetchone()[0] == 1,
                    'state-schema')
            moment(self.connection.execute('SELECT last_clock FROM metadata WHERE id=1').fetchone()[0])
        except BaseException:
            self.connection.close()
            raise

    def close(self):
        self.connection.close()

    def capacity(self):
        pages = self.connection.execute('PRAGMA page_count').fetchone()[0]
        size = self.connection.execute('PRAGMA page_size').fetchone()[0]
        require(pages * size <= LIMIT_BYTES and self.path.stat().st_size <= LIMIT_BYTES,
                'state-capacity')

    @contextlib.contextmanager
    def transaction(self, now):
        value = stamp(now)
        self.connection.execute('BEGIN IMMEDIATE')
        try:
            self.capacity()
            previous = self.connection.execute('SELECT last_clock FROM metadata WHERE id=1').fetchone()
            require(previous is not None and moment(previous[0]) <= now, 'clock-regressed')
            self.connection.execute('UPDATE metadata SET last_clock=? WHERE id=1', (value,))
            yield
            self.capacity()
            self.connection.execute('COMMIT')
        except BaseException:
            self.connection.execute('ROLLBACK')
            raise

    def get(self, claim):
        identifier(claim)
        row = self.connection.execute('SELECT * FROM claims WHERE claim=?', (claim,)).fetchone()
        require(row is not None, 'claim-not-found')
        return dict(row)

    def prepare(self, generation, slot, exporter, files, now):
        identifier(generation)
        digest(exporter, 40)
        content = manifest(files)
        window(slot)
        require(moment(slot) == newest_slot(now), 'stale-slot')
        claim = str(uuid.uuid4())
        with self.transaction(now):
            require(self.connection.execute('SELECT count(*) FROM claims').fetchone()[0] < LIMIT_CLAIMS,
                    'claim-capacity')
            require(self.connection.execute(
                "SELECT count(*) FROM claims WHERE state IN ('Prepared','Attempting','Accepted','Unknown')"
            ).fetchone()[0] == 0, 'outstanding-claim')
            self.connection.execute('INSERT INTO claims VALUES(?,?,?,?,?,?,NULL,?,?,NULL)',
                (claim, generation, slot, exporter, content, 'Prepared', stamp(now), stamp(now)))
            self.connection.execute('INSERT INTO transitions(claim,state,at) VALUES(?,?,?)',
                                    (claim, 'Prepared', stamp(now)))
        return self.get(claim)

    def transition(self, claim, state, now, run_id=None, receipt_hash=None):
        allowed = {'Prepared': ('Attempting', 'Failed-known'),
                   'Attempting': ('Accepted', 'Unknown'),
                   'Accepted': ('Verified', 'Unknown'), 'Unknown': ()}
        with self.transaction(now):
            row = self.get(claim)
            require(state in allowed.get(row['state'], ()), 'invalid-transition')
            require((state == 'Accepted' and type(run_id) is int and 0 < run_id < 2**63)
                    or (state != 'Accepted' and run_id is None), 'invalid-run-id')
            require((state == 'Verified' and receipt_hash is not None)
                    or (state != 'Verified' and receipt_hash is None), 'invalid-receipt')
            if receipt_hash is not None:
                digest(receipt_hash)
            self.connection.execute(
                'UPDATE claims SET state=?,run_id=coalesce(?,run_id),updated=?,receipt_hash=? WHERE claim=?',
                (state, run_id, stamp(now), receipt_hash, claim))
            self.connection.execute('INSERT INTO transitions(claim,state,at) VALUES(?,?,?)',
                                    (claim, state, stamp(now)))
        return self.get(claim)

    def begin(self, claim, now):
        row = self.get(claim)
        require(moment(row['slot']) == newest_slot(now), 'stale-slot')
        return self.transition(claim, 'Attempting', now)

    def accepted(self, claim, run_id, now):
        return self.transition(claim, 'Accepted', now, run_id=run_id)

    def unknown(self, claim, now):
        return self.transition(claim, 'Unknown', now)

    def verify(self, claim, receipt, proof, now):
        row = self.get(claim)
        require(row['state'] == 'Accepted', 'invalid-transition')
        try:
            start, end = window(row['slot'])
            require(type(receipt) is dict and set(receipt) == {
                'schema', 'claim', 'generation', 'slot', 'exporter', 'manifest',
                'run_id', 'window_start', 'window_end', 'sha256', 'bytes'}, 'receipt-schema')
            require(type(receipt['schema']) is int and receipt['schema'] == 1)
            for key in ('claim', 'generation', 'slot', 'exporter', 'run_id'):
                require(type(receipt[key]) is type(row[key]) and receipt[key] == row[key])
            require(manifest(receipt['manifest']) == row['manifest'])
            require(receipt['window_start'] == start and receipt['window_end'] == end)
            digest(receipt['sha256'])
            require(type(receipt['bytes']) is int and 0 < receipt['bytes'] <= 64 * 1024 * 1024)
            require(type(proof) is dict and set(proof) == {
                'run_id', 'attempt', 'event', 'branch', 'workflow', 'head', 'manifest',
                'created', 'completed', 'job_success', 'pin_success', 'receipt_success'})
            require(type(proof['run_id']) is int and proof['run_id'] == row['run_id'])
            require(type(proof['attempt']) is int and proof['attempt'] == 1)
            require(proof['event'] == 'workflow_dispatch' and proof['branch'] == 'main'
                    and type(proof['workflow']) is int and proof['workflow'] == 350269402)
            digest(proof['head'], 40)
            require(manifest(proof['manifest']) == row['manifest'])
            created, completed = moment(proof['created']), moment(proof['completed'])
            require(moment(row['created']) <= created <= completed <= now
                    and created < moment(row['slot']) + dt.timedelta(hours=1)
                    and (completed-created).total_seconds() <= 900)
            require(all(proof[k] is True for k in ('job_success', 'pin_success', 'receipt_success')))
            canonical = json.dumps(receipt, sort_keys=True, separators=(',', ':')).encode()
            require(len(canonical) <= 16384)
            return self.transition(claim, 'Verified', now,
                                   receipt_hash=hashlib.sha256(canonical).hexdigest())
        except (ValueError, TypeError, KeyError, OverflowError):
            if row['state'] == 'Accepted':
                self.unknown(claim, now)
            raise ValueError('unverified-outcome') from None

    def retire(self, now, protected_since):
        require(type(protected_since) is dt.datetime and protected_since.tzinfo == UTC
                and protected_since <= now)
        cutoff = min(now - dt.timedelta(days=21), protected_since)
        with self.transaction(now):
            rows = self.connection.execute(
                "SELECT claim FROM claims WHERE state IN ('Verified','Failed-known') AND updated<? LIMIT 100",
                (stamp(cutoff),)).fetchall()
            for row in rows:
                self.connection.execute('DELETE FROM transitions WHERE claim=?', (row[0],))
                self.connection.execute('DELETE FROM claims WHERE claim=?', (row[0],))
            if rows:
                self.connection.execute(
                    'INSERT INTO retirement VALUES(?,?) ON CONFLICT(day) DO UPDATE SET claims=claims+excluded.claims',
                    (stamp(now)[:10], len(rows)))
        return len(rows)
