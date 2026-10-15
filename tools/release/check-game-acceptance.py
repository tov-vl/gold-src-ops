#!/usr/bin/env python3
"""Read-only addon acceptance through existing SSH aliases; dry run by default."""
import argparse
import base64
import hashlib
import json
import re
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from acceptance_checks import require, validate_profile


class Refusal(Exception):
    def __init__(self, report):
        self.report = report


def invoke(role, profile):
    require(role in ('game-host', 'control-plane', 'control-plane-hook'))
    validate_profile(profile)
    directory = Path(__file__).resolve().parent
    worker = (directory / 'acceptance_checks.py').read_text(encoding='utf-8')
    protocol = (directory.parents[1] / 'ops/gameserver/addon_runtime_protocol.py').read_bytes()
    code = ('import base64,types\nprotocol=types.ModuleType("runtime_protocol")\n'
            + 'exec(compile(base64.b64decode(' + repr(base64.b64encode(protocol).decode())
            + '),"runtime_protocol","exec"),protocol.__dict__)\n' + worker
            + '\nremote_main(' + repr(role) + ',' + repr(profile) + ',protocol)\n')
    alias = profile['gameAlias' if role == 'game-host' else 'controlPlaneAlias']
    args = ['ssh', '-F', profile['sshConfig'], '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=yes',
            '-o', 'ConnectTimeout=10', '-o', 'ConnectionAttempts=1', '-T', alias, 'sudo -n python3 -I -B -']
    result = subprocess.run(args, input=code.encode(), capture_output=True, timeout=20 if role == 'control-plane-hook' else 65)
    require(len(result.stdout) <= 8192)
    value = json.loads(result.stdout)
    if result.returncode != 0:
        report = {key: value.get(key) for key in ('role', 'check', 'category')}
        require(report['role'] == role and all(isinstance(report[k], str) and re.fullmatch('[a-zA-Z_]{1,40}', report[k]) for k in ('check', 'category')))
        raise Refusal({'result': 'failed', **report})
    require(value['result'] == 'passed' and value['role'] == role)
    return value


def invoke_report(role, profile):
    """Return sanitized evidence for a nonce-bound external hook receipt."""
    require(role == 'control-plane-hook')
    try:
        return invoke(role, profile)
    except Refusal as error:
        return error.report
    except Exception as error:
        category = type(error).__name__
        if category not in ('TimeoutExpired', 'OSError', 'ValueError', 'JSONDecodeError', 'UnicodeDecodeError', 'KeyError', 'TypeError'):
            category = 'Exception'
        return {'result': 'failed', 'role': role, 'check': 'coordinator_transport', 'category': category}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--profile-sha256', required=True)
    parser.add_argument('--execute', action='store_true')
    args = parser.parse_args()
    require(args.profile.is_file() and not args.profile.is_symlink() and args.profile.stat().st_size <= 4096)
    raw = args.profile.read_bytes()
    require(hashlib.sha256(raw).hexdigest() == args.profile_sha256)
    profile = json.loads(raw)
    validate_profile(profile)
    if not args.execute:
        print(json.dumps({'result': 'validated-plan', 'network': False, 'secretRead': False}))
        return
    before = invoke('game-host', profile)
    remote = invoke('control-plane', profile)
    after = invoke('game-host', profile)
    require(before['units'] == after['units'] and before['baselineSha256'] == after['baselineSha256'])
    print(json.dumps({'result': 'game-acceptance-passed', 'version': remote['version'],
                      'localA2s': True, 'lookup': True, 'baselineFiles': after['baselineFiles'],
                      'pending': False, 'runtimeUnchanged': True}))


if __name__ == '__main__':
    try:
        main()
    except Refusal as error:
        print(json.dumps(error.report))
        sys.exit(1)
    except Exception:
        print('GAME_ACCEPTANCE=failed; inspect the fixed boundary before another check', file=sys.stderr)
        sys.exit(1)
