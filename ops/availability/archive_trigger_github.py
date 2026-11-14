"""Fixed GitHub endpoints, bounded reads and a single POST with no retries."""
import base64
import datetime as dt
import hashlib
import http.client
import importlib.util
import json
from pathlib import Path
import re
from urllib.parse import urlsplit

SPEC = importlib.util.spec_from_file_location('trigger_workflow', Path(__file__).with_name('archive_trigger_workflow.py'))
workflow = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(workflow)
state = workflow.state
BASE = '/repos/tov-vl/gold-src-ops'
WORKFLOW = 350269402


class ReadUnavailable(Exception):
    """A failed GET leaves the existing claim pending; it never permits POST."""


class GitHub:
    def __init__(self, token, connection=http.client.HTTPSConnection):
        state.require(type(token) is str and 1 <= len(token) <= 512 and not any(c.isspace() for c in token))
        self._token, self._connection = token, connection
        self.gets, self.posts = 0, 0

    def _exchange(self, host, method, path, body=None, authenticated=True):
        headers = {'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2026-03-10',
                   'User-Agent': 'goldsrcops-archive-trigger', 'Cache-Control': 'no-cache'}
        if authenticated:
            state.require(host == 'api.github.com')
            headers['Authorization'] = 'Bearer ' + self._token
        connection = self._connection(host, timeout=10)
        try:
            if body is not None:
                headers['Content-Type'] = 'application/json'
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            raw = response.read(524289)
            state.require(len(raw) <= 524288, 'response-bound')
            return response.status, response.getheader('Content-Type', ''), response.getheader('Location'), raw
        finally:
            connection.close()

    def get(self, suffix, logs=False):
        # Suffixes are generated internally; callers cannot choose a host or arbitrary route.
        patterns = (r'/actions/runs/[1-9][0-9]*/attempts/1',
                    r'/actions/runs/[1-9][0-9]*/attempts/1/jobs\?per_page=10',
                    r'/actions/jobs/[1-9][0-9]*/logs',
                    r'/contents/(?:' + '|'.join(re.escape(p) for p in workflow.FILES) + r')\?ref=[a-f0-9]{40}')
        state.require(any(re.fullmatch(p, suffix) for p in patterns))
        self.gets += 1
        state.require(self.gets <= 8, 'request-budget')
        try:
            status, content_type, location, raw = self._exchange('api.github.com', 'GET', BASE+suffix)
        except (OSError, http.client.HTTPException):
            raise ReadUnavailable('github-read-unavailable') from None
        if status in (429, 500, 502, 503, 504):
            raise ReadUnavailable('github-read-unavailable')
        if logs and status == 302:
            url = urlsplit(location or '')
            state.require(url.scheme == 'https' and url.hostname is not None
                and url.hostname.endswith('.blob.core.windows.net') and url.port in (None, 443)
                and url.username is None and url.password is None and not url.fragment)
            self.gets += 1
            state.require(self.gets <= 8)
            try:
                status, content_type, _, raw = self._exchange(url.hostname, 'GET',
                    url.path + ('?' + url.query if url.query else ''), authenticated=False)
            except (OSError, http.client.HTTPException):
                raise ReadUnavailable('github-read-unavailable') from None
        state.require(status == 200, 'github-read-failed')
        if logs:
            return raw
        state.require(content_type.split(';')[0] == 'application/json')
        return workflow.decode(raw, 524288)

    def dispatch(self, request, probe=False):
        state.require(self.posts == 0 and type(probe) is bool)
        self.posts += 1
        body = json.dumps({'ref': 'main', 'inputs': {'confirm_archive': True,
            'trigger_request': json.dumps(request, separators=(',', ':')), 'probe_only': probe}}).encode()
        state.require(len(body) <= 16384)
        status, content_type, _, raw = self._exchange('api.github.com', 'POST',
            BASE+f'/actions/workflows/{WORKFLOW}/dispatches', body)
        state.require(status == 200 and content_type.split(';')[0] == 'application/json')
        result = workflow.decode(raw)
        run_id = result.get('workflow_run_id')
        state.require(type(run_id) is int and 0 < run_id < 2**63)
        return {'status': 200, 'workflow_run_id': run_id}

    def evidence(self, row, now):
        run_id = row['run_id']
        state.require(type(run_id) is int and 0 < run_id < 2**63)
        run = self.get(f'/actions/runs/{run_id}/attempts/1')
        state.require(run.get('id') == run_id and type(run.get('id')) is int
            and run.get('workflow_id') == WORKFLOW and run.get('event') == 'workflow_dispatch'
            and run.get('head_branch') == 'main' and type(run.get('run_attempt')) is int
            and run['run_attempt'] == 1)
        if run.get('status') != 'completed':
            return None
        state.require(run.get('conclusion') == 'success', 'remote-outcome-uncertain')
        head = state.digest(run.get('head_sha'), 40)
        files = {}
        for path in workflow.FILES:
            obj = self.get(f'/contents/{path}?ref={head}')
            state.require(obj.get('encoding') == 'base64' and obj.get('path') == path
                and type(obj.get('size')) is int and 0 < obj['size'] <= 131072)
            raw = base64.b64decode(obj['content'].replace('\n', ''), validate=True)
            state.require(len(raw) == obj['size'])
            files[path] = hashlib.sha256(raw.replace(b'\r\n', b'\n')).hexdigest()
        state.require(state.manifest(files) == row['manifest'])
        jobs = self.get(f'/actions/runs/{run_id}/attempts/1/jobs?per_page=10')
        state.require(type(jobs.get('total_count')) is int and jobs['total_count'] == 1
                      and len(jobs.get('jobs', [])) == 1)
        job = jobs['jobs'][0]
        state.require(job.get('name') == 'Archive Primary Evidence' and job.get('run_id') == run_id
            and type(job.get('run_attempt')) is int and job['run_attempt'] == 1
            and job.get('status') == 'completed' and job.get('conclusion') == 'success'
            and type(job.get('id')) is int and 0 < job['id'] < 2**63)
        steps = job.get('steps')
        state.require(type(steps) is list and len(steps) <= 32)
        for name in ('Validate Archive admission', 'Verify pinned exporter revision',
                     'Export and archive primary evidence', 'Validate external Archive receipt'):
            found = [s for s in steps if s.get('name') == name]
            state.require(len(found) == 1 and found[0].get('conclusion') == 'success'
                          and found[0].get('status') == 'completed')
        raw = self.get(f"/actions/jobs/{job['id']}/logs", logs=True)
        # Match only a timestamped stdout line, never the echoed shell source.
        lines = re.findall(rb'^\d{4}-\d{2}-\d{2}T[^ ]+ ARCHIVE_TRIGGER_RECEIPT=(\{[^\r\n]+\})\r?$', raw, re.M)
        state.require(len(lines) == 1)
        receipt = workflow.decode(lines[0])
        proof = dict(run_id=run_id, attempt=1, event='workflow_dispatch', branch='main',
            workflow=WORKFLOW, head=head, manifest=files, created=run['created_at'],
            completed=job['completed_at'], job_success=True, pin_success=True, receipt_success=True)
        state.require(state.moment(job['started_at']) >= state.moment(run['created_at']))
        return receipt, proof


def reconcile(ledger, claim, github, now, clock=None):
    row = ledger.get(claim)
    state.require(row['state'] == 'Accepted')
    try:
        evidence = github.evidence(row, now)
        return row if evidence is None else ledger.verify(claim, *evidence, clock() if clock else now)
    except ReadUnavailable:
        return row
    except Exception:
        if ledger.get(claim)['state'] == 'Accepted':
            ledger.unknown(claim, now)
        raise ValueError('reconciliation-uncertain') from None


def projection(ledger, generation, now):
    state.identifier(generation)
    # Projection is produced by the trusted adapter, with no mutable health bit.
    state.require(ledger.connection.execute(
        "SELECT count(*) FROM claims WHERE state IN ('Unknown','Attempting')").fetchone()[0] == 0)
    row = ledger.connection.execute(
        "SELECT * FROM claims WHERE generation=? AND state='Verified' ORDER BY created DESC LIMIT 1",
        (generation,)).fetchone()
    state.require(row is not None)
    state.digest(row['receipt_hash'])
    return dict(generation=generation, observed_at=state.stamp(now), claim=row['claim'],
                slot=row['slot'], run_id=row['run_id'])
