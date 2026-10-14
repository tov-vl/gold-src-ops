#!/usr/bin/env python3
"""Acceptance boundaries: no local RCON, no replay, bounded and sanitized evidence."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/release'))
import acceptance_checks as checks
spec = importlib.util.spec_from_file_location('coordinator', ROOT / 'tools/release/check-game-acceptance.py')
coordinator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coordinator)
spec = importlib.util.spec_from_file_location('fixture_probe', ROOT / 'ops/gameserver/addon-runtime-probe.py')
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


def profile(config):
    return dict(schema=1, sshConfig=str(config), gameAlias='game-fixture', controlPlaneAlias='control-fixture',
                host='192.0.2.10', port=27015, expectedName='Fixture', version='0.25.0',
                manifestSha256='a' * 64, baselineSha256='b' * 64)


class AcceptanceTests(unittest.TestCase):
    def test_fixture_gate_refuses_managed_host_even_with_opt_in(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name); container = root / 'container'; managed = root / 'managed'
            container.touch()
            with self.assertRaises(ValueError): fixture.require_isolated_fixture(False, container, managed)
            fixture.require_isolated_fixture(True, container, managed)
            managed.mkdir()
            with self.assertRaises(ValueError): fixture.require_isolated_fixture(True, container, managed)
            with self.assertRaises(ValueError): fixture.require_isolated_fixture(True, root / 'absent', root / 'absent')

    def test_cli_refuses_before_reading_password_or_contacting_server(self):
        args = ['probe', '--expectations', '/absent', '--expectations-sha256', 'a' * 64,
                '--error-directory', '/absent', '--port', '27015', 'weapon-selection', 'b' * 64]
        with patch.object(sys, 'argv', args), patch.object(fixture, 'read_password') as secret, patch.object(fixture, 'query') as query:
            with self.assertRaises(ValueError): fixture.main()
            secret.assert_not_called(); query.assert_not_called()

    def test_remote_guard_rejects_loopback_local_public_ip_and_managed_host(self):
        for host, addresses, managed in [('127.0.0.1', set(), False), ('192.0.2.10', {'192.0.2.10'}, False),
                                          ('192.0.2.10', set(), True), ('0.0.0.0', set(), False)]:
            with self.subTest(host=host, managed=managed), self.assertRaises(ValueError):
                checks.refuse_local_rcon(host, addresses, managed)
        checks.refuse_local_rcon('192.0.2.10', {'192.0.2.20'}, False)

    def test_control_guard_runs_before_secret_or_network(self):
        local = [{'addr_info': [{'family': 'inet', 'local': '192.0.2.10'}]}]
        with patch.object(checks, 'run', return_value=json.dumps(local).encode()), patch.object(checks, 'trusted') as secret, \
             patch.object(checks.urllib.request, 'urlopen') as http:
            with self.assertRaises(ValueError): checks.control_plane(profile(Path('/fixture')), fixture)
            secret.assert_not_called(); http.assert_not_called()

    def test_lookup_rejects_stale_mixed_partial_and_busy_responses(self):
        nonce = 'a' * 16
        good = f'GSLOOKUP 1 {nonce} 1000 0 0\nGSPEND 1 {nonce} 1000 0 0\n'
        checks.empty_lookup(good, nonce, 1001)
        for value, now in [(good, 1031), (good, 990), (good.replace('GSPEND', 'GSLOOKUP'), 1001),
                           (good.replace('1000 0 0\n', '1001 0 0\n', 1), 1001),
                           (good + 'unexpected\n', 1001), (f'GSLOOKUP 1 {nonce} busy 10', 1001)]:
            with self.subTest(value=value), self.assertRaises(ValueError): checks.empty_lookup(value, nonce, now)
        with self.assertRaises(ValueError): checks.empty_lookup(good, 'b' * 16, 1001)

    def test_baseline_allows_identical_repeated_lines_but_rejects_conflicts(self):
        line = 'a' * 64 + '  /fixture/config\n'
        self.assertEqual({'/fixture/config': 'a' * 64}, checks.baseline_entries((line + line).encode()))
        for raw in [(line + line.replace('a' * 64, 'b' * 64)).encode(), b'invalid', (line * 251).encode()]:
            with self.assertRaises(ValueError): checks.baseline_entries(raw)

    def test_mount_order_is_irrelevant_but_drift_and_duplicates_are_not(self):
        a = {'Destination': '/a', 'Source': '/fixture/a', 'RW': False}
        b = {'Destination': '/b', 'Source': '/fixture/b', 'RW': True}
        self.assertEqual(checks.canonical_mounts([a, b]), checks.canonical_mounts([dict(reversed(list(b.items()))), a]))
        for changed in [[a], [a, a, b], [a, {**b, 'RW': False}], [a, {**b, 'Source': '/other'}]]:
            self.assertNotEqual(checks.canonical_mounts([a, b]), checks.canonical_mounts(changed))

    def test_dry_run_no_network_and_tamper_refused(self):
        with tempfile.TemporaryDirectory() as name:
            p = Path(name) / 'profile.json'; p.write_text(json.dumps(profile(Path(name) / 'ssh')))
            args = ['check', '--profile', str(p), '--profile-sha256', hashlib.sha256(p.read_bytes()).hexdigest()]
            with patch.object(sys, 'argv', args), patch.object(coordinator, 'invoke') as invoke, patch('sys.stdout', new=io.StringIO()) as out:
                coordinator.main(); invoke.assert_not_called()
                self.assertEqual(json.loads(out.getvalue())['result'], 'validated-plan')
                p.write_text(p.read_text() + ' ')
                with self.assertRaises(ValueError): coordinator.main()
                invoke.assert_not_called()

    def test_control_failure_is_not_retried_and_runtime_drift_refuses(self):
        with tempfile.TemporaryDirectory() as name:
            p = Path(name) / 'profile.json'; p.write_text(json.dumps(profile(Path(name) / 'ssh')))
            args = ['check', '--profile', str(p), '--profile-sha256', hashlib.sha256(p.read_bytes()).hexdigest(), '--execute']
            game = {'units': {'game': 'before'}, 'baselineSha256': 'b' * 64}
            with patch.object(sys, 'argv', args), patch.object(coordinator, 'invoke', side_effect=[game, ValueError('fixture')]) as invoke:
                with self.assertRaises(ValueError): coordinator.main()
                self.assertEqual(['game-host', 'control-plane'], [c.args[0] for c in invoke.call_args_list])
            with patch.object(sys, 'argv', args), patch.object(coordinator, 'invoke', side_effect=[game, {}, {**game, 'units': {'game': 'after'}}]):
                with self.assertRaises(ValueError): coordinator.main()


if __name__ == '__main__': unittest.main(verbosity=2)
