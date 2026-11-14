"""Linux service, credential, stop and publication rehearsals without network."""
import copy
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import select
import signal
import shutil
import subprocess
import tempfile
import unittest
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('trigger_service', ROOT/'ops/availability/archive_trigger_service.py')
service = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(service)
state = service.state
NOW = dt.datetime(2026,11,14,12,53,tzinfo=state.UTC)


@unittest.skipUnless(os.name == 'posix', 'Linux service rehearsal')
class Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.projection = self.root/'latest.json'
        self.ledger = state.Ledger.create(self.root/'ledger.db',NOW)
        self.config = dict(schema=1,mode='external',generation=str(uuid.uuid4()),exporter='a'*40,
            manifest=service.workflow.content_manifest(ROOT),activation=state.stamp(NOW),
            coverage_end=state.stamp(NOW-dt.timedelta(hours=1)),assessment_closed=False)
        self.calls=[]
        test=self
        class Client:
            def dispatch(self, request):
                test.calls.append(request)
                return {'status':200,'workflow_run_id':123}
            def evidence(self, row, now):
                return None
        self.client = Client()

    def tearDown(self):
        self.ledger.close()
        self.temp.cleanup()

    def run_pass(self, config=None, factory=None, clock=lambda:NOW):
        return service.run_pass(self.ledger, config or self.config, self.projection,
                                factory or (lambda:self.client),clock)

    def test_paused_never_loads_token_or_sends(self):
        def forbidden():raise AssertionError('credential loaded')
        config=dict(self.config,mode='paused')
        self.assertEqual(self.run_pass(config,forbidden),'paused')
        self.assertEqual(json.loads(self.projection.read_text()),{})

    def test_one_dispatch_then_pending_poll_without_new_post(self):
        self.assertEqual(self.run_pass(),'dispatched')
        self.assertEqual(self.run_pass(),'reconciled')
        self.assertEqual(len(self.calls),1)

    def test_pause_drains_accepted_without_dispatch(self):
        self.run_pass()
        self.assertEqual(self.run_pass(dict(self.config,mode='paused')),'reconciled')
        self.assertEqual(len(self.calls),1)

    def test_unknown_blocks_even_changed_generation(self):
        self.run_pass()
        row=self.ledger.connection.execute('SELECT claim FROM claims').fetchone()
        self.ledger.unknown(row['claim'],NOW)
        self.assertEqual(self.run_pass(dict(self.config,generation=str(uuid.uuid4()))),'blocked-uncertain')
        self.assertEqual(len(self.calls),1)
        self.assertEqual(json.loads(self.projection.read_text()),{})

    def test_missing_token_does_not_reserve_new_claim(self):
        def missing():raise FileNotFoundError()
        with self.assertRaises(FileNotFoundError):self.run_pass(factory=missing)
        self.assertEqual(self.ledger.connection.execute('SELECT count(*) FROM claims').fetchone()[0],0)

    def test_bounded_catchup_rejects_more_than_24_hours(self):
        config=dict(self.config,coverage_end=state.stamp(NOW-dt.timedelta(hours=25)))
        with self.assertRaisesRegex(ValueError,'catchup-bound-exceeded'):self.run_pass(config)
        self.assertEqual(self.calls,[])

    def test_already_covered_slot_is_not_reexported(self):
        config=dict(self.config,coverage_end=state.window(state.stamp(NOW))[1])
        self.assertEqual(self.run_pass(config),'slot-covered')
        self.assertEqual(self.calls,[])

    def test_restart_prepared_claim_dispatches_only_once(self):
        row=self.ledger.prepare(self.config['generation'],state.stamp(NOW),'a'*40,self.config['manifest'],NOW)
        self.assertEqual(self.run_pass(),'dispatched')
        self.assertEqual(self.ledger.get(row['claim'])['state'],'Accepted')
        self.assertEqual(self.run_pass(),'reconciled')
        self.assertEqual(len(self.calls),1)

    def test_stale_prepared_claim_closes_without_send(self):
        row=self.ledger.prepare(self.config['generation'],state.stamp(NOW),'a'*40,self.config['manifest'],NOW)
        self.assertEqual(self.run_pass(clock=lambda:NOW+dt.timedelta(hours=1)),'prepared-retired')
        self.assertEqual(self.ledger.get(row['claim'])['state'],'Failed-known')
        self.assertEqual(self.calls,[])

    def test_lock_spans_network_and_blocks_second_owner(self):
        with service.locked(self.root):
            with self.assertRaises(BlockingIOError):
                with service.locked(self.root):pass
        with service.locked(self.root):pass

    def test_atomic_projection_replaces_only_known_file(self):
        service.publish(self.projection,{'first':1})
        service.publish(self.projection,{'second':2})
        self.assertEqual(json.loads(self.projection.read_text()),{'second':2})
        self.assertEqual(self.projection.stat().st_mode & 0o777,0o640)
        self.assertEqual(list(self.root.glob('.projection-*')),[])

    def test_symlink_projection_never_overwrites_target(self):
        target=self.root/'target';target.write_text('preserve')
        self.projection.symlink_to(target)
        with self.assertRaises(ValueError):service.publish(self.projection,{})
        self.assertEqual(target.read_text(),'preserve')

    def test_credential_permissions_bounds_and_no_symlinks(self):
        path=self.root/'github-token';path.write_text('github_pat_synthetic\n');path.chmod(0o600)
        self.assertEqual(service.load_token(self.root),'github_pat_synthetic')
        path.chmod(0o644)
        with self.assertRaises(ValueError):service.load_token(self.root)
        path.chmod(0o600);path.write_text('x'*514)
        with self.assertRaises(ValueError):service.load_token(self.root)
        path.unlink();path.symlink_to(self.root/'ledger.db')
        with self.assertRaises(ValueError):service.load_token(self.root)

    def test_symlink_credential_directory_is_rejected(self):
        path=self.root/'github-token';path.write_text('synthetic');path.chmod(0o600)
        link=self.root/'link';link.symlink_to(self.root,target_is_directory=True)
        with self.assertRaises(ValueError):service.load_token(link)

    def test_config_strict_schema(self):
        self.assertEqual(service.configuration(json.dumps(self.config).encode()),self.config)
        for bad in (dict(self.config,schema=True),dict(self.config,mode='other'),
                    dict(self.config,token='synthetic'),dict(self.config,assessment_closed=1)):
            with self.subTest(bad=bad),self.assertRaises(ValueError):
                service.configuration(json.dumps(bad).encode())

    def test_process_stop_releases_lock_but_retains_attempt(self):
        read_fd,write_fd=os.pipe()
        child=os.fork()
        if child==0:
            os.close(read_fd)
            try:
                with service.locked(self.root):
                    ledger=state.Ledger(self.root/'ledger.db')
                    row=ledger.prepare(self.config['generation'],state.stamp(NOW),'a'*40,self.config['manifest'],NOW)
                    ledger.begin(row['claim'],NOW)
                    os.write(write_fd,b'R')
                    signal.pause()
            finally:os._exit(0)
        os.close(write_fd)
        try:
            self.assertTrue(select.select([read_fd],[],[],5)[0])
            self.assertEqual(os.read(read_fd,1),b'R')
        finally:
            os.kill(child,signal.SIGTERM);os.waitpid(child,0);os.close(read_fd)
        with service.locked(self.root):
            self.assertEqual(self.run_pass(),'blocked-uncertain')
        self.assertEqual(self.ledger.connection.execute('SELECT state FROM claims').fetchone()[0],'Attempting')

    def test_service_unit_has_credential_and_no_restart_or_shell(self):
        unit=(ROOT/'ops/production/systemd/goldsrcops-archive-trigger.service').read_text()
        for setting in ('LoadCredential=github-token:', 'User=goldsrcops-archive-trigger',
                        'Restart=no','TimeoutStartSec=90s','KillMode=control-group',
                        'ProtectSystem=strict','MemoryMax=128M','TasksMax=16'):
            self.assertIn(setting,unit)
        self.assertNotIn('/bin/sh',unit)
        self.assertNotIn('Environment=GITHUB_TOKEN',unit)

    def test_unit_verification_on_native_temporary_files(self):
        for name in ('goldsrcops-archive-trigger.service','goldsrcops-archive-trigger.timer'):
            target=self.root/name
            shutil.copyfile(ROOT/'ops/production/systemd'/name,target)
            target.chmod(0o644)
        result=subprocess.run(['systemd-analyze','verify',str(self.root/'goldsrcops-archive-trigger.service'),
            str(self.root/'goldsrcops-archive-trigger.timer')],stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=20)
        self.assertEqual(result.returncode,0,result.stderr.decode())
        self.assertEqual(result.stderr,b'')

    def test_exclusive_stdin_store_never_overwrites(self):
        spec=importlib.util.spec_from_file_location('credential_store',ROOT/'ops/availability/archive_trigger_store_credential.py')
        helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
        self.root.chmod(0o700)
        helper.store(self.root,b'github_pat_synthetic',owner=os.geteuid())
        self.assertEqual(service.load_token(self.root),'github_pat_synthetic')
        with self.assertRaises(FileExistsError):helper.store(self.root,b'other_synthetic',owner=os.geteuid())
        self.assertEqual(service.load_token(self.root),'github_pat_synthetic')

    def test_hidden_transfer_uses_stdin_not_arguments(self):
        spec=importlib.util.spec_from_file_location('credential_transfer',ROOT/'tools/release/set-archive-trigger-credential.py')
        helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
        calls=[]
        def run(args,**kwargs):
            calls.append((args,kwargs))
            return subprocess.CompletedProcess(args,0,b'ARCHIVE_TRIGGER_CREDENTIAL=stored-exclusively\n')
        helper.transfer('approved-fixture','github_pat_synthetic',run)
        self.assertEqual(calls[0][1]['input'],b'github_pat_synthetic')
        self.assertNotIn('github_pat_synthetic',repr(calls[0][0]))
        self.assertIn('StrictHostKeyChecking=yes',calls[0][0])
        with self.assertRaises(ValueError):helper.transfer('-oProxyCommand=other','synthetic',run)

    def test_package_is_review_only_verified_and_exclusive(self):
        spec=importlib.util.spec_from_file_location('trigger_package',ROOT/'tools/release/build-archive-trigger.py')
        helper=importlib.util.module_from_spec(spec);spec.loader.exec_module(helper)
        def identity(args,**kwargs):
            return 'a'*40 if args[1]=='rev-parse' else b' M fixture.py'
        with patch.object(helper.subprocess,'check_output',side_effect=identity):
            target,_=helper.build(self.root/'package','2.132.0',review=True)
            with self.assertRaises(ValueError):helper.build(self.root/'package','2.132.0',review=True)
            with self.assertRaises(ValueError):helper.build(self.root/'release','2.132.0',review=False)
        import zipfile,hashlib
        with zipfile.ZipFile(target) as archive:
            metadata=json.loads(archive.read('manifest.json'))
            self.assertTrue(metadata['reviewOnly'])
            for item in metadata['files']:
                self.assertEqual(hashlib.sha256(archive.read(item['path'])).hexdigest(),item['sha256'])
            self.assertEqual(json.loads(archive.read('config.template.json'))['mode'],'paused')


if __name__=='__main__':unittest.main()
