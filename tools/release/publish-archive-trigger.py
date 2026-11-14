"""Publish one signed component tag, or promote exact candidate assets without rebuilding."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('archive_package', ROOT/'tools/release/build-archive-trigger.py')
package = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(package)
REPO = 'tov-vl/gold-src-ops'
VERSION = r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)'
TAG = re.compile(r'archive-trigger-v(' + VERSION + r')(-rc\.([1-9][0-9]*))?')
LIMIT = 16 * 1024 * 1024


def require(condition):
    if not condition:
        raise ValueError('Archive publication contract rejected')


def decode(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result)
            result[key] = value
        return result
    def invalid(_):
        raise ValueError('Invalid JSON number')
    return json.loads(raw, object_pairs_hook=unique, parse_constant=invalid)


def tag_parts(tag):
    match = TAG.fullmatch(tag)
    require(match is not None)
    return match[1], match[2] is not None


def promotion(annotation, version):
    candidates = re.findall(r'^Candidate: (\S+)$', annotation, re.MULTILINE)
    hashes = re.findall(r'^SHA256: ([a-f0-9]{64})$', annotation, re.MULTILINE)
    require(len(candidates) == len(hashes) == 1)
    base, candidate = tag_parts(candidates[0])
    require(candidate and base == version)
    return candidates[0], hashes[0]


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True, timeout=30).strip()


class GitHub:
    def command(self, *args):
        result = subprocess.run(['gh', *args], cwd=ROOT, stdout=subprocess.PIPE,
                                stderr=subprocess.DEVNULL, timeout=120, check=False)
        require(result.returncode == 0 and len(result.stdout) <= 1024 * 1024)
        return result.stdout

    def get(self, path, missing=False):
        require(path.startswith('repos/' + REPO + '/'))
        result = subprocess.run(['gh', 'api', '--method', 'GET', '--include', path],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=30, check=False)
        require(len(result.stdout) <= 1024 * 1024)
        header, separator, body = result.stdout.replace(b'\r\n', b'\n').partition(b'\n\n')
        status = header.split(b'\n', 1)[0].split()
        require(separator and len(status) >= 2)
        if status[1] == b'404' and missing:
            return None
        require(result.returncode == 0 and status[1] == b'200')
        return decode(body)

    def release(self, tag, missing=False):
        tag_parts(tag)
        # REST tag lookup excludes drafts; resolve their ID with a read-only query.
        query = ('query($tag:String!){repository(owner:"tov-vl",name:"gold-src-ops")'
                 '{release(tagName:$tag){databaseId}}}')
        document = decode(self.command('api', 'graphql', '-f', 'query='+query, '-f', 'tag='+tag))
        require(type(document) is dict and not document.get('errors'))
        repository = document.get('data', {}).get('repository')
        require(type(repository) is dict and 'release' in repository)
        release = repository['release']
        if release is None:
            require(missing)
            return None
        require(type(release) is dict and type(release.get('databaseId')) is int and release['databaseId'] > 0)
        result = self.get(f'repos/{REPO}/releases/{release["databaseId"]}')
        require(result.get('id') == release['databaseId'] and result.get('tag_name') == tag)
        return result

    def download(self, tag, target):
        self.command('release', 'download', tag, '--repo', REPO, '--dir', str(target),
                     '--pattern', 'archive-trigger-*.zip', '--pattern', 'archive-trigger-*.zip.sha256')


def verify_tag(tag, github):
    tag_parts(tag)
    ref = 'refs/tags/' + tag
    require(git('cat-file', '-t', ref) == 'tag')
    tag_sha = git('rev-parse', ref)
    revision = git('rev-parse', ref + '^{commit}')
    require(re.fullmatch('[a-f0-9]{40}', revision) is not None and revision == git('rev-parse', 'HEAD'))
    require(subprocess.run(['git', 'merge-base', '--is-ancestor', revision, 'origin/main'],
                           cwd=ROOT, check=False).returncode == 0)
    remote = github.get(f'repos/{REPO}/git/ref/tags/{tag}')
    require(remote.get('object') == {'type': 'tag', 'sha': tag_sha,
            'url': f'https://api.github.com/repos/{REPO}/git/tags/{tag_sha}'})
    signed = github.get(f'repos/{REPO}/git/tags/{tag_sha}')
    require(signed.get('tag') == tag and signed.get('verification', {}).get('verified') is True
            and signed.get('object', {}).get('type') == 'commit'
            and signed['object']['sha'] == revision)
    return revision, signed['message']


def verify_assets(directory, version, revision, expected=None):
    name = f'archive-trigger-{version}.zip'
    archive_path = Path(directory)/name
    checksum_path = Path(directory)/(name + '.sha256')
    require(set(p.name for p in Path(directory).iterdir()) == {name, name + '.sha256'})
    require(archive_path.is_file() and not archive_path.is_symlink() and 0 < archive_path.stat().st_size <= LIMIT)
    require(checksum_path.is_file() and not checksum_path.is_symlink() and checksum_path.stat().st_size <= 256)
    digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    require(checksum_path.read_text(encoding='ascii') == digest + '  ' + name + '\n')
    require(expected is None or digest == expected)
    with zipfile.ZipFile(archive_path) as archive:
        entries = archive.infolist()
        names = set(package.FILES) | {'config.template.json', 'manifest.json'}
        require(len(entries) == len(names) and set(archive.namelist()) == names)
        require(all(0 < item.file_size <= 524288 and item.external_attr >> 16 == 0o100644 for item in entries))
        manifest = decode(archive.read('manifest.json'))
        require(set(manifest) == {'schema', 'purpose', 'version', 'sourceRevision', 'reviewOnly',
                'workingTree', 'risk', 'files', 'runtimeAcceptance', 'credentialIncluded', 'automaticInstallation'})
        require(type(manifest['schema']) is int and manifest['schema'] == 1
                and manifest['purpose'] == 'archive-external-trigger' and manifest['version'] == version
                and manifest['sourceRevision'] == revision and manifest['risk'] == 'R3'
                and manifest['reviewOnly'] is False and manifest['workingTree'] is False
                and manifest['credentialIncluded'] is False and manifest['automaticInstallation'] is False
                and manifest['runtimeAcceptance'] == 'pending')
        require(type(manifest['files']) is list and len(manifest['files']) == len(names) - 1)
        require(all(type(item) is dict and set(item) == {'path', 'bytes', 'sha256'} for item in manifest['files']))
        require(set(item['path'] for item in manifest['files']) == names - {'manifest.json'})
        for item in manifest['files']:
            raw = archive.read(item['path'])
            require(type(item['bytes']) is int and len(raw) == item['bytes']
                    and hashlib.sha256(raw).hexdigest() == item['sha256'])
        for path in package.FILES:
            require(archive.read(path) == (ROOT/path).read_bytes().replace(b'\r\n', b'\n'))
        template = decode(archive.read('config.template.json'))
        require(template == dict(schema=1, mode='paused', generation='SET_AT_ROLLOUT',
                exporter='SET_REVIEWED_EXPORTER_PIN',
                manifest={path: hashlib.sha256(archive.read(path)).hexdigest() for path in package.FILES[:3]},
                activation='SET_AT_ROLLOUT', coverage_end='SET_FROM_VERIFIED_ARCHIVE_RECEIPT', assessment_closed=False))
    return archive_path, checksum_path, digest


def release_assets(release, tag, candidate, assets):
    require(type(release) is dict and release.get('tag_name') == tag
            and release.get('prerelease') is candidate)
    items = release.get('assets')
    require(type(items) is list and len(items) == 2)
    expected = {path.name: path for path in assets}
    require(set(item['name'] for item in items) == set(expected))
    for item in items:
        path = expected[item['name']]
        require(item.get('state') == 'uploaded' and item.get('size') == path.stat().st_size
                and item.get('digest') == 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest())


def publish(tag, output, github=None):
    github = github or GitHub()
    version, candidate = tag_parts(tag)
    revision, annotation = verify_tag(tag, github)
    require(not git('status', '--porcelain'))
    require(github.release(tag, missing=True) is None)
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    assets_dir = output/'assets'
    if candidate:
        package.build(assets_dir, version)
        expected = None
        candidate_tag = tag
    else:
        candidate_tag, expected = promotion(annotation, version)
        candidate_revision, _ = verify_tag(candidate_tag, github)
        require(candidate_revision == revision)
        release = github.release(candidate_tag)
        require(release.get('draft') is False and release.get('prerelease') is True)
        assets_dir.mkdir()
        github.download(candidate_tag, assets_dir)
    archive, checksum, digest = verify_assets(assets_dir, version, revision, expected)
    if not candidate:
        release_assets(release, candidate_tag, True, (archive, checksum))
    notes = output/'release-notes.md'
    notes.write_text(f'Archive trigger {version}\n\nSource: `{revision}`.\n\n'
        f'Package SHA-256: `{digest}`.\n\nCandidate: `{candidate_tag}`.\n\n'
        'R3 runtime acceptance is recorded separately. Publication does not install or activate the service.\n', encoding='utf-8')
    args = ['release', 'create', tag, str(archive), str(checksum), '--repo', REPO,
            '--verify-tag', '--draft', '--latest=false', '--title', tag, '--notes-file', str(notes)]
    if candidate:
        args.append('--prerelease')
    github.command(*args)
    draft = github.release(tag)
    require(draft.get('draft') is True)
    release_assets(draft, tag, candidate, (archive, checksum))
    with tempfile.TemporaryDirectory() as check_dir:
        github.download(tag, check_dir)
        verify_assets(check_dir, version, revision, digest)
    # One publish attempt. Any failure leaves the draft/publication for explicit reconciliation.
    github.command('release', 'edit', tag, '--repo', REPO, '--draft=false', '--latest=false')
    published = github.release(tag)
    require(published.get('draft') is False)
    release_assets(published, tag, candidate, (archive, checksum))
    receipt = dict(tag=tag, candidate=candidate_tag, sourceRevision=revision, sha256=digest,
                   mode='build' if candidate else 'promote', providerImmutable=published.get('immutable') is True)
    (output/'publication.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
    return receipt


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--tag', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(publish(args.tag, args.output), sort_keys=True))
    except Exception:
        raise SystemExit('Archive publication stopped; preserve state and reconcile before another write.') from None
