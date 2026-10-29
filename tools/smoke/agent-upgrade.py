#!/usr/bin/env python3
"""Isolated Linux rehearsal: real files/SQLite, simulated service manager and guard."""
import base64
import importlib.util
import contextlib
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('agent_upgrade', ROOT / 'ops/gameserver/agent-upgrade.py')
upgrade = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(upgrade)


class Fixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='agent-upgrade-')
        self.root = Path(self.temporary.name)
        frozen = self.root / 'agent-upgrade.py'
        frozen.write_bytes(Path(upgrade.__file__).read_bytes())
        os.chmod(frozen, 0o644)
        source_patch = patch.object(upgrade, '__file__', str(frozen))
        source_patch.start()
        self.addCleanup(source_patch.stop)
        self.patches = []
        for name in ('CONFIG', 'INSTALL', 'SYSTEMD', 'STATE', 'HOME'):
            path = self.root / name.lower()
            path.mkdir(mode=0o700)
            self.patches.append(patch.object(upgrade, name, path))
        self.patches.append(patch.object(upgrade, 'GUARD', self.root / 'guard'))
        self.patches.append(patch.object(upgrade, 'CAPTURE', self.root / 'capture.py'))
        for item in self.patches:
            item.start()
        self.addCleanup(self.temporary.cleanup)
        for item in self.patches:
            self.addCleanup(item.stop)
        self.group = patch.object(upgrade.grp, 'getgrnam', return_value=type('Group', (), {'gr_gid': 0})())
        self.group.start()
        self.addCleanup(self.group.stop)
        upgrade.GUARD.write_bytes(b'unchanged fixture guard')
        os.chmod(upgrade.GUARD, 0o755)
        old_hash = '1' * 64
        self.old_release = upgrade.INSTALL / 'releases' / old_hash
        self.old_release.mkdir(parents=True)
        (self.old_release / 'agent').mkdir()
        (self.old_release / 'gameserver').mkdir()
        payload = []
        for name, raw, mode in ((upgrade.BINARY, b'previous binary', 0o750),
                                ('agent/run.sh', b'unchanged launcher', 0o750),
                                ('agent/appsettings.json', b'{}', 0o640),
                                ('gameserver/producer', b'unchanged game payload', 0o640)):
            path = self.old_release / name
            path.write_bytes(raw)
            os.chmod(path, mode)
            payload.append({'path': name, 'length': len(raw), 'mode': f'{mode:04o}', 'sha256': upgrade.sha(raw)})
        self.manifest = {'schemaVersion': 1, 'bundleVersion': '2.26.0-rc.6', 'sourceRevision': 'a' * 40, 'payload': payload}
        manifest = upgrade.encode(self.manifest)
        (self.old_release / 'manifest.json').write_bytes(manifest)
        os.chmod(self.old_release / 'manifest.json', 0o640)
        paths = upgrade.inventory()
        paths[1].parent.mkdir(parents=True)
        unit = f'ExecStart={self.old_release}/agent/run.sh\n'.encode()
        old = [f'schema_version=1\nbundle_version=2.26.0-rc.6\nsource_revision={"a" * 40}\nbundle_sha256={old_hash}\nmanifest_sha256={upgrade.sha(manifest)}\nservice_unit_sha256={upgrade.sha(unit)}\n'.encode(),
               f'pilot_marker_sha256=placeholder\nbundle_sha256={old_hash}\nmanifest_sha256={upgrade.sha(manifest)}\nidentity_sha256={"b" * 64}\n'.encode(),
               b'activation_state_sha256=placeholder\n', unit,
               b'pilot_marker_sha256=placeholder\npilot_gate_sha256=placeholder\nactivation_state_sha256=placeholder\nmanifest_sha256=placeholder\nagent_unit_sha256=placeholder\n',
               b'', b"    pending_addon = ledger.root / 'addon-upgrade-pending.json'\n"]
        for path, raw in zip(paths, old):
            path.write_bytes(raw)
            os.chmod(path, 0o600)
        paths[5].write_bytes(''.join(upgrade.sha(paths[n].read_bytes()) + '  ' + str(paths[n]) + '\n' for n in (3, 4)).encode())
        self.before = {p: p.read_bytes() for p in paths}
        queue = upgrade.HOME / 'game-event-agent/queue/game-event-agent.db'
        queue.parent.mkdir(parents=True)
        with contextlib.closing(sqlite3.connect(queue)) as connection:
            connection.executescript("PRAGMA user_version=2; CREATE TABLE agent_state(source_instance_id TEXT, next_sequence_number INTEGER); INSERT INTO agent_state VALUES ('retained-source', 42); CREATE TABLE game_event_outbox(status INTEGER); CREATE TABLE game_event_spool_receipt(event_id TEXT);")
        for name in ('incoming', 'processing', 'accepted', 'rejected'):
            (upgrade.HOME / 'server/cstrike/addons/amxmodx/data/goldsrcops-spool' / name).mkdir(parents=True)
        self.queue = upgrade.queue_snapshot()
        self.game = {'ActiveState': 'active', 'SubState': 'running', 'MainPID': '123', 'InvocationID': 'fixed', 'NRestarts': '0'}
        self.active = True
        self.actions = []
        self.fail_start = False
        self.interrupt_start = False
        def run(args, timeout=45):
            args = [str(x) for x in args]
            if args[0] == str(upgrade.GUARD):
                return 'PERSISTENT_GAME_EVENT_GUARD=passed\n'
            self.actions.append(args)
            if args[1] in ('stop', 'start'):
                self.assertEqual(args[2], upgrade.AGENT)
                if args[1] == 'start' and self.interrupt_start:
                    raise upgrade.Uncertain('simulated timeout')
                if args[1] == 'start' and self.fail_start:
                    self.fail_start = False
                    raise ValueError('completed start failure')
                self.active = args[1] == 'start'
            if args[1] == 'show':
                return 'inactive\n' if not self.active else 'active\n'
            return ''
        def unit(name):
            if name == upgrade.AGENT and not self.active:
                raise ValueError('inactive')
            return self.game
        self.diagnostic = patch.object(upgrade, 'diagnostic_status')
        for item in (patch.object(upgrade, 'run', side_effect=run), patch.object(upgrade, 'unit', side_effect=unit),
                     self.diagnostic,
                     patch.object(upgrade.time, 'sleep')):
            item.start()
            self.addCleanup(item.stop)
        self.archive = self.root / 'agent.zip'
        raw = b'new binary'
        self.candidate = {'schemaVersion': 1, 'purpose': 'game-event-agent', 'version': '2.95.0',
                          'sourceRevision': 'c' * 40, 'targetRuntime': 'linux-x64', 'queueSchemaVersion': 2,
                          'payload': [{'path': upgrade.BINARY, 'mode': '0750', 'length': len(raw), 'sha256': upgrade.sha(raw)}]}
        with zipfile.ZipFile(self.archive, 'x') as bundle:
            bundle.writestr('manifest.json', upgrade.encode(self.candidate))
            bundle.writestr(upgrade.BINARY, raw)
        os.chmod(self.archive, 0o600)
        self.archive_hash = upgrade.sha(self.archive.read_bytes())
        self.directory = self.root / 'transition'

    def prepare(self):
        identity = upgrade.prepare(self.archive, self.archive_hash, self.directory)
        return upgrade.load_plan(self.directory, identity)

    def test_prepare_wrong_hash_refuses_without_changes(self):
        with self.assertRaises(ValueError):
            upgrade.prepare(self.archive, '0' * 64, self.directory)
        self.assertFalse(self.directory.exists())
        self.assertEqual(self.actions, [])
        self.assertEqual(self.before, {p: p.read_bytes() for p in self.before})

    def test_apply_and_explicit_rollback_preserve_source_queue_and_game(self):
        plan = self.prepare()
        new_manifest = json.loads((self.directory / 'release/manifest.json').read_bytes())
        self.assertEqual([x for x in new_manifest['payload'] if x['path'] != upgrade.BINARY],
                         [x for x in self.manifest['payload'] if x['path'] != upgrade.BINARY])
        upgrade.execute(self.directory, plan)
        self.assertEqual(json.loads((self.directory / 'status.json').read_bytes())['phase'], 'accepted')
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        upgrade.execute(self.directory, plan, rollback=True)
        self.assertEqual({p: p.read_bytes() for p in self.before}, self.before)
        self.assertEqual(upgrade.queue_snapshot(), self.queue)
        self.assertFalse((upgrade.STATE / 'agent-upgrade-pending.json').exists())
        self.assertTrue(self.active)

    def test_reference_drift_refuses_before_stop(self):
        plan = self.prepare()
        upgrade.inventory()[2].write_bytes(b'drift')
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        self.assertEqual(self.actions, [])

    def test_pending_work_refuses_before_prepare(self):
        queue = upgrade.HOME / 'game-event-agent/queue/game-event-agent.db'
        with contextlib.closing(sqlite3.connect(queue)) as connection:
            connection.execute('INSERT INTO game_event_outbox VALUES (1)')
            connection.commit()
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertFalse(self.directory.exists())

    def test_completed_start_failure_rolls_back_exact_refs(self):
        plan = self.prepare()
        self.fail_start = True
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        self.assertEqual(json.loads((self.directory / 'status.json').read_bytes())['phase'], 'rolled-back')
        self.assertEqual({p: p.read_bytes() for p in self.before}, self.before)
        self.assertEqual(upgrade.queue_snapshot(), self.queue)
        self.assertTrue(self.active)

    def test_uncertain_start_preserves_claim_and_never_retries(self):
        plan = self.prepare()
        self.interrupt_start = True
        with self.assertRaises(upgrade.Uncertain):
            upgrade.execute(self.directory, plan)
        count = len(self.actions)
        self.assertEqual(json.loads((self.directory / 'status.json').read_bytes())['phase'], 'reconciliation-required')
        self.assertTrue((upgrade.STATE / 'agent-upgrade-pending.json').is_file())
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        self.assertEqual(len(self.actions), count)
        self.assertEqual(upgrade.queue_snapshot(), self.queue)

    def test_package_and_plan_tampering_refused(self):
        plan = self.prepare()
        (self.directory / 'release' / upgrade.BINARY).write_bytes(b'tampered')
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        self.assertEqual(self.actions, [])

    def test_idle_evidence_failure_rolls_back_instead_of_accepting(self):
        plan = self.prepare()
        upgrade.diagnostic_status.side_effect = ValueError('stale cycle')
        with self.assertRaises(ValueError):
            upgrade.execute(self.directory, plan)
        self.assertEqual(json.loads((self.directory / 'status.json').read_bytes())['phase'], 'rolled-back')
        self.assertEqual({p: p.read_bytes() for p in self.before}, self.before)

    def test_partial_reference_install_restores_exact_old_boundary(self):
        plan = self.prepare()
        install = upgrade.install_refs
        def partial(value, direction):
            if direction == 'new':
                first = value['refs'][0]
                upgrade.atomic(Path(first['path']), base64.b64decode(first['new']), first['mode'], first['gid'])
                raise ValueError('completed partial install')
            install(value, direction)
        with patch.object(upgrade, 'install_refs', side_effect=partial):
            with self.assertRaises(ValueError):
                upgrade.execute(self.directory, plan)
        self.assertEqual({p: p.read_bytes() for p in self.before}, self.before)
        self.assertEqual(json.loads((self.directory / 'status.json').read_bytes())['phase'], 'rolled-back')


if __name__ == '__main__':
    if os.geteuid() != 0:
        raise SystemExit('Run this disposable fixture as root.')
    unittest.main()
