#!/usr/bin/env python3
"""Build a source-bound, reproducible Linux i386 nVault module in isolation."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tarfile
import urllib.request
import uuid
import zipfile

COMPONENT = Path(__file__).resolve().parent
REPO = COMPONENT.parents[1]
LOCK = json.loads((COMPONENT / 'source.lock.json').read_bytes())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def identity():
    return {str(p.relative_to(REPO)).replace('\\', '/'): digest(p)
            for p in sorted(COMPONENT.rglob('*')) if p.is_file() and not p.is_symlink()
            and (p.suffix in ('.cpp', '.h', '.py', '.sh', '.json') or p.name in ('Dockerfile', 'README.md'))}


def command(arguments, timeout=180):
    return subprocess.run(arguments, check=True, timeout=timeout, capture_output=True, text=True).stdout


def fetch(item, cache, output):
    name, revision = item['repository'], item['revision']
    archive = cache / (name + '-' + revision + '.tar.gz')
    if not archive.exists():
        temporary = archive.with_name(archive.name + '.partial-' + uuid.uuid4().hex)
        try:
            url = 'https://codeload.github.com/alliedmodders/' + name + '/tar.gz/' + revision
            with urllib.request.urlopen(url, timeout=90) as response, temporary.open('xb') as stream:
                if not response.url.startswith('https://codeload.github.com/'):
                    raise ValueError('Unexpected source redirect')
                size = 0
                while block := response.read(1024*1024):
                    size += len(block)
                    if size > 32*1024*1024:
                        raise ValueError('Source archive exceeds limit')
                    stream.write(block)
            if digest(temporary) != item['sha256']:
                raise ValueError('Source digest mismatch')
            temporary.rename(archive)
        finally:
            temporary.unlink(missing_ok=True)
    if archive.is_symlink() or digest(archive) != item['sha256']:
        raise ValueError('Cached source digest mismatch')
    prefix = name + '-' + revision
    with tarfile.open(archive) as source:
        members = source.getmembers()
        if len(members) > 10000 or sum(m.size for m in members) > 128*1024*1024:
            raise ValueError('Extracted source exceeds limit')
        for member in members:
            if (member.issym() or member.islnk() or not (member.name == prefix or member.name.startswith(prefix + '/'))
                    or not (output / member.name).resolve().is_relative_to(output.resolve())):
                raise ValueError('Invalid source archive member')
        source.extractall(output, filter='data')
    return output / prefix, archive


def docker_run(image, source, amtl, output, script, arguments, tests=False):
    args = ['docker', 'run', '--rm', '--network', 'none', '--cpus', '2', '--memory', '1g']
    if tests:
        args += ['--tmpfs', '/space:rw,size=64k,mode=1777']
    args += ['--mount', f'type=bind,source={source},target=/source,readonly',
             '--mount', f'type=bind,source={amtl},target=/amtl,readonly',
             '--mount', f'type=bind,source={output / "component-source"},target=/repo,readonly',
             '--mount', f'type=bind,source={output},target=/out', image,
             'bash', '/repo/native/nvault-durability/' + script, *arguments]
    command(args, timeout=300)


def verify(package):
    manifest = json.loads((package / 'manifest.json').read_bytes())
    binary = package / 'production/nvault_amxx_i386.so'
    raw = binary.read_bytes()
    if (manifest['sources'] != identity() or manifest['upstream'] != LOCK or digest(binary) != manifest['sha256']
            or raw[:7] != b'\x7fELF\x01\x01\x01' or raw[18:20] != b'\x03\x00'
            or any(value in raw for value in (b'NVAULT_FAIL', b'NVAULT_KILL', b'NVAULT_SLOW_IO', b'nvault_test_clock', b'nvault_test_failure'))):
        raise ValueError('Module identity, ABI or production-build mismatch')
    if digest(package / 'corresponding-source.zip') != manifest['correspondingSourceSha256']:
        raise ValueError('Corresponding source package mismatch')
    testing = package / 'testing/nvault_amxx_i386.so'
    test_bytes = testing.read_bytes()
    if digest(testing) != manifest['testingSha256'] or b'nvault_test_clock' not in test_bytes or b'NVAULT_FAIL' not in test_bytes:
        raise ValueError('Fault/timing module identity mismatch')
    return manifest


def build(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    before = identity()
    snapshot = output / 'component-source'
    for path in before:
        target = snapshot / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((REPO / path).read_bytes())
    cache = args.cache.resolve()
    cache.mkdir(parents=True, exist_ok=True)
    source, amxx_archive = fetch(LOCK['amxx'], cache, output)
    amtl, amtl_archive = fetch(LOCK['amtl'], cache, output)
    image = args.builder_image
    if args.build_builder:
        tag = 'goldsrcops-nvault-build-' + uuid.uuid4().hex
        command(['docker', 'build', '--tag', tag, str(snapshot / 'native/nvault-durability')], timeout=600)
        image = json.loads(command(['docker', 'image', 'inspect', tag]))[0]['Id']
    if not re.fullmatch(r'(?:[a-z0-9./_-]+@)?sha256:[0-9a-f]{64}', image):
        raise ValueError('Builder identity must be immutable')
    for name in ('upstream-a', 'upstream-b', 'build-a', 'build-b', 'build-testing'):
        parameters = ['/source', '/amtl', '/out/' + name]
        if name.startswith('build-'):
            parameters.append('/repo/native/nvault-durability')
        if name == 'build-testing':
            parameters.append('testing')
        docker_run(image, source, amtl, output, 'build.sh', parameters)
    original = digest(output / 'upstream-a/nvault_amxx_i386.so')
    if original != digest(output / 'upstream-b/nvault_amxx_i386.so') or original != LOCK['upstreamRebuildSha256']:
        raise ValueError('Original source build gate drifted')
    binary = output / 'build-a/nvault_amxx_i386.so'
    if binary.read_bytes() != (output / 'build-b/nvault_amxx_i386.so').read_bytes():
        raise ValueError('Patched module is not reproducible')
    packages = (output / 'build-a/packages.txt').read_text()
    for name, version in LOCK['compilerPackages'].items():
        if name + '\t' + version + '\n' not in packages:
            raise ValueError('Compiler package version mismatch')
    if 'libstdc++.so' in (output / 'build-a/elf-dynamic.txt').read_text():
        raise ValueError('Module must not replace or depend on the engine C++ runtime')
    docker_run(image, source, amtl, output, 'tests/run.sh', ['/source', '/amtl', '/out/tests'], tests=True)
    for mode in ('plain', 'asan'):
        if 'NVAULT_STORAGE_TESTS=passed' not in (output / ('tests/' + mode + '.log')).read_text():
            raise ValueError('Missing native storage acceptance')
    if identity() != before:
        raise ValueError('Component source changed during build')
    package = output / 'package'
    (package / 'production').mkdir(parents=True)
    (package / 'production/nvault_amxx_i386.so').write_bytes(binary.read_bytes())
    (package / 'testing').mkdir()
    testing = output / 'build-testing/nvault_amxx_i386.so'
    (package / 'testing/nvault_amxx_i386.so').write_bytes(testing.read_bytes())
    with zipfile.ZipFile(package / 'corresponding-source.zip', 'x', compression=zipfile.ZIP_DEFLATED) as archive:
        paths = [(amxx_archive, 'upstream/' + amxx_archive.name), (amtl_archive, 'upstream/' + amtl_archive.name)]
        paths += [(REPO / path, path) for path in before]
        paths += [(source / 'public/licenses/LICENSE.txt', 'LICENSE.txt')]
        for path, name in paths:
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, path.read_bytes())
    manifest = dict(schema=1, sources=before, upstream=LOCK, sha256=digest(binary), builder=image,
                    testingSha256=digest(testing),
                    correspondingSourceSha256=digest(package / 'corresponding-source.zip'),
                    packagesSha256=digest(output / 'build-a/packages.txt'), reproducible=True,
                    nativeTests='plain-and-address-undefined-sanitizers', productionChanged=False)
    (package / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    verify(package)
    print(json.dumps(dict(status='module-built-and-verified', sha256=manifest['sha256'])), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--verify', type=Path)
    parser.add_argument('--cache', type=Path)
    parser.add_argument('--output', type=Path)
    group = parser.add_mutually_exclusive_group()
    group.add_argument('--builder-image')
    group.add_argument('--build-builder', action='store_true')
    args = parser.parse_args()
    if args.verify:
        verify(args.verify.resolve())
        print('NVAULT_PACKAGE=verified')
    else:
        if not args.cache or not args.output or not (args.build_builder or args.builder_image):
            parser.error('Build requires cache/output and an immutable builder or --build-builder')
        build(args)


if __name__ == '__main__':
    main()
