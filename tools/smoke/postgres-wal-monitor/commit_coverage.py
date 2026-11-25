"""Expose a bounded sampled commit-to-verified-WAL signal in the isolated lab."""

import argparse
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import math
from pathlib import Path
import time

PREFIX = 'goldsrcops_postgres_wal_commit_'
MAX_SNAPSHOT_BYTES = 32768
MAX_WITNESSES = 128
FRESHNESS_SECONDS = 25
FUTURE_TOLERANCE_SECONDS = 2


def timestamp(value):
    if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
        raise ValueError('invalid timestamp')
    return value


def bounded_integer(value, minimum=0):
    if type(value) is not int or not minimum <= value <= 0xffffffffffffffff:
        raise ValueError('invalid integer')
    return value


def evaluate(snapshot, now):
    if not isinstance(snapshot, dict) or type(snapshot.get('schema')) is not int or snapshot['schema'] != 1:
        raise ValueError('unsupported snapshot')
    captured = timestamp(snapshot.get('observed_epoch'))
    if not -FUTURE_TOLERANCE_SECONDS <= now - captured <= FRESHNESS_SECONDS:
        raise ValueError('stale or future observation')

    through = bounded_integer(snapshot.get('verified_through'), 1)
    latest = bounded_integer(snapshot.get('latest_sequence'))
    witnesses = snapshot.get('witnesses')
    if not isinstance(witnesses, list) or len(witnesses) > MAX_WITNESSES:
        raise ValueError('invalid witness list')

    previous_id, previous_time, previous_offset = 0, 0, 0
    pending = []
    for witness in witnesses:
        if not isinstance(witness, dict):
            raise ValueError('invalid witness')
        sequence = bounded_integer(witness.get('sequence'), 1)
        committed = timestamp(witness.get('committed_epoch'))
        offset = bounded_integer(witness.get('wal_upper_bound'), 1)
        if not (previous_id < sequence <= latest and previous_time <= committed <= captured
                and previous_offset <= offset):
            raise ValueError('inconsistent witness order')
        previous_id, previous_time, previous_offset = sequence, committed, offset
        if offset > through:
            pending.append(committed)

    if previous_id != latest:
        raise ValueError('latest observed commit is missing')
    return dict(known=1, pending_witnesses=len(pending),
                oldest_uncovered_age_seconds=max(0, now - pending[0]) if pending else 0)


def collect(path, now):
    try:
        with path.open('rb') as stream:
            body = stream.read(MAX_SNAPSHOT_BYTES + 1)
        if len(body) > MAX_SNAPSHOT_BYTES:
            raise ValueError('oversized snapshot')
        return evaluate(json.loads(body), now)
    except (OSError, ValueError, UnicodeError, OverflowError, RecursionError):
        return {'known': 0}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/metrics':
            self.send_error(404)
            return
        values = collect(self.server.snapshot, time.time())
        body = ''.join(f'{PREFIX}{key} {value}\n' for key, value in values.items()).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/plain; version=0.0.4; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('snapshot', type=Path)
    args = parser.parse_args()
    with HTTPServer(('0.0.0.0', 8181), Handler) as server:
        server.snapshot = args.snapshot
        server.serve_forever()
