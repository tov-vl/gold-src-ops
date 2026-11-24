"""Retain bounded Alertmanager notifications for the private test network."""

import json
from http.server import BaseHTTPRequestHandler, HTTPServer


class Observer(BaseHTTPRequestHandler):
    def reply(self, code, value):
        data = json.dumps(value).encode()
        self.send_response(code)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.reply(200, self.server.events) if self.path == '/events' else self.reply(404, {})

    def do_POST(self):
        size = self.headers.get('Content-Length', '')
        if self.path != '/hook' or not size.isdecimal() or not 0 < int(size) <= 65536:
            self.reply(400, {})
            return
        doc = json.loads(self.rfile.read(int(size)))
        batch = [dict(name=alert['labels']['alertname'], status=alert['status'], fingerprint=alert['fingerprint'],
                      severity=alert['labels']['severity'], receiver=doc['receiver']) for alert in doc['alerts']]
        if len(self.server.events) + len(batch) > 100:
            self.reply(429, {})
            return
        self.server.events.extend(batch)
        self.reply(200, {})

    def log_message(self, *_):
        pass


if __name__ == '__main__':
    with HTTPServer(('0.0.0.0', 8182), Observer) as server:
        server.events = []
        server.serve_forever()
