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
import gameplay_pilot_checks as pilot_checks
pilot_spec = importlib.util.spec_from_file_location('pilot', ROOT / 'tools/release/check-gameplay-pilot.py')
pilot = importlib.util.module_from_spec(pilot_spec)
pilot_spec.loader.exec_module(pilot)
spec = importlib.util.spec_from_file_location('coordinator', ROOT / 'tools/release/check-game-acceptance.py')
coordinator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coordinator)
spec = importlib.util.spec_from_file_location('fixture_probe', ROOT / 'ops/gameserver/addon-runtime-probe.py')
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
import addon_runtime_protocol as protocol


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
    def test_existing_addon_receipt_remains_supported(self):
        checks.addon_receipt(dict(schema_version='2', state='enabled', manifest_sha256='a' * 64),
                             dict(manifestSha256='a' * 64), {})

    def test_paired_receipt_requires_exact_frozen_module_and_contract(self):
        receipt = dict(schema_version='3', state='enabled', manifest_sha256='a' * 64, nvault_process_contract='1')
        relatives = dict(plugin='plugins/goldsrcops_weapon_selection.amxx',
            config='configs/plugins/goldsrcops-weapon-selection.cfg', loader='configs/plugins-goldsrcops-weapons.ini',
            dictionary='data/lang/goldsrcops-player-menu.txt', nvault='modules/nvault_amxx_i386.so')
        entries = {'/var/lib/goldsrc/server/cstrike/addons/amxmodx/' + path: 'b' * 64 for path in relatives.values()}
        receipt.update({kind + '_sha256': 'b' * 64 for kind in relatives})
        target = dict(manifestSha256='a' * 64)
        checks.addon_receipt(receipt, target, entries)
        for changed in (dict(receipt, nvault_sha256='c' * 64), dict(receipt, nvault_process_contract='0'),
                        dict(receipt, extra='unexpected'), dict(receipt, schema_version='4')):
            with self.assertRaises(ValueError):
                checks.addon_receipt(changed, target, entries)
        with self.assertRaises(ValueError):
            checks.addon_receipt(receipt, target, {})

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



class GameplayPilotTests(unittest.TestCase):
    def setUp(self):
        self.profile = {
            'schema': 1, 'acceptance': profile(Path('/fixture')),
            'sshConfigSha256': 'c' * 64,
            'mapAddon': {'version': '0.2.0', 'manifestSha256': 'd' * 64,
                         'payload': dict.fromkeys(pilot_checks.MAP_FILES, 'e' * 64)},
            'engine': {'protocol': 48, 'rehlds': '3.15.0.896-dev', 'build': 4419},
        }
        self.game = {
            'checks': {'game.players': {'expected': 0, 'observed': 0}},
            'a2s': {'map': 'de_dust2', 'players': 0, 'bots': 0},
            'invocation': 'f' * 32,
            'weaponReceiptSha256': 'a' * 64, 'mapReceiptSha256': 'b' * 64,
        }
        self.expected = pilot.expected_cvars()
        self.responses = {
            'goldsrcops_weapons_status': (
                'WEAPON_SELECTION_STATUS version=0.25.0 enabled=1\n'
                'PLAYER_RULES_STATUS version=0.25.0 source=live_cvars\n'
                'PLAYER_PREFERENCES_STATUS version=0.25.0 storage=nvault errors=0\n'
                'KILL_HEAL_STATUS version=0.25.0 enabled=1 amount=15\n'
                'KILL_AMMO_STATUS version=0.25.0 enabled=1'
            ),
            'goldsrcops_stats_status': (
                'PLAYER_STATS_STATUS version=0.25.0 storage=nvault errors=0\n'
                'PLAYER_LEADERBOARD_STATUS version=0.25.0 ready=1 errors=0\n'
                'PLAYER_PROFILE_STATUS version=0.25.0 source=loaded_player_state'
            ),
            'goldsrcops_maps_status': (
                'MAP_MENU_STATUS version=0.2.0 enabled=1 cycle_ready=1 maps=5 timeleft=native '
                'voting_enabled=1 language_bridge=1'
            ),
            'version': 'Protocol version 48\nReHLDS version: 3.15.0.896-dev\nBuild date: fixture (4419)',
            'mp_timeleft': '"mp_timeleft" is "19:57"',
            **{name: f'"{name}" is "{value}"' for name, value in self.expected.items()},
        }

    def collect(self, runtime, after=None):
        result = Mock(side_effect=[self.game, runtime, after if after is not None else copy.deepcopy(self.game)])
        report = pilot.preflight(self.profile, collect=result)
        self.assertEqual([call.args[0] for call in result.call_args_list], ['game', 'runtime', 'game'])
        return report

    def parse(self):
        return pilot_checks.runtime_observations(self.responses, self.profile, self.expected)

    def test_ready_includes_fresh_baseline_header_and_normalizes_cvar(self):
        self.responses['mp_forcerespawn'] = '"mp_forcerespawn" is 1.000'
        report = self.collect(self.parse())
        self.assertEqual(report['result'], 'ready', (report['drift'], report['unavailable']))
        self.assertEqual(report['timeLeft'], '19:57')
        self.assertIn('de_dust2', report['pilotHeader'])
        self.assertIn('preferences.storage=nvault', report['pilotHeader'])
        self.assertNotIn('game-fixture', json.dumps(report))
        self.assertNotIn(self.game['invocation'], json.dumps(report))

    def test_cvar_drift_reports_expected_and_observed(self):
        self.responses['mp_respawn_immunitytime'] = '"mp_respawn_immunitytime" is "4"'
        report = self.collect(self.parse())
        self.assertEqual(report['result'], 'drift')
        self.assertEqual(report['drift'], {'mp_respawn_immunitytime': {'expected': '2', 'observed': '4'}})

    def test_missing_and_duplicate_server_fields_are_unavailable(self):
        del self.responses['mp_timeleft']
        self.responses['goldsrcops_maps_status'] += '\n' + self.responses['goldsrcops_maps_status']
        report = self.collect(self.parse())
        self.assertEqual(report['result'], 'unavailable')
        self.assertIn('game.timeLeftAvailable', report['unavailable'])
        self.assertIn('mapAddon.version', report['unavailable'])

    def test_map_transition_invalidates_combined_observation(self):
        after = copy.deepcopy(self.game)
        after['a2s']['map'] = 'de_nuke'
        report = self.collect(self.parse(), after)
        self.assertEqual(report['result'], 'drift')
        self.assertFalse(report['drift']['game.stableDuringSample']['observed'])

    def test_occupied_host_stops_before_rcon(self):
        self.game['checks']['game.players']['observed'] = 1
        collect = Mock(return_value=self.game)
        report = pilot.preflight(self.profile, collect=collect)
        self.assertEqual(report['result'], 'drift')
        self.assertEqual(collect.call_count, 1)

    def test_ssh_timeout_and_remote_refusal_have_sanitized_categories(self):
        for exit_code, diagnostic, category in (
            (255, b'ssh: host fixture-private: Connection timed out', 'ssh-connect-timeout'),
            (1, b'fixture-private file refused', 'remote-check-refused'),
        ):
            with self.subTest(category=category), patch.object(pilot.subprocess, 'run', return_value=
                    subprocess.CompletedProcess([], exit_code, b'', diagnostic)) as transport:
                report = pilot.preflight(self.profile)
                self.assertEqual(report['result'], 'unavailable')
                self.assertEqual(report['collectionErrors'], {'game.before': category})
                self.assertNotIn('fixture-private', json.dumps(report))
                transport.assert_called_once()

    def test_a2s_challenge_projects_the_map_and_counts_only(self):
        fields = b'Fixture\0de_dust2\0cstrike\0Counter-Strike\0'
        legacy = protocol.HEADER + b'm192.0.2.10:27015\0' + fields + bytes((0, 20, 48, 100, 108, 0, 0, 0, 0))
        source = protocol.HEADER + b'I\x30' + fields + bytes((10, 0, 0, 20, 0))
        request = protocol.HEADER + b'TSource Engine Query\0'
        for packet in (legacy, source):
            with self.subTest(kind=packet[4]), patch.object(pilot_checks.socket, 'socket') as socket:
                connection = socket.return_value.__enter__.return_value
                connection.recv.side_effect = [protocol.HEADER + b'ABCD1', packet]
                self.assertEqual(pilot_checks.game_info(self.profile['acceptance'], fixture.policy.parse_info),
                                 {'map': 'de_dust2', 'players': 0, 'bots': 0})
                self.assertEqual([call.args[0] for call in connection.send.call_args_list],
                                 [request, request + b'BCD1'])

    def test_profile_must_match_exact_reviewed_bytes(self):
        with tempfile.TemporaryDirectory() as item:
            path = Path(item) / 'pilot.json'
            raw = json.dumps(self.profile).encode()
            path.write_bytes(raw)
            self.assertEqual(pilot.load_profile(path, hashlib.sha256(raw).hexdigest()), self.profile)
            path.write_bytes(raw + b' ')
            with self.assertRaises(ValueError):
                pilot.load_profile(path, hashlib.sha256(raw).hexdigest())

    def test_receipt_and_bytes_drift_are_detected_independently(self):
        probes = {}
        fields = {'state': 'enabled', 'schema_version': '2', 'manifest_sha256': 'd' * 64, 'plugin_sha256': 'e' * 64}
        with patch.object(pilot_checks, 'receipt', return_value=(fields, 'f' * 64)), \
             patch.object(pilot_checks, 'trusted', return_value=b'changed plugin'):
            pilot_checks.check_receipt(probes, 'map', Path('/receipt'), '2', 'd' * 64,
                                      {'plugin': 'e' * 64}, {'plugin': 'plugins/map.amxx'})
        self.assertEqual(probes['map.receipt.plugin']['observed'], 'e' * 64)
        self.assertNotEqual(probes['map.file.plugin']['observed'], probes['map.file.plugin']['expected'])

    def test_pilot_protocol_rejects_lookup_and_cvar_writes_before_network(self):
        with patch.object(protocol, '_query') as transport:
            for text in ('mp_forcerespawn 1', 'changelevel de_nuke',
                         'goldsrcops_player_lookup 0123456789abcdef 41', 'status'):
                with self.assertRaises(ValueError):
                    protocol.query_pilot(('192.0.2.10', 27015), b'fixture', text)
            transport.assert_not_called()
            protocol.query_pilot(('192.0.2.10', 27015), b'fixture', 'mp_timeleft')
            transport.assert_called_once()


if __name__ == '__main__': unittest.main(verbosity=2)
