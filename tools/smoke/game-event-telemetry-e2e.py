#!/usr/bin/env python3
"""Deliver verified native round records through the real agent, API and PostgreSQL."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
from datetime import datetime
from uuid import UUID

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('engine_ci', Path(__file__).with_name('engine-ci.py'))
engine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(engine)
SCENARIOS = dict(ordinary='goldsrcops_bomb_explode', **{
    'lost-ack': 'goldsrcops_bomb_defuse', 'unavailable-restart': 'goldsrcops_round_timeout'})
PROJECT = ROOT / 'tools/GoldSrcOps.TelemetryAcceptance/GoldSrcOps.TelemetryAcceptance.csproj'


def read_records(evidence, compiled):
    engine.verify_fixtures(compiled)
    result_path = evidence / 'result.json'
    if result_path.is_symlink() or result_path.stat().st_size > 16384:
        raise ValueError('Invalid engine result receipt')
    result = json.loads(result_path.read_bytes())
    lock = json.loads(engine.LOCK.read_bytes())
    if (result.get('result') != 'passed' or result.get('mode') != 'gameplay'
            or result.get('network') != 'none' or result.get('publishedPorts') != 0
            or result.get('cleanup') != 'passed' or result.get('containerExitCode') != 0
            or result.get('lockSha256') != engine.digest(engine.LOCK)
            or result.get('fixtureManifestSha256') != engine.digest(compiled / 'manifest.json')
            or result.get('cases') != len(lock['cases']) or result.get('compiledFixtures') != len(engine.FIXTURES)):
        raise ValueError('Native engine evidence is not accepted for this checkout')
    engine.validate_evidence(evidence, lock['cases'])
    records = {}
    for scenario, command in SCENARIOS.items():
        filename = (evidence / 'round-records' / (command + '.tsv')).read_text().split('\t')[0]
        records[scenario] = (evidence / 'round-records' / command / filename).read_bytes()
    return records, {name: result[name] for name in ('lockSha256', 'fixtureManifestSha256', 'cases')}


def command(arguments, log):
    options = dict(cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if os.name == 'nt':
        options['creationflags'] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        options['start_new_session'] = True
    with subprocess.Popen(arguments, **options) as process:
        try:
            output, _ = process.communicate(timeout=180)
        except subprocess.TimeoutExpired:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], check=True,
                               capture_output=True, timeout=15)
            else:
                os.killpg(process.pid, signal.SIGKILL)
            process.communicate(timeout=15)
            raise
        if len(output) > 4 * 1024 * 1024:
            raise ValueError('Acceptance diagnostics exceeded bound')
        log.append(output.decode('utf-8', errors='replace'))
        if process.returncode:
            raise ValueError('Telemetry acceptance command failed; inspect retained diagnostics')


def validate_result(result, records, queue_recovery=False, server_isolation=False):
    if queue_recovery and server_isolation:
        raise ValueError('Telemetry modes are mutually exclusive')
    if server_isolation:
        validate_isolation(result, records)
        return
    names = {'bounded-capacity', 'restart-and-drain', 'dead-letter-isolation'} if queue_recovery else set(SCENARIOS)
    expected = 'native-telemetry-queue-recovery-accepted' if queue_recovery else 'native-telemetry-e2e-accepted'
    if (result.get('status') != expected or len(result.get('scenarios', [])) != 3
            or {case['scenario'] for case in result['scenarios']} != names
            or any(case['status'] != 'passed' for case in result['scenarios'])):
        raise ValueError('Incomplete telemetry acceptance receipt')
    hashes = {name: hashlib.sha256(raw).hexdigest() for name, raw in records.items()}
    if not queue_recovery:
        if any(case['nativeSha256'].lower() != hashes[case['scenario']] for case in result['scenarios']):
            raise ValueError('Acceptance receipt is not bound to native input')
        return
    if result.get('persistedRows') != 5 or {name: value.lower() for name, value in result['nativeInputHashes'].items()} != hashes:
        raise ValueError('Queue receipt is not bound to all native inputs')
    cases = {case['scenario']: case for case in result['scenarios']}
    full, drained, dead = (cases[name] for name in ('bounded-capacity', 'restart-and-drain', 'dead-letter-isolation'))
    if (full['capacity'] != 2 or full['maximumObservedDepth'] != 2 or full['queued'] != 2 or full['deferred'] != 1
            or full['persistedRows'] != 0 or full['snapshot']['nextSequence'] != 3 or len(full['snapshot']['entries']) != 2
            or full['deferredSha256'].lower() not in hashes.values()
            or full['cycles']['importOutcome'] != 'blocked' or full['cycles']['bothCompleted'] is not True):
        raise ValueError('Missing bounded-capacity or deferred-input evidence')
    if (drained['maximumObservedDepth'] != 2 or drained['nextSequence'] != 4 or drained['persistedRows'] != 3
            or drained['queueDrained'] is not True or drained['spoolDrained'] is not True
            or drained['source'] != full['snapshot']['source'] or drained['readerProjection'] != 'matched'):
        raise ValueError('Incomplete restart and drain evidence')
    for restarted, previous in ((drained['restartedCycles'], full['cycles']), (dead['restartedCycles'], dead['previousCycles'])):
        started = datetime.fromisoformat(restarted['startedAtUtc'].replace('Z', '+00:00'))
        if (restarted['bothCompleted'] is not True or restarted['importOutcome'] != 'idle' or restarted['deliveryOutcome'] != 'idle'
                or started == datetime.fromisoformat(previous['startedAtUtc'].replace('Z', '+00:00'))
                or any(datetime.fromisoformat(restarted[key].replace('Z', '+00:00')) < started
                       for key in ('importCompletedAtUtc', 'deliveryCompletedAtUtc'))):
            raise ValueError('Restart did not provide fresh completed worker cycles')
    if (dead['capacity'] != 3 or not 1 <= dead['maximumObservedDepth'] <= 3 or dead['persistedRows'] != 2
            or dead['deadLetterRetainedAcrossRestart'] is not True or dead['automaticReplay'] is not False
            or dead['readerProjection'] != 'matched' or len(dead['snapshot']['entries']) != 1
            or dead['snapshot']['nextSequence'] != 4 or dead['restartedCycles']['bothCompleted'] is not True
            or dead['restartedCycles']['importOutcome'] != 'idle' or dead['restartedCycles']['deliveryOutcome'] != 'idle'):
        raise ValueError('Incomplete dead-letter restart evidence')
    if full['serverId'] != drained['serverId'] or dead['serverId'] == drained['serverId'] or dead['snapshot']['source'] == drained['source']:
        raise ValueError('Queue scenarios do not have the expected isolated bindings')
    entry = dead['snapshot']['entries'][0]
    if (entry['status'] != 2 or entry['attempts'] != 1 or entry['failure'] != 'conflict'
            or entry['identity']['payloadSha256'] != dead['rejected']['payloadSha256'] or dead['rejected']['status'] != 409
            or entry['identity']['eventId'] != dead['rejected']['request']['eventId']):
        raise ValueError('Dead-letter identity, bytes or classification changed')
    native = [json.loads(raw)['event'] for raw in records.values()]
    seen = set()
    for case, count in ((drained, 3), (dead, 2)):
        if len(case['accepted']) != count:
            raise ValueError('Missing or duplicate successful delivery')
        for observation in case['accepted']:
            request, receipt = observation['request'], observation['receipt']
            if (observation['status'] != 202 or receipt['duplicate'] is not False
                    or any(receipt[key] != request[key] for key in ('eventId', 'sourceInstanceId', 'sequenceNumber'))
                    or receipt['serverId'] != case['serverId']
                    or request['sourceInstanceId'] != (case['source'] if case is drained else case['snapshot']['source'])
                    or request['eventId'] in seen):
                raise ValueError('Successful delivery identity or receipt mismatch')
            seen.add(request['eventId'])
            if not any(all(request[key] == event[key] for key in ('type', 'map', 'players', 'bots'))
                       and datetime.fromisoformat(request['occurredAtUtc'].replace('Z', '+00:00')) ==
                       datetime.fromisoformat(event['occurredAtUtc'].replace('Z', '+00:00')) for event in native):
                raise ValueError('Delivered request differs from native event')
    delivered = {item['receipt']['eventId']: item for item in drained['accepted']}
    for queued in full['snapshot']['entries']:
        identity = queued['identity']
        if (identity['eventId'] not in delivered or delivered[identity['eventId']]['payloadSha256'] != identity['payloadSha256']
                or delivered[identity['eventId']]['receipt']['sequenceNumber'] != identity['sequence']):
            raise ValueError('Recovery replaced a queued identity, sequence or payload')
    if dead['rejected']['request']['eventId'] in seen:
        raise ValueError('Rejected event was reported as delivered')


def validate_isolation(result, records):
    names = {'parallel-delivery', 'one-server-unavailable', 'cross-server-forbidden', 'recover-and-isolate'}
    if (result.get('status') != 'native-telemetry-server-isolation-accepted' or result.get('persistedRows') != 6
            or len(result.get('scenarios', [])) != 4 or {case['scenario'] for case in result['scenarios']} != names
            or any(case['status'] != 'passed' or case['bothAgentsRunning'] is not True for case in result['scenarios'])
            or {name: value.lower() for name, value in result['nativeInputHashes'].items()} !=
            {name: hashlib.sha256(raw).hexdigest() for name, raw in records.items()}):
        raise ValueError('Incomplete or unbound two-server acceptance')
    cases = {case['scenario']: case for case in result['scenarios']}
    parallel, outage, forbidden, recovered = (cases[name] for name in (
        'parallel-delivery', 'one-server-unavailable', 'cross-server-forbidden', 'recover-and-isolate'))
    first, second = recovered['firstServerId'], recovered['secondServerId']
    if (first == second or any(case['firstServerId'] != first or case['secondServerId'] != second for case in (parallel, outage))
            or forbidden['credentialServerId'] != first or forbidden['targetServerId'] != second
            or forbidden['httpStatus'] != 403 or forbidden['beforeRows'] != 3 or forbidden['afterRows'] != 3
            or forbidden['secondPersistedRowsAfterDenial'] != 3 or forbidden['recoveryReleased'] is not False
            or outage['recoveryReleased'] is not False or outage['firstPersistedRows'] != 1 or outage['secondPersistedRows'] != 2
            or parallel['persistedRows'] != 2 or recovered['persistedRows'] != 6 or recovered['readerProjection'] != 'matched-per-server'):
        raise ValueError('Server binding, denial or independent progress changed')
    if recovered['firstFinal']['source'] == recovered['secondFinal']['source']:
        raise ValueError('Servers share a queue source')
    native = [json.loads(raw)['event'] for raw in records.values()]
    all_ids = set()
    for prefix, server in (('first', first), ('second', second)):
        final, initial = recovered[prefix + 'Final'], parallel[prefix + 'Initial']
        observations = recovered[prefix + 'Accepted']
        if (final['source'] != initial['source'] or initial['nextSequence'] != 2 or len(initial['entries']) != 1
                or final['nextSequence'] != 4 or final['entries'] or final['spoolReceipts'] != 0
                or len(observations) != 3 or sorted(item['receipt']['sequenceNumber'] for item in observations) != [1, 2, 3]
                or parallel[prefix + 'Accepted'] != observations[:1]):
            raise ValueError('Missing independent queue identity, sequence or drain')
        occurrences = set()
        for item in observations:
            request, receipt = item['request'], item['receipt']
            if (item['status'] != 202 or receipt['duplicate'] is not False or receipt['serverId'] != server
                    or request['sourceInstanceId'] != final['source'] or request['eventId'] in all_ids
                    or any(receipt[key] != request[key] for key in ('eventId', 'sourceInstanceId', 'sequenceNumber'))):
                raise ValueError('Cross-server or duplicate accepted event')
            occurrence = datetime.fromisoformat(request['occurredAtUtc'].replace('Z', '+00:00'))
            if occurrence in occurrences or not any(
                    all(request[key] == event[key] for key in ('type', 'map', 'players', 'bots')) and
                    occurrence == datetime.fromisoformat(event['occurredAtUtc'].replace('Z', '+00:00')) for event in native):
                raise ValueError('Two-server delivery differs from native input')
            occurrences.add(occurrence)
            all_ids.add(request['eventId'])
        cycles, previous = recovered[prefix + 'Cycles'], parallel[prefix + 'Cycles']
        started = datetime.fromisoformat(cycles['startedAtUtc'].replace('Z', '+00:00'))
        if (cycles['startedAtUtc'] != previous['startedAtUtc'] or previous['bothCompleted'] is not True
                or cycles['bothCompleted'] is not True or cycles['importOutcome'] != 'idle' or cycles['deliveryOutcome'] != 'idle'
                or any(datetime.fromisoformat(cycles[key].replace('Z', '+00:00')) < started
                       for key in ('importCompletedAtUtc', 'deliveryCompletedAtUtc'))):
            raise ValueError('Both worker loops did not complete in the retained processes')
    first_delivered = {item['request']['eventId']: item for item in recovered['firstAccepted']}
    for snapshot, source, count, next_sequence, delivered in (
            (parallel['firstInitial'], recovered['firstFinal']['source'], 1, 2, first_delivered),
            (parallel['secondInitial'], recovered['secondFinal']['source'], 1, 2,
             {item['request']['eventId']: item for item in recovered['secondAccepted']}),
            (outage['firstSnapshot'], recovered['firstFinal']['source'], 1, 3, first_delivered),
            (forbidden['beforeRecovery'], recovered['firstFinal']['source'], 2, 4, first_delivered)):
        if snapshot['source'] != source or len(snapshot['entries']) != count or snapshot['nextSequence'] != next_sequence:
            raise ValueError('Pending queue identity or depth changed')
        for entry in snapshot['entries']:
            identity = entry['identity']
            item = delivered.get(identity['eventId'])
            if (item is None or identity['source'] != source or identity['nextSequence'] != next_sequence
                    or identity['payloadSha256'] != item['payloadSha256'] or identity['sequence'] != item['request']['sequenceNumber']
                    or entry['status'] not in (0, 1)):
                raise ValueError('Recovery replaced queued bytes or sequence')
    rejected = outage['rejectedDelivery']
    if (rejected != forbidden['attempted'] or rejected['status'] != 503 or rejected['receipt'] is not None
            or rejected['request']['sourceInstanceId'] != recovered['firstFinal']['source']
            or rejected['request']['sequenceNumber'] != 2
            or rejected['payloadSha256'] != first_delivered[rejected['request']['eventId']]['payloadSha256']
            or outage['secondAccepted'] != recovered['secondAccepted'][:2]
            or forbidden['secondAcceptedAfterDenial'] != recovered['secondAccepted']):
        raise ValueError('Missing held-back request or healthy progress before recovery')
    if parallel['firstAccepted'][0]['request']['occurredAtUtc'] == parallel['secondAccepted'][0]['request']['occurredAtUtc']:
        raise ValueError('Initial Reader histories do not distinguish the servers')


def validate_postgres_recovery(result, records, point_in_time=False):
    snapshot = result['snapshot']
    entries = result['events']
    status = 'native-telemetry-postgres-pitr-accepted' if point_in_time else 'native-telemetry-postgres-recovery-accepted'
    row_counts = (2, 2, 3) if point_in_time else (2, 1, 2)
    expected = (
        ('in-snapshot', 'ordinary', (True, True, False)),
        ('acknowledged-after-snapshot', 'lost-ack', (point_in_time, point_in_time, False)),
        ('pending-at-cutover', 'unavailable-restart', (True, True, True)),
    )
    if (result['status'] != status
            or result['queueDrained'] is not True
            or (result['rowsBeforeCutover'], result['rowsAfterRestore'], result['rowsAfterDelivery']) != row_counts
            or snapshot['freshContainer'] is not True
            or len(entries) != len(expected)):
        raise ValueError('Invalid PostgreSQL recovery boundary')
    if point_in_time:
        if (snapshot['earlyTarget'] != 'before-late-ack' or snapshot['lateTarget'] != 'after-late-ack'
                or snapshot['rowsAtEarlyTarget'] != 1 or snapshot['missingWalRejected'] is not True
                or not all(type(snapshot[key]) is int and snapshot[key] > 0 for key in
                           ('earlyRestoreMilliseconds', 'lateRestoreMilliseconds', 'forcedArchiveMilliseconds'))):
            raise ValueError('Invalid PITR target evidence')
        for prefix in ('base', 'wal'):
            validate_archive(snapshot[prefix + 'Bytes'], snapshot[prefix + 'Sha256'], 32 * 1024 * 1024)
    else:
        validate_archive(snapshot['bytes'], snapshot['sha256'], 1024 * 1024)
    identities = set()
    sources = set()
    for sequence, (entry, (position, record, outcomes)) in enumerate(zip(entries, expected, strict=True), start=1):
        actual = tuple(entry[name] for name in ('database', 'reader', 'replayed'))
        if (entry['position'] != position or entry['sequence'] != sequence
                or entry['nativeSha256'].lower() != hashlib.sha256(records[record]).hexdigest()
                or any(observed is not wanted for observed, wanted in zip(actual, outcomes, strict=True))):
            raise ValueError('PostgreSQL recovery outcome differs from its event boundary')
        identities.add(UUID(entry['eventId']))
        sources.add(UUID(entry['source']))
    if len(identities) != len(expected) or len(sources) != 1:
        raise ValueError('PostgreSQL recovery changed source or reused an event identity')


def validate_archive(size, sha256, maximum):
    if type(size) is not int or not 0 < size <= maximum or len(sha256) != 64 or len(bytes.fromhex(sha256)) != 32:
        raise ValueError('Invalid PostgreSQL archive identity')


def execute(evidence, compiled, output, queue_recovery=False, server_isolation=False, postgres_recovery=False, postgres_pitr=False):
    if sum((queue_recovery, server_isolation, postgres_recovery, postgres_pitr)) > 1:
        raise ValueError('Telemetry modes are mutually exclusive')
    records, binding = read_records(evidence, compiled)
    output.mkdir(parents=True, exist_ok=False)
    log = []
    try:
        command(['dotnet', 'build', str(PROJECT), '-p:AuditPipeline=true', '--nologo'], log)
        with tempfile.TemporaryDirectory(prefix='native-telemetry-e2e-') as temporary:
            root = Path(temporary)
            inputs = root / 'inputs'
            inputs.mkdir()
            for scenario, raw in records.items():
                (inputs / (scenario + '.json')).write_bytes(raw)
            harness = PROJECT.parent / 'bin/Debug/net10.0/GoldSrcOps.TelemetryAcceptance.dll'
            agent = ROOT / 'src/GoldSrcOps.GameEventAgent/bin/Debug/net10.0/GoldSrcOps.GameEventAgent.dll'
            arguments = ['dotnet', str(harness), str(ROOT), str(agent), str(inputs), str(root / 'runtime'), str(root / 'result.json')]
            if queue_recovery:
                arguments.append('queue-recovery')
            if server_isolation:
                arguments.append('server-isolation')
            if postgres_recovery:
                arguments.append('postgres-recovery')
            if postgres_pitr:
                arguments.append('postgres-pitr')
            command(arguments, log)
            result = json.loads((root / 'result.json').read_bytes())
            if postgres_recovery or postgres_pitr:
                validate_postgres_recovery(result, records, point_in_time=postgres_pitr)
            else:
                validate_result(result, records, queue_recovery, server_isolation)
            result['nativeEngine'] = binding
            sources = [PROJECT, Path(__file__).resolve(), Path(__file__).with_name('test-game-event-telemetry-e2e.py')]
            sources.extend(sorted(PROJECT.parent.glob('*.cs')))
            result['acceptanceSourcesSha256'] = {path.relative_to(ROOT).as_posix(): engine.digest(path) for path in sources}
            result['componentsSha256'] = {name: engine.digest(path) for name, path in (
                ('api', harness.parent / 'GoldSrcOps.Api.dll'), ('agent', agent), ('acceptance', harness))}
        result['cleanup'] = 'temporary queues, synthetic credentials and database removed'
        (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print('NATIVE_TELEMETRY_POSTGRES_PITR=passed early=1 late=2 final=3 missing_wal=rejected' if postgres_pitr else
              'NATIVE_TELEMETRY_POSTGRES_RECOVERY=passed restored=1 replayed=1 acknowledged_missing=1' if postgres_recovery else
              'NATIVE_TELEMETRY_SERVER_ISOLATION=passed scenarios=4 rows=6' if server_isolation else
              'NATIVE_TELEMETRY_QUEUE_RECOVERY=passed scenarios=3 rows=5' if queue_recovery else 'NATIVE_TELEMETRY_E2E=passed scenarios=3 rows=3')
    finally:
        (output / 'runner.log').write_text('\n'.join(log), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--compiled', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument('--queue-recovery', action='store_true')
    modes.add_argument('--server-isolation', action='store_true')
    modes.add_argument('--postgres-recovery', action='store_true')
    modes.add_argument('--postgres-pitr', action='store_true')
    args = parser.parse_args()
    execute(args.evidence.resolve(), args.compiled.resolve(), args.output.resolve(),
            args.queue_recovery, args.server_isolation, args.postgres_recovery, args.postgres_pitr)


if __name__ == '__main__':
    main()
