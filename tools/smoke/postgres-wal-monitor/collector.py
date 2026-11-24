"""Expose fresh WAL queue and disk observations from the mounted WAL directory."""

import argparse
import os
import re
import signal
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

READY = re.compile(r'[0-9A-F]{24}\.ready')
PREFIX = 'goldsrcops_postgres_wal_'
METRICS = ('ready_segments', 'oldest_ready_age_seconds', 'filesystem_available_bytes')


def collect(wal_directory, now):
    timestamps = []
    with os.scandir(wal_directory / 'archive_status') as entries:
        for entry in entries:
            if not READY.fullmatch(entry.name):
                continue
            try:
                if entry.is_file(follow_symlinks=False):
                    timestamps.append(entry.stat(follow_symlinks=False).st_mtime)
            except FileNotFoundError:
                # PostgreSQL can rename .ready to .done during the scan.
                continue
    disk = os.statvfs(wal_directory)
    age = max(0, now - min(timestamps)) if timestamps else 0
    return dict(zip(METRICS, (len(timestamps), age, disk.f_bavail * disk.f_frsize), strict=True))


def read_with_deadline(read, seconds):
    def expired(*_):
        raise TimeoutError('WAL observation deadline exceeded')

    before = signal.signal(signal.SIGALRM, expired)
    try:
        signal.setitimer(signal.ITIMER_REAL, seconds)
        return read()
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, before)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/metrics':
            self.send_error(404)
            return
        try:
            values = read_with_deadline(lambda: collect(self.server.wal_directory, time.time()), 0.75)
        except OSError:
            self.send_error(503, 'WAL observation unavailable')
            return
        body = ''.join(f'{PREFIX}{name} {value}\n' for name, value in values.items()).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/plain; version=0.0.4; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('wal_directory', type=Path)
    args = parser.parse_args()
    with HTTPServer(('0.0.0.0', 8181), Handler) as server:
        server.wal_directory = args.wal_directory
        server.serve_forever()
