#!/usr/bin/env python3
"""Real ReHLDS and packages; container process adapter replaces systemd only."""
import importlib.util
import json
import os
from pathlib import Path
import pwd
import shutil
import signal
import subprocess
import time
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('fixture', HERE / 'addon-upgrade.py')
f = importlib.util.module_from_spec(spec)
spec.loader.exec_module(f)
u = f.u
put = f.put


class EngineRehearsal(f.WorkflowTests):
    def make_package(self, name):
        source = Path('/' + name)
        expected = {'previous': '03950d9b54e20718fcb836891652b8879e1ec03a02837f420492d3a12bedef61',
                    'candidate': '7dbc46af897046258478eca5f8d38a575e36924aa0f92bdd7d79a30f467bb075'}
        self.assertEqual(expected[name], u.baseline.digest((source/'manifest.json').read_bytes()))
        target = self.root / name
        shutil.copytree(source, target)
        for p in target.rglob('*'):
            p.chmod(0o700 if p.is_dir() else 0o600)
        return {'directory': str(target), 'sha256': u.digest_file(target / 'manifest.json')}

    def prepare_engine(self):
        self.host.stop()  # Stop the initial service adapter before attaching the real process.
        for path in self.host.vault.iterdir():
            path.unlink()  # Only synthetic placeholder data from the parent fixture.
        installed = {p: p.read_bytes() for p in (self.amxx / 'configs').rglob('*') if p.is_file()}
        shutil.copytree('/engine-base', self.server, dirs_exist_ok=True, symlinks=True)
        for path, data in installed.items():
            path.write_bytes(data)
        self.root.chmod(0o755)
        self.server.chmod(0o755)
        steam = pwd.getpwnam('steam')
        self.uid, self.gid = steam.pw_uid, steam.pw_gid
        for path in [self.server, *self.server.rglob('*')]:
            os.chown(path, self.uid, self.gid, follow_symlinks=False)
        for path in [self.amxx / 'plugins', self.amxx / 'configs', self.amxx / 'configs/plugins', self.amxx / 'data', self.amxx / 'data/lang']:
            os.chown(path, 0, self.gid)
            path.chmod(0o750)
        for rel in u.package(self.previous, self.kind)[0]:
            path = self.server / rel
            os.chown(path, 0, self.gid)
            path.chmod(0o640)
        os.chown(self.config / (self.kind+'-installed'), 0, self.gid)
        self.host.vault.chmod(0o700)
        wrapper = self.scripts / 'game-event-persistent.sh'
        wrapper.write_text(wrapper.read_text().replace('service_group=root; service_user=root;', 'service_group=steam; service_user=steam;'))
        self.secret = os.urandom(24).hex()
        put(self.root / 'password', self.secret)
        put(self.server / 'cstrike/server.cfg', 'hostname Rehearsal\nsv_lan 1\nsv_password ""\nrcon_password "'+self.secret+'"\nmp_round_infinite 1\n', 0o640)
        os.chown(self.server / 'cstrike/server.cfg', 0, self.gid)
        self.process = None
        self.generation = 0
        self.addCleanup(self.cleanup_engine)
        self.host.stop = self.stop_engine
        self.host.start = self.start_engine
        self.host.unit = self.unit
        self.host.empty = lambda: u.policy.probe({'host': '127.0.0.1', 'port': 27025, 'expectedName': 'Rehearsal'})
        self.host.config = {'host': '127.0.0.1', 'port': 27025, 'expectedName': 'Rehearsal'}
        self.plugin_list = self.amxx / 'configs/plugins-goldsrcops-weapons.ini'
        self.loader = self.plugin_list.read_bytes()
        shutil.copyfile('/compiled/addon_upgrade_vault.amxx', self.amxx / 'plugins/addon_upgrade_vault.amxx')
        (self.amxx / 'plugins/addon_upgrade_vault.amxx').chmod(0o644)
        self.plugin_list.write_text('addon_upgrade_vault.amxx\n')
        self.start_engine()
        self.console('upgrade_vault_seed', 'UPGRADE_VAULT_SEED=passed')
        self.stop_engine()
        self.saved = self.vault_snapshot()
        self.plugin_list.write_bytes(self.loader)
        self.start_engine()
        expectations = {self.previous['sha256']: '0.24.0', self.candidate['sha256']: '0.25.0'}
        put(self.root / 'expectations.json', json.dumps(expectations))
        probe = self.root / 'accept.sh'
        put(probe, '#!/usr/bin/env bash\nset -Eeuo pipefail\nexec python3 -I -B /repo/ops/gameserver/addon-runtime-probe.py '
            '--expectations '+str(self.root/'expectations.json')+' --expectations-sha256 '+u.digest_file(self.root/'expectations.json')+
            ' --error-directory '+str(self.amxx/'logs')+' --port 27025 "$@" < '+str(self.root/'password')+'\n')
        self.probe = {'path': str(probe), 'sha256': u.digest_file(probe)}
        self.plan['acceptance'] = self.probe
        self.new_plan(self.previous, self.candidate, 'upgrade')
        self.host.accept(self.previous)
        install = self.host.install
        def checked_install(*args):
            before = self.closed_vault_state()
            install(*args)
            self.assertEqual(before, self.closed_vault_state())
        self.host.install = checked_install

    def closed_vault_state(self):
        self.assertIsNone(self.process)
        result = {}
        for path in self.host.vault.iterdir():
            stat = path.stat()
            result[path.name] = (path.read_bytes(), stat.st_mode, stat.st_uid, stat.st_gid, stat.st_mtime_ns)
        return result

    def vault_snapshot(self):
        return {p.name: p.read_bytes() for p in self.host.vault.glob('*.vault')}

    def unit(self, name):
        if name == u.AGENT:
            return {'ActiveState': 'active', 'SubState': 'running', 'InvocationID': 'agent-adapter', 'NRestarts': '0'}
        running = self.process is not None and self.process.poll() is None
        return {'ActiveState': 'active' if running else 'inactive', 'SubState': 'running' if running else 'dead',
                'InvocationID': str(self.generation), 'NRestarts': '0'}

    def start_engine(self):
        self.assertTrue(self.process is None or self.process.poll() is not None)
        self.generation += 1
        self.log = self.root / ('engine-'+str(self.generation)+'.log')
        self.output = self.log.open('wb')
        self.process = subprocess.Popen(['gosu', 'steam', './hlds_run', '-norestart', '-game', 'cstrike',
            '-strictportbind', '+ip', '127.0.0.1', '-port', '27025', '+map', 'de_dust2',
            '+servercfgfile', 'server.cfg', '-maxplayers', '4'], cwd=self.server,
            stdin=subprocess.PIPE, stdout=self.output, stderr=subprocess.STDOUT, start_new_session=True)
        put(self.root / 'state-active', 'active')
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            self.assertIsNone(self.process.poll(), 'Engine exited during startup')
            try:
                if self.host.empty():
                    return
            except (ValueError, OSError):
                pass
            time.sleep(0.2)
        raise AssertionError('Engine readiness timed out')

    def console(self, command, marker):
        offset = self.log.stat().st_size
        self.process.stdin.write((command+'\n').encode())
        self.process.stdin.flush()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if marker in self.log.read_bytes()[offset:].decode('utf-8', errors='replace'):
                return
            time.sleep(0.1)
        raise AssertionError('Console acceptance marker missing: '+marker+'\n'+self.log.read_text(errors='replace')[-4000:].replace(self.secret, '[redacted]'))

    def stop_engine(self):
        if self.process is not None and self.process.poll() is None:
            self.process.stdin.write(b'quit\n')
            self.process.stdin.flush()
            self.assertEqual(0, self.process.wait(timeout=15))
            self.process.stdin.close()
            self.output.close()
        self.process = None
        put(self.root / 'state-active', 'inactive')

    def cleanup_engine(self):
        if self.process is not None and self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGKILL)
            self.process.wait(timeout=10)
            self.process.stdin.close()
            self.output.close()

    def new_plan(self, previous, candidate, operation):
        self.plan.update(previous=previous, candidate=candidate, operationId=operation,
                         baselineSha256=u.digest_file(self.base))
        put(self.plan_path, json.dumps(self.plan))
        self.journal = u.Journal(self.state, u.digest_file(self.plan_path))
        self.host.plan, self.host.journal = self.plan, self.journal

    def transition(self, expected, package):
        u.read_plan(self.plan_path, self.journal.identity)
        with u.policy.lock(self.config/'game-event-pilot-install.lock', shared=True) as lock_fd:
            fd = u.fcntl.fcntl(lock_fd, u.fcntl.F_DUPFD_CLOEXEC, 10)
            self.host.transition_fd = fd
            try:
                result = u.execute(self.plan, self.journal, self.host)
                failure = self.journal.directory/'failed.json'
                self.assertEqual(expected, result, failure.read_text() if failure.exists() else '')
            finally:
                os.close(fd)
                self.host.transition_fd = None
        self.assertFalse(self.journal.pending.exists())
        old, new = self.host.prepare(package, u.digest_file(self.base))
        self.assertEqual(old, new)
        self.host.accept(package)
        self.stop_engine()
        self.assertEqual(self.saved, self.vault_snapshot())
        self.start_engine()
        print('ENGINE_TRANSITION='+expected+' operation='+self.plan['operationId'], flush=True)

    def lookup(self, kills):
        # Exact response row proves both persistent activation and data after restart.
        self.console('goldsrcops_player_lookup 0123456789abcdef 52656865617273616c',
                     'GSPROW 1 1 '+kills+' 9 52656865617273616C')

    def test_engine_transitions(self):
        self.prepare_engine()
        # A disk-side candidate cannot prove the still-running predecessor.
        paths, hashes = u.package(self.candidate, self.kind)
        binary = self.server / paths[0]
        predecessor = binary.read_bytes()
        try:
            binary.write_bytes((Path(self.candidate['directory'])/paths[0]).read_bytes())
            self.assertEqual(hashes[paths[0]], u.digest_file(binary))
            with self.assertRaises(ValueError):
                self.host.accept(self.candidate)
        finally:
            binary.write_bytes(predecessor)
        self.transition('accepted', self.candidate)
        # transition() has already performed a cold restart. No RCON CVAR writes.
        self.lookup('37')
        self.console('changelevel de_inferno', 'Mapchange to de_inferno')
        self.host.accept(self.candidate)
        self.lookup('37')
        # Simulate new saved progress after activation using the isolated vault helper.
        self.stop_engine()
        self.plugin_list.write_text('addon_upgrade_vault.amxx\n')
        self.start_engine()
        self.console('upgrade_vault_advance', 'UPGRADE_VAULT_ADVANCE=passed')
        self.stop_engine()
        self.saved = self.vault_snapshot()
        self.plugin_list.write_bytes(self.loader)
        self.start_engine()
        self.lookup('38')
        self.new_plan(self.candidate, self.previous, 'planned-rollback')
        self.transition('accepted', self.previous)
        # Only the candidate fails an additional frozen acceptance condition.
        # The same standard probe first proves its actual loaded version.
        reject = self.root/'reject.sh'
        put(reject, '#!/usr/bin/env bash\nset -Eeuo pipefail\nbash '+self.probe['path']+' "$@"\n[[ "$2" != "'+self.candidate['sha256']+'" ]]\n')
        self.plan['acceptance'] = {'path': str(reject), 'sha256': u.digest_file(reject)}
        self.new_plan(self.previous, self.candidate, 'rejected-candidate')
        self.transition('rolled-back', self.previous)
        self.assertEqual(self.original_baseline, self.base.read_bytes())
        self.stop_engine()
        self.plugin_list.write_text('addon_upgrade_vault.amxx\n')
        self.start_engine()
        self.console('upgrade_vault_verify advanced', 'UPGRADE_VAULT_VERIFY=passed')
        self.stop_engine()
        self.assertEqual(self.saved, self.vault_snapshot())
        self.assertFalse(list((self.amxx/'logs').glob('error_*.log')))
        print('ADDON_ENGINE_REHEARSAL=passed upgrade_rollback_rejection_vaults_baseline', flush=True)


if __name__ == '__main__':
    # Do not repeat the synthetic parent suite or its fault injectors on a real engine.
    suite = unittest.TestSuite([EngineRehearsal('test_engine_transitions')])
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    raise SystemExit(not result.wasSuccessful())
