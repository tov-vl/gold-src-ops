#!/usr/bin/env python3
"""Assemble verified existing CI bytes; never compile or publish a paired bundle."""
import argparse
import json
from pathlib import Path
import sys

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'ops/gameserver'))
import player_storage_pair as pair


def build(previous, previous_module, native, fixtures, output):
    old_manifest = pair.read(previous / 'manifest.json')
    old_item = dict(directory=str(previous), sha256=pair.digest(old_manifest))
    _, old = pair.package(old_item, False)
    stock = pair.read(previous_module, 16 * 1024 * 1024)
    native_raw = pair.read(native / 'manifest.json')
    fixture_raw = pair.read(fixtures / 'manifest.json')
    n, f = pair.decode(native_raw), pair.decode(fixture_raw)
    for name, expected in {**n['sources'], **f['sources']}.items():
        path = REPO / name
        pair.require(path.resolve().is_relative_to(REPO.resolve())
                     and pair.digest(pair.read(path, 16 * 1024 * 1024)) == expected, 'Source input drift.')
    files = dict(old)
    files[pair.PLUGIN] = pair.read(fixtures / 'goldsrcops_weapon_selection.amxx', 8 * 1024 * 1024)
    files[pair.MODULE] = pair.read(native / 'production/nvault_amxx_i386.so', 16 * 1024 * 1024)
    manifest = dict(schema=1, purpose='paired-player-storage', addonVersion='0.40.0', processContract=1,
        amxxVersion='1.10.0.5481', reApiVersion='5.24.0.300',
        predecessor=dict(addonManifestSha256=old_item['sha256'], moduleSha256=pair.digest(stock)),
        payload=[dict(path=p, sha256=pair.digest(files[p])) for p in pair.PATHS],
        nativeManifest=dict(path='proof/native-manifest.json', sha256=pair.digest(native_raw)),
        fixtureManifest=dict(path='proof/fixture-manifest.json', sha256=pair.digest(fixture_raw)))
    source = pair.read(native / 'corresponding-source.zip', 32 * 1024 * 1024)
    pair.require(not output.exists() and not any(p.is_symlink() for p in (output, *output.parents)))
    output.mkdir(mode=0o700)
    files.update({'proof/native-manifest.json': native_raw, 'proof/fixture-manifest.json': fixture_raw,
                  'proof/corresponding-source.zip': source,
                  'manifest.json': (json.dumps(manifest, indent=2) + '\n').encode()})
    for name, raw in files.items():
        target = output / name
        target.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
        with target.open('xb') as stream:
            target.chmod(0o600)
            stream.write(raw)
    item = dict(directory=str(output), sha256=pair.digest(files['manifest.json']))
    pair.package(item, True)
    return item


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--previous', type=Path, required=True)
    parser.add_argument('--previous-module', type=Path, required=True)
    parser.add_argument('--native-package', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    item = build(*(p.absolute() for p in (args.previous, args.previous_module, args.native_package,
                                         args.fixtures, args.output)))
    print(json.dumps(dict(state='assembled-and-verified', manifestSha256=item['sha256'], published=False)))


if __name__ == '__main__':
    main()
