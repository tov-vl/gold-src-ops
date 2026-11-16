#!/usr/bin/env python3
"""Accept native producer records through the existing offline agent importer."""
import argparse
from collections import Counter
from contextlib import closing
import csv
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[2]
CASES = {'bot_only': (1, 2, 2), 'mixed': (1, 2, 1), 'spectator': (1, 2, 1),
         'after_disconnect': (1, 1, 0), 'duplicate': (2, 1, 0),
         'disabled_then_enabled': (1, 1, 0), 'capacity_recovered': (1, 1, 0),
         'write_recovered': (1, 1, 0), 'rename_recovered': (1, 1, 0)}


def read_records(evidence):
    records = []
    counts = Counter()
    seen = set()
    receipted = set()
    base = evidence / 'producer-records'
    manifest = evidence / 'producer-records.tsv'
    if manifest.stat().st_size > 16384:
        raise ValueError('Producer transfer receipt exceeds bound')
    with manifest.open(encoding='utf-8', newline='') as source:
        for fields in csv.reader(source, delimiter='\t'):
            if len(fields) != 6 or len(records) >= 10:
                raise ValueError('Invalid producer transfer receipt')
            label, filename, players, bots, length, checksum = fields
            if label not in CASES or not re.fullmatch(r'[0-9a-f-]{36}\.json', filename):
                raise ValueError('Unknown producer case or invalid file name')
            identity = uuid.UUID(filename[:-5])
            if identity.version != 4 or str(identity) + '.json' != filename or filename in seen:
                raise ValueError('Noncanonical or duplicate producer identity')
            path = base / label / filename
            if (not path.resolve().is_relative_to(base.resolve()) or path.is_symlink()
                    or path.parent.is_symlink() or base.is_symlink()):
                raise ValueError('Producer record escapes evidence')
            raw = path.read_bytes()
            expected = CASES[label]
            if (not 0 < len(raw) <= 4096 or str(len(raw)) != length
                    or hashlib.sha256(raw).hexdigest() != checksum
                    or (players, bots) != (str(expected[1]), str(expected[2]))):
                raise ValueError('Producer transfer or roster mismatch')
            value = json.loads(raw)
            if (value['recordId'] != str(identity) or value['spoolVersion'] != 1
                    or value['event']['type'] != 'round.ended'
                    or (value['event']['players'], value['event']['bots']) != expected[1:]):
                raise ValueError('Native JSON does not match the expected roster')
            seen.add(filename)
            receipted.add(path.resolve())
            counts[label] += 1
            records.append((filename, raw, value['event']))
    if counts != Counter({name: value[0] for name, value in CASES.items()}):
        raise ValueError('Missing or duplicate producer case records')
    if {p.resolve() for p in base.rglob('*') if p.is_file()} != receipted:
        raise ValueError('Unreceipted producer evidence file')
    return records


def execute(evidence, output):
    records = read_records(evidence)
    output.mkdir(parents=True, exist_ok=False)
    log = []

    def command(args, env=None, expected=0):
        result = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, timeout=180)
        if len(result.stdout) + len(result.stderr) > 4 * 1024 * 1024:
            raise ValueError('Agent diagnostic output exceeded bound')
        log.append(result.stdout.decode(errors='replace') + result.stderr.decode(errors='replace'))
        if result.returncode != expected:
            raise ValueError('Agent contract command failed; inspect retained diagnostic log')
        return result.stdout.decode()

    try:
        command(['dotnet', 'build', 'src/GoldSrcOps.GameEventAgent/GoldSrcOps.GameEventAgent.csproj',
                 '-p:AuditPipeline=true', '--nologo'])
        agent = ROOT / 'src/GoldSrcOps.GameEventAgent/bin/Debug/net10.0/GoldSrcOps.GameEventAgent.dll'
        with tempfile.TemporaryDirectory(prefix='producer-agent-contract-') as temporary:
            scratch = Path(temporary)
            incoming = scratch / 'spool/incoming'
            incoming.mkdir(parents=True)
            queue = scratch / 'queue.db'
            env = {name: value for name, value in os.environ.items()
                   if not name.lower().startswith('gameeventagent')}
            env.update(GameEventAgent__QueuePath=str(queue), GameEventAgent__Spool__RootPath=str(scratch / 'spool'),
                       GameEventAgent__Spool__Enabled='true', GameEventAgent__Spool__BatchSize='1000',
                       GameEventAgent__Delivery__Enabled='false', DOTNET_ENVIRONMENT='Production')
            command(['dotnet', str(agent), 'initialize-queue', '--fresh'], env)
            for filename, raw, _ in records:
                (incoming / filename).write_bytes(raw)
            imported = command(['dotnet', str(agent), 'import-spool'], env)
            if f'imported={len(records)}, reconciled=0, finalized=0, rejected=0, deferred=0' not in imported:
                raise ValueError('Native producer import count mismatch')
            with closing(sqlite3.connect(queue.as_uri() + '?mode=ro', uri=True)) as database:
                rows = database.execute('SELECT event_type, map, players, bots, status, attempt_count FROM game_event_outbox').fetchall()
            expected = Counter((event['type'], event['map'], event['players'], event['bots'], 0, 0)
                               for _, _, event in records)
            if Counter(rows) != expected or any(incoming.iterdir()):
                raise ValueError('Agent queue differs from native records')
            original = json.loads(records[0][1])
            invalid = []
            for case in ('filename', 'version', 'roster', 'unknown', 'truncated'):
                value = json.loads(json.dumps(original))
                filename = str(uuid.uuid4()) + '.json'
                if case != 'filename':
                    value['recordId'] = filename[:-5]
                if case == 'version':
                    value['spoolVersion'] = 2
                if case == 'roster':
                    value['event']['bots'] = value['event']['players'] + 1
                if case == 'unknown':
                    value['event']['humanActivity'] = True
                raw = b'{' if case == 'truncated' else json.dumps(value).encode()
                (incoming / filename).write_bytes(raw)
                invalid.append(case)
            rejected = command(['dotnet', str(agent), 'import-spool'], env, expected=1)
            if f'imported=0, reconciled=0, finalized=0, rejected={len(invalid)}, deferred=0' not in rejected:
                raise ValueError('Actual agent validator did not reject malformed controls')
            with closing(sqlite3.connect(queue.as_uri() + '?mode=ro', uri=True)) as database:
                remaining = database.execute('SELECT COUNT(*) FROM game_event_outbox').fetchone()[0]
            if remaining != len(records) or len(list((scratch / 'spool/rejected').glob('*.json'))) != len(invalid):
                raise ValueError('Rejected controls changed the accepted queue')
        result = dict(status='native-producer-agent-contract-accepted',records=len(records),cases=len(CASES),
                      invalidControls=invalid,deliveryEnabled=False,queue='temporary synthetic queue removed',
                      recordsSha256={filename: hashlib.sha256(raw).hexdigest() for filename, raw, _ in records})
        (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print('PRODUCER_AGENT_CONTRACT=passed records=' + str(len(records)) + ' rejected_controls=' + str(len(invalid)))
    finally:
        (output / 'agent-cli.log').write_text('\n'.join(log), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    execute(args.evidence.resolve(), args.output.resolve())


if __name__ == '__main__':
    main()
