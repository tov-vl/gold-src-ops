"""Strict identity-free host memory and sampled CPU observation contract."""
import datetime as dt
import json
import math

UTC = dt.timezone.utc
MAX_JSON = 4096
MAX_NUMBER = 9223372036854775807
GROUPS = ('memory', 'cpu')


def require(value):
    if not value:
        raise ValueError('Invalid resource observation.')


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def decode(raw):
    require(len(raw) <= MAX_JSON)
    return json.loads(raw, object_pairs_hook=unique, parse_constant=lambda _: require(False))


def number(value):
    require(type(value) is int and 0 <= value <= MAX_NUMBER)
    return value


def real(value, low, high):
    require(type(value) in (int, float) and math.isfinite(value) and low <= value <= high)
    return value


def validate_group(name, row):
    require(type(row) is dict and type(row.get('available')) is bool)
    if not row['available']:
        require(set(row) == {'available'})
    elif name == 'memory':
        require(set(row) == {'available', 'totalBytes', 'availableBytes', 'swapTotalBytes', 'swapFreeBytes'})
        require(0 < number(row['totalBytes']) and number(row['availableBytes']) <= row['totalBytes'])
        require(number(row['swapFreeBytes']) <= number(row['swapTotalBytes']))
    else:
        require(name == 'cpu' and set(row) == {'available', 'busyRatio', 'stealRatio', 'sampleSeconds'})
        real(row['busyRatio'], 0, 1)
        real(row['stealRatio'], 0, 1)
        require(row['busyRatio'] + row['stealRatio'] <= 1.000000000001)
        real(row['sampleSeconds'], 0.75, 3)


def validate(value, now):
    require(type(value) is dict and set(value) == {'schemaVersion', 'observedAtUtc', 'memory', 'cpu'})
    require(type(value['schemaVersion']) is int and value['schemaVersion'] == 1)
    raw = value['observedAtUtc']
    require(type(raw) is str and len(raw) <= 40)
    observed = dt.datetime.fromisoformat(raw.replace('Z', '+00:00'))
    require(observed.tzinfo is not None and -5 <= (now - observed).total_seconds() <= 30)
    for name in GROUPS:
        validate_group(name, value[name])
    return observed
