#!/usr/bin/env python3
"""Local real-Web browser check with a synthetic bounded search API; no production."""
import argparse
import datetime
import hashlib
import http.server
import json
import math
import os
from pathlib import Path
import shutil
import socket
import subprocess
import threading
import time
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class Fixture(http.server.BaseHTTPRequestHandler):
    names = ['FixtureAlpha', 'FixtureAlphabet', 'FixtureBeta']
    cache = {}
    admitted = 0.0
    posts = 0
    reject_contains = False

    def log_message(self, *_):
        pass

    def respond(self, value, status=200):
        data = json.dumps(value).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        if self.path != '/api/public/leaderboard':
            return self.respond({}, 404)
        self.respond({'state': 'fresh', 'capturedAtUtc': now(), 'entries': [
            {'position': i + 1, 'name': n, 'rank': 0, 'kills': 1, 'deaths': 0}
            for i, n in enumerate(self.names)]})

    def do_POST(self):
        if self.path != '/api/public/players/search':
            return self.respond({}, 404)
        if Fixture.posts >= 14:
            return self.respond({}, 400)
        raw = bytearray()
        if self.headers.get('Transfer-Encoding') == 'chunked':
            while True:
                size = int(self.rfile.readline(32).strip(), 16)
                if size == 0:
                    assert self.rfile.readline(2) == b'\r\n'
                    break
                assert 0 < size <= 1024 - len(raw)
                raw.extend(self.rfile.read(size))
                assert self.rfile.read(2) == b'\r\n'
        else:
            size = int(self.headers.get('Content-Length', '0'))
            assert 0 < size <= 1024
            raw.extend(self.rfile.read(size))
        Fixture.posts += 1
        body = json.loads(raw)
        name, mode = body['name'], body.get('match', 'exact')
        if Fixture.reject_contains and mode == 'contains':
            return self.respond({'state': 'unavailable', 'capturedAtUtc': None, 'hasMore': False,
                                 'entries': [], 'retryAfterSeconds': None}, 503)
        key = mode, name
        clock = time.monotonic()
        if key in self.cache and clock - self.cache[key][0] < 60:
            return self.respond(self.cache[key][1])
        remaining = 10 - (clock - Fixture.admitted)
        if remaining > 0:
            return self.respond({'state': 'busy', 'capturedAtUtc': None, 'hasMore': False,
                                 'entries': [], 'retryAfterSeconds': math.ceil(remaining)}, 429)
        Fixture.admitted = clock
        matches = [n for n in self.names if n == name] if mode == 'exact' else [
            n for n in self.names if name.lower() in n.lower()]
        value = {'state': 'fresh', 'capturedAtUtc': now(), 'hasMore': False, 'retryAfterSeconds': None,
                 'entries': [{'ordinal': i + 1, 'name': n, 'rank': 0, 'kills': 1, 'deaths': 0}
                             for i, n in enumerate(matches)]}
        self.cache[key] = clock, value
        self.respond(value)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--node', default=shutil.which('node'))
    parser.add_argument('--browser-channel', choices=['msedge', 'chrome'])
    faults = parser.add_mutually_exclusive_group()
    faults.add_argument('--reject-contains', action='store_true', help='Exercise a synthetic failure report')
    faults.add_argument('--delay-disposal', action='store_true',
                        help='Delay the final RU response cleanup to exercise context transition')
    args = parser.parse_args()
    Fixture.reject_contains = args.reject_contains
    assert args.node and Path(args.node).is_file(), 'Use an existing Node installation'
    output = ROOT / 'artifacts' / ('browser-rehearsal-' + uuid.uuid4().hex[:12])
    output.mkdir(parents=True, exist_ok=False)
    folder = output / 'web'
    checker = ROOT / 'tools/release/check-player-search-browser.cjs'
    checker_hash = hashlib.sha256(checker.read_bytes()).hexdigest()
    subprocess.run(['dotnet', 'publish', 'src/GoldSrcOps.Web', '-c', 'Debug', '--no-build',
                    '--no-restore', '-o', str(folder)], cwd=ROOT, check=True, timeout=60,
                   stdout=subprocess.DEVNULL)
    api = http.server.HTTPServer(('127.0.0.1', 0), Fixture)
    thread = threading.Thread(target=api.serve_forever, daemon=True)
    thread.start()
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    env = {k: v for k, v in os.environ.items() if '__' not in k and not k.startswith(
        ('ASPNETCORE_', 'DOTNET_', 'GOLDSRCOPS_', 'OTEL_'))}
    env.update({'ASPNETCORE_ENVIRONMENT': 'Integration', 'ASPNETCORE_URLS': f'http://127.0.0.1:{port}',
                'GoldSrcOpsApi__BaseUrl': f'http://127.0.0.1:{api.server_port}/',
                'Authentication__Enabled': 'false', 'DataProtection__PersistKeys': 'false',
                'PublicPlayerSearch__Enabled': 'true', 'Telemetry__Otlp__Enabled': 'false',
                'Logging__LogLevel__Default': 'Warning'})
    process = None
    try:
        with (output / 'web.log').open('wb') as log:
            process = subprocess.Popen(['dotnet', str(folder / 'GoldSrcOps.Web.dll'), '--contentRoot', str(folder)],
                                       cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
            client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            for _ in range(60):
                assert process.poll() is None, 'Web fixture exited; inspect local log'
                try:
                    with client.open(f'http://127.0.0.1:{port}/health/live', timeout=2) as response:
                        if response.status == 200:
                            break
                except OSError:
                    time.sleep(.25)
            else:
                raise RuntimeError('Local Web startup timed out')
            # HTTP is allowed only here, to this freshly bound loopback fixture.
            script = """const [port, channel, delay] = process.argv.slice(1);
if (!/^[0-9]+$/.test(port) || Number(port) < 1 || Number(port) > 65535) process.exit(2);
const {check} = require('./tools/release/check-player-search-browser.cjs');
const playwright = require('./tests/GoldSrcOps.WebTests/bin/Debug/net10.0/.playwright/package');
let delayedDisposals = 0;
const driver = delay === 'yes' ? {chromium: {launch: async options => {
 const browser = await playwright.chromium.launch(options), create = browser.newContext.bind(browser);
 let contexts = 0;
 browser.newContext = async options => {
  const context = await create(options), register = context.route.bind(context);
  if (++contexts !== 1) return context;
  let posts = 0;
  context.route = (pattern, handler) => register(pattern, route => {
   if (route.request().method() === 'POST' && ++posts === 4) {
    const fetch = route.fetch.bind(route);
    route.fetch = async options => {
     const response = await fetch(options), dispose = response.dispose.bind(response);
     response.dispose = async () => {
      delayedDisposals++; await new Promise(resolve => setTimeout(resolve, 1000)); await dispose();
     };
     return response;
    };
   }
   return handler(route);
  });
  return context;
 };
 return browser;
}}} : playwright;
check('http://127.0.0.1:' + port, driver, channel || undefined, true).then(report => {
 report.delayedDisposals = delayedDisposals;
 console.log(JSON.stringify(report)); process.exitCode = report.result === 'browser-acceptance-passed' ? 0 : 1;
}).catch(() => { process.exitCode = 2; });"""
            result = subprocess.run([args.node, '-e', script, str(port), args.browser_channel or '',
                                     'yes' if args.delay_disposal else 'no'],
                                    cwd=ROOT, capture_output=True, timeout=210)
            assert len(result.stdout) <= 65536 and result.stdout, 'Missing bounded checker report'
            report = json.loads(result.stdout)
            if args.delay_disposal and result.returncode == 0:
                assert report['delayedDisposals'] == 1, 'Disposal race was not exercised'
            report['revision'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT).decode().strip()
            assert hashlib.sha256(checker.read_bytes()).hexdigest() == checker_hash, 'Checker changed during rehearsal'
            report['checkerSha256'] = checker_hash
            report['webAssemblySha256'] = hashlib.sha256((folder / 'GoldSrcOps.Web.dll').read_bytes()).hexdigest()
            report['fixtureApiPosts'] = Fixture.posts
            report['injectedContainsRefusal'] = args.reject_contains
            report['injectedDisposalDelay'] = args.delay_disposal
            report['scope'] = 'local real Web with synthetic API; no production'
            (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
            print(json.dumps(report), flush=True)
            print('Evidence: ' + str(output), flush=True)
            return result.returncode
    finally:
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        api.shutdown()
        api.server_close()
        thread.join(timeout=5)
        # Only the exact publish directory created by this invocation is disposable.
        assert folder.resolve().parent == output.resolve() and not folder.is_symlink()
        shutil.rmtree(folder)


if __name__ == '__main__':
    raise SystemExit(main())
