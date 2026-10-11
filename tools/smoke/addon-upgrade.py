#!/usr/bin/env python3
"""Root-only disposable fixture: real installer, receipts, vault files and baseline."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / 'ops/gameserver'
spec = importlib.util.spec_from_file_location('upgrade', SOURCE / 'addon-upgrade.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


def put(path, text, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text)
    path.chmod(mode)


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.kind = 'map-menu' if self._testMethodName.startswith('test_map_menu_') else 'weapon-selection'
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config, self.server, self.state = [self.root / n for n in ('config', 'server', 'state')]
        self.scripts, self.bin = self.root / 'scripts', self.root / 'bin'
        for p in (self.config, self.server, self.state, self.scripts, self.bin):
            p.mkdir(mode=0o700)
        self.amxx = self.server / 'cstrike/addons/amxmodx'
        for part in ('plugins', 'configs/plugins', 'data/lang', 'data/vault'):
            (self.amxx / part).mkdir(parents=True, mode=0o750)
        for part in ('plugins', 'configs', 'configs/plugins', 'data', 'data/lang'):
            (self.amxx / part).chmod(0o750)
        (self.amxx / 'data/vault').chmod(0o700)
        for name in u.bundle.NAMES:
            put(self.amxx / 'data/vault' / name, 'saved-state-' + name)
        for name in ('weapon-selection-addon.sh', 'addon-upgrade-guard.sh', 'player-data-schedule-guard.sh'):
            shutil.copyfile(SOURCE / name, self.scripts / name)
        put(self.scripts / 'game-event-persistent.sh', '''source "$REPO_SOURCE/game-event-persistent.sh"
read_prepared_marker() { prepared_operator_user=operator; service_group=root; service_user=root; }
verify_persistent_files() { read_prepared_marker; marker_schema_version=2; marker_profile_sha256="$FAST_LOADOUT_PROFILE_SHA256"; }
require_unit_state() { [[ "$(cat "$FIXTURE/state-active")" == active ]]; }
require_settled_agent_status() { :; }
''')
        put(self.bin / 'systemctl', r'''#!/usr/bin/env python3
import os,pathlib,sys
r=pathlib.Path(os.environ['FIXTURE']);a=sys.argv[1:]
if a[0] in ('start','stop'):
 assert a[1]=='goldsrcops-gameserver.service'
 with (r/'calls').open('a') as f:f.write(a[0]+'\n')
 if (r/('fail-'+a[0])).exists():sys.exit(1)
 (r/'state-active').write_text('active' if a[0]=='start' else 'inactive')
 if a[0]=='start':
  (r/'invocation').write_text((r/'invocation').read_text()+'x')
  (r/'loaded-receipt').write_bytes((r/'config'/(os.environ['ADDON_KIND']+'-installed')).read_bytes())
else:
 assert a[0]=='show'
 active=(r/'state-active').read_text()
 if '--value' in a:
  print('0' if 'MainPID' in a else active)
 else:
  agent='goldsrcops-game-event-agent.service' in a
  print('ActiveState='+('active' if agent else active))
  print('SubState='+('running' if agent or active=='active' else 'dead'))
  print('InvocationID='+('agent' if agent else (r/'invocation').read_text()))
  print('NRestarts=0')
''', 0o700)
        put(self.bin / 'install', '''#!/usr/bin/env bash
if [[ "${!#}" == *goldsrcops_weapon_selection.amxx && -e "$FIXTURE/fail-install" ]]; then
 printf broken > "${!#}"
 exit 1
fi
exec /usr/bin/install "$@"
''', 0o700)
        put(self.bin / 'pgrep', '#!/usr/bin/env bash\n[[ "$(cat "$FIXTURE/state-active")" == active ]]\n', 0o700)
        self.env = patch.dict(os.environ, {'PATH': str(self.bin) + ':' + os.environ['PATH'],
            'FIXTURE': str(self.root), 'ADDON_KIND': self.kind, 'REPO_SOURCE': str(SOURCE), 'SUDO_USER': 'operator',
            'SSH_CONNECTION': 'fixture', 'GOLDSRCOPS_CONFIGURATION_DIRECTORY': str(self.config),
            'GOLDSRCOPS_SERVICE_HOME': str(self.root)})
        self.env.start()
        self.addCleanup(self.env.stop)
        self.globals = patch.multiple(u, CONFIG=self.config, SERVER=self.server, DIRECTORY=self.scripts)
        self.globals.start()
        self.addCleanup(self.globals.stop)
        put(self.root / 'state-active', 'inactive')
        put(self.root / 'invocation', 'game')
        self.previous = self.make_package('previous')
        self.candidate = self.make_package('candidate')
        u.run(['bash', str(self.scripts / 'weapon-selection-addon.sh')] + (['--map-menu'] if self.kind=='map-menu' else []) + ['--install', '--bundle', self.previous['directory'],
               '--manifest-sha256', self.previous['sha256'], '--apply'])
        put(self.root / 'unrelated', 'preserve')
        paths, _ = u.package(self.previous, self.kind)
        files = [self.server / p for p in paths] + [self.config / (self.kind+'-installed'), self.root / 'unrelated']
        self.base = self.config / 'player-data-backup-baseline.sha256'
        put(self.base, ''.join(u.digest_file(p) + '  ' + str(p) + '\n' for p in files))
        self.original_baseline = self.base.read_bytes()
        acceptance = self.root / 'accept.sh'
        put(acceptance, '''#!/usr/bin/env bash
set -Eeuo pipefail
[[ "$1" == "$ADDON_KIND" ]]
grep -Fxq "manifest_sha256=$2" "$FIXTURE/loaded-receipt"
if [[ -e "$FIXTURE/reject-candidate" && "$2" == "$(cat "$FIXTURE/candidate-hash")" ]]; then exit 1; fi
''')
        put(self.root / 'candidate-hash', self.candidate['sha256'])
        self.plan = {'schema': 1, 'operationId': 'fixture-upgrade', 'kind': self.kind, 'previous': self.previous, 'candidate': self.candidate,
                     'baselineSha256': u.baseline.digest(self.original_baseline),
                     'acceptance': {'path': str(acceptance), 'sha256': u.digest_file(acceptance)}}
        self.plan_path = self.root / 'plan.json'
        put(self.plan_path, json.dumps(self.plan))
        self.journal = u.Journal(self.state, u.digest_file(self.plan_path))
        self.host = u.Host.__new__(u.Host)
        self.host.plan, self.host.journal, self.host.base = self.plan, self.journal, self.base
        self.host.vault = self.amxx / 'data/vault'
        self.host.empty = lambda: not (self.root / 'busy').exists()
        self.host.start()
        (self.root / 'calls').unlink()

    def make_package(self, name):
        d = self.root / name
        weapon=self.kind=='weapon-selection'
        names=('goldsrcops_weapon_selection.amxx','goldsrcops-weapon-selection.cfg',
               'plugins-goldsrcops-weapons.ini','goldsrcops-player-menu.txt') if weapon else (
               'goldsrcops_map_menu.amxx','goldsrcops-map-menu.cfg',
               'plugins-goldsrcops-maps.ini','goldsrcops-map-menu.txt')
        paths=['cstrike/addons/amxmodx/'+folder+'/'+n for folder,n in zip(
               ('plugins','configs/plugins','configs','data/lang'),names)]
        command='goldsrcops_weapons_enabled' if weapon else 'goldsrcops_maps_enabled'
        for p, text in zip(paths, (name, command+' "1"\n', names[0]+'\n', name)):
            put(d / p, text)
        manifest = {'schemaVersion': 2, 'purpose': 'game-host-addon' if weapon else 'map-menu-addon',
                    'productionInstallSupported': True, 'enabledByDefault': True,
                    'amxxVersion': '1.10.0.5481', 'reApiVersion': '5.24.0.300',
                    'sourceSha256': 'a'*64, 'payload': [{'path': p, 'sha256': u.digest_file(d/p)} for p in paths]}
        put(d / 'manifest.json', json.dumps(manifest))
        return {'directory': str(d), 'sha256': u.digest_file(d / 'manifest.json')}

    def execute(self):
        u.read_plan(self.plan_path, self.journal.identity)
        return u.execute(self.plan, self.journal, self.host)

    def assert_final(self, expected, manifest):
        self.assertEqual(expected, self.execute())
        self.assertFalse(self.journal.pending.exists())
        self.assertEqual(self.plan, u.policy.read_json(self.journal.directory/'plan.json'))
        self.assertEqual('agent', u.policy.read_json(self.journal.directory/'preflight-passed.json')['agent']['InvocationID'])
        self.assertIn('manifest_sha256='+manifest['sha256'], (self.config/(self.kind+'-installed')).read_text())
        old,new = self.host.prepare(manifest, u.digest_file(self.base))
        self.assertEqual(old,new)
        for name in u.bundle.NAMES:
            self.assertEqual('saved-state-'+name, (self.host.vault/name).read_text())
        self.assertEqual('active', self.host.unit(u.GAME)['ActiveState'])
        with self.assertRaises(ValueError):self.execute()

    def test_inherited_transition_lock_stays_held_through_installer(self):
        with u.policy.lock(self.config/'game-event-pilot-install.lock', shared=True) as lock_fd:
            fd=u.fcntl.fcntl(lock_fd,u.fcntl.F_DUPFD_CLOEXEC,10)
            self.addCleanup(os.close,fd)
            self.host.transition_fd=fd
            self.assert_final('accepted',self.candidate)
            with self.assertRaises(BlockingIOError):
                with u.policy.lock(self.config/'game-event-pilot-install.lock',shared=True):pass

    def test_wrong_inherited_lock_is_rejected_without_install(self):
        wrong=self.root/'wrong.lock'
        put(wrong,'')
        with wrong.open('r') as stream:
            fd=u.fcntl.fcntl(stream.fileno(),u.fcntl.F_DUPFD_CLOEXEC,10)
            self.addCleanup(os.close,fd)
            self.host.transition_fd=fd
            self.assert_final('rolled-back',self.previous)

    def test_upgrade_and_no_replay(self):
        self.assert_final('accepted', self.candidate)
        self.assertEqual(['stop','start'], (self.root/'calls').read_text().splitlines())
        self.assertEqual(3, sum(a!=b for a,b in zip(self.original_baseline.splitlines(),self.base.read_bytes().splitlines())))

    def test_map_menu_upgrade(self):
        self.assert_final('accepted',self.candidate)

    def test_map_menu_rollback(self):
        put(self.root/'reject-candidate','yes')
        self.assert_final('rolled-back',self.previous)

    def test_busy_refuses_before_stop(self):
        put(self.root/'busy','yes')
        self.assert_final('refused', self.previous)
        self.assertFalse((self.root/'calls').exists())

    def test_failed_install_restores_known_predecessor(self):
        put(self.root/'fail-install','yes')
        self.assert_final('rolled-back', self.previous)
        self.assertEqual(self.original_baseline,self.base.read_bytes())

    def test_failed_candidate_acceptance_rolls_back(self):
        put(self.root/'reject-candidate','yes')
        self.assert_final('rolled-back',self.previous)
        self.assertEqual(['stop','start','stop','start'],(self.root/'calls').read_text().splitlines())

    def test_failed_guard_after_baseline_update_rolls_back_baseline(self):
        original = self.host.guard
        def guard(phase):
            if phase=='baseline' and not (self.journal.directory/'baseline-rollback.json').exists():
                raise ValueError('candidate guard failed')
            original(phase)
        self.host.guard=guard
        self.assert_final('rolled-back',self.previous)
        self.assertEqual(self.original_baseline,self.base.read_bytes())
        self.assertTrue((self.journal.directory/'baseline-forward-evidence/result.json').exists())
        self.assertTrue((self.journal.directory/'baseline-rollback-evidence/result.json').exists())

    def test_start_failure_is_not_retried(self):
        put(self.root/'fail-start','yes')
        with self.assertRaises(u.Uncertain):self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop','start'],(self.root/'calls').read_text().splitlines())
        with self.assertRaises(ValueError):self.execute()

    def test_timeout_is_not_rolled_back_or_retried(self):
        self.host.install=lambda *a: (_ for _ in ()).throw(u.Uncertain('timeout'))
        with self.assertRaises(u.Uncertain):self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop'],(self.root/'calls').read_text().splitlines())

    def test_new_player_blocks_automatic_rollback(self):
        accept=self.host.accept
        def fail(item):
            if item==self.candidate:
                put(self.root/'busy','yes')
                raise ValueError('rejected')
            accept(item)
        self.host.accept=fail
        with self.assertRaises(ValueError):self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual(['stop','start'],(self.root/'calls').read_text().splitlines())

    def test_closed_vault_drift_never_becomes_successful_rollback(self):
        install=self.host.install
        def corrupt(*a):
            install(*a)
            put(self.host.vault/u.bundle.NAMES[0],'unexpected drift')
        self.host.install=corrupt
        with self.assertRaises(ValueError):self.execute()
        self.assertTrue(self.journal.pending.exists())
        self.assertEqual('unexpected drift',(self.host.vault/u.bundle.NAMES[0]).read_text())
        self.assertEqual(['stop'],(self.root/'calls').read_text().splitlines())

    def test_unrelated_drift_refuses_before_stop(self):
        put(self.root/'unrelated','changed')
        self.assertEqual('refused',self.execute())
        self.assertFalse((self.root/'calls').exists())

    def test_plan_and_payload_tampering_refused(self):
        with self.assertRaises(ValueError):u.read_plan(self.plan_path,'0'*64)
        p=Path(self.candidate['directory'])/'cstrike/addons/amxmodx/plugins/goldsrcops_weapon_selection.amxx'
        put(p,'tampered')
        with self.assertRaises(ValueError):self.execute()
        self.assertFalse(self.journal.pending.exists())
        self.assertFalse((self.root/'calls').exists())

    def test_missing_ssh_context_refused(self):
        with patch.dict(os.environ, {'SSH_CONNECTION':''}):
            with self.assertRaises(ValueError):self.host.guard('identity')
        self.host.guard('identity')

    def test_pending_blocks_different_plan(self):
        self.journal.claim(self.plan)
        other=u.Journal(self.state,'b'*64)
        with self.assertRaises(ValueError):other.claim(self.plan)
        self.assertTrue(self.journal.pending.exists())

    def test_process_timeout_kills_command_group(self):
        with self.assertRaises(u.Uncertain):u.run(['bash','-c','sleep 30'],timeout=0.05)


if __name__=='__main__':
    if os.geteuid()!=0:raise SystemExit('Run in a disposable Linux root fixture.')
    os.umask(0o077)
    unittest.main(verbosity=2)
