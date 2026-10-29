"""Bounded, identity-free projection of the passive agent status contract."""
import datetime as dt
import json
import math

UTC = dt.timezone.utc
STATES = {'disabled': 0, 'unavailable': 1, 'starting': 2, 'running': 3,
          'stale': 4, 'failed': 5, 'retrying': 6, 'blocked': 7, 'stopped': 8}
OUTCOMES = (None, 'idle', 'progress', 'failed', 'retrying', 'blocked')
TIMES = ('lastStartedAtUtc', 'lastCompletedAtUtc', 'lastSuccessfulAtUtc', 'stoppedAtUtc')
COUNTS = ('queueCapacity', 'pending', 'inFlight', 'deadLetter', 'spoolReady', 'spoolProcessing', 'spoolRejected')
MAX_JSON = 16384


def require(condition):
    if not condition:
        raise ValueError('Invalid agent observation.')


def unique(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def decode(raw):
    require(len(raw) <= MAX_JSON)
    return json.loads(raw, object_pairs_hook=unique, parse_constant=lambda _: require(False))


def number(value, maximum=2147483647):
    require(type(value) is int and 0 <= value <= maximum)
    return value


def moment(value, now):
    require(type(value) is str and len(value) <= 40)
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(parsed.tzinfo is not None and 0 < parsed.timestamp() <= now.timestamp() + 5)
    return parsed


def loop_state(value, active, now):
    require(set(value) == {'enabled', 'state', 'staleAfterSeconds', *TIMES, 'outcome'})
    require(type(value['enabled']) is bool and value['state'] in STATES and value['outcome'] in OUTCOMES)
    budget = number(value['staleAfterSeconds'], 10000000)
    require(not value['enabled'] or budget > 0)
    times = {key: moment(value[key], now) if value[key] is not None else None for key in TIMES}
    start, end, success, stop = (times[key] for key in TIMES)
    require(end is None or start is not None)
    require((end is None) == (value['outcome'] is None))
    require(success is None or end is not None and success <= end)
    if not value['enabled']:
        return 'disabled'
    if not active or stop is not None:
        return 'stopped'
    if start is None:
        return 'unavailable'
    latest = max(start, end) if end is not None else start
    if (now - latest).total_seconds() > budget:
        return 'stale'
    if end is None:
        return 'starting'
    if start > end:
        return 'running'
    return value['outcome'] if value['outcome'] in ('failed', 'retrying', 'blocked') else 'running'


def validate(value, now):
    require(type(value) is dict and type(value.get('schemaVersion')) is int
            and value['schemaVersion'] == 1 and type(value.get('available')) is bool)
    if not value['available']:
        require(set(value) == {'schemaVersion', 'available'})
        return value
    require(set(value) == {'schemaVersion', 'available', 'observedAtUtc', 'serviceActive',
                          *COUNTS, 'spoolAvailable', 'oldestWaitingAgeSeconds', 'loops'})
    observed = moment(value['observedAtUtc'], now)
    require(-5 <= (now - observed).total_seconds() <= 30)
    require(type(value['serviceActive']) is bool and type(value['spoolAvailable']) is bool)
    for name in COUNTS:
        number(value[name])
    require(1 <= value['queueCapacity'] <= 100000)
    age = value['oldestWaitingAgeSeconds']
    if value['pending'] + value['inFlight'] == 0:
        require(age is None)
    else:
        require(type(age) in (int, float) and math.isfinite(age) and 0 <= age <= 10000000000)
    require(type(value['loops']) is dict and set(value['loops']) == {'import', 'delivery'})
    for loop in value['loops'].values():
        loop_state(loop, value['serviceActive'], now)
    return value


def project(snapshot, active, now):
    require(type(snapshot['schemaVersion']) is int and snapshot['schemaVersion'] == 1 and type(active) is bool)
    value = {'schemaVersion': 1, 'available': True, 'observedAtUtc': snapshot['observedAtUtc'],
             'serviceActive': active, 'queueCapacity': snapshot['queueCapacity'],
             'pending': snapshot['queue']['pending'], 'inFlight': snapshot['queue']['inFlight'],
             'deadLetter': snapshot['queue']['deadLetter'], 'spoolAvailable': snapshot['spoolAvailable'],
             'spoolReady': snapshot['spool']['ready'], 'spoolProcessing': snapshot['spool']['processing'],
             'spoolRejected': snapshot['spool']['rejected'],
             'oldestWaitingAgeSeconds': snapshot['oldestWaitingAgeSeconds'], 'loops': {}}
    for name, enabled in (('import', snapshot['spoolImportEnabled']), ('delivery', snapshot['deliveryEnabled'])):
        source = snapshot['health'][name]
        cycle = source['cycle'] or dict.fromkeys((*TIMES, 'outcome'))
        value['loops'][name] = {'enabled': enabled, 'state': source['state'],
                               'staleAfterSeconds': source['staleAfterSeconds'],
                               **{key: cycle[key] for key in (*TIMES, 'outcome')}}
    return validate(value, now)
