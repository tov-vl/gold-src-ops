#!/usr/bin/env python3
"""Disposable Linux-root pair/CAS/backup workflow acceptance; no network or services."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
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

    def backup_guard(self):
        # Exercise the real baseline/quiescence shell guard. Only service and
        # persistent-profile dependencies belong to this disposable adapter.
        u.Host.backup_guard(self)

    def guard(self, phase):
        if phase == 'quiescent':
            p.require(self.active is False)
        elif phase in ('preflight', 'running', 'baseline'):
            p.require(self.active is True)
        else:
            p.require(phase == 'identity')

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
        self.active = True
        put(self.root / 'service-active', 'active')
        self.generation += 1

    def stop(self):
        self.calls.append('stop')
        if (self.root / 'stop-uncertain').exists():
            raise u.Uncertain('Interrupted stop')
        self.active = False
        put(self.root / 'service-active', 'inactive')

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
        self.server, self.config, self.state = [self.root / x for x in ('server', 'config', 'state')]
        for directory in (self.server, self.config, self.state):
            directory.mkdir(mode=0o700)
        self.vault = self.server / p.AMXX / 'data/vault'
        self.vault.mkdir(parents=True, mode=0o700)
        for name in u.base.bundle.NAMES:
            put(self.vault / name, b'' if name.endswith('.journal') else vault(), 0o640)
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
        put(self.root / 'unrelated', 'preserve')
        rows = [(p.digest(v), str(self.server / k)) for k,v in self.old.items()]
        rows += [(p.digest(self.old_receipt), str(self.receipt)), (p.digest(b'preserve'), str(self.root / 'unrelated'))]
        self.before = ''.join(h + '  ' + name + '\n' for h,name in rows).encode()
        self.after = p.transition(self.before, self.server, self.config, self.old, self.new, self.old_receipt, self.new_receipt)
        self.base = self.config / 'player-data-backup-baseline.sha256'
        put(self.base, self.before)
        put(self.root / 'baseline-before', self.before)
        put(self.root / 'baseline-after', self.after)
        self.hook = self.root / 'hook.sh'
        put(self.hook, '#!/bin/sh\nexec python3 -B '+str(Path(__file__).resolve())+' --fixture-hook "$@"\n')
        ref = lambda x: dict(path=str(x), sha256=p.digest(p.read(x)))
        self.plan = dict(schema=1, operationId='fixture', previous=self.previous, candidate=self.candidate,
            previousModule=ref(self.root / 'stock.so'),
            baselines=dict(previous=ref(self.root / 'baseline-before'), candidate=ref(self.root / 'baseline-after')),
            hooks={key: ref(self.hook) for key in u.HOOKS})
        put(self.root / 'candidate-hash', self.candidate['sha256'])
        scripts, commands = self.root / 'scripts', self.root / 'bin'
        scripts.mkdir(mode=0o700); commands.mkdir(mode=0o700)
        shutil.copyfile(SOURCE / 'player-data-schedule-guard.sh', scripts / 'player-data-schedule-guard.sh')
        put(scripts / 'game-event-persistent.sh', 'source "$REPO_SOURCE/game-event-persistent.sh"\n'
            'configuration_directory="$PAIR_FIXTURE/config"\n'
            'live_amxx_root="$PAIR_FIXTURE/server/cstrike/addons/amxmodx"\n'
            'service_user=root; service_group=root\nverify_persistent_files() { :; }\n')
        put(commands / 'systemctl', '#!/bin/sh\ncase "$*" in *MainPID*) echo 0;; *) cat "$PAIR_FIXTURE/service-active";; esac\n', 0o700)
        put(commands / 'pgrep', '#!/bin/sh\nexit 1\n', 0o700)
        put(self.root / 'service-active', 'active')
        self.environment = patch.dict(os.environ, {'PAIR_FIXTURE': str(self.root), 'REPO_SOURCE': str(SOURCE),
                                                   'PATH': str(commands) + ':' + os.environ['PATH']})
        self.environment.start()
        self.addCleanup(self.environment.stop)
        original_directory = patch.object(u, 'DIRECTORY', scripts)
        original_directory.start()
        self.addCleanup(original_directory.stop)
        if os.environ.get('PAIR_READER_DIR'):
            put(self.root / 'probe-new', (Path(os.environ['PAIR_READER_DIR']) / 'player-storage-new').read_bytes(), 0o755)
            scratch = self.root / 'scratch-base'
            scratch.mkdir(mode=0o700)
            os.chown(scratch, 65534, 65534)
        self.refresh()

    def refresh(self):
        put(self.root / 'plan.json', json.dumps(self.plan))
        self.identity = p.digest(p.read(self.root / 'plan.json'))
        self.journal = u.Journal(self.state, self.identity)
        self.host = FixtureHost.__new__(FixtureHost)
        self.host.root, self.host.plan, self.host.journal = self.root, self.plan, self.journal
        self.host.case_name = self._testMethodName
        self.host.server, self.host.config_root, self.host.base, self.host.vault = self.server, self.config, self.base, self.vault
        self.host.active, self.host.generation, self.host.calls = True, 1, []
        self.host.initialize()

    def execute(self):
        u.read_plan(self.root / 'plan.json', self.identity, self.server, self.config)
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
        self.terminal('accepted', 'candidate')
        self.assertEqual(['stop', 'start', 'stop', 'start'], self.host.calls)
        self.assertEqual(3, sum(a != b for a,b in zip(self.before.splitlines(), self.after.splitlines())))

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
        self.before += self.before.splitlines(keepends=True)[-1]
        self.after = p.transition(self.before, self.server, self.config, self.old, self.new,
                                  self.old_receipt, self.new_receipt)
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
        for index in range(4):
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
