#!/usr/bin/env python3
"""Check both closed-format directions through the existing backup transport."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build-output', required=True, type=Path)
    parser.add_argument('--builder-image', required=True)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', args.builder_image):
        parser.error('Immutable builder image required')
    spec = importlib.util.spec_from_file_location('player_bundle', ROOT / 'ops/gameserver/player-data-bundle.py')
    bundle = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(bundle)
    output = args.build_output.resolve()
    evidence = args.output.resolve() if args.output else output / 'compatibility'
    if evidence.parent != output:
        parser.error('Compatibility output must be a direct build-output child')
    evidence.mkdir(exist_ok=False)

    def run(version, mode, directory):
        command = ['docker', 'run', '--rm', '--network', 'none', '--cpus', '1', '--memory', '256m',
                   '--mount', f'type=bind,source={output},target=/out']
        # Native checkpoints stay 0600. Match their owner to the host reader.
        if os.name != 'nt':
            command += ['--user', f'{os.getuid()}:{os.getgid()}']
        command += [args.builder_image,
                   '/out/tests/compatibility-' + version, mode, '/out/' + evidence.name + '/' + directory]
        result = subprocess.run(command, timeout=30, capture_output=True, text=True)
        (evidence / (directory + '-' + version + '-' + mode + '.log')).write_text(
            result.stdout + result.stderr, encoding='utf-8')
        result.check_returncode()
        if result.stdout.strip() != 'NVAULT_CLOSED_FORMAT=passed':
            raise ValueError('Missing closed-format acceptance marker')

    results = []
    for writer, reader in (('old', 'new'), ('new', 'old')):
        name = writer + '-to-' + reader
        original = evidence / (name + '-original')
        original.mkdir()
        run(writer, 'seed', original.name)
        before = bundle.inventory(original)
        if any(before[n] is not None for n in bundle.NAMES if n.endswith('.journal')):
            raise ValueError('Closed format must not leave a journal')
        archive = bundle.create(original)
        bundle.inspect(archive)
        bundle.write_new(evidence / (name + '.tar'), archive)
        restored = evidence / (name + '-restored')
        bundle.restore(archive, restored)
        if bundle.inventory(restored) != before:
            raise ValueError('Backup transport changed bytes or absent-journal state')
        run(reader, 'read', restored.name)
        if bundle.inventory(original) != before:
            raise ValueError('Original data changed during restore acceptance')
        # Reopen with the writer too: the reader's checkpoint remains compatible.
        run(writer, 'read', restored.name)
        results.append(dict(direction=name, result='passed', archiveSha256=hashlib.sha256(archive).hexdigest(),
                            vaults=2, entries=16, timestamps='preserved', absentJournals='preserved', originals='unchanged'))
    (evidence / 'result.json').write_text(json.dumps(dict(result='passed', directions=results,
        network='none', publishedPorts=0, productionChanged=False), indent=2) + '\n', encoding='utf-8')
    print('NVAULT_BACKUP_COMPATIBILITY=passed directions=2')


if __name__ == '__main__':
    main()
