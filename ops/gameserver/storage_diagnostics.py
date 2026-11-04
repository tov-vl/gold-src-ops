"""Bounded storage facts; no device, mount, path or file inventory crosses SSH."""
import datetime as dt
import json

UTC = dt.timezone.utc
ROLES = ('queue', 'spool', 'players', 'backup')
MAX_JSON = 8192
MAX_NUMBER = 9223372036854775807


def require(value):
    if not value:
        raise ValueError('Invalid storage observation.')


def unique(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value)
        value[key] = item
    return value


def decode(raw):
    require(len(raw) <= MAX_JSON)
    return json.loads(raw, object_pairs_hook=unique, parse_constant=lambda _: require(False))


def number(value):
    require(type(value) is int and 0 <= value <= MAX_NUMBER)
    return value


def validate_row(row):
    require(type(row) is dict and type(row.get('available')) is bool)
    if not row['available']:
        require(set(row) == {'available'})
        return
    require(set(row) == {'available', 'bytesTotal', 'bytesAvailable', 'inodesTotal', 'inodesAvailable'})
    require(0 < number(row['bytesTotal']) and number(row['bytesAvailable']) <= row['bytesTotal'])
    if row['inodesTotal'] is None:
        require(row['inodesAvailable'] is None)
    else:
        require(0 < number(row['inodesTotal']) and number(row['inodesAvailable']) <= row['inodesTotal'])


def validate(value, now):
    require(type(value) is dict and set(value) == {'schemaVersion', 'observedAtUtc', 'roles'})
    require(type(value['schemaVersion']) is int and value['schemaVersion'] == 1)
    raw = value['observedAtUtc']
    require(type(raw) is str and len(raw) <= 40)
    observed = dt.datetime.fromisoformat(raw.replace('Z', '+00:00'))
    require(observed.tzinfo is not None and -5 <= (now - observed).total_seconds() <= 30)
    require(type(value['roles']) is dict and set(value['roles']) == set(ROLES))
    for row in value['roles'].values():
        validate_row(row)
    return observed
