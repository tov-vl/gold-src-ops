"""Exercise the bounded hosted-runner mirror transformation without host mutations."""
import importlib.util
from pathlib import Path
import unittest

SPEC = importlib.util.spec_from_file_location('browser_apt', Path(__file__).resolve().parents[1] / 'ci/prepare-browser-apt.py')
APT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(APT)
SOURCES = (b'Types: deb\nURIs: mirror+file:/etc/apt/apt-mirrors.txt\n'
           b'Suites: noble noble-updates noble-backports noble-security\n'
           b'Components: main universe restricted multiverse\n'
           b'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n')


class MirrorTests(unittest.TestCase):
    def test_removes_only_the_failed_cloud_mirror(self):
        self.assertEqual(APT.plan(APT.AZURE + APT.OFFICIAL, SOURCES), APT.OFFICIAL)

    def test_already_prepared_input_is_idempotent(self):
        self.assertEqual(APT.plan(APT.OFFICIAL, SOURCES), APT.OFFICIAL)

    def test_unknown_missing_duplicate_or_reordered_mirrors_fail_closed(self):
        for value in (b'', APT.AZURE, APT.OFFICIAL + APT.AZURE, APT.AZURE + APT.OFFICIAL + APT.OFFICIAL,
                      APT.OFFICIAL.replace(b'https:', b'http:'), APT.OFFICIAL + b'https://unknown.invalid/\n'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                APT.plan(value, SOURCES)

    def test_missing_mirror_transport_or_signing_key_fails_closed(self):
        for value in (b'', SOURCES.replace(b'mirror+file:', b'file:'), SOURCES.replace(b'ubuntu-archive-keyring.gpg', b'other.gpg')):
            with self.subTest(value=value), self.assertRaises(ValueError):
                APT.plan(APT.AZURE + APT.OFFICIAL, value)

    def test_workflow_keeps_all_browser_tests_and_prepares_before_install(self):
        workflow = (Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml').read_text(encoding='utf-8')
        browser = workflow.split('  browser-smoke:', 1)[1].split('  publish-image:', 1)[0]
        self.assertLess(browser.index('prepare-browser-apt.py'), browser.index('install --with-deps --only-shell chromium'))
        self.assertIn('--filter FullyQualifiedName~BrowserTokenBoundaryTests', browser)
        self.assertNotIn('continue-on-error', browser)


if __name__ == '__main__':
    unittest.main()
