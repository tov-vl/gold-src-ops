"""Offline GitHub boundary, workflow admission, receipt and observer integration."""
import base64
import contextlib
import copy
import datetime as dt
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT/path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

github = load('github_adapter', 'ops/availability/archive_trigger_github.py')
workflow, state = github.workflow, github.state
monitor = load('monitor_adapter', 'ops/availability/archive-monitor.py')
NOW = dt.datetime(2026, 11, 14, 12, 53, tzinfo=state.UTC)


class Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.ledger = state.Ledger.create(Path(self.temp.name)/'ledger.db', NOW)
        self.generation = str(uuid.uuid4())
        self.row = self.ledger.prepare(self.generation, state.stamp(NOW), 'b'*40,
                                       workflow.content_manifest(ROOT), NOW)
        self.sent, self.responses = [], []
        test = self
        class Connection:
            def __init__(self, host, timeout):
                test.assertEqual(timeout, 10)
                self.host = host
            def request(self, method, path, body=None, headers=None):
                test.sent.append((self.host, method, path, body, headers))
            def getresponse(self):
                value = test.responses.pop(0)
                if isinstance(value, BaseException):
                    raise value
                return value
            def close(self):
                pass
        self.api = github.GitHub('synthetic-test-value', Connection)

    def tearDown(self):
        self.ledger.close()
        self.temp.cleanup()

    def response(self, value, status=200, location=None, raw=False):
        class Response:
            def __init__(self):
                self.status = status
            def read(self, bound):
                return (value if raw else json.dumps(value).encode())[:bound]
            def getheader(self, name, default=None):
                return {'Content-Type': 'application/json', 'Location': location}.get(name, default)
        return Response()

    def request(self):
        start, end = state.window(self.row['slot'])
        return dict(claim=self.row['claim'], generation=self.generation,
            slot=self.row['slot'], exporter='b'*40, manifest=workflow.content_manifest(ROOT),
            window_start=start, window_end=end)

    def event(self, probe=False):
        return {'inputs': {'confirm_archive': 'true', 'probe_only': str(probe).lower(),
                           'trigger_request': json.dumps(self.request())}}

    def gate(self, event=None, mode='external', enabled='false', attempt='1'):
        return workflow.gate(event or self.event(), mode, enabled, self.generation,
                             'b'*40, ROOT, NOW, attempt, 'refs/heads/main')

    def fixture(self):
        request = self.request()
        receipt = workflow.receipt(request, 123, 'c'*64, 512)
        run = dict(id=123, workflow_id=350269402, event='workflow_dispatch',
            head_branch='main', run_attempt=1, status='completed', conclusion='success',
            head_sha='d'*40, created_at=state.stamp(NOW))
        steps = [dict(name=name, status='completed', conclusion='success') for name in
            ('Validate Archive admission', 'Verify pinned exporter revision',
             'Export and archive primary evidence', 'Validate external Archive receipt')]
        job = dict(id=99, run_id=123, run_attempt=1, name='Archive Primary Evidence',
            status='completed', conclusion='success', started_at=state.stamp(NOW),
            completed_at=state.stamp(NOW), steps=steps)
        return receipt, run, dict(total_count=1, jobs=[job])

    def queue_evidence(self, receipt, run, jobs):
        self.responses.append(self.response(run))
        for path in workflow.FILES:
            raw = (ROOT/path).read_bytes()
            self.responses.append(self.response(dict(path=path, size=len(raw), encoding='base64',
                                                    content=base64.b64encode(raw).decode())))
        self.responses.append(self.response(jobs))
        raw = (state.stamp(NOW) + ' ARCHIVE_TRIGGER_RECEIPT=' + json.dumps(receipt) + '\n').encode()
        self.responses.append(self.response(raw, raw=True))

    def accepted(self):
        self.ledger.begin(self.row['claim'], NOW)
        self.ledger.accepted(self.row['claim'], 123, NOW)

    def test_dispatch_single_fixed_post_and_id(self):
        self.responses.append(self.response({'workflow_run_id':123, 'run_url':'https://untrusted.invalid'}))
        row = state.attempt_once(self.ledger, self.row['claim'], NOW, self.api.dispatch)
        self.assertEqual(row['state'], 'Accepted')
        host, method, path, body, headers = self.sent[0]
        self.assertEqual((host, method, path), ('api.github.com','POST',
            github.BASE+'/actions/workflows/350269402/dispatches'))
        self.assertEqual(headers['X-GitHub-Api-Version'], '2026-03-10')
        self.assertEqual(json.loads(body)['ref'], 'main')
        with self.assertRaises(ValueError):
            self.api.dispatch(self.request())
        self.assertEqual(len(self.sent), 1)

    def test_timeout_and_204_do_not_retry(self):
        for response in (TimeoutError(), self.response({}, status=204)):
            self.responses = [response]
            api = github.GitHub('synthetic-test-value', self.api._connection)
            with self.assertRaises((ValueError, TimeoutError)):
                api.dispatch(self.request())
            with self.assertRaises(ValueError):
                api.dispatch(self.request())

    def test_get_routes_and_budget(self):
        for path in ('https://evil.invalid', '/actions/runs/1/cancel', '/contents/other?ref='+'a'*40):
            with self.assertRaises(ValueError):
                self.api.get(path)
        self.assertEqual(len(self.sent), 0)
        self.api.gets = 8
        with self.assertRaises(ValueError):
            self.api.get('/actions/runs/123/attempts/1')

    def test_cdn_redirect_never_receives_authorization(self):
        self.responses = [self.response({}, status=302,
            location='https://fixture.blob.core.windows.net/logs?sig=synthetic'),
            self.response(b'log', raw=True)]
        self.assertEqual(self.api.get('/actions/jobs/99/logs', logs=True), b'log')
        self.assertNotIn('Authorization', self.sent[-1][-1])

    def test_untrusted_redirect_and_second_redirect_rejected(self):
        for url in ('http://fixture.blob.core.windows.net/x', 'https://evil.invalid/x',
                    'https://user@fixture.blob.core.windows.net/x',
                    'https://fixture.blob.core.windows.net:444/x'):
            self.responses = [self.response({}, status=302, location=url)]
            with self.assertRaises(ValueError):
                self.api.get('/actions/jobs/99/logs', logs=True)

    def test_gate_modes_probe_and_wrong_owner(self):
        self.assertTrue(self.gate(self.event(probe=True))[1])
        self.assertFalse(self.gate()[1])
        for mode, enabled in [('legacy','true'),('paused','false'),('maintenance','false'),('external','true')]:
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                self.gate(mode=mode, enabled=enabled)
        with self.assertRaises(ValueError):
            self.gate(attempt='2')
        self.assertEqual(self.gate({'schedule':True}, mode='legacy', enabled='true'), (None, False))
        self.assertEqual(self.gate({'inputs':{'confirm_archive':'true'}}, mode='maintenance'), (None, False))

    def test_changed_content_and_window_and_generation_rejected(self):
        for key, value in [('manifest',{}),('generation',str(uuid.uuid4())),
                           ('window_end','2026-11-14T12:49:00Z')]:
            event = self.event()
            req = json.loads(event['inputs']['trigger_request']);req[key] = value
            event['inputs']['trigger_request'] = json.dumps(req)
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.gate(event)

    def test_cycle_exact_hash_receipt_one_put_and_raw_cleanup(self):
        invocations, paths = [], []
        def invoke(args, writing=False):
            invocations.append((args, writing))
            if not writing:
                path = Path(args[args.index('--output')+1]);paths.append(path)
                path.write_bytes(b'{"synthetic":true}\n')
                return b''
            path = Path(args[-1]);raw = path.read_bytes()
            import hashlib
            return f'Archive completed: {len(raw)} bytes stored under a content-addressed key with SHA-256 {hashlib.sha256(raw).hexdigest()}.'.encode()
        with contextlib.redirect_stdout(io.StringIO()):
            result = workflow.cycle(self.request(), 'unused.dll', NOW, 123, invoke)
        self.assertEqual(result['run_id'], 123)
        self.assertEqual([writing for _, writing in invocations], [False,True])
        self.assertFalse(paths[0].exists())

    def test_failed_put_is_not_retried_and_raw_is_cleaned(self):
        paths, puts = [], []
        def invoke(args, writing=False):
            if not writing:
                path=Path(args[args.index('--output')+1]);paths.append(path);path.write_bytes(b'{}\n')
                return b''
            puts.append(1);raise TimeoutError()
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(TimeoutError):
            workflow.cycle(self.request(),'unused.dll',NOW,123,invoke)
        self.assertEqual(len(puts),1)
        self.assertFalse(paths[0].exists())

    def test_dispatch_to_ledger_to_independent_observer(self):
        self.accepted()
        receipt, run, jobs = self.fixture()
        self.queue_evidence(receipt, run, jobs)
        row = github.reconcile(self.ledger, self.row['claim'], self.api, NOW)
        self.assertEqual(row['state'], 'Verified')
        mapping = github.projection(self.ledger, self.generation, NOW)
        calls=[]
        def fetch(path):
            calls.append(path)
            return jobs if '/jobs?' in path else run
        result = dict(monitor.collect_external(mapping,self.generation,NOW,fetch))
        self.assertEqual(result['has_success'],1)
        self.assertEqual(result['last_success_timestamp_seconds'],NOW.timestamp())
        self.assertEqual(len(calls),2)

    def test_mismatched_receipt_persists_unknown(self):
        self.accepted()
        receipt, run, jobs = self.fixture();receipt['claim']=str(uuid.uuid4())
        self.queue_evidence(receipt, run, jobs)
        with self.assertRaises(ValueError):
            github.reconcile(self.ledger,self.row['claim'],self.api,NOW)
        self.assertEqual(self.ledger.get(self.row['claim'])['state'],'Unknown')
        with self.assertRaises(ValueError):
            github.projection(self.ledger,self.generation,NOW)

    def test_completion_during_reads_uses_later_evaluation_clock(self):
        self.accepted()
        receipt,run,jobs=self.fixture()
        jobs['jobs'][0]['completed_at']=state.stamp(NOW+dt.timedelta(seconds=1))
        self.queue_evidence(receipt,run,jobs)
        row=github.reconcile(self.ledger,self.row['claim'],self.api,NOW,
                             clock=lambda:NOW+dt.timedelta(seconds=2))
        self.assertEqual(row['state'],'Verified')

    def test_observer_never_credits_manual_probe_or_other_generation(self):
        receipt, run, jobs = self.fixture()
        mapping=dict(generation=self.generation,observed_at=state.stamp(NOW),claim=self.row['claim'],
                     slot=state.stamp(NOW),run_id=123)
        variants=[(dict(mapping,generation=str(uuid.uuid4())),run,jobs),
                  (mapping,dict(run,event='schedule'),jobs),
                  (mapping,dict(run,run_attempt=2),jobs)]
        probe=copy.deepcopy(jobs);probe['jobs'][0]['steps'][-1]['conclusion']='skipped'
        variants.append((mapping,run,probe))
        for m,r,j in variants:
            result=dict(monitor.collect_external(m,self.generation,NOW,
                       lambda path: j if '/jobs?' in path else r))
            self.assertEqual(result['observation_success'],0)

    def test_transient_read_does_not_release_or_poison_accepted_claim(self):
        self.accepted()
        self.responses = [TimeoutError()]
        row = github.reconcile(self.ledger,self.row['claim'],self.api,NOW)
        self.assertEqual(row['state'], 'Accepted')
        with self.assertRaises(ValueError):
            self.ledger.prepare(self.generation,state.stamp(NOW+dt.timedelta(hours=1)),
                                'b'*40,workflow.content_manifest(ROOT),NOW+dt.timedelta(hours=1))

    def test_corrupt_json_duplicate_fields_and_oversize_fail(self):
        for raw in (b'{"a":1,"a":2}', b'{"a":NaN}', b'x'*16385, b'\xff'):
            with self.subTest(raw=raw[:15]), self.assertRaises((ValueError,UnicodeError)):
                workflow.decode(raw)
        self.responses=[self.response(b'x'*524289,raw=True)]
        with self.assertRaises(ValueError):
            self.api.get('/actions/runs/123/attempts/1')

    def test_workflow_probe_has_no_secret_bearing_step(self):
        text=(ROOT/'.github/workflows/availability-evidence.yml').read_text()
        self.assertIn('path: trigger',text)
        step=text.split('      - name: Export and archive primary evidence\n',1)[1]
        self.assertTrue(step.startswith('        if: ${{ !inputs.probe_only }}\n'))
        self.assertIn('TRIGGER_GENERATION:',step)
        self.assertIn('python3 -B trigger/ops/availability/archive_trigger_workflow.py run',step)
        self.assertLess(text.index('Validate Archive admission'),text.index('GOLDSRCOPS_B2_WRITE_APPLICATION_KEY:'))
        gate_source=(ROOT/'ops/availability/archive_trigger_workflow.py').read_text()
        self.assertIn("not k.startswith('GOLDSRCOPS_B2_')",gate_source)


if __name__ == '__main__':
    unittest.main()
