"""Fixed HTTP archive endpoint for the isolated WAL outage rehearsal."""

import hashlib
import json
import os
import re
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from tempfile import NamedTemporaryFile

ROOT = Path('/archive')
MAX_BYTES = 32 * 1024 * 1024
WAL_NAME = re.compile(r'(?:[0-9A-F]{24}(?:\.[0-9A-F]{8}\.backup)?|[0-9A-F]{8}\.history)')


def object_path(key):
    if key == '/base':
        return ROOT / 'base.tar.gz'
    if key.startswith('/wal/') and WAL_NAME.fullmatch(key.removeprefix('/wal/')):
        return ROOT / key.removeprefix('/wal/')
    return None


def atomic_save(destination, data):
    with NamedTemporaryFile(dir=ROOT, delete=False) as temporary:
        path = Path(temporary.name)
        try:
            temporary.write(data)
            temporary.flush()
            os.fsync(temporary.fileno())
            os.replace(path, destination)
        finally:
            path.unlink(missing_ok=True)
    directory = os.open(ROOT, os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


class Archive(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(10)

    def reply(self, status, payload=b''):
        self.send_response(status)
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        if payload:
            self.wfile.write(payload)

    def do_GET(self):
        if self.path == '/health':
            return self.reply(200, b'ok\n')
        checksums = self.path.startswith('/sha')
        path = object_path(self.path.removeprefix('/sha') if checksums else self.path)
        if path is None or not path.is_file() or not path.with_suffix(path.suffix + '.sha256').is_file():
            return self.reply(404)
        digest = path.with_suffix(path.suffix + '.sha256')
        return self.reply(200, (digest if checksums else path).read_bytes())

    def do_POST(self):
        path = object_path(self.path)
        if path is None:
            return self.reply(404)
        if os.environ.get('LAB_READ_ONLY') == '1':
            return self.reply(405)
        length = self.headers.get('Content-Length', '')
        if not length.isascii() or not length.isdecimal() or len(length) > 8 or not 0 < int(length) <= MAX_BYTES:
            return self.reply(400)
        expected = self.headers.get('X-Content-SHA256', '')
        if not re.fullmatch('[0-9a-f]{64}', expected):
            return self.reply(400)
        data = self.rfile.read(int(length))
        if len(data) != int(length) or hashlib.sha256(data).hexdigest() != expected:
            return self.reply(422)
        existing = path.is_file()
        if existing and hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            return self.reply(409)
        try:
            if not existing:
                atomic_save(path, data)
            atomic_save(path.with_suffix(path.suffix + '.sha256'), (expected + '\n').encode())
        except OSError:
            return self.reply(507)
        print(json.dumps(dict(key=self.path, bytes=len(data), sha256=expected, duplicate=existing)), flush=True)
        return self.reply(200 if existing else 201, b'ok\n')

    def log_message(self, *_):
        pass


if __name__ == '__main__':
    HTTPServer(('0.0.0.0', 8080), Archive).serve_forever()
