"""Offline publication faults and byte-preserving promotion; never calls GitHub."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('publication', ROOT/'tools/release/publish-archive-trigger.py')
publication = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publication)
REVISION = 'a' * 40
CANDIDATE = 'archive-trigger-v2.132.0-rc.1'
STABLE = 'archive-trigger-v2.132.0'


class Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.identity = patch.object(publication.package.subprocess, 'check_output', side_effect=self.git_identity)
        self.identity.start()

    def tearDown(self):
        self.identity.stop()
        self.temp.cleanup()

    @staticmethod
    def git_identity(args, **_):
        if args[1] == 'rev-parse':
            return REVISION
        if args[1] == 'status':
            return b''
        raise AssertionError(args)

    def package(self, directory='source'):
        path, digest = publication.package.build(self.root/directory, '2.132.0')
        return path.parent, digest

    def test_strict_tag_contract(self):
        self.assertEqual(publication.tag_parts(CANDIDATE), ('2.132.0', True))
        self.assertEqual(publication.tag_parts(STABLE), ('2.132.0', False))
        for tag in ('v2.132.0', CANDIDATE+'extra', 'archive-trigger-v02.132.0',
                    STABLE+'-rc.0', STABLE+'-rc.01', STABLE+'+other', STABLE+'\n'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                publication.tag_parts(tag)

    def test_promotion_requires_exact_signed_annotation(self):
        text = f'Candidate: {CANDIDATE}\nSHA256: {"b" * 64}\n'
        self.assertEqual(publication.promotion(text, '2.132.0'), (CANDIDATE, 'b' * 64))
        for invalid in ('', text+text, text.replace('2.132.0', '2.133.0'),
                        text.replace('-rc.1', ''), text.replace('b'*64, 'B'*64)):
            with self.subTest(text=invalid), self.assertRaises(ValueError):
                publication.promotion(invalid, '2.132.0')

    def test_read_adapter_only_accepts_200_or_explicit_404(self):
        client = publication.GitHub()
        path = f'repos/{publication.REPO}/releases/tags/{CANDIDATE}'
        for header, code, body, missing, expected in (
                ('200 OK', 0, '{"draft":false}', False, {'draft': False}),
                ('404 Not Found', 1, '{}', True, None)):
            result = subprocess.CompletedProcess([], code, ('HTTP/2.0 '+header+'\r\nHeader: x\r\n\r\n'+body).encode())
            with patch.object(publication.subprocess, 'run', return_value=result) as run:
                self.assertEqual(client.get(path, missing), expected)
                self.assertIn('GET', run.call_args.args[0])
        for header in ('403 Forbidden', '500 Server Error', '302 Found'):
            result = subprocess.CompletedProcess([], 1, ('HTTP/2.0 '+header+'\n\n{}').encode())
            with patch.object(publication.subprocess, 'run', return_value=result), self.assertRaises(ValueError):
                client.get(path, missing=True)

    def test_draft_and_published_release_lookup_resolves_id(self):
        client = publication.GitHub()
        for draft in (True, False):
            response = dict(id=123, tag_name=CANDIDATE, draft=draft)
            with patch.object(client, 'command', return_value=b'{"data":{"repository":{"release":{"databaseId":123}}}}') as query, \
                    patch.object(client, 'get', return_value=response) as get:
                self.assertEqual(client.release(CANDIDATE), response)
                self.assertEqual(get.call_args.args[0], f'repos/{publication.REPO}/releases/123')
                self.assertIn('graphql', query.call_args.args)
                self.assertIn('query=', query.call_args.args[3])
                self.assertNotIn('mutation', query.call_args.args[3])

    def test_missing_release_is_distinct_from_query_or_permission_failure(self):
        client = publication.GitHub()
        with patch.object(client, 'command', return_value=b'{"data":{"repository":{"release":null}}}'):
            self.assertIsNone(client.release(CANDIDATE, missing=True))
            with self.assertRaises(ValueError):
                client.release(CANDIDATE)
        for raw in (b'{"errors":[{"message":"forbidden"}]}', b'{"data":{"repository":null}}',
                    b'{"data":{"repository":{"release":{"databaseId":true}}}}'):
            with patch.object(client, 'command', return_value=raw), self.assertRaises(ValueError):
                client.release(CANDIDATE, missing=True)
        with patch.object(client, 'command', side_effect=ValueError('transport failure')), self.assertRaises(ValueError):
            client.release(CANDIDATE, missing=True)

    def test_signed_annotated_main_tag_is_required(self):
        tag_sha = 'b'*40
        def git(*args):
            if args[0] == 'cat-file':
                return 'tag'
            return tag_sha if args[1] == 'refs/tags/'+CANDIDATE else REVISION
        class Tags:
            verified = True
            remote_sha = tag_sha
            def get(self, path):
                if '/git/ref/' in path:
                    return {'object': {'type': 'tag', 'sha': self.remote_sha,
                        'url': f'https://api.github.com/repos/{publication.REPO}/git/tags/{self.remote_sha}'}}
                return {'tag': CANDIDATE, 'verification': {'verified': self.verified},
                        'object': {'type': 'commit', 'sha': REVISION}, 'message': 'candidate'}
        tags = Tags()
        with patch.object(publication, 'git', side_effect=git), patch.object(publication.subprocess, 'run',
                return_value=subprocess.CompletedProcess([], 0)):
            self.assertEqual(publication.verify_tag(CANDIDATE, tags), (REVISION, 'candidate'))
            tags.verified = False
            with self.assertRaises(ValueError):
                publication.verify_tag(CANDIDATE, tags)
            tags.verified = True
            tags.remote_sha = 'c'*40
            with self.assertRaises(ValueError):
                publication.verify_tag(CANDIDATE, tags)
        with patch.object(publication, 'git', return_value='commit'), self.assertRaises(ValueError):
            publication.verify_tag(CANDIDATE, tags)
        with patch.object(publication, 'git', side_effect=git), patch.object(publication.subprocess, 'run',
                return_value=subprocess.CompletedProcess([], 1)), self.assertRaises(ValueError):
            publication.verify_tag(CANDIDATE, tags)

    def test_clean_package_verified_and_review_rejected(self):
        directory, digest = self.package()
        self.assertEqual(publication.verify_assets(directory, '2.132.0', REVISION)[2], digest)
        for version, revision, expected in (('2.133.0', REVISION, digest),
                ('2.132.0', 'b'*40, digest), ('2.132.0', REVISION, 'b'*64)):
            with self.subTest(version=version, revision=revision), self.assertRaises((ValueError, FileNotFoundError)):
                publication.verify_assets(directory, version, revision, expected)
        self.rewrite(directory, lambda files: files.update({'manifest.json': json.dumps(
            dict(json.loads(files['manifest.json']), reviewOnly=True)).encode()}))
        with self.assertRaises(ValueError):
            publication.verify_assets(directory, '2.132.0', REVISION)

    @staticmethod
    def rewrite(directory, change):
        path = directory/'archive-trigger-2.132.0.zip'
        with zipfile.ZipFile(path) as archive:
            files = {name: archive.read(name) for name in archive.namelist()}
        change(files)
        with zipfile.ZipFile(path, 'w') as archive:
            for name, raw in files.items():
                info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                info.external_attr = 0o100644 << 16
                archive.writestr(info, raw)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        Path(str(path)+'.sha256').write_text(digest+'  '+path.name+'\n', encoding='ascii')

    def test_bad_payload_is_rejected_even_with_updated_outer_hash(self):
        for index, change in enumerate((
                lambda files: files.update({'../../unexpected': b'x'}),
                lambda files: files.update({'ops/availability/archive_trigger_service.py': b'changed'}),
                lambda files: files.update({'manifest.json': b'{"schema":1,"schema":1}'}))):
            directory, _ = self.package('payload-'+str(index))
            self.rewrite(directory, change)
            with self.subTest(index=index), self.assertRaises(ValueError):
                publication.verify_assets(directory, '2.132.0', REVISION)

    def fake(self):
        test = self
        class Fake:
            def __init__(self):
                self.records = {}
                self.files = {}
                self.commands = []
                self.fail_publish = False

            def release(self, tag, missing=False):
                return copy.deepcopy(self.records.get(tag))

            def command(self, *args):
                self.commands.append(args)
                tag = args[2]
                if args[1] == 'create':
                    test.assertNotIn(tag, self.records)
                    self.files[tag] = {Path(path).name: Path(path).read_bytes() for path in args[3:5]}
                    self.records[tag] = dict(tag_name=tag, draft=True, prerelease='--prerelease' in args,
                        immutable=False, assets=[dict(name=name, state='uploaded', size=len(raw),
                        digest='sha256:'+hashlib.sha256(raw).hexdigest()) for name, raw in self.files[tag].items()])
                elif args[1] == 'edit':
                    if self.fail_publish:
                        raise TimeoutError('uncertain publish')
                    self.records[tag]['draft'] = False
                else:
                    raise AssertionError(args)
                return b''

            def download(self, tag, target):
                for name, raw in self.files[tag].items():
                    (Path(target)/name).write_bytes(raw)
        return Fake()

    def run_publication(self, tag, fake, directory, annotation=''):
        with patch.object(publication, 'verify_tag', return_value=(REVISION, annotation)):
            return publication.publish(tag, self.root/directory, fake)

    def test_candidate_and_stable_reuse_exact_bytes_without_building(self):
        fake = self.fake()
        candidate = self.run_publication(CANDIDATE, fake, 'candidate')
        annotation = f'Candidate: {CANDIDATE}\nSHA256: {candidate["sha256"]}\n'
        with patch.object(publication.package, 'build', side_effect=AssertionError('stable rebuild')):
            stable = self.run_publication(STABLE, fake, 'stable', annotation)
        self.assertEqual(candidate['sha256'], stable['sha256'])
        self.assertEqual(stable['mode'], 'promote')
        self.assertEqual(fake.files[CANDIDATE], fake.files[STABLE])
        self.assertEqual([command[1] for command in fake.commands], ['create', 'edit', 'create', 'edit'])
        self.assertFalse(stable['providerImmutable'])

    def test_existing_release_stops_before_any_write(self):
        fake = self.fake()
        fake.records[CANDIDATE] = {'draft': True}
        with self.assertRaises(ValueError):
            self.run_publication(CANDIDATE, fake, 'existing')
        self.assertEqual(fake.commands, [])

    def test_uncertain_publication_preserves_draft_and_is_not_retried(self):
        fake = self.fake()
        fake.fail_publish = True
        with self.assertRaises(TimeoutError):
            self.run_publication(CANDIDATE, fake, 'uncertain')
        self.assertTrue(fake.records[CANDIDATE]['draft'])
        self.assertEqual(len(fake.commands), 2)
        with self.assertRaises(ValueError):
            self.run_publication(CANDIDATE, fake, 'no-retry')
        self.assertEqual(len(fake.commands), 2)

    def test_tampered_candidate_rejected_before_stable_creation(self):
        fake = self.fake()
        candidate = self.run_publication(CANDIDATE, fake, 'candidate')
        name = 'archive-trigger-2.132.0.zip'
        fake.files[CANDIDATE][name] += b'x'
        annotation = f'Candidate: {CANDIDATE}\nSHA256: {candidate["sha256"]}\n'
        with self.assertRaises(ValueError):
            self.run_publication(STABLE, fake, 'tampered', annotation)
        self.assertNotIn(STABLE, fake.records)

    def test_workflow_component_uses_full_gates_and_separate_tag_prefix(self):
        workflow = (ROOT/'.github/workflows/ci.yml').read_text()
        self.assertIn('- "archive-trigger-v*"', workflow)
        job = workflow.split('  publish-archive-trigger:', 1)[1].split('  publish-image:', 1)[0]
        for dependency in ('quality', 'client-lab', 'container-smoke', 'browser-smoke'):
            self.assertIn('      - '+dependency, job)
        self.assertIn("startsWith(github.ref, 'refs/tags/archive-trigger-v')", job)
        self.assertIn('cancel-in-progress: false', job)
        self.assertNotIn('workflow_dispatch', job)
        self.assertNotIn('packages: write', job)


if __name__ == '__main__':
    unittest.main()
