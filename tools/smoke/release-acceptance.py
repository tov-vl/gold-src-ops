#!/usr/bin/env python3
"""Acceptance boundaries: no local RCON, no replay, bounded and sanitized evidence."""
import copy
from contextlib import contextmanager
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import subprocess
import time
import unittest
from unittest.mock import Mock, patch

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


@contextmanager
def control_fixture(version='0.26.0', after=None, lookup=None, env=None):
    target = profile(Path('/fixture'))
    target['version'] = version
    container = {
        'Config': {'Env': env if env is not None else [
            'PublicServerJoin__Host=192.0.2.10', 'PublicServerJoin__Port=27015']},
        'State': {'Running': True, 'StartedAt': 'fixture-start'},
        'RestartCount': 0, 'Image': 'fixture-image',
        'Mounts': [{'Destination': '/run/secrets/rcon-password', 'Source': '/fixture/secret',
                    'Type': 'bind', 'RW': False}],
    }
    changed = copy.deepcopy(container)
    if after: after(changed)
    inspections = iter([container, changed])

    def command(args, **kwargs):
        if args[0] == 'ip':
            return b'[{"addr_info":[{"family":"inet","local":"192.0.2.20"}]}]'
        if args[:2] == ['docker', 'ps']: return b'fixture-api'
        if args[:2] == ['docker', 'inspect']: return json.dumps([next(inspections)]).encode()
        raise AssertionError('Unexpected subprocess')

    def query(address, password, command):
        if command == fixture.COMMANDS[0]:
            return (f'WEAPON_SELECTION_STATUS version={version} enabled=1 persistent_stats=1\n'
                    f'PLAYER_PREFERENCES_STATUS version={version} storage=nvault errors=0')
        if command == fixture.COMMANDS[1]:
            return (f'PLAYER_STATS_STATUS version={version} storage=nvault errors=0\n'
                    f'PLAYER_LEADERBOARD_STATUS version={version} ready=1 errors=0')
        nonce = command.split()[1]; now = int(time.time())
        if lookup: return lookup(nonce, now)
        return f'GSLOOKUP 1 {nonce} {now} 0 0\nGSPEND 1 {nonce} {now} 0 0'

    protocol = Mock(COMMANDS=fixture.COMMANDS, read_password=fixture.read_password,
                    verify=fixture.verify, query=Mock(side_effect=query))
    with patch.object(checks, 'run', side_effect=command) as run, \
         patch.object(checks.Path, 'exists', return_value=False), \
         patch.object(checks.Path, 'is_symlink', return_value=False), \
         patch.object(checks, 'trusted', return_value=b'fixture-password') as secret, \
         patch.object(checks.urllib.request, 'urlopen') as http:
        http.return_value.__enter__.return_value.read.return_value = b'{"state":"offline"}'
        yield target, protocol, run, secret, http


class AcceptanceTests(unittest.TestCase):
    def test_restart_poll_lag_blocks_final_acceptance_but_not_live_hook(self):
        # The same offline public projection spans both candidate and rollback hooks.
        for version in ('0.25.0', '0.26.0'):
            with self.subTest(version=version), control_fixture(version) as (target, protocol, run, secret, http):
                with self.assertRaises(checks.CheckFailure) as failure:
                    checks.control_plane(target, protocol)
                self.assertEqual('public_online_state', failure.exception.check)
                secret.assert_not_called(); protocol.query.assert_not_called()
                http.reset_mock()
                result = checks.control_plane(target, protocol, hook=True)
                self.assertEqual(('passed', 'control-plane-hook', version),
                                 (result['result'], result['role'], result['version']))
                http.assert_not_called()
                self.assertEqual(3, protocol.query.call_count)

    def test_hook_endpoint_binding_precedes_secret_and_rcon(self):
        for env in ([], ['PublicServerJoin__Host=192.0.2.11', 'PublicServerJoin__Port=27015'],
                    ['PublicServerJoin__Host=192.0.2.10', 'PublicServerJoin__Port=27016'],
                    ['PublicServerJoin__Host=192.0.2.10'] * 2 + ['PublicServerJoin__Port=27015']):
            with self.subTest(env=env), control_fixture(env=env) as (target, protocol, run, secret, http):
                with self.assertRaises(checks.CheckFailure) as failure:
                    checks.control_plane(target, protocol, hook=True)
                self.assertEqual('configured_endpoint', failure.exception.check)
                secret.assert_not_called(); protocol.query.assert_not_called(); http.assert_not_called()

    def test_hook_wrong_version_and_busy_still_refuse_without_retry(self):
        with control_fixture() as (target, protocol, run, secret, http):
            target['version'] = '0.25.0'
            with self.assertRaises(checks.CheckFailure) as failure:
                checks.control_plane(target, protocol, hook=True)
            self.assertEqual('rcon_status', failure.exception.check)
            self.assertEqual(2, protocol.query.call_count)
        with control_fixture(lookup=lambda nonce, now: f'GSLOOKUP 1 {nonce} busy 10') as (target, protocol, run, secret, http):
            with self.assertRaises(checks.CheckFailure) as failure:
                checks.control_plane(target, protocol, hook=True)
            self.assertEqual('lookup_frame', failure.exception.check)
            self.assertEqual(3, protocol.query.call_count)

    def test_hook_rejects_runtime_mount_and_endpoint_drift(self):
        changes = [lambda c: c['State'].update(StartedAt='changed'),
                   lambda c: c['Mounts'][0].update(RW=True),
                   lambda c: c['Config']['Env'].append('PublicServerJoin__Port=27016')]
        for change in changes:
            with self.subTest(change=change), control_fixture(after=change) as (target, protocol, run, secret, http):
                with self.assertRaises(checks.CheckFailure) as failure:
                    checks.control_plane(target, protocol, hook=True)
                self.assertEqual('api_runtime', failure.exception.check)
                self.assertEqual(3, protocol.query.call_count)

    def test_hook_budget_exhaustion_prevents_rcon(self):
        with control_fixture() as (target, protocol, run, secret, http), \
             patch.object(checks.time, 'monotonic', side_effect=[0, 1, 2, 3, 12]):
            with self.assertRaises(checks.CheckFailure) as failure:
                checks.control_plane(target, protocol, hook=True)
            self.assertEqual('rcon_status_transport', failure.exception.check)
            protocol.query.assert_not_called()
            self.assertTrue(all(call.kwargs['timeout'] <= 5 for call in run.call_args_list))

    def test_hook_report_preserves_stage_but_never_exception_text(self):
        report = dict(result='failed', role='control-plane-hook', check='lookup_frame', category='ValueError')
        for error, expected in ((coordinator.Refusal(report), report),
                                (subprocess.TimeoutExpired('private-command', 20),
                                 dict(result='failed', role='control-plane-hook', check='coordinator_transport', category='TimeoutExpired')),
                                (RuntimeError('private-content'),
                                 dict(result='failed', role='control-plane-hook', check='coordinator_transport', category='Exception'))):
            with self.subTest(error=type(error).__name__), patch.object(coordinator, 'invoke', side_effect=error) as invoke:
                self.assertEqual(expected, coordinator.invoke_report('control-plane-hook', profile(Path('/fixture'))))
                invoke.assert_called_once()
        with patch.object(checks.os, 'geteuid', return_value=0, create=True), \
             patch.object(checks, 'control_plane', side_effect=checks.CheckFailure('lookup_frame', 'ValueError')), \
             patch('sys.stdout', new=io.StringIO()) as out:
            with self.assertRaises(SystemExit): checks.remote_main('control-plane-hook', profile(Path('/fixture')), fixture)
            self.assertEqual(report, json.loads(out.getvalue()))

    def test_hook_has_short_ssh_deadline_and_invalid_role_never_connects(self):
        for role, timeout in (('control-plane-hook', 20), ('control-plane', 65)):
            value = dict(result='passed', role=role)
            response = subprocess.CompletedProcess([], 0, json.dumps(value).encode(), b'')
            with patch.object(coordinator.subprocess, 'run', return_value=response) as run:
                self.assertEqual(value, coordinator.invoke(role, profile(Path('/fixture'))))
                self.assertEqual(timeout, run.call_args.kwargs['timeout'])
        with patch.object(coordinator.subprocess, 'run') as run:
            with self.assertRaises(ValueError): coordinator.invoke('unknown', profile(Path('/fixture')))
            run.assert_not_called()

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
