#!/usr/bin/env python3
"""Run pinned gameplay fixtures in disposable, network-isolated containers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[2]
LOCK = Path(__file__).with_name('engine-ci-lock.json')
ERROR = re.compile(r'ASSERTION_FAILED|Run time error|Load fails|bad load|ML_NOTFOUND|failures=[1-9][0-9]*')
FINAL = 'PLAYER_PREFERENCES_RUNTIME=passed'
ENGINE_TIMEOUT_SECONDS = 600
OBJECTIVES = {'goldsrcops_bomb_explode', 'goldsrcops_bomb_defuse', 'goldsrcops_round_timeout'}
CRASH_CASES = [dict(plugin='player_data_crash_smoke', command=command, map='de_dust2', markers=[marker])
               for command, marker in [('goldsrcops_crash_baseline', 'PLAYER_DATA_BASELINE'),
                                       ('goldsrcops_crash_write', 'PLAYER_DATA_BEFORE_KILL'),
                                       ('goldsrcops_crash_recover', 'PLAYER_DATA_RECOVERED'),
                                       ('goldsrcops_crash_reopen', 'PLAYER_DATA_REOPENED')]]
RUNTIME_PATHS = ('tools/smoke/game-player-preferences-runtime.sh', 'tools/smoke/game-player-data-crash-runtime.sh',
                 'tools/smoke/game-map-menu-runtime.sh', 'ops/gameserver/managed-profile.sh',
                 'ops/gameserver/fast-reentry-loadout-v1.cfg',
                 'samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt',
                 'samples/amxmodx-map-menu/goldsrcops-map-menu.txt')
PROCESS_CASES = [dict(plugin='process_storage_smoke', command='goldsrcops_storage_' + name, map='de_dust2',
                     markers=['PROCESS_STORAGE_BENCHMARK' if name == 'benchmark' else 'PROCESS_STORAGE_FAILURE'])
                 for name in ('benchmark', 'fail_stats', 'fail_preferences', 'fail_streak', 'fail_hud', 'fail_name')]


def freeze_runtime(directory):
    snapshot = directory / 'runtime-source'
    snapshot.mkdir()
    hashes = {}
    for path in RUNTIME_PATHS:
        source, target = ROOT / path, snapshot / path
        if source.is_symlink():
            raise ValueError('Linked runtime source is not allowed')
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.read_bytes())
        hashes[path] = digest(target)
    return snapshot, hashes


def digest(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def verify(path, expected):
    if not path.is_file() or path.is_symlink() or digest(path) != expected:
        raise ValueError('Asset digest mismatch: ' + path.name)


def download(asset, cache):
    path = cache / asset['name']
    if not path.exists():
        temporary = path.with_suffix(path.suffix + '.partial-' + uuid.uuid4().hex)
        try:
            with urllib.request.urlopen(asset['url'], timeout=60) as response, temporary.open('xb') as target:
                if not response.url.startswith('https://'):
                    raise ValueError('HTTPS required')
                size = 0
                while block := response.read(1024 * 1024):
                    size += len(block)
                    if size > 32 * 1024 * 1024:
                        raise ValueError('Asset exceeds download budget')
                    target.write(block)
            verify(temporary, asset['sha256'])
            temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)
    verify(path, asset['sha256'])
    return path


def docker(*args, timeout=120, stdout=None):
    return subprocess.run(['docker', *args], check=True, timeout=timeout,
                          stdout=stdout if stdout is not None else subprocess.PIPE,
                          stderr=subprocess.STDOUT, text=True)


def ensure_image(reference):
    if not re.fullmatch(r'[a-z0-9./_-]+@sha256:[a-f0-9]{64}', reference):
        raise ValueError('Image must be digest-pinned')
    inspected = subprocess.run(['docker', 'image', 'inspect', reference], capture_output=True, text=True, timeout=30)
    if inspected.returncode:
        docker('pull', reference, timeout=300)
    data = json.loads(docker('image', 'inspect', reference).stdout)[0]
    if reference not in data['RepoDigests'] or data['Architecture'] != 'amd64':
        raise ValueError('Unexpected image identity or architecture')
    return data['Id']


def validate_log(text, marker):
    if ERROR.search(text):
        raise ValueError('Engine assertion, runtime or localization error')
    matches = re.findall(r'^' + re.escape(marker) + r'=passed(?: [^\r\n]*)?$', text, re.M)
    if len(matches) != 1 or not re.search(r'(?:^| )failures=0(?: |$)', matches[0]):
        raise ValueError('Missing, duplicate or failed scenario marker: ' + marker)


def validate_evidence(evidence, cases):
    for case in cases:
        text = (evidence / (case['command'] + '.log')).read_text(encoding='utf-8', errors='replace')
        for marker in case['markers']:
            validate_log(text, marker)
    if any(path.stat().st_size for path in evidence.glob('error_*.log')):
        raise ValueError('AMXX error log is not empty')
    validate_objective_records(evidence, cases)
    if any(case['command'] == 'goldsrcops_crash_write' for case in cases):
        validate_crash_evidence(evidence)
    if any(case['command'] == 'goldsrcops_storage_benchmark' for case in cases):
        validate_storage_timing(evidence)


def validate_storage_timing(evidence):
    log = (evidence / 'goldsrcops_storage_benchmark.log').read_text(errors='replace')
    rows = re.findall(r'^PROCESS_STORAGE_TIMING samples=10000 identities=32 handler_p99_us=([0-9]+) max_us=([0-9]+)$', log, re.M)
    # The approved performance gate is p99. Keep max as evidence, bounded by
    # the existing container lifetime, rather than adding a separate max SLO.
    if len(rows) != 1 or not 0 <= int(rows[0][0]) <= 5000 or not int(rows[0][0]) <= int(rows[0][1]) <= ENGINE_TIMEOUT_SECONDS * 1000000:
        raise ValueError('Missing or excessive full-handler wall-time evidence')
    return dict(samples=10000, identities=32, handlerP99Us=int(rows[0][0]), handlerMaxUs=int(rows[0][1]))


def validate_crash_evidence(evidence):
    receipt = evidence / 'process-kill.tsv'
    if receipt.is_symlink() or receipt.stat().st_size > 32 or receipt.read_text() != '9\t137\n':
        raise ValueError('Engine must terminate by SIGKILL with exit 137')
    for case in CRASH_CASES:
        log = (evidence / (case['command'] + '.log')).read_text(errors='replace')
        ended = re.findall(r'^PLAYER_DATA_PLUGIN_END=called$', log, re.M)
        if len(ended) != (0 if case['command'] == 'goldsrcops_crash_write' else 1):
            raise ValueError('Wrong clean/crash shutdown boundary')
    for name in ('goldsrcops-player-stats-v1', 'goldsrcops-player-preferences-v1'):
        baseline = evidence / 'baseline' / (name + '.vault')
        checkpoint = evidence / 'post-crash' / (name + '.vault')
        journal = checkpoint.with_suffix('.journal')
        if baseline.parent.is_symlink() or checkpoint.parent.is_symlink():
            raise ValueError('Crash snapshots must not use linked directories')
        for path in (baseline, checkpoint, journal):
            if path.is_symlink() or not 8 < path.stat().st_size <= 1024*1024:
                raise ValueError('Missing or unbounded synthetic crash snapshot')
        if baseline.read_bytes() != checkpoint.read_bytes():
            raise ValueError('Crash updates must not be in the baseline checkpoint')


def validate_objective_records(evidence, cases):
    identities = set()
    for case in cases:
        command = case['command']
        if command not in OBJECTIVES:
            continue
        root = evidence / 'round-records' / command
        receipt = root.with_suffix('.tsv')
        if root.parent.is_symlink() or root.is_symlink() or receipt.is_symlink() or receipt.stat().st_size > 256:
            raise ValueError('Objective receipt exceeds bound')
        fields = receipt.read_text().strip().split('\t')
        if len(fields) != 3 or not re.fullmatch(r'[0-9a-f-]{36}\.json', fields[0]):
            raise ValueError('Invalid objective transfer receipt')
        filename, size, checksum = fields
        identity = uuid.UUID(filename[:-5])
        path = root / filename
        if (identity.version != 4 or str(identity) + '.json' != filename or identity in identities
                or root.is_symlink() or path.is_symlink() or not path.resolve().is_relative_to(root.resolve())
                or {p.name for p in root.iterdir()} != {filename}):
            raise ValueError('Duplicate or escaping objective record')
        if not 0 < path.stat().st_size <= 4096:
            raise ValueError('Objective record exceeds bound')
        raw = path.read_bytes()
        if not 0 < len(raw) <= 4096 or str(len(raw)) != size or hashlib.sha256(raw).hexdigest() != checksum:
            raise ValueError('Objective record byte receipt mismatch')
        value = json.loads(raw)
        if (set(value) != {'spoolVersion', 'recordId', 'event'} or type(value['spoolVersion']) is not int or value['spoolVersion'] != 1
                or value['recordId'] != str(identity) or set(value['event']) != {'type', 'occurredAtUtc', 'map', 'players', 'bots'}
                or value['event']['type'] != 'round.ended' or value['event']['map'] != case['map']
                or value['event']['players'] != 3 or value['event']['bots'] != 3):
            raise ValueError('Unexpected native objective event')
        identities.add(identity)


FIXTURES = {
    'process_storage_smoke.amxx': 'tools/smoke/amxx-process-storage.sma',
    'player_data_crash_smoke.amxx': 'tools/smoke/amxx-player-data-crash.sma',
    'round_objectives_smoke.amxx': 'tools/smoke/amxx-round-objectives.sma',
    'goldsrcops_game_events.amxx': 'samples/amxmodx-game-event-producer/goldsrcops_game_events.sma',
    'game_event_producer_smoke.amxx': 'tools/smoke/amxx-game-event-producer.sma',
    'goldsrcops_weapon_selection.amxx': 'samples/amxmodx-weapon-selection/goldsrcops_weapon_selection.sma',
    'player_preferences_smoke.amxx': 'tools/smoke/amxx-player-preferences.sma',
    'player_menu_smoke.amxx': 'tools/smoke/amxx-player-menu.sma',
    'spawn_loadout_smoke.amxx': 'tools/smoke/amxx-spawn-loadout.sma',
    'map_stats_smoke.amxx': 'tools/smoke/amxx-map-stats.sma',
    'persistent_stats_smoke.amxx': 'tools/smoke/amxx-persistent-stats.sma',
    'goldsrcops_map_menu.amxx': 'samples/amxmodx-map-menu/goldsrcops_map_menu.sma',
    'map_menu_smoke.amxx': 'tools/smoke/amxx-map-menu.sma',
    'map_language_provider_smoke.amxx': 'tools/smoke/amxx-map-language-provider.sma',
    'engine_failure_smoke.amxx': 'tools/smoke/amxx-engine-failure.sma',
}


def source_identity():
    paths = list(FIXTURES.values()) + ['samples/amxmodx-weapon-selection/goldsrcops-player-menu.txt',
                                     'samples/amxmodx-weapon-selection/goldsrcops-process-storage.inc',
                                     'samples/amxmodx-map-menu/goldsrcops-map-menu.txt',
                                     'tools/smoke/amxx-map-menu.ps1',
                                     'tools/smoke/amxx-multiplayer.inc',
                                     'ops/gameserver/fast-reentry-loadout-v1.cfg',
                                     'tools/smoke/amxx-weapon-selection.ps1',
                                     'tools/smoke/amxx-game-event-producer.ps1']
    return {path: digest(ROOT / path) for path in paths}


def package_fixtures(compiled, output):
    output.mkdir(parents=True, exist_ok=False)
    for name in FIXTURES:
        if (compiled / name).is_symlink() or not (compiled / name).is_file():
            raise ValueError('Missing compiled fixture: ' + name)
        shutil.copyfile(compiled / name, output / name)
    manifest = {'schema': 1, 'toolchain': {'amxx': '1.10.0.5481', 'reapi': '5.24.0.300'},
                'sources': source_identity(), 'payload': {name: digest(output / name) for name in FIXTURES}}
    (output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(f'ENGINE_FIXTURES=packaged files={len(FIXTURES)}')


def verify_fixtures(compiled):
    manifest = json.loads((compiled / 'manifest.json').read_bytes())
    if (manifest['schema'] != 1 or manifest['sources'] != source_identity()
            or manifest['toolchain'] != {'amxx': '1.10.0.5481', 'reapi': '5.24.0.300'}
            or set(manifest['payload']) != set(FIXTURES)):
        raise ValueError('Compiled fixtures do not match this checkout')
    for name, expected in manifest['payload'].items():
        verify(compiled / name, expected)
    return digest(compiled / 'manifest.json')


def cases_tsv(cases):
    lines = []
    commands = set()
    for case in cases:
        plugins = [case['plugin'], *case.get('companionPlugins', [])]
        if (len(set(plugins)) != len(plugins)
                or any(name + '.amxx' not in FIXTURES for name in plugins)
                or not re.fullmatch(r'[a-z0-9_]+', case['command'])
                or case['command'] in commands
                or not re.fullmatch(r'[a-z0-9_]+', case['map'])
                or not case['markers']
                or type(case.get('timeoutSeconds', 45)) is not int
                or not 20 <= case.get('timeoutSeconds', 45) <= 210
                or any(not re.fullmatch(r'[A-Z0-9_]+', marker) for marker in case['markers'])):
            raise ValueError('Invalid or duplicate engine case')
        commands.add(case['command'])
        lines.append('\t'.join([','.join(plugins), case['command'], case['markers'][0], case['map'], str(case.get('timeoutSeconds', 45))]) + '\n')
    return ''.join(lines)


def execute(cache, output, compiled, negative=False, missing_map_provider=False, missing_spawn_protection=False,
            disabled_producer=False, blocked_objective=False, player_data_crash=False, discard_crash_journals=False,
            nvault_module=None, process_storage=False):
    started = time.monotonic()
    output.mkdir(parents=True, exist_ok=False)
    evidence = output / 'evidence'
    evidence.mkdir()
    inputs = output / 'inputs'
    inputs.mkdir()
    cache.mkdir(parents=True, exist_ok=True)
    lock = json.loads(LOCK.read_bytes())
    result = {'result': 'failed', 'mode': ('negative-assertion' if negative else
                                       'missing-map-provider' if missing_map_provider else
                                       'missing-spawn-protection' if missing_spawn_protection else
                                       'disabled-producer' if disabled_producer else
                                       'blocked-objective' if blocked_objective else
                                       'discard-crash-journals' if discard_crash_journals else
                                       'player-data-crash' if player_data_crash else
                                       'process-storage-helpers' if process_storage else 'gameplay'),
              'lockSha256': digest(LOCK), 'network': 'none', 'publishedPorts': 0}
    owned = []
    try:
        result['fixtureManifestSha256'] = verify_fixtures(compiled)
        runtime_source, result['runtimeSourceHashes'] = freeze_runtime(inputs)
        for asset in lock['assets']:
            archive = download(asset, cache)
            destination = inputs / asset['directory']
            destination.mkdir()
            if asset['name'].endswith('.zip'):
                with zipfile.ZipFile(archive) as source:
                    for entry in source.infolist():
                        if not (destination / entry.filename).resolve().is_relative_to(destination.resolve()):
                            raise ValueError('Archive path escapes extraction directory')
                    source.extractall(destination)
            else:
                with tarfile.open(archive) as source:
                    source.extractall(destination, filter='data')
        result['images'] = {name: ensure_image(value['reference']) for name, value in lock['images'].items()}
        if nvault_module is not None:
            if nvault_module.is_symlink() or not 0 < nvault_module.stat().st_size <= 8*1024*1024:
                raise ValueError('Invalid isolated nVault module')
            header = nvault_module.read_bytes()[:20]
            if header[:7] != b'\x7fELF\x01\x01\x01' or header[18:20] != b'\x03\x00':
                raise ValueError('nVault test module must be ELF32 little-endian i386')
            shutil.copyfile(nvault_module, inputs / 'nvault_amxx_i386.so')
            result['nvaultModuleSha256'] = digest(inputs / 'nvault_amxx_i386.so')
        steam_name = 'gameplay-steam-' + uuid.uuid4().hex
        owned.append(steam_name)
        docker('create', '--name', steam_name, '--network', 'none', '--entrypoint', '/bin/true',
               lock['images']['steam']['reference'])
        steam = inputs / 'steamclient.so'
        docker('cp', steam_name + ':' + lock['images']['steam']['path'], str(steam))
        verify(steam, lock['images']['steam']['sha256'])
        cases = ([lock['negativeCase']] if negative else
                 [lock['missingMapProviderCase']] if missing_map_provider else
                 [lock['missingSpawnProtectionCase']] if missing_spawn_protection else
                 [lock['disabledProducerCase']] if disabled_producer else
                 [lock['blockedObjectiveCase']] if blocked_objective else
                 CRASH_CASES if player_data_crash or discard_crash_journals else
                 PROCESS_CASES if process_storage else lock['cases'])
        (inputs / 'cases.tsv').write_text(cases_tsv(cases), encoding='utf-8', newline='\n')
        name = 'gameplay-engine-' + uuid.uuid4().hex
        owned.append(name)
        docker('create', '--name', name, '--network', 'none', '--cpus', '2', '--memory', '1g',
               '--pids-limit', '256', '--security-opt', 'no-new-privileges',
               '--mount', f'type=bind,source={runtime_source},target=/repo,readonly',
               '--mount', f'type=bind,source={inputs},target=/inputs,readonly',
               '--mount', f'type=bind,source={compiled},target=/compiled,readonly',
               '--mount', f'type=bind,source={evidence},target=/evidence',
               '--env', 'DISCARD_CRASH_JOURNALS=' + ('1' if discard_crash_journals else '0'),
               '--entrypoint', '/usr/bin/timeout', lock['images']['engine']['reference'],
               '--signal=TERM', '--kill-after=10', str(ENGINE_TIMEOUT_SECONDS), 'bash',
               '/repo/tools/smoke/game-player-data-crash-runtime.sh' if player_data_crash or discard_crash_journals
               else '/repo/tools/smoke/game-player-preferences-runtime.sh')
        inspected = json.loads(docker('inspect', name).stdout)[0]
        host = inspected['HostConfig']
        if (host['NetworkMode'] != 'none' or host['PortBindings'] or host['Privileged']
                or host['Memory'] != 1073741824 or host['NanoCpus'] != 2000000000
                or host['PidsLimit'] != 256 or inspected['Image'] != result['images']['engine']):
            raise ValueError('Unexpected container isolation')
        with (evidence / 'runner.log').open('w', encoding='utf-8') as log:
            docker('start', '-a', name, timeout=ENGINE_TIMEOUT_SECONDS + 30, stdout=log)
        state = json.loads(docker('inspect', name).stdout)[0]['State']
        result['containerExitCode'] = state['ExitCode']
        if state['Running'] or state['ExitCode'] != 0 or state['OOMKilled']:
            raise ValueError('Engine container failed')
        validate_evidence(evidence, cases)
        if process_storage:
            result['storageTiming'] = validate_storage_timing(evidence)
        final = 'PLAYER_DATA_CRASH_RUNTIME=passed' if player_data_crash or discard_crash_journals else FINAL
        if final not in (evidence / 'runner.log').read_text(encoding='utf-8'):
            raise ValueError('Missing runtime completion marker')
        result.update(result='passed', cases=len(cases), compiledFixtures=len(FIXTURES))
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        result['error'] = type(error).__name__ + ': ' + str(error)
        raise
    finally:
        cleanup_failed = False
        for name in reversed(owned):
            try:
                docker('rm', '-f', '-v', name, timeout=30)
            except (OSError, subprocess.SubprocessError):
                cleanup_failed = True
        result['cleanup'] = 'failed' if cleanup_failed else 'passed'
        if cleanup_failed:
            result['result'] = 'failed'
        result['durationSeconds'] = round(time.monotonic() - started, 3)
        (evidence / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(result), flush=True)
    if cleanup_failed:
        raise RuntimeError('Owned container cleanup failed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--compiled', required=True, type=Path)
    parser.add_argument('--package-fixtures', action='store_true')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--nvault-module', type=Path, default=os.environ.get('GOLDSRCOPS_ISOLATED_NVAULT_MODULE'),
                        help='Verified local ELF32 module for isolated engine checks only.')
    negative = parser.add_mutually_exclusive_group()
    negative.add_argument('--process-storage', action='store_true', help='Exercise commit-failure UI and handler timing with a test-only module.')
    negative.add_argument('--player-data-crash', action='store_true', help='Verify journal recovery after process SIGKILL.')
    negative.add_argument('--discard-crash-journals-check', action='store_true', help='Remove synthetic journals after KILL; recovery must fail.')
    negative.add_argument('--blocked-objective-check', action='store_true', help='Disable natural objective completion; must fail.')
    negative.add_argument('--disabled-producer-check', action='store_true', help='Disable the round-event fixture producer; must fail.')
    negative.add_argument('--missing-spawn-protection-check', action='store_true', help='Disable fixture spawn protection; must fail.')
    negative.add_argument('--missing-map-provider-check', action='store_true', help='Require a missing map language provider; must fail.')
    negative.add_argument('--negative-check', action='store_true', help='Run a deliberately failing engine fixture; must exit nonzero.')
    args = parser.parse_args()
    try:
        if args.package_fixtures:
            package_fixtures(args.compiled.resolve(), args.output.resolve())
        else:
            if args.cache is None:
                parser.error('--cache is required for execution')
            execute(args.cache.resolve(), args.output.resolve(), args.compiled.resolve(),
                    args.negative_check, args.missing_map_provider_check, args.missing_spawn_protection_check,
                    args.disabled_producer_check, args.blocked_objective_check,
                    args.player_data_crash, args.discard_crash_journals_check,
                    args.nvault_module.resolve() if args.nvault_module else None, args.process_storage)
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
