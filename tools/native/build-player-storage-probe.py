#!/usr/bin/env python3
"""Build bounded i386 scratch/reopen tools from hash-verified module sources."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

REPO = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('native_build', REPO / 'native/nvault-durability/build.py')
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--builder-image', required=True)
    args = parser.parse_args()
    if not re.fullmatch('sha256:[a-f0-9]{64}', args.builder_image):
        parser.error('Immutable local builder image required')
    output, cache = args.output.resolve(), args.cache.resolve()
    output.mkdir(exist_ok=False)
    source, amxx_archive = native.fetch(native.LOCK['amxx'], cache, output)
    amtl, amtl_archive = native.fetch(native.LOCK['amtl'], cache, output)
    names = list(native.identity()) + ['tools/native/player-storage-probe.cpp',
        'tools/native/build-player-storage-probe.sh', 'tools/native/build-player-storage-probe.py']
    hashes = {name: native.digest(REPO / name) for name in names}
    snapshot = output / 'source-snapshot'
    for name in names:
        target = snapshot / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPO / name, target)
    command = ['docker', 'run', '--rm', '--pull', 'never', '--network', 'none', '--cpus', '2', '--memory', '1g',
        '--mount', f'type=bind,source={snapshot},target=/repo,readonly',
        '--mount', f'type=bind,source={source},target=/source,readonly',
        '--mount', f'type=bind,source={amtl},target=/amtl,readonly',
        '--mount', f'type=bind,source={output},target=/out', '--entrypoint', 'bash', args.builder_image,
        '/repo/tools/native/build-player-storage-probe.sh', '/source', '/amtl', '/repo/native/nvault-durability', '/out/build']
    result = subprocess.run(command, capture_output=True, timeout=180)
    (output / 'build.log').write_bytes(result.stdout + result.stderr)
    result.check_returncode()
    if any(native.digest(REPO / name) != h for name, h in hashes.items()):
        raise ValueError('Sources changed during build')
    package = output / 'package'
    package.mkdir()
    binaries = {}
    for version in ('old', 'new'):
        name = 'player-storage-' + version
        raw = (output / 'build' / name).read_bytes()
        if raw[:7] != b'\x7fELF\x01\x01\x01' or raw[18:20] != b'\x03\x00':
            raise ValueError('Linux i386 executable required')
        if any(x in raw for x in (b'NVAULT_FAIL', b'NVAULT_KILL', b'NVAULT_SLOW_IO', b'nvault_test_clock')):
            raise ValueError('Fault-hook executable rejected')
        (package / name).write_bytes(raw)
        binaries[name] = hashlib.sha256(raw).hexdigest()
    with zipfile.ZipFile(package / 'corresponding-source.zip', 'x', compression=zipfile.ZIP_DEFLATED) as archive:
        files = [(snapshot / name, name) for name in names]
        files += [(amxx_archive, 'upstream/' + amxx_archive.name), (amtl_archive, 'upstream/' + amtl_archive.name),
                  (source / 'public/licenses/LICENSE.txt', 'LICENSE.txt')]
        for path, name in files:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, path.read_bytes())
    manifest = dict(schema=1, target='linux-i386', builder=args.builder_image, upstream=native.LOCK,
        sources=hashes, payload=binaries, correspondingSourceSha256=native.digest(package / 'corresponding-source.zip'))
    (package / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(state='probe-built', payload=binaries, published=False)))


if __name__ == '__main__':
    main()
