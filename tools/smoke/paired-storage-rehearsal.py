#!/usr/bin/env python3
"""Freeze local sources/probe artifacts and run the root fixture with no network."""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import uuid

REPO = Path(__file__).resolve().parents[2]
FILES = ['ops/gameserver/' + name for name in (
    'paired-addon-upgrade.py', 'player_storage_pair.py', 'addon-upgrade.py',
    'player-data-baseline.py', 'player-data-bundle.py', 'player_data_schedule.py',
    'player_data_manual.py', 'player_data_reconciliation.py', 'player-data-schedule-guard.sh',
    'player-data-scheduled-capture.py',
    'addon-upgrade-guard.sh', 'game-event-persistent.sh', 'fast-reentry-v1.cfg')]
FILES += ['tools/smoke/paired-addon-upgrade.py', 'tools/smoke/paired-storage-runtime.json']


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--probe-package', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    probes, output = args.probe_package.resolve(), args.output.resolve()
    manifest = json.loads((probes / 'manifest.json').read_bytes())
    if manifest['schema'] != 1 or manifest['target'] != 'linux-i386' or set(manifest['payload']) != {'player-storage-old', 'player-storage-new'}:
        raise ValueError('Unexpected probe contract')
    for name, expected in manifest['sources'].items():
        path = REPO / name
        if not path.resolve().is_relative_to(REPO) or sha(path) != expected:
            raise ValueError('Probe source mismatch')
    for name, expected in manifest['payload'].items():
        if sha(probes / name) != expected:
            raise ValueError('Probe binary mismatch')
    if sha(probes / 'corresponding-source.zip') != manifest['correspondingSourceSha256']:
        raise ValueError('Probe corresponding source mismatch')
    spec = importlib.util.spec_from_file_location('paired_engine_inputs', REPO / 'tools/smoke/engine-ci.py')
    engine = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(engine)
    engine_reference = json.loads((REPO / 'tools/smoke/engine-ci-lock.json').read_bytes())['images']['engine']['reference']
    runtime = json.loads((REPO / 'tools/smoke/paired-storage-runtime.json').read_bytes())
    if runtime['schema'] != 1 or runtime['i386LibraryPath'] != '/lib32':
        raise ValueError('Unexpected runtime library contract')
    reference = runtime['python']
    image = engine.ensure_image(reference)
    engine_image = engine.ensure_image(engine_reference)
    output.mkdir(exist_ok=False)
    tooling = output / 'tooling'
    tooling.mkdir()
    engine.download(runtime['jq'], tooling).chmod(0o755)
    sources = {name: sha(REPO / name) for name in FILES}
    snapshot = output / 'source-snapshot'
    for name in FILES:
        target = snapshot / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPO / name, target)
    probe_snapshot = output / 'probe-inputs'
    shutil.copytree(probes, probe_snapshot)
    runtime32 = output / 'runtime32'
    runtime32.mkdir()
    # Materialize library symlinks inside the pinned image, avoiding privileged
    # Windows symlink creation and never extracting archive links on the host.
    copied = subprocess.run(['docker', 'run', '--rm', '--pull', 'never', '--network', 'none', '--read-only',
        '--entrypoint', 'tar', engine_reference, '--dereference', '--hard-dereference', '-cf', '-', '-C', '/lib32', '.'],
        check=True, capture_output=True, timeout=30)
    if len(copied.stdout) > 128 * 1024 * 1024:
        raise ValueError('Runtime library archive bound')
    with tarfile.open(fileobj=io.BytesIO(copied.stdout)) as archive:
        members = archive.getmembers()
        if len(members) > 2048:
            raise ValueError('Runtime library count bound')
        for member in members:
            target = runtime32 / member.name
            if not target.resolve().is_relative_to(runtime32) or member.size > 16 * 1024 * 1024:
                raise ValueError('Runtime library path/size bound')
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            elif member.isfile():
                target.parent.mkdir(parents=True, exist_ok=True)
                with target.open('xb') as stream:
                    stream.write(archive.extractfile(member).read())
                target.chmod(0o755)
            else:
                raise ValueError('Runtime archive links are forbidden')
    if not (runtime32 / 'ld-linux.so.2').exists():
        raise ValueError('Missing pinned i386 loader')
    name = 'paired-storage-' + uuid.uuid4().hex
    receipts = output / 'receipts'
    receipts.mkdir()
    command = ['docker', 'run', '--rm', '--name', name, '--pull', 'never', '--network', 'none',
        '--cpus', '2', '--memory', '512m', '--pids-limit', '256', '--security-opt', 'no-new-privileges:true',
        '--mount', f'type=bind,source={snapshot},target=/repo,readonly',
        '--mount', f'type=bind,source={probe_snapshot},target=/probes,readonly',
        '--mount', f'type=bind,source={runtime32},target=/runtime32,readonly',
        '--mount', f'type=bind,source={tooling},target=/tooling,readonly',
        '--mount', f'type=bind,source={receipts},target=/evidence',
        '--env', 'PAIR_READER_DIR=/probes', '--env', 'PAIR_I386_LOADER=/runtime32/ld-linux.so.2',
        '--env', 'PAIR_EVIDENCE=/evidence',
        '--env', 'PATH=/tooling:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin',
        '--entrypoint', 'python3', reference,
        '-B', '/repo/tools/smoke/paired-addon-upgrade.py']
    try:
        result = subprocess.run(command, capture_output=True, timeout=300)
    except subprocess.TimeoutExpired:
        subprocess.run(['docker', 'rm', '-f', name], capture_output=True, timeout=30)
        raise
    log = result.stdout + result.stderr
    (output / 'rehearsal.log').write_bytes(log)
    result.check_returncode()
    match = re.search(rb'Ran (\d+) tests', log)
    if not match or b'\nOK\n' not in log.replace(b'\r\n', b'\n'):
        raise ValueError('Missing fixture completion')
    if any(sha(snapshot / name) != expected or sha(REPO / name) != expected for name, expected in sources.items()):
        raise ValueError('Sources changed during rehearsal')
    measurements = []
    for path in receipts.glob('*.json'):
        receipt = json.loads(path.read_bytes())
        if receipt['kind'] == 'probe' and receipt['case'] != 'test_storage_probe_p99_gate':
            measurements.append({key: receipt['receipt'][key] for key in ('samples', 'p99Us', 'maxUs')})
    if not measurements or any(x['p99Us'] > 2000 for x in measurements):
        raise ValueError('Missing accepted native probe measurements')
    report = dict(schema=1, state='verified', tests=int(match[1]), sources=sources, image=image,
        imageReference=reference, probeManifestSha256=sha(probe_snapshot / 'manifest.json'),
        jqSha256=sha(tooling / 'jq'),
        i386Image=engine_image, i386ImageReference=engine_reference,
        isolation=dict(network='none', publishedPorts=0),
        backupTransport='local encrypted off-host-directory emulation; no provider claim',
        runtime='service/process adapter; no production or real-engine rollout claim', productionChanged=False)
    report['probeMeasurements'] = measurements
    report['receipts'] = {path.name: sha(path) for path in sorted(receipts.glob('*.json'))}
    (output / 'result.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(state='paired-rehearsal-passed', tests=report['tests'], productionChanged=False)))


if __name__ == '__main__':
    main()
