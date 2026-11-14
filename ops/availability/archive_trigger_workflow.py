"""Workflow admission and one-attempt export wrapper; inputs never become shell code."""
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading

SPEC = importlib.util.spec_from_file_location('trigger_state', Path(__file__).with_name('archive_trigger_state.py'))
state = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(state)
FILES = ('.github/workflows/availability-evidence.yml',
         'ops/availability/archive_trigger_workflow.py', 'ops/availability/archive_trigger_state.py')


def decode(raw, limit=16384):
    state.require(type(raw) is bytes and 0 < len(raw) <= limit)
    def pairs(items):
        result = {}
        for k, v in items:
            state.require(k not in result)
            result[k] = v
        return result
    return json.loads(raw.decode('utf-8'), object_pairs_hook=pairs,
                      parse_constant=lambda _: state.require(False))


def content_manifest(root):
    return {p: hashlib.sha256((Path(root)/p).read_bytes().replace(b'\r\n', b'\n')).hexdigest()
            for p in FILES}


def validate(request, root, now, generation, exporter):
    state.require(type(request) is dict and set(request) == {
        'claim', 'generation', 'slot', 'exporter', 'manifest', 'window_start', 'window_end'})
    state.identifier(request['claim'])
    state.require(request['generation'] == state.identifier(generation))
    state.require(request['exporter'] == state.digest(exporter, 40))
    state.require(state.moment(request['slot']) == state.newest_slot(now), 'stale-slot')
    state.require((request['window_start'], request['window_end']) == state.window(request['slot']))
    state.require(request['manifest'] == content_manifest(root), 'workflow-content-changed')
    state.manifest(request['manifest'])
    return request


def gate(event, mode, enabled, generation, exporter, root, now, attempt, branch):
    state.require(mode in ('legacy', 'external', 'maintenance', 'paused'))
    state.require(branch == 'refs/heads/main' and attempt == '1')
    inputs = event.get('inputs', {})
    state.require(type(inputs) is dict)
    encoded = inputs.get('trigger_request', '')
    probe = inputs.get('probe_only', 'false') in (True, 'true')
    confirmed = inputs.get('confirm_archive', 'false') in (True, 'true')
    if encoded:
        state.require(mode == 'external' and enabled == 'false' and confirmed)
        state.require(not any(inputs.get(k) for k in
            ('expected_segment_sha256', 'expected_report_sha256', 'expected_record_count', 'window_end_utc')))
        request = validate(decode(encoded.encode()), root, now, generation, exporter)
        return request, probe
    state.require(not probe)
    if 'schedule' in event:
        state.require(mode == 'legacy' and enabled == 'true')
    else:
        state.require(mode in ('legacy', 'maintenance') and confirmed)
    return None, False


def receipt(request, run_id, sha, length):
    state.digest(sha)
    state.require(type(run_id) is int and 0 < run_id < 2**63)
    state.require(type(length) is int and 0 < length <= 64*1024*1024)
    return dict(schema=1, run_id=run_id, sha256=sha, bytes=length, **request)


def cycle(request, assembly, now, run_id, invoke=None):
    state.require(state.moment(request['slot']) == state.newest_slot(now), 'stale-slot')
    def command(arguments, writing=False):
        environment = dict(os.environ)
        if not writing:
            environment = {k: v for k, v in environment.items() if not k.startswith('GOLDSRCOPS_B2_')}
        with subprocess.Popen(['dotnet', str(assembly), *arguments], env=environment,
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as process:
            timer = threading.Timer(600, process.kill)
            timer.daemon = True
            timer.start()
            try:
                output = process.stdout.read(524289)
                if len(output) > 524288:
                    process.kill()
                code = process.wait()
                state.require(code == 0 and len(output) <= 524288, 'export-command-failed')
                return output
            finally:
                timer.cancel()
                if process.poll() is None:
                    process.kill()
                    process.wait()
    invoke = invoke or command
    with tempfile.TemporaryDirectory(prefix='availability-trigger-') as folder:
        segment = Path(folder)/'primary.jsonl'
        invoke(['export', '--window-start', request['window_start'], '--window-end', request['window_end'],
                '--job', 'goldsrcops-api-ready-primary-shadow', '--probe', os.environ.get('GOLDSRCOPS_AVAILABILITY_PRIMARY_PROBE', ''),
                '--environment', 'production', '--role', 'primary', '--monitor-revision', 'v2-4-shadow-001',
                '--location', 'Frankfurt', '--output', str(segment), '--overlap-minutes', '10', '--step-seconds', '15'])
        state.require(segment.is_file() and not segment.is_symlink() and 0 < segment.stat().st_size <= 64*1024*1024)
        sha = hashlib.sha256(segment.read_bytes()).hexdigest()
        length = segment.stat().st_size
        # Retain the expected object identity before the sole PUT, even if it fails.
        print('ARCHIVE_TRIGGER_ATTEMPT=' + json.dumps(receipt(request, run_id, sha, length), separators=(',', ':')), flush=True)
        output = invoke(['archive', '--input', str(segment)], writing=True)
        expected = f'Archive completed: {length} bytes stored under a content-addressed key with SHA-256 {sha}.'
        state.require(output.decode('utf-8').strip() == expected, 'archive-receipt-mismatch')
        result = receipt(request, run_id, sha, length)
        print('ARCHIVE_TRIGGER_RECEIPT=' + json.dumps(result, separators=(',', ':')), flush=True)
        return result


def main():
    now = dt.datetime.now(state.UTC).replace(microsecond=0)
    root = Path(__file__).resolve().parents[2]
    request_file = Path(os.environ['RUNNER_TEMP'])/'archive-trigger-request.json'
    state.require(len(sys.argv) == 2 and sys.argv[1] in ('gate', 'run', 'receipt'))
    if sys.argv[1] == 'gate':
        event = decode(Path(os.environ['GITHUB_EVENT_PATH']).read_bytes(), 524288)
        # GitHub schedule payload does not necessarily have a top-level schedule key.
        if os.environ['GITHUB_EVENT_NAME'] == 'schedule':
            event['schedule'] = True
        request, probe = gate(event, os.environ.get('TRIGGER_MODE') or 'legacy',
            os.environ.get('SCHEDULE_ENABLED', ''), os.environ.get('TRIGGER_GENERATION', ''),
            os.environ.get('EXPORTER_REVISION', ''), root, now,
            os.environ['GITHUB_RUN_ATTEMPT'], os.environ['GITHUB_REF'])
        if request is not None:
            with request_file.open('x', encoding='utf-8') as stream:
                json.dump(request, stream)
            if probe:
                print('ARCHIVE_TRIGGER_PROBE=admitted-no-write')
    elif sys.argv[1] == 'run':
        request = validate(decode(request_file.read_bytes()), root, now,
            os.environ['TRIGGER_GENERATION'], os.environ['EXPORTER_REVISION'])
        cycle(request, Path(os.environ['RUNNER_TEMP'])/'availability-exporter/GoldSrcOps.AvailabilityExporter.dll',
              now, int(os.environ['GITHUB_RUN_ID']))
    else:
        # This step runs only after the writing step succeeded; the observer
        # checks it in addition to the exact receipt downloaded by the adapter.
        state.require(request_file.is_file())
        print('ARCHIVE_TRIGGER_RECEIPT=validated-by-successful-writer')


if __name__ == '__main__':
    try:
        main()
    except Exception:
        print('Archive trigger stopped; reconcile before retry.', file=sys.stderr)
        sys.exit(1)
