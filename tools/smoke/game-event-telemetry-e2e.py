#!/usr/bin/env python3
"""Deliver verified native round records through the real agent, API and PostgreSQL."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('engine_ci', Path(__file__).with_name('engine-ci.py'))
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)
SCENARIOS = dict(ordinary='goldsrcops_bomb_explode', **{
    'lost-ack': 'goldsrcops_bomb_defuse', 'unavailable-restart': 'goldsrcops_round_timeout'})
PROJECT = ROOT / 'tools/GoldSrcOps.TelemetryAcceptance/GoldSrcOps.TelemetryAcceptance.csproj'


def read_records(evidence, compiled):
    engine.verify_fixtures(compiled)
    result_path = evidence / 'result.json'
    if result_path.is_symlink() or result_path.stat().st_size > 16384:
        raise ValueError('Invalid engine result receipt')
    result = json.loads(result_path.read_bytes())
    lock = json.loads(engine.LOCK.read_bytes())
    if (result.get('result') != 'passed' or result.get('mode') != 'gameplay'
            or result.get('network') != 'none' or result.get('publishedPorts') != 0
            or result.get('cleanup') != 'passed' or result.get('containerExitCode') != 0
            or result.get('lockSha256') != engine.digest(engine.LOCK)
            or result.get('fixtureManifestSha256') != engine.digest(compiled / 'manifest.json')
            or result.get('cases') != len(lock['cases']) or result.get('compiledFixtures') != len(engine.FIXTURES)):
        raise ValueError('Native engine evidence is not accepted for this checkout')
    engine.validate_evidence(evidence, lock['cases'])
    records = {}
    for scenario, command in SCENARIOS.items():
        filename = (evidence / 'round-records' / (command + '.tsv')).read_text().split('\t')[0]
        records[scenario] = (evidence / 'round-records' / command / filename).read_bytes()
    return records, {name: result[name] for name in ('lockSha256', 'fixtureManifestSha256', 'cases')}


def command(arguments, log):
    options = dict(cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if os.name == 'nt':
        options['creationflags'] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        options['start_new_session'] = True
    with subprocess.Popen(arguments, **options) as process:
        try:
            output, _ = process.communicate(timeout=180)
        except subprocess.TimeoutExpired:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], check=True,
                               capture_output=True, timeout=15)
            else:
                os.killpg(process.pid, signal.SIGKILL)
            process.communicate(timeout=15)
            raise
        if len(output) > 4 * 1024 * 1024:
            raise ValueError('Acceptance diagnostics exceeded bound')
        log.append(output.decode('utf-8', errors='replace'))
        if process.returncode:
            raise ValueError('Telemetry acceptance command failed; inspect retained diagnostics')


def execute(evidence, compiled, output):
    records, binding = read_records(evidence, compiled)
    output.mkdir(parents=True, exist_ok=False)
    log = []
    try:
        command(['dotnet', 'build', str(PROJECT), '-p:AuditPipeline=true', '--nologo'], log)
        with tempfile.TemporaryDirectory(prefix='native-telemetry-e2e-') as temporary:
            root = Path(temporary)
            inputs = root / 'inputs'
            inputs.mkdir()
            for scenario, raw in records.items():
                (inputs / (scenario + '.json')).write_bytes(raw)
            harness = PROJECT.parent / 'bin/Debug/net10.0/GoldSrcOps.TelemetryAcceptance.dll'
            agent = ROOT / 'src/GoldSrcOps.GameEventAgent/bin/Debug/net10.0/GoldSrcOps.GameEventAgent.dll'
            command(['dotnet', str(harness), str(ROOT), str(agent), str(inputs), str(root / 'runtime'),
                     str(root / 'result.json')], log)
            result = json.loads((root / 'result.json').read_bytes())
            if (result.get('status') != 'native-telemetry-e2e-accepted'
                    or {case['scenario'] for case in result['scenarios']} != set(SCENARIOS)
                    or len(result['scenarios']) != 3):
                raise ValueError('Incomplete telemetry acceptance receipt')
            for case in result['scenarios']:
                if case['status'] != 'passed' or case['nativeSha256'].lower() != hashlib.sha256(records[case['scenario']]).hexdigest():
                    raise ValueError('Acceptance receipt is not bound to native input')
            result['nativeEngine'] = binding
            sources = [PROJECT, Path(__file__).resolve(), Path(__file__).with_name('test-game-event-telemetry-e2e.py')]
            sources.extend(sorted(PROJECT.parent.glob('*.cs')))
            result['acceptanceSourcesSha256'] = {path.relative_to(ROOT).as_posix(): engine.digest(path) for path in sources}
            result['componentsSha256'] = {name: engine.digest(path) for name, path in (
                ('api', harness.parent / 'GoldSrcOps.Api.dll'), ('agent', agent), ('acceptance', harness))}
        result['cleanup'] = 'temporary queues, synthetic credentials and database removed'
        (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print('NATIVE_TELEMETRY_E2E=passed scenarios=3 rows=3')
    finally:
        (output / 'runner.log').write_text('\n'.join(log), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--compiled', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    execute(args.evidence.resolve(), args.compiled.resolve(), args.output.resolve())


if __name__ == '__main__':
    main()
