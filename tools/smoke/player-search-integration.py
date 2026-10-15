#!/usr/bin/env python3
"""Opt-in local Web/API/RCON rehearsal; creates and destroys its own Docker resources.

Requires built Debug API/Web projects, Docker, dotnet-ef and the pinned AMXX/engine
input cache documented in player-substring-search-feasibility.md. Never accepts a
remote endpoint or production credentials. Uses synthetic disposable fixtures.
"""
import argparse
import hashlib
import html
from http.cookiejar import CookieJar
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
IMAGE = 'archont94/counter-strike1.6@sha256:2294ee98981ef1fe4443d9c6a802c74fc9639164ed7a22e294256afb46c32bdb'
OLD = '5f520a5a46e046c2a8212f7893adc033c77419e5'
SERVER = 'd9f871dd-b6b7-44b1-908f-a8e9acb6e142'


def require(value, message):
    if not value:
        raise RuntimeError(message)


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise RuntimeError('Unexpected redirect')


class Rehearsal:
    def __init__(self, inputs):
        self.inputs = inputs.resolve(strict=True)
        self.run_id = uuid.uuid4().hex[:12]
        self.output = ROOT / 'artifacts' / ('player-search-integration-' + self.run_id)
        self.output.mkdir(parents=True, exist_ok=False)
        self.docker = shutil.which('docker') or 'C:/Program Files/Docker/Docker/resources/bin/docker.exe'
        self.network = 'lookup-net-' + self.run_id
        self.game = 'lookup-game-' + self.run_id
        self.db = 'lookup-db-' + self.run_id
        self.containers = []
        self.network_created = False
        self.processes = []
        self.logs = []
        self.engine = None
        self.api_process = None
        self.game_port = self.db_port = 0
        self.api_port, self.web_port = port(), port()
        self.client = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(), urllib.request.HTTPCookieProcessor(CookieJar()))
        self.evidence = {'revision': self.call(['git', 'rev-parse', 'HEAD']).strip(), 'checks': [], 'scope': 'local synthetic HTTP/PostgreSQL/RCON; no production'}

    def call(self, args, *, env=None, data=None, timeout=120):
        result = subprocess.run([str(a) for a in args], cwd=ROOT, env=env, input=data,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
        text = result.stdout.decode('utf-8', errors='replace')
        require(result.returncode == 0, f'Local command failed ({args[0]}): {text[-2500:]}')
        return text

    def docker_call(self, *args, **kwargs):
        return self.call([self.docker, *args], **kwargs)

    def mark(self, name):
        self.evidence['checks'].append(name)
        print('PASS ' + name, flush=True)

    def start(self, args, name, env=None, stdin=False):
        log = (self.output / (name + '.log')).open('wb')
        self.logs.append(log)
        process = subprocess.Popen([str(a) for a in args], cwd=ROOT, env=env,
                                   stdin=subprocess.PIPE if stdin else subprocess.DEVNULL,
                                   stdout=log, stderr=subprocess.STDOUT)
        self.processes.append(process)
        return process

    def wait_marker(self, process, name, marker, seconds=40):
        path = self.output / (name + '.log')
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            text = path.read_text(encoding='utf-8', errors='replace')
            if marker in text:
                return
            require(process.poll() is None, f'{name} exited: {text[-2500:]}')
            time.sleep(.25)
        raise RuntimeError(f'{name} did not become ready: {text[-1500:]}')

    def stop(self, process):
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)

    def command(self, command):
        require(self.engine is not None and self.engine.poll() is None, 'Fixture engine unavailable')
        self.engine.stdin.write((command + '\n').encode())
        self.engine.stdin.flush()

    def engine_start(self, plugin, name):
        require(self.engine is None, 'Engine already running')
        self.docker_call('exec', self.game, 'sh', '-c',
                         f"printf '{plugin}.amxx\\n' > /server/cstrike/addons/amxmodx/configs/plugins.ini")
        self.engine = self.start([self.docker, 'exec', '-i', '-w', '/server', self.game,
                                 'runuser', '-u', 'steam', '--', './hlds_run', '-norestart', '-insecure',
                                 '-game', 'cstrike', '-strictportbind', '+ip', '0.0.0.0', '-port', '27025',
                                 '+map', 'de_dust2', '+servercfgfile', 'server.cfg', '-maxplayers', '4'], name, stdin=True)
        if plugin == 'seed':
            self.wait_marker(self.engine, name, 'LOOKUP_INTEGRATION_SEED=passed')
        else:
            time.sleep(5)
            self.command('goldsrcops_weapons_status')
            version = '0.25.0' if plugin == 'old' else '0.26.0'
            self.wait_marker(self.engine, name, 'WEAPON_SELECTION_STATUS version=' + version)
        text = (self.output / (name + '.log')).read_text(encoding='utf-8', errors='replace')
        require(not re.search('Run time error|Load fails|bad load|ML_NOTFOUND', text), 'Engine plugin failure')

    def engine_stop(self):
        if self.engine:
            self.command('quit')
            self.engine.wait(timeout=15)
            require(self.engine.returncode == 0, 'Engine did not close cleanly')
            self.engine.stdin.close()
            self.engine = None

    def vaults(self):
        result = self.docker_call('exec', self.game, 'sh', '-c',
            'sha256sum /server/cstrike/addons/amxmodx/data/vault/goldsrcops-player-*-v1.vault')
        hashes = {Path(line.split()[1]).name: line.split()[0] for line in result.splitlines()}
        require(len(hashes) == 2, 'Unexpected vault set')
        return hashes

    def clean_environment(self):
        env = {k: v for k, v in os.environ.items() if '__' not in k and not k.startswith(('ASPNETCORE_', 'DOTNET_', 'GOLDSRCOPS_', 'OTEL_'))}
        env.update({'ASPNETCORE_ENVIRONMENT': 'Integration', 'Logging__LogLevel__Default': 'Warning',
                    'Logging__LogLevel__Microsoft.AspNetCore': 'Warning', 'Authentication__Enabled': 'false',
                    'DataProtection__PersistKeys': 'false', 'PublicPlayerSearch__Enabled': 'true',
                    'PublicLeaderboard__Enabled': 'false', 'Polling__Enabled': 'false',
                    'CommandDispatcher__Enabled': 'false', 'SnapshotRetention__Enabled': 'false',
                    'GameEventRetention__Enabled': 'false', 'AlertDelivery__Enabled': 'false',
                    'Telemetry__Otlp__Enabled': 'false', 'Authentication__Schemes__Bearer__ValidIssuer': 'lookup-fixture',
                    'Authentication__Schemes__Bearer__ValidAudiences__0': 'lookup-fixture',
                    'PublicServerJoin__ServerId': SERVER, 'PublicServerJoin__Name': 'Synthetic fixture',
                    'PublicServerJoin__Host': 'play.example.test', 'PublicServerJoin__Port': '27015',
                    'RconSecrets__fixture': 'lookup-fixture-only',
                    'ConnectionStrings__GoldSrcOps': f'Host=127.0.0.1;Port={self.db_port};Database=lookup;Username=lookup;Password=lookup-fixture-only'})
        return env

    def request(self, base, path, body=None, form=False):
        require(base in (self.api_port, self.web_port) and path.startswith('/'), 'Non-fixture HTTP target')
        data = None if body is None else (urllib.parse.urlencode(body).encode() if form else json.dumps(body, ensure_ascii=False).encode())
        headers = {} if data is None else {'Content-Type': 'application/x-www-form-urlencoded' if form else 'application/json'}
        request = urllib.request.Request(f'http://127.0.0.1:{base}{path}', data=data, headers=headers)
        try:
            response = self.client.open(request, timeout=8)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read(262145)
            require(len(raw) <= 262144, 'Unexpected oversized response')
            return response.status, response.headers, raw.decode('utf-8')

    def healthy(self, process, port, path):
        for _ in range(60):
            require(process.poll() is None, 'Local host exited during startup; inspect retained log')
            try:
                if self.request(port, path)[0] == 200:
                    return
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(.5)
        raise RuntimeError('Local host startup timed out')

    def api_start(self, name):
        self.stop(self.api_process)
        env = self.clean_environment()
        env['ASPNETCORE_URLS'] = f'http://127.0.0.1:{self.api_port}'
        folder = self.output / 'api'
        self.api_process = self.start(['dotnet', folder / 'GoldSrcOps.Api.dll', '--contentRoot', folder], name, env)
        self.healthy(self.api_process, self.api_port, '/health/ready')

    def search(self, name, mode='exact', expected='fresh'):
        body = {'name': name}
        if mode != 'legacy':
            body['match'] = mode
        status, headers, raw = self.request(self.api_port, '/api/public/players/search', body)
        result = json.loads(raw)
        require(result['state'] == expected, f'API state: expected {expected}, received {result["state"]}')
        require(status == {'fresh': 200, 'busy': 429, 'invalid': 400, 'unavailable': 503}[expected], 'API HTTP mismatch')
        require('no-store' in headers.get('Cache-Control', ''), 'Missing API no-store')
        require('STEAM_' not in raw and 'lookup-fixture-only' not in raw, 'Private data in public output')
        if expected == 'busy':
            require(1 <= result['retryAfterSeconds'] <= 10 and headers.get('Retry-After') is not None, 'Unbounded retry')
        return result

    def web(self, name, mode, language, state='fresh'):
        path = '/leaderboard?lang=' + language
        _, _, page = self.request(self.web_port, path)
        token = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', page)
        require(token, 'Antiforgery token absent')
        status, headers, page = self.request(self.web_port, path, {'_handler': 'player-search',
            '__RequestVerificationToken': html.unescape(token[1]), 'Model.Name': name, 'Model.Match': mode}, True)
        require(status == 200 and f'data-search-state="{state}"' in page, 'Web state mismatch')
        require(re.search(fr'<option value="{mode}"[^>]* selected>', page), 'Web lost selected mode')
        require('no-store' in headers.get('Cache-Control', '') and headers.get('Referrer-Policy') == 'no-referrer', 'Web privacy headers missing')
        require(headers.get('Location') is None and 'STEAM_' not in page and 'lookup-fixture-only' not in page, 'Web boundary leak')
        return page

    def setup(self):
        compiled = self.output / 'compiled'
        compiled.mkdir()
        compiler = self.inputs / 'amxx-1.10.0.5481/addons/amxmodx/scripting/amxxpc.exe'
        include = compiler.parent / 'include'
        reapi = self.inputs / 'reapi-5.24.0.300/addons/amxmodx/scripting/include'
        old_source = self.output / 'old.sma'
        old_source.write_text(self.call(['git', 'show', OLD + ':samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma']), encoding='utf-8')
        for name, source in [('seed', ROOT / 'tools/smoke/player-search-seed.sma'),
                             ('current', ROOT / 'samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma'), ('old', old_source)]:
            text = self.call([compiler, source, '-i' + str(include), '-i' + str(reapi), '-o' + str(compiled / (name + '.amxx'))])
            require('warning' not in text.lower() and 'error' not in text.lower(), 'Addon compilation diagnostic')
        self.evidence['tool_sha256'] = {name: hashlib.sha256((ROOT / 'tools/smoke' / name).read_bytes()).hexdigest()
            for name in ('player-search-integration.py', 'player-search-engine-setup.sh', 'player-search-seed.sma')}
        self.evidence['old_revision'] = OLD
        self.evidence['compiled'] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in compiled.glob('*.amxx')}
        # Verify copied extraction contents against the archives that the setup pins by SHA-256.
        for folder, archive in [('rehlds', 'rehlds-bin-3.15.0.896.zip'), ('regamedll', 'regamedll-bin-5.30.0.814.zip'),
                                ('metamod', 'metamod-bin-1.3.0.149.zip'), ('reapi', 'reapi-bin-5.24.0.300.zip')]:
            with zipfile.ZipFile(self.inputs / 'assets' / archive) as zipped:
                for item in zipped.infolist():
                    if not item.is_dir():
                        target = self.inputs / 'extracted' / folder / item.filename
                        require(target.read_bytes() == zipped.read(item), 'Extracted engine input differs from archive')
        self.docker_call('network', 'create', self.network)
        self.network_created = True
        self.docker_call('run', '--pull', 'never', '-d', '--name', self.game, '--network', self.network, '--cpus', '2', '--memory', '1g',
                         '-p', '127.0.0.1::27025/udp', '--mount', f'type=bind,src={ROOT},dst=/repo,readonly',
                         '--mount', f'type=bind,src={self.inputs},dst=/inputs,readonly',
                         '--mount', f'type=bind,src={compiled},dst=/compiled,readonly',
                         '--entrypoint', 'bash', IMAGE, '/repo/tools/smoke/player-search-engine-setup.sh')
        self.containers.append(self.game)
        for _ in range(60):
            status = self.call([self.docker, 'exec', self.game, 'sh', '-c', 'if test -f /tmp/integration-ready; then echo ready; fi'])
            if status.strip() == 'ready':
                break
            time.sleep(.5)
        else:
            raise RuntimeError('Engine setup timed out')
        self.game_port = int(self.docker_call('port', self.game, '27025/udp').strip().split(':')[-1])
        self.engine_start('seed', 'seed')
        self.engine_stop()
        self.baseline = self.vaults()
        self.mark('synthetic-vault-seed')
        self.engine_start('current', 'engine-current')
        self.docker_call('run', '--pull', 'never', '-d', '--name', self.db, '--network', self.network, '--cpus', '1', '--memory', '512m',
                         '-p', '127.0.0.1::5432', '-e', 'POSTGRES_USER=lookup', '-e', 'POSTGRES_DB=lookup',
                         '-e', 'POSTGRES_PASSWORD=lookup-fixture-only', '--tmpfs', '/var/lib/postgresql/data', 'postgres:17-alpine')
        self.containers.append(self.db)
        self.db_port = int(self.docker_call('port', self.db, '5432/tcp').strip().split(':')[-1])
        for _ in range(40):
            result = subprocess.run([self.docker, 'exec', self.db, 'pg_isready', '-U', 'lookup'], capture_output=True)
            if result.returncode == 0:
                break
            time.sleep(.5)
        env = self.clean_environment()
        migration = self.call(['dotnet', 'ef', 'database', 'update', '--no-build', '--project', 'src/GoldSrcOps.Infrastructure',
                               '--startup-project', 'src/GoldSrcOps.Api', '--', '--environment', 'Integration'], env=env)
        (self.output / 'migration.log').write_text(migration, encoding='utf-8')
        sql = f'''INSERT INTO goldsrcops.servers ("Id","Name","Game",host,query_port,rcon_port,"IsEnabled","PollIntervalSeconds","CreatedAtUtc")
VALUES ('{SERVER}','Synthetic fixture','GoldSrc','127.0.0.1',{self.game_port},{self.game_port},true,60,now());
INSERT INTO goldsrcops.server_credentials ("Id","ServerId","Kind","SecretReference","CreatedAtUtc")
VALUES ('965f4028-5c40-4f16-8084-0a13d5a552ae','{SERVER}','RconPassword','rcon-secret://fixture',now());'''
        self.docker_call('exec', '-i', self.db, 'psql', '-U', 'lookup', '-d', 'lookup', '-v', 'ON_ERROR_STOP=1', data=sql.encode())
        self.evidence['images'] = {image: self.docker_call('image', 'inspect', '--format', '{{.Id}}', image).strip()
            for image in (IMAGE, 'postgres:17-alpine')}
        for project in ('Api', 'Web'):
            self.call(['dotnet', 'publish', f'src/GoldSrcOps.{project}', '-c', 'Debug', '--no-build', '--no-restore',
                       '-o', self.output / project.lower()])
        self.api_start('api')
        env['ASPNETCORE_URLS'] = f'http://127.0.0.1:{self.web_port}'
        env['GoldSrcOpsApi__BaseUrl'] = f'http://127.0.0.1:{self.api_port}/'
        folder = self.output / 'web'
        web = self.start(['dotnet', folder / 'GoldSrcOps.Web.dll', '--contentRoot', folder], 'web', env)
        self.healthy(web, self.web_port, '/health/live')
        self.mark('real-hosts-postgresql-migrations-ready')

    def verify(self):
        exact = self.search('Alex', 'legacy')
        require(len(exact['entries']) == 2 and not exact['hasMore'], 'Exact results differ')
        self.search('Alex', 'contains', 'busy')
        self.search('ab', 'contains', 'invalid')
        time.sleep(10.2)
        partial = self.search('Alex', 'contains')
        require(len(partial['entries']) == 10 and partial['hasMore'], 'Partial truncation differs')
        require(partial['entries'][2]['name'] == '[TAG]Alex-02', 'Actual stored name lost')
        require(self.search('Alex', 'exact') == exact, 'Exact cache contaminated')
        require(self.search('Alex', 'contains') == partial, 'Contains cache not reused')
        for language in ('ru', 'en'):
            self.web('Alex', 'exact', language)
            self.web('Alex', 'contains', language)
            self.web('Nobody', 'contains', language, 'busy')
        self.mark('http-web-both-modes-cache-separation-shared-cooldown')
        # Restart API only: the game still owns its cooldown even with an empty API cache.
        self.api_start('api-fresh-cache')
        self.search('Unknown', 'exact', 'busy')
        time.sleep(10.2)
        self.search('Unknown', 'exact')
        self.search('Unknown', 'contains', 'busy')
        time.sleep(10.2)
        require(self.search('Unknown', 'contains')['entries'] == [], 'Empty contains result differs')
        self.mark('addon-shared-cooldown-survives-api-restart-empty-cache')
        for name, mode, expected_name in [('Ab', 'exact', 'Ab'), ('рил', 'contains', 'КИРИЛЛ'), ('😀😀😀', 'contains', '😀😀😀Tail'), ('<b>', 'contains', '<b>Alex</b>')]:
            time.sleep(10.2)
            result = self.search(name, mode)
            require(result['entries'][0]['name'] == expected_name, 'Unicode or short-name match differs')
            page = self.web(name, mode, 'en')
            require(expected_name in html.unescape(page), 'Web lost original result name')
            if name == '<b>':
                require('<b>Alex</b>' not in page and '&lt;b&gt;Alex&lt;/b&gt;' in page, 'Web result not escaped')
        self.mark('real-rcon-short-exact-russian-four-byte-and-html-names')
        self.command('goldsrcops_player_lookup_enabled 0')
        time.sleep(10.2)
        self.search('Unavailable', 'contains', 'unavailable')
        self.web('Unavailable', 'contains', 'ru', 'busy')
        time.sleep(10.2)
        self.web('Unavailable', 'contains', 'en', 'unavailable')
        self.command('goldsrcops_player_lookup_enabled 1')
        self.mark('addon-unavailable-propagates-without-retry')
        self.engine_stop()
        require(self.vaults() == self.baseline, 'Vault bytes changed after both modes')
        self.engine_start('old', 'engine-old')
        self.api_start('api-old-addon')
        require(len(self.search('Alex', 'legacy')['entries']) == 2, 'Legacy lookup failed on old addon')
        time.sleep(10.2)
        self.search('Alex', 'contains', 'unavailable')
        self.mark('new-api-old-addon-exact-compatible-v2-fails-closed')
        self.engine_stop()
        require(self.vaults() == self.baseline, 'Rollback changed vault bytes')
        self.engine_start('current', 'engine-reopened')
        self.api_start('api-reopened')
        require(len(self.search('Alex', 'contains')['entries']) == 10, 'Reopened vault search failed')
        self.engine_stop()
        require(self.vaults() == self.baseline, 'Reopen changed vault bytes')
        errors = self.docker_call('exec', self.game, 'find', '/server/cstrike/addons/amxmodx/logs', '-name', 'error_*.log')
        require(not errors.strip(), 'AMXX error log present')
        self.evidence['vault_sha256'] = self.baseline
        self.mark('vault-bytes-preserved-after-queries-rollback-and-reopen')
        counts = {}
        for name, expected in [('engine-current', (4, 7)), ('engine-old', (1, 1)), ('engine-reopened', (0, 1))]:
            log = (self.output / (name + '.log')).read_text(encoding='utf-8', errors='strict')
            commands = re.findall(r'^rcon .* (goldsrcops_player_lookup(?:_v2)?) [A-F0-9]{16} [A-F0-9]+\s*$', log, re.MULTILINE)
            actual = (commands.count('goldsrcops_player_lookup'), commands.count('goldsrcops_player_lookup_v2'))
            require(actual == expected, f'Unexpected RCON command counts in {name}: {actual}')
            counts[name] = {'v1': actual[0], 'v2': actual[1]}
        self.evidence['rcon_commands'] = counts
        self.mark('rcon-counts-no-automatic-retry-or-fallback')

    def cleanup(self):
        if self.engine and self.engine.poll() is None:
            try:
                self.engine_stop()
            except (OSError, RuntimeError, subprocess.TimeoutExpired):
                self.stop(self.engine)
        for process in reversed(self.processes):
            self.stop(process)
        for log in self.logs:
            log.close()
        for container in reversed(self.containers):
            (self.output / (container + '.log')).write_text(self.docker_call('logs', container), encoding='utf-8')
            self.docker_call('rm', '-f', container)
        if self.network_created:
            self.docker_call('network', 'rm', self.network)
        # Exact task-created publish folders only; logs and compiled fixture evidence remain.
        for name in ('api', 'web'):
            directory = self.output / name
            if directory.exists():
                require(not directory.is_symlink() and directory.resolve().parent == self.output.resolve(), 'Unsafe cleanup path')
                shutil.rmtree(directory)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inputs', type=Path, required=True)
    args = parser.parse_args()
    rehearsal = Rehearsal(args.inputs)
    print('Evidence directory: ' + str(rehearsal.output), flush=True)
    started = time.monotonic()
    try:
        rehearsal.setup()
        rehearsal.verify()
        rehearsal.evidence['result'] = 'passed'
    except Exception as error:
        rehearsal.evidence['result'] = 'failed'
        rehearsal.evidence['error'] = str(error)
        raise
    finally:
        try:
            rehearsal.cleanup()
        finally:
            rehearsal.evidence['duration_seconds'] = round(time.monotonic() - started, 3)
            (rehearsal.output / 'result.json').write_text(json.dumps(rehearsal.evidence, indent=2), encoding='utf-8')
    print('PLAYER_SEARCH_INTEGRATION=passed', flush=True)


if __name__ == '__main__':
    main()
