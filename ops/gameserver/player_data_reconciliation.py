"""Evidence contract for closing an uncertain weekly slot after a later restart."""
import datetime as dt
import hashlib
import json
import re
from zoneinfo import ZoneInfo

STATE = 'closed-uncertain'
PROOF_STATE = 'current-absence-after-restart'
MOSCOW = ZoneInfo('Europe/Moscow')


def require(condition):
    if not condition:
        raise ValueError('Invalid uncertain-slot evidence.')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def decode(raw):
    require(len(raw) <= 16384)
    value = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique_object,
                       parse_constant=lambda _: require(False))
    require(isinstance(value, dict))
    return value


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def timestamp(value):
    require(isinstance(value, str))
    parsed = dt.datetime.fromisoformat(value)
    require(parsed.tzinfo is not None and parsed.timestamp() > 0)
    return parsed


def due(slot):
    require(isinstance(slot, str) and re.fullmatch(r'\d{4}-\d{2}-\d{2}', slot) is not None)
    date = dt.date.fromisoformat(slot)
    require(date.weekday() == 6)
    return dt.datetime.combine(date, dt.time(4), MOSCOW)


def original(record, slot):
    require(set(record) == {'schema', 'slot', 'state', 'startedUtc'}
            and type(record['schema']) is int and record['schema'] == 1
            and record['slot'] == slot and record['state'] == 'started')
    start = timestamp(record['startedUtc'])
    require(due(slot) <= start < due(slot) + dt.timedelta(minutes=5))
    return start


def proof(value, slot, completed):
    require(set(value) == {'schema', 'slot', 'state', 'observedUtc', 'gameStartedUtc',
                           'gameInvocationId', 'gamePendingAbsent', 'gameSlotAbsent'}
            and type(value['schema']) is int and value['schema'] == 1
            and value['slot'] == slot and value['state'] == PROOF_STATE
            and value['gamePendingAbsent'] is True and value['gameSlotAbsent'] is True
            and isinstance(value['gameInvocationId'], str)
            and re.fullmatch(r'[a-f0-9]{32}', value['gameInvocationId']) is not None)
    observed = timestamp(value['observedUtc'])
    started = timestamp(value['gameStartedUtc'])
    # This proves only current absence after restart, never the old attempt's outcome.
    require(due(slot) + dt.timedelta(minutes=5) < started <= observed
            and completed - dt.timedelta(minutes=2) <= observed <= completed)


def validate(root, slot, record, moment, reader, private):
    """Validate the retained terminal state without changing copy age or any files."""
    require(set(record) == {'schema', 'slot', 'state', 'startedUtc', 'completedUtc'}
            and type(record['schema']) is int and record['schema'] == 1
            and record['state'] == STATE and record['slot'] == slot)
    completed = timestamp(record['completedUtc'])
    require(due(slot) + dt.timedelta(minutes=5) < completed <= moment)
    cycle = private(root / slot, directory=True)
    require({p.name for p in cycle.iterdir()} ==
            {'result.json', 'original-pending.json', 'original-result.json', 'reconciliation.json'})
    evidence = reader(cycle / 'reconciliation.json')
    require(set(evidence) == {'schema', 'state', 'slot', 'startedUtc', 'completedUtc',
                              'originalPending', 'originalResult', 'gameProof', 'gameProofSha256',
                              'lastSuccessSha256', 'pendingSha256', 'resultSha256'}
            and type(evidence['schema']) is int and evidence['schema'] == 1
            and all(evidence[k] == record[k] for k in ('state', 'slot', 'startedUtc', 'completedUtc')))
    for key in ('lastSuccessSha256', 'pendingSha256', 'resultSha256', 'gameProofSha256'):
        require(isinstance(evidence[key], str) and re.fullmatch(r'[a-f0-9]{64}', evidence[key]) is not None)
    for name, key, hash_key in (('original-pending.json', 'originalPending', 'pendingSha256'),
                                ('original-result.json', 'originalResult', 'resultSha256')):
        retained = reader(cycle / name)
        original(retained, slot)
        require(retained == evidence[key] and retained['startedUtc'] == record['startedUtc']
                and digest(private(cycle / name).read_bytes()) == evidence[hash_key])
    require(evidence['originalPending'] == evidence['originalResult']
            and evidence['pendingSha256'] == evidence['resultSha256'])
    # Keep the exact original proof bytes as well as structured fields in the evidence.
    proof_bytes = evidence['gameProof']
    require(isinstance(proof_bytes, str))
    raw = proof_bytes.encode('utf-8')
    require(digest(raw) == evidence['gameProofSha256'])
    proof(decode(raw), slot, completed)
    success_path = root / 'last-success.json'
    success = reader(success_path)
    require(success['schema'] == 1 and success['state'] == 'verified'
            and success['workload'] == 'goldsrcops-player-data-v1'
            and timestamp(success['capturedUtc']) <= timestamp(record['startedUtc'])
            and digest(private(success_path).read_bytes()) == evidence['lastSuccessSha256'])
    for key in ('snapshotId', 'bundleSha256'):
        require(isinstance(success[key], str) and re.fullmatch(r'[a-f0-9]{64}', success[key]) is not None)
    return evidence
