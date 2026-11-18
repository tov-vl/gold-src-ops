#!/usr/bin/env python3
"""Disposable Linux-root pair/CAS/backup workflow acceptance; no network or services."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import stat
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid
import zlib

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / 'ops/gameserver'
sys.path.insert(0, str(SOURCE))
import player_storage_pair as p
spec = importlib.util.spec_from_file_location('paired_upgrade', SOURCE / 'paired-addon-upgrade.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


def put(path, data, mode=0o600):
    path.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
    path.write_bytes(data.encode() if isinstance(data, str) else data)
    path.chmod(mode)


def amxx(names):
    table_end = 56 + len(names) * 8
    strings = b'\x7f\0'
    table = b''
    for name in names:
        table += struct.pack('<II', 0, table_end + len(strings))
        strings += name.encode() + b'\0'
    code = table_end + len(strings)
    image = struct.pack('<IHBBhh11i', code + 4, 0xF1E0, 8, 8, 0, 8,
        code, code + 4, code + 4, code + 64, 0, 56, 56, table_end, table_end, table_end, table_end) + table + strings + b'\0' * 4
    compressed = zlib.compress(image)
    return struct.pack('<IHB', 0x414D5858, 0x300, 1) + struct.pack('<BIIII', 4, len(compressed), len(image), len(image) + 64, 24) + compressed


def vault(value=b'saved', stamp=1700000000):
    key = b'synthetic-player'
    return struct.pack('<IHIIBH', 0x6E564C54, 0x200, 1, stamp, len(key), len(value)) + key + value


def native_command(binary, *arguments):
    loader = os.environ.get('PAIR_I386_LOADER')
    prefix = [loader, '--library-path', str(Path(loader).parent)] if loader else []
    return prefix + [str(binary), *arguments]


def fixture_hook():
    phase, directory, identity, manifest = sys.argv[2:]
    root, directory = Path(os.environ['PAIR_FIXTURE']), Path(directory)
    result = dict(schema=1, state='verified', phase=phase, planSha256=identity)
    if phase == 'storage-probe':
        if os.environ.get('PAIR_READER_DIR'):
            scratch = root / 'scratch-base' / ('.player-storage-probe-' + uuid.uuid4().hex)
            process = subprocess.run(['runuser', '-u', 'nobody', '--', *native_command(root / 'probe-new', '--probe', str(scratch))],
                check=True, capture_output=True, text=True, timeout=30)
            result.update(p.decode(process.stdout))
        else:
            result.update(samples=10000, p99Us=8, maxUs=1700000)
        if (root / 'slow-probe').exists():
            result['p99Us'] = 2001
    elif phase.startswith('backup-'):
        raw = (directory / 'closed.tar').read_bytes()
        expected = p.decode((directory / 'expected.json').read_bytes())
        if (root / 'reject-backup-before').exists() and phase == 'backup-before':
            result['state'] = 'refused'
            print(json.dumps(result))
            return
        offhost = root / 'off-host' / phase
        offhost.mkdir(parents=True)
        key = directory / 'fixture-passphrase'
        put(key, os.urandom(32).hex())
        encrypted = offhost / 'closed.enc'
        # Real local encryption/readback with an independent process and directory;
        # no provider or remote-host availability claim is made by this fixture.
        command = ['openssl', 'enc', '-aes-256-cbc', '-pbkdf2', '-pass', 'file:' + str(key)]
        subprocess.run(command + ['-in', str(directory / 'closed.tar'), '-out', str(encrypted)],
                       check=True, capture_output=True, timeout=20)
        restored = subprocess.run(command + ['-d', '-in', str(encrypted)],
                       check=True, capture_output=True, timeout=20).stdout
        if restored != raw:
            raise ValueError('Encrypted readback mismatch')
        semantics = []
        for reader in ('old', 'new'):
            target = directory / ('.player-storage-copy-' + uuid.uuid4().hex)
            u.base.bundle.restore(restored, target)
            before = p.semantic(u.base.bundle.inventory(target))
            if os.environ.get('PAIR_READER_DIR'):
                subprocess.run(native_command(Path(os.environ['PAIR_READER_DIR']) / ('player-storage-' + reader),
                    '--reopen-copy', str(target)), check=True, capture_output=True, timeout=20)
            after = p.semantic(u.base.bundle.inventory(target))
            if before != after:
                raise ValueError('Reader changed semantic data')
            semantics.append(after)
        result.update(plaintextSha256=p.digest(raw), restoredSha256=p.digest(restored),
            semanticSha256=expected['semanticSha256'], oldReaderSemanticSha256=semantics[0],
            newReaderSemanticSha256=semantics[1], encrypted=True, offHost=True, independentRecovery=True)
        if (root / 'bad-recovery').exists():
            result['oldReaderSemanticSha256'] = '0' * 64
    else:
        candidate = manifest == (root / 'candidate-hash').read_text()
        if (root / 'reject-candidate').exists() and candidate:
            result['state'] = 'refused'
            print(json.dumps(result))
            return
        result.update(manifestSha256=manifest, addonVersion='0.40.0' if candidate else '0.39.0',
                      storageHealthy=True, processContract=1 if candidate else 0)
    print(json.dumps(result))


class FixtureHost(u.Host):
    def hook(self, kind, phase, directory, manifest_hash):
        result = u.Host.hook(self, kind, phase, directory, manifest_hash)
        if os.environ.get('PAIR_EVIDENCE'):
            evidence = Path(os.environ['PAIR_EVIDENCE'])
            owner = evidence.stat()
            target = evidence / (uuid.uuid4().hex + '.json')
            with target.open('x', encoding='utf-8') as stream:
                json.dump(dict(case=self.case_name, kind=kind, receipt=result), stream)
                # Export only the sanitized receipt to the runner that owns the mount.
                if (owner.st_uid, owner.st_gid) != (os.geteuid(), os.getegid()):
                    os.fchown(stream.fileno(), owner.st_uid, owner.st_gid)
        return result

    def unit(self, name):
        agent = name == u.AGENT
        return dict(ActiveState='active' if agent or self.active else 'inactive',
                    SubState='running' if agent or self.active else 'dead',
                    InvocationID='agent' if agent else str(self.generation), NRestarts='0')

    def empty(self):
        return not (self.root / 'busy').exists()

    def start(self):
        self.calls.append('start')
        if (self.root / 'start-uncertain').exists():
            raise u.Uncertain('Interrupted start')
        self.validate_static('candidate' if p.read(self.base) == self.after else 'previous')
        u.base.run(['bash', str(self.persistent_guard), '--guard-game'])
        self.active = True
        put(self.root / 'service-active', 'active')
        self.generation += 1

    def stop(self):
        self.calls.append('stop')
        if (self.root / 'stop-uncertain').exists():
            raise u.Uncertain('Interrupted stop')
        self.active = False
        put(self.root / 'service-active', 'inactive')
        self.guard('quiescent')

    def mapped(self):
        # Unit fixture replaces only the OS service/process adapter.
        # Real mapping is still mandatory in the production Host.
        p.require(self.active)


class PairTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='paired-storage-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.root.chmod(0o711)
        scripts = self.root / 'staged-scripts'
        for name in ('game-event-persistent.sh', 'addon-upgrade-guard.sh', 'player-data-schedule-guard.sh'):
            put(scripts / name, (SOURCE / name).read_bytes())
        directory = patch.object(u, 'DIRECTORY', scripts)
        directory.start()
        self.addCleanup(directory.stop)
        self.server, self.config, self.state = [self.root / x for x in ('server', 'config', 'state')]
        for directory in (self.server, self.config, self.state):
            directory.mkdir(mode=0o700)
        self.vault = self.server / p.AMXX / 'data/vault'
        self.vault.mkdir(parents=True, mode=0o700)
        service = pwd.getpwnam('nobody')
        os.chown(self.vault, service.pw_uid, service.pw_gid)
        for name in u.base.bundle.NAMES:
            put(self.vault / name, b'' if name.endswith('.journal') else vault(), 0o640)
            os.chown(self.vault / name, service.pw_uid, service.pw_gid)
        self.original_data = u.base.bundle.inventory(self.vault)
        self.old_module = b'\x7fELF\x01\x01\x01' + bytes(11) + b'\x03\0old-GetVaultMngr'
        self.new_module = b'\x7fELF\x01\x01\x01' + bytes(11) + b'\x03\0new-GetVaultMngr'
        self.old = {p.PLUGIN: amxx(['nvault_set']), p.CONFIG: b'goldsrcops_weapons_enabled "1"\n',
                    p.LOADER: b'goldsrcops_weapon_selection.amxx\n', p.DICTIONARY: b'[en]\nKEY = value\n'}
        self.new = {**self.old, p.PLUGIN: amxx(['nvault_set', 'nvault_process_commit', 'nvault_process_version']),
                    p.MODULE: self.new_module}
        olddir, newdir = self.root / 'previous', self.root / 'candidate'
        old_manifest = dict(schemaVersion=2, purpose='game-host-addon', productionInstallSupported=True,
            enabledByDefault=True, amxxVersion='1.10.0.5481', reApiVersion='5.24.0.300',
            payload=[dict(path=k, sha256=p.digest(v)) for k, v in self.old.items()])
        for k, v in self.old.items():
            put(olddir / k, v)
        put(olddir / 'manifest.json', json.dumps(old_manifest))
        self.previous = dict(directory=str(olddir), sha256=p.digest(p.read(olddir / 'manifest.json')))
        put(self.root / 'stock.so', self.old_module)
        native = dict(schema=1, upstream=dict(processContract=1, target='linux-i386'), reproducible=True,
                      sha256=p.digest(self.new_module), testingSha256='0' * 64, correspondingSourceSha256=p.digest(b'source'))
        fixture = dict(schema=1, toolchain=dict(amxx='1.10.0.5481', reapi='5.24.0.300'),
                       payload={'goldsrcops_weapon_selection.amxx': p.digest(self.new[p.PLUGIN])})
        for k, v in self.new.items():
            put(newdir / k, v)
        put(newdir / 'proof/native-manifest.json', json.dumps(native))
        put(newdir / 'proof/fixture-manifest.json', json.dumps(fixture))
        put(newdir / 'proof/corresponding-source.zip', b'source')
        self.new_manifest = dict(schema=1, purpose='paired-player-storage', addonVersion='0.40.0', processContract=1,
            amxxVersion='1.10.0.5481', reApiVersion='5.24.0.300',
            predecessor=dict(addonManifestSha256=self.previous['sha256'], moduleSha256=p.digest(self.old_module)),
            payload=[dict(path=k, sha256=p.digest(self.new[k])) for k in p.PATHS],
            nativeManifest=dict(path='proof/native-manifest.json', sha256=p.digest(p.read(newdir / 'proof/native-manifest.json'))),
            fixtureManifest=dict(path='proof/fixture-manifest.json', sha256=p.digest(p.read(newdir / 'proof/fixture-manifest.json'))))
        put(newdir / 'manifest.json', json.dumps(self.new_manifest))
        self.candidate = dict(directory=str(newdir), sha256=p.digest(p.read(newdir / 'manifest.json')))
        self.old[p.MODULE] = self.old_module
        self.receipt = self.config / 'weapon-selection-installed'
        self.old_receipt = p.receipt(self.previous['sha256'], {k:p.digest(v) for k,v in self.old.items()})
        self.new_receipt = p.receipt(self.candidate['sha256'], {k:p.digest(v) for k,v in self.new.items()}, True)
        for k, v in self.old.items():
            put(self.server / k, v, 0o640)
        put(self.receipt, self.old_receipt, 0o640)
        persistent, guard_changes = self.persistent_profile()
        put(self.root / 'unrelated', 'preserve')
        rows = [(p.digest(v), str(self.server / k)) for k,v in self.old.items()]
        rows += [(p.digest(self.old_receipt), str(self.receipt)), (p.digest(b'preserve'), str(self.root / 'unrelated'))]
        rows += [(p.digest(old), str(path)) for path, old, _ in guard_changes]
        self.before = ''.join(h + '  ' + name + '\n' for h,name in rows).encode()
        self.after = p.transition(self.before, self.server, self.config, self.old, self.new,
                                  self.old_receipt, self.new_receipt, guard_changes)
        self.base = self.config / 'player-data-backup-baseline.sha256'
        put(self.base, self.before)
        put(self.root / 'baseline-before', self.before)
        put(self.root / 'baseline-after', self.after)
        self.hook = self.root / 'hook.sh'
        put(self.hook, '#!/bin/sh\nexec python3 -B '+str(Path(__file__).resolve())+' --fixture-hook "$@"\n')
        ref = lambda x: dict(path=str(x), sha256=p.digest(p.read(x)))
        self.plan = dict(schema=2, operationId='fixture', previous=self.previous, candidate=self.candidate,
            previousModule=ref(self.root / 'stock.so'),
            baselines=dict(previous=ref(self.root / 'baseline-before'), candidate=ref(self.root / 'baseline-after')),
            hooks={key: ref(self.hook) for key in u.HOOKS}, persistent=persistent)
        put(self.root / 'candidate-hash', self.candidate['sha256'])
        commands = self.root / 'bin'
        commands.mkdir(mode=0o700)
        put(commands / 'systemctl', '#!/bin/sh\ncase "$*" in\n'
            '*MainPID*) echo 0;;\nis-enabled*) echo enabled;;\n'
            '*goldsrcops-game-event-agent.service*) echo active;;\n'
            '*) cat "$PAIR_FIXTURE/service-active";;\nesac\n', 0o700)
        put(commands / 'pgrep', '#!/bin/sh\nexit 1\n', 0o700)
        put(self.root / 'service-active', 'active')
        self.environment = patch.dict(os.environ, {'PAIR_FIXTURE': str(self.root), 'SUDO_USER': 'operator',
            'SSH_CONNECTION': 'fixture', 'PATH': str(commands) + ':' + os.environ['PATH'],
            'GOLDSRCOPS_SERVICE_HOME': str(self.root), 'GOLDSRCOPS_CONFIGURATION_DIRECTORY': str(self.config),
            'GOLDSRCOPS_LIBEXEC_DIRECTORY': str(self.root / 'libexec'),
            'GOLDSRCOPS_INSTALLATION_DIRECTORY': str(self.root / 'install'),
            'GOLDSRCOPS_SYSTEMD_DIRECTORY': str(self.root / 'systemd')})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        if os.environ.get('PAIR_READER_DIR'):
            put(self.root / 'probe-new', (Path(os.environ['PAIR_READER_DIR']) / 'player-storage-new').read_bytes(), 0o755)
            scratch = self.root / 'scratch-base'
            scratch.mkdir(mode=0o700)
            os.chown(scratch, 65534, 65534)
        self.refresh()

    def persistent_profile(self):
        config, root = self.config, self.root
        release = root / 'install/game-event-pilot/releases' / ('a' * 64)
        activation = root / 'install/game-event-pilot/activation/state'
        self.persistent_guard = root / 'libexec/goldsrcops-game-event-persistent'
        self.installed_backup_guard = root / 'backup/ops/gameserver/game-event-persistent.sh'
        guard_source = u.DIRECTORY / 'game-event-persistent.sh'
        old_guard = guard_source.read_bytes() + b'\n# Legacy fixture identity.\n'
        put(self.persistent_guard, old_guard, 0o755)
        put(self.installed_backup_guard, old_guard + b'# Backup source identity.\n', 0o644)
        for name in ('player-data-schedule-guard.sh', 'addon-upgrade-guard.sh'):
            put(self.installed_backup_guard.with_name(name), (SOURCE / name).read_bytes())
        producer = self.server / p.AMXX / 'configs/amxx.cfg'
        put(producer, 'goldsrcops_events_enabled 1\ngoldsrcops_spool_max_pending 1000\n', 0o640)
        put(self.server / 'cstrike/liblist.gam', 'fixture')
        self.overlay = release / 'manifest.json'
        payload = [{'path': 'gameserver/' + p.MODULE, 'sha256': p.digest(self.old_module), 'mode': '0640'},
                   {'path': 'gameserver/' + str(producer.relative_to(self.server)),
                    'sha256': p.digest(b'disabled producer'), 'mode': '0640'}]
        put(self.overlay, json.dumps({'payload': payload}), 0o640)
        put(config / 'host-prepared', 'schema_version=1\noperator_user=operator\nservice_user=nobody\n', 0o640)
        pilot = config / 'game-event-pilot-installed'
        put(pilot, f'schema_version=1\nbundle_sha256={"a" * 64}\nmanifest_sha256={p.digest(p.read(self.overlay))}\n', 0o640)
        gate = config / 'game-event-pilot-enabled'
        put(gate, 'fixture', 0o640)
        put(activation, 'fixture')
        put(config / 'secrets/game-event-agent-client-secret', 'fixture')
        environment = config / 'game-event-agent.env'
        put(environment, 'GameEventAgent__Spool__Enabled=true\nGameEventAgent__Delivery__Enabled=true\n', 0o640)
        stats = dict(schemaVersion=1, deliveryEnabled=True, queue=dict(pending=0,inFlight=0,deadLetter=0),
                     spool=dict(ready=0,processing=0,rejected=0))
        executable = release / 'agent/GoldSrcOps.GameEventAgent'
        put(executable, "#!/bin/sh\nprintf '%s\\n' '" + json.dumps(stats) + "'\n", 0o750)
        gid = pwd.getpwnam('nobody').pw_gid
        for directory in executable.parents:
            if directory == root:
                break
            directory.chmod(0o750)
            os.chown(directory, 0, gid)
        os.chown(executable, 0, gid)
        game_unit = root / 'systemd' / u.GAME
        agent_unit = root / 'systemd' / u.AGENT
        game_dropin = game_unit.with_name(game_unit.name + '.d') / '30-game-event-persistent.conf'
        agent_dropin = agent_unit.with_name(agent_unit.name + '.d') / '30-game-event-persistent.conf'
        put(game_unit, '[Service]\n', 0o644)
        put(agent_unit, '[Service]\n', 0o644)
        put(game_dropin, f'ExecCondition={self.persistent_guard} --guard-game\n', 0o644)
        put(agent_dropin, f'ExecCondition=!{self.persistent_guard} --guard-agent\n', 0o644)
        pins = dict(guard=self.persistent_guard, game_drop_in=game_dropin, agent_drop_in=agent_dropin,
                    pilot_marker=pilot, pilot_gate=gate, activation_state=activation, manifest=self.overlay,
                    liblist=self.server / 'cstrike/liblist.gam', environment=environment, producer=producer,
                    game_unit=game_unit, agent_unit=agent_unit)
        public = config / 'server-public.cfg'
        runtime = config / 'runtime-enabled'
        profile = self.server / 'cstrike/goldsrcops-fast-reentry-v1.cfg'
        rotation = self.server / 'cstrike/goldsrcops-mapcycle.txt'
        selected = config / 'managed-profile-active'
        put(public, 'exec goldsrcops-fast-reentry-v1.cfg\n', 0o640)
        put(profile, (SOURCE / 'fast-reentry-v1.cfg').read_bytes(), 0o640)
        put(rotation, 'de_dust2\n', 0o640)
        public_hash = p.digest(p.read(public))
        put(runtime, f'public_config_sha256={public_hash}\n', 0o640)
        profile_pins = dict(public=public, runtime_enabled=runtime, profile=profile, mapcycle=rotation)
        put(selected, 'schema_version=1\nprofile_id=fast-reentry-v1\n'
                      'backup_name=managed-profile-20261118T000000Z-000001\n'
                      f'baseline_public_sha256={public_hash}\nbaseline_runtime_enabled_sha256={p.digest(p.read(runtime))}\n' +
                      ''.join(f'{name}_sha256={p.digest(p.read(path))}\n' for name, path in profile_pins.items()), 0o640)
        pins.update(profile_pins, active_profile=selected)
        marker = config / 'game-event-persistent-active'
        put(marker, 'schema_version=2\npolicy_id=persistent-gameplay-v1\nprofile_id=fast-reentry-v1\n'
                    'backup_name=persistent-gameplay-20261118T000000Z-000001\n' +
                    ''.join(f'{name}_sha256={p.digest(p.read(path))}\n' for name, path in pins.items()), 0o640)
        for path in (self.server / p.MODULE, producer, pilot, gate, environment, marker, selected,
                     public, runtime, profile, rotation):
            os.chown(path, 0, gid)
        put(root / 'guard-before', old_guard)
        put(root / 'marker-before', p.read(marker))
        inputs = dict(previousGuard=root / 'guard-before', previousMarker=root / 'marker-before',
                      previousBackupGuard=root / 'backup-guard-before', candidateGuard=guard_source)
        put(inputs['previousBackupGuard'], p.read(self.installed_backup_guard))
        plan = {'persistent': {k: dict(path=str(v), sha256=p.digest(p.read(v))) for k, v in inputs.items()}}
        files = u.persistent_files(plan, config, self.persistent_guard, self.installed_backup_guard, self.new)
        return plan['persistent'], files[:2]

    def refresh(self):
        put(self.root / 'plan.json', json.dumps(self.plan))
        self.identity = p.digest(p.read(self.root / 'plan.json'))
        self.journal = u.Journal(self.state, self.identity)
        self.host = FixtureHost.__new__(FixtureHost)
        self.host.root, self.host.plan, self.host.journal = self.root, self.plan, self.journal
        self.host.case_name = self._testMethodName
        self.host.server, self.host.config_root, self.host.base, self.host.vault = self.server, self.config, self.base, self.vault
        self.host.persistent_guard = self.persistent_guard
        self.host.installed_backup_guard = self.installed_backup_guard
        self.host.active, self.host.generation, self.host.calls = True, 1, []
        self.host.initialize()

    def execute(self):
        u.read_plan(self.root / 'plan.json', self.identity, self.server, self.config,
                    self.persistent_guard, self.installed_backup_guard)
        return u.execute(self.plan, self.journal, self.host)

    def terminal(self, expected, which):
        self.assertEqual(expected, self.execute())
        self.assertFalse(self.journal.pending.exists())
        self.host.validate_static(which)
        self.assertTrue(self.host.active)
        self.assertEqual(self.original_data, u.base.bundle.inventory(self.vault))
        with self.assertRaises(ValueError):
            self.execute()

    def test_forward_pair_baseline_receipt_and_no_replay(self):
        overlay = self.overlay.read_bytes()
        self.terminal('accepted', 'candidate')
        self.assertEqual(['stop', 'start', 'stop', 'start'], self.host.calls)
        self.assertEqual(5, sum(a != b for a,b in zip(self.before.splitlines(), self.after.splitlines())))
        self.assertEqual(overlay, self.overlay.read_bytes())
        self.assertEqual(p.read(self.persistent_guard), p.read(self.installed_backup_guard))
        u.base.run(['bash', str(self.installed_backup_guard.with_name('addon-upgrade-guard.sh')), 'baseline'])

    def test_stock_overlay_and_guard_pass_preflight(self):
        script = SOURCE / 'addon-upgrade-guard.sh'
        for phase in ('identity', 'preflight', 'storage-overlay'):
            proc = subprocess.run(['bash', str(script), phase], capture_output=True, text=True, timeout=15)
            self.assertEqual(0, proc.returncode, (phase, proc.stderr))
        self.assertEqual(p.digest(self.old_module), proc.stdout.strip())

    def test_installed_guard_rejects_mixed_pair_and_partial_marker(self):
        self.terminal('accepted', 'candidate')
        guard = ['bash', str(self.persistent_guard), '--guard-game']
        marker = self.config / 'game-event-persistent-active'
        original = marker.read_bytes()
        plugin = self.server / p.PLUGIN
        with self.subTest('predecessor addon with candidate module'):
            put(plugin, self.old[p.PLUGIN], 0o640)
            with self.assertRaises(ValueError):
                u.base.run(guard)
            put(plugin, self.new[p.PLUGIN], 0o640)
        with self.subTest('single storage pin'):
            put(marker, b''.join(line for line in original.splitlines(keepends=True)
                                 if not line.startswith(b'storage_plugin_sha256=')), 0o640)
            with self.assertRaises(ValueError):
                u.base.run(guard)
            put(marker, original, 0o640)
        self.assertIn('PERSISTENT_GAME_EVENT_GUARD=passed', u.base.run(guard))

    def test_capture_and_transition_locks_stay_held_without_reacquiring(self):
        paths = (self.state / 'schedule.lock', self.config / 'player-data-backup.lock',
                 self.config / 'game-event-pilot-install.lock')
        with u.policy.lock(paths[0]), u.policy.lock(paths[1], shared=True), u.policy.lock(paths[2], shared=True):
            self.terminal('accepted', 'candidate')
            for path in paths:
                with self.assertRaises(BlockingIOError):
                    with u.policy.lock(path, shared=path.parent == self.config):
                        pass

    def test_identical_repeated_invariants_keep_inventory_shape(self):
        invariant = next(line for line in self.before.splitlines(keepends=True)
                         if line.endswith(b'/unrelated\n'))
        self.before += invariant * 2
        guard, marker, _ = u.persistent_files(self.plan, self.config, self.persistent_guard,
                                              self.installed_backup_guard, self.new)
        self.after = p.transition(self.before, self.server, self.config, self.old, self.new,
                                  self.old_receipt, self.new_receipt, (guard, marker))
        put(self.base, self.before)
        for kind, raw in (('before', self.before), ('after', self.after)):
            put(self.root / ('baseline-' + kind), raw)
        self.plan['baselines'] = {key: dict(path=str(self.root / ('baseline-' + name)), sha256=p.digest(raw))
            for key,name,raw in (('previous','before',self.before), ('candidate','after',self.after))}
        self.refresh()
        self.terminal('accepted', 'candidate')
        self.assertEqual(len(self.before.splitlines()), len(self.after.splitlines()))
        self.assertEqual(self.before.splitlines()[-2:], self.after.splitlines()[-2:])

    def test_valid_but_unexpected_closed_data_change_never_restarts(self):
        replace = u.replace_exact
        def change(path, expected, target, metadata, journal, phase):
            replace(path, expected, target, metadata, journal, phase)
            if phase == 'candidate-replace-0':
                put(self.vault / u.base.bundle.NAMES[0], vault(b'unexpected'), 0o600)
        with patch.object(u, 'replace_exact', change):
            with self.assertRaises(u.Uncertain):
                self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop'], self.host.calls)
        self.assertEqual(vault(b'unexpected'), (self.vault / u.base.bundle.NAMES[0]).read_bytes())

    def test_occupied_refuses_without_stop(self):
        put(self.root / 'busy', 'yes')
        self.terminal('refused', 'previous')
        self.assertEqual([], self.host.calls)

    def test_storage_probe_p99_gate(self):
        put(self.root / 'slow-probe', 'yes')
        self.terminal('refused', 'previous')

    def test_backup_refusal_restarts_unchanged_predecessor(self):
        put(self.root / 'reject-backup-before', 'yes')
        self.terminal('rolled-back', 'previous')
        self.assertEqual(['stop', 'start'], self.host.calls)

    def test_bad_independent_readback_refuses_before_replace(self):
        put(self.root / 'bad-recovery', 'yes')
        self.terminal('rolled-back', 'previous')

    def test_candidate_runtime_failure_rolls_back_pair_and_baseline(self):
        put(self.root / 'reject-candidate', 'yes')
        self.terminal('rolled-back', 'previous')

    def test_completed_failure_after_each_replace_rolls_back(self):
        original = u.replace_exact
        for index in range(7):
            with self.subTest(index=index):
                self.setUp()
                def fail(path, expected, target, metadata, journal, phase):
                    original(path, expected, target, metadata, journal, phase)
                    if phase == 'candidate-replace-' + str(index):
                        raise ValueError('Completed phase failed verification')
                with patch.object(u, 'replace_exact', fail):
                    self.terminal('rolled-back', 'previous')

    def test_unknown_or_interrupted_replace_retains_pending(self):
        with patch.object(u, 'replace_exact', side_effect=u.Uncertain('interrupted replace')):
            with self.assertRaises(u.Uncertain):
                self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop'], self.host.calls)
        with self.assertRaises(ValueError):
            self.execute()

    def test_os_replace_interruption_keeps_temp_and_pending(self):
        replace = os.replace
        def fail(source, target):
            if str(source).endswith('.pair-new'):
                raise OSError('simulated uncertain replace')
            return replace(source, target)
        with patch.object(u.os, 'replace', fail):
            with self.assertRaises(u.Uncertain):
                self.execute()
        self.assertTrue((self.server / p.MODULE).with_name('nvault_amxx_i386.so.pair-new').exists())
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop'], self.host.calls)

    def test_hook_without_classified_result_requires_reconciliation(self):
        put(self.hook, '#!/bin/sh\nexit 1\n')
        self.plan['hooks'] = {key: dict(path=str(self.hook), sha256=p.digest(p.read(self.hook))) for key in u.HOOKS}
        self.refresh()
        with self.assertRaises(u.Uncertain):
            self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual([], self.host.calls)

    def test_finished_rollback_preserves_static_metadata(self):
        before = {path: (path.stat().st_mode, path.stat().st_uid, path.stat().st_gid, path.stat().st_mtime_ns)
                  for path, _, _ in self.host.changes}
        put(self.root / 'reject-candidate', 'yes')
        self.terminal('rolled-back', 'previous')
        self.assertEqual(before, {path: (path.stat().st_mode, path.stat().st_uid, path.stat().st_gid, path.stat().st_mtime_ns)
                                 for path, _, _ in self.host.changes})

    def test_stale_baseline_plan_fails_before_claim(self):
        target = Path(self.plan['baselines']['candidate']['path'])
        put(target, self.before)
        self.plan['baselines']['candidate']['sha256'] = p.digest(self.before)
        self.refresh()
        with self.assertRaises(ValueError):
            self.execute()
        self.assertFalse(self.journal.pending.exists())

    def test_missing_module_fails_before_claim(self):
        (Path(self.candidate['directory']) / p.MODULE).unlink()
        with self.assertRaises(OSError):
            self.execute()
        self.assertFalse(self.journal.pending.exists())

    def test_consistently_rehashed_testing_or_non_i386_module_is_rejected(self):
        directory = Path(self.candidate['directory'])
        for raw in (self.new_module + b'NVAULT_FAIL', self.new_module + b'nvault_test_clock',
                    self.new_module[:4] + b'\x02' + self.new_module[5:]):
            with self.subTest(binary=p.digest(raw)):
                put(directory / p.MODULE, raw)
                native = p.decode(p.read(directory / 'proof/native-manifest.json'))
                native['sha256'] = p.digest(raw)
                put(directory / 'proof/native-manifest.json', json.dumps(native))
                self.new_manifest['nativeManifest']['sha256'] = p.digest(p.read(directory / 'proof/native-manifest.json'))
                for row in self.new_manifest['payload']:
                    if row['path'] == p.MODULE:
                        row['sha256'] = p.digest(raw)
                put(directory / 'manifest.json', json.dumps(self.new_manifest))
                item = dict(directory=str(directory), sha256=p.digest(p.read(directory / 'manifest.json')))
                with self.assertRaises(ValueError):
                    p.package(item, True)

    def test_addon_pending_blocks_another_plan(self):
        self.journal.claim(self.plan)
        other = u.Journal(self.state, '0' * 64)
        with self.assertRaises(ValueError):
            other.claim(self.plan)
        self.assertTrue(self.journal.pending.exists())

    def test_existing_weekly_capture_refuses_paired_pending_before_claim(self):
        self.journal.claim(self.plan)
        spec = importlib.util.spec_from_file_location('paired_schedule_fixture', SOURCE / 'player-data-scheduled-capture.py')
        capture = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(capture)
        from unittest.mock import Mock
        ledger = Mock(root=self.state)
        moment = u.policy.dt.datetime(2026, 11, 22, 1, tzinfo=u.policy.UTC)
        with self.assertRaises(ValueError):
            capture.capture('2026-11-22', ledger, Mock(), clock=lambda: moment)
        ledger.claim.assert_not_called()
        self.assertEqual([], self.host.calls)

    def test_loaded_module_check_uses_engine_in_cgroup_and_exact_inode(self):
        module = self.server / p.MODULE
        info = module.stat()
        device = f'{os.major(info.st_dev):02x}:{os.minor(info.st_dev):02x}'
        read = Path.read_bytes
        for inode, valid in ((info.st_ino, True), (info.st_ino + 1, False)):
            with self.subTest(valid=valid):
                def contents(path):
                    if str(path).endswith('/cgroup.procs'):
                        return b'777\n778\n'
                    if str(path) == '/proc/778/maps':
                        return f'1000-2000 r-xp 00000000 {device} {inode} {module}\n'.encode()
                    return read(path)
                with patch.object(u.base, 'run', return_value='/system.slice/fixture.service\n'), \
                     patch.object(Path, 'glob', return_value=iter([])), \
                     patch.object(Path, 'read_bytes', contents), \
                     patch.object(os, 'readlink', side_effect=lambda path: '/bin/bash' if '/777/' in str(path) else '/fixture/hlds_linux'):
                    if valid:
                        u.Host.mapped(self.host)
                    else:
                        with self.assertRaises(ValueError):
                            u.Host.mapped(self.host)

    def test_start_and_stop_uncertainty_are_never_retried(self):
        for marker in ('start-uncertain', 'stop-uncertain'):
            with self.subTest(marker=marker):
                self.setUp()
                put(self.root / marker, 'yes')
                with self.assertRaises(u.Uncertain):
                    self.execute()
                self.assertTrue(self.journal.pending.exists())
                self.assertEqual(['stop', 'start'] if marker.startswith('start') else ['stop'], self.host.calls)

    def test_new_progress_survives_rollback(self):
        accept = self.host.accept_pair
        def reject(which, phase):
            accept(which, phase)
            if phase == 'candidate-runtime':
                put(self.vault / u.base.bundle.NAMES[0], vault(b'new-progress'), 0o600)
                raise ValueError('Completed runtime refusal after progress')
        self.host.accept_pair = reject
        self.assertEqual('rolled-back', self.execute())
        self.host.validate_static('previous')
        self.assertEqual(vault(b'new-progress'), (self.vault / u.base.bundle.NAMES[0]).read_bytes())

    def test_player_join_blocks_post_capture_and_rollback_stop(self):
        accept = self.host.accept_pair
        def join(which, phase):
            accept(which, phase)
            if phase == 'candidate-runtime':
                put(self.root / 'busy', 'yes')
        self.host.accept_pair = join
        with self.assertRaises(u.Uncertain):
            self.execute()
        self.assertEqual(['stop', 'start'], self.host.calls)
        self.assertTrue(self.journal.pending.exists())

    def test_semantic_change_is_reconciliation_not_data_restore(self):
        accept = self.host.accept_pair
        def change(which, phase):
            accept(which, phase)
            if phase == 'candidate-runtime':
                put(self.vault / u.base.bundle.NAMES[0], vault(b'changed'), 0o600)
        self.host.accept_pair = change
        with self.assertRaises(u.Uncertain):
            self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(vault(b'changed'), (self.vault / u.base.bundle.NAMES[0]).read_bytes())
        self.assertEqual(['stop', 'start', 'stop'], self.host.calls)

    def test_auxiliary_files_block_without_deleting_them(self):
        auxiliary = self.vault / 'synthetic.vault.checkpoint.retained-0'
        put(auxiliary, 'retain')
        self.assertEqual('refused', self.execute())
        self.assertEqual(b'retain', auxiliary.read_bytes())
        self.assertFalse(self.journal.pending.exists())
        self.assertEqual([], self.host.calls)

    def test_baseline_and_unrelated_drift_refuse_before_stop(self):
        put(self.root / 'unrelated', 'changed')
        self.assertEqual('refused', self.execute())
        self.assertEqual([], self.host.calls)

    def test_installed_backup_guard_drift_refuses_before_stop(self):
        put(self.installed_backup_guard, b'# Drift\n', 0o644)
        self.assertEqual('refused', self.execute())
        self.assertEqual([], self.host.calls)

    def test_new_native_consumer_refuses_before_stop(self):
        put(self.server / p.AMXX / 'plugins/unexpected.amxx', amxx(['nvault_open']))
        self.terminal('refused', 'previous')

    def test_pending_identity_mismatch_is_not_cleared(self):
        self.journal.claim(self.plan)
        u.policy.write_json(self.journal.pending, dict(schema=1, planSha256='0' * 64), replace=True)
        with self.assertRaises(u.Uncertain):
            self.journal.finish('refused')
        self.assertTrue(self.journal.pending.exists())

    def test_changed_pending_after_start_does_not_trigger_more_stops(self):
        accept = self.host.accept_pair
        def foreign_pending(which, phase):
            accept(which, phase)
            if phase == 'candidate-runtime':
                u.policy.write_json(self.journal.pending, dict(schema=1, planSha256='0' * 64), replace=True)
        self.host.accept_pair = foreign_pending
        with self.assertRaises(u.Uncertain):
            self.execute()
        self.assertEqual(['stop', 'start'], self.host.calls)
        self.assertEqual('0' * 64, u.policy.read_json(self.journal.pending)['planSha256'])

    def test_packages_and_frozen_baseline_reject_corruption(self):
        target = Path(self.candidate['directory']) / p.MODULE
        put(target, self.new_module + b'NVAULT_FAIL')
        with self.assertRaises(ValueError):
            self.execute()
        self.assertFalse(self.journal.pending.exists())

    def test_symlink_hardlink_and_writable_inputs_rejected(self):
        target = Path(self.candidate['directory']) / p.MODULE
        other = self.root / 'other'
        for mode in ('symlink', 'hardlink', 'writable'):
            with self.subTest(mode=mode):
                target.unlink()
                if other.exists():
                    other.unlink()
                put(other, self.new_module)
                if mode == 'symlink':
                    target.symlink_to(other)
                elif mode == 'hardlink':
                    os.link(other, target)
                else:
                    put(target, self.new_module, 0o666)
                with self.assertRaises(ValueError):
                    self.execute()
        self.assertFalse(self.journal.pending.exists())


class ContractTests(unittest.TestCase):
    def test_native_inventory_bounds_and_required_contract(self):
        raw = amxx(['nvault_process_commit', 'nvault_process_version'])
        self.assertEqual({'nvault_process_commit', 'nvault_process_version'}, p.native_names(raw))
        for bad in (b'', raw[:20], raw + b'x'):
            if bad == raw + b'x':
                bad = bytearray(raw)
                struct.pack_into('<I', bad, 12, 16 * 1024 * 1024 + 1)
            with self.assertRaises(ValueError):
                p.native_names(bad)

    def test_duplicate_json_and_conflicting_baseline_entries_rejected(self):
        with self.assertRaises(ValueError):
            p.decode(b'{"schema":1,"schema":1}')
        row = ('0' * 64 + '  /synthetic\n').encode()
        self.assertEqual(2, len(p.baseline_rows(row + row)))
        with self.assertRaises(ValueError):
            p.baseline_rows(row + row.replace(b'0', b'1'))

    def test_semantics_include_values_timestamps_and_names(self):
        a = {'one.vault': vault(), 'one.journal': None}
        self.assertNotEqual(p.semantic(a), p.semantic({**a, 'one.vault': vault(stamp=1700000001)}))
        self.assertNotEqual(p.semantic(a), p.semantic({**a, 'one.vault': vault(b'changed')}))
        with self.assertRaises(ValueError):
            p.semantic({**a, 'one.journal': b'pending'})

    def test_semantics_ignore_checkpoint_entry_order(self):
        first, second = vault(b'first')[10:], vault(b'second')[10:].replace(b'synthetic-player', b'synthetic-second')
        header = struct.pack('<IHI', 0x6E564C54, 0x200, 2)
        self.assertEqual(p.semantic({'one.vault': header + first + second}),
                         p.semantic({'one.vault': header + second + first}))


if __name__ == '__main__':
    os.umask(0o077)
    if len(sys.argv) > 1 and sys.argv[1] == '--fixture-hook':
        fixture_hook()
    else:
        if os.geteuid() != 0:
            raise SystemExit('Run only in a disposable Linux root fixture.')
        unittest.main(verbosity=2)
