#!/usr/bin/env python3
"""Fixed read-only SSH endpoint. No caller-supplied path, environment or command."""
import datetime as dt
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import agent_diagnostics as contract

CONFIG = Path('/etc/goldsrcops/gameserver')
INSTALL = Path('/opt/goldsrcops/gameserver/game-event-pilot/releases')
GUARD = Path('/usr/local/libexec/goldsrcops-game-event-persistent')
HOME = Path('/var/lib/goldsrc')
AGENT = 'goldsrcops-game-event-agent.service'


def trusted(path, mode):
    contract.require(not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    contract.require(stat.S_ISREG(info.st_mode) and info.st_uid == 0 and info.st_nlink == 1
                     and stat.S_IMODE(info.st_mode) == mode and info.st_size <= contract.MAX_JSON)
    return path.read_bytes()


def pairs(raw):
    lines = [line.split('=', 1) for line in raw.decode().splitlines()]
    contract.require(all(len(x) == 2 and x[0] and x[1] for x in lines))
    return contract.unique(lines)


def run(arguments, timeout=10):
    with subprocess.Popen([str(x) for x in arguments], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          start_new_session=True,
                          env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C', 'TZ': 'UTC'}) as process:
        try:
            raw, _ = process.communicate(timeout=timeout)
        except BaseException:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise
        contract.require(process.returncode == 0 and len(raw) <= contract.MAX_JSON)
        return raw


def capture():
    # Existing guard validates manifest, binary, environment, secret metadata and
    # the complete persistent reference graph. Never initialize or open SQLite here.
    info = GUARD.stat()
    contract.require(not GUARD.is_symlink() and info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o755)
    contract.require(run([GUARD, '--guard-agent']).strip() == b'PERSISTENT_GAME_EVENT_GUARD=passed')
    marker = pairs(trusted(CONFIG / 'game-event-pilot-installed', 0o640))
    digest = marker['bundle_sha256']
    contract.require(re.fullmatch('[a-f0-9]{64}', digest))
    environment = pairs(trusted(CONFIG / 'game-event-agent.env', 0o640))
    contract.require(all(re.fullmatch('[A-Za-z_][A-Za-z0-9_]*', key) for key in environment))
    environment.update({'HOME': str(HOME), 'PATH': '/usr/bin:/bin',
        'DOTNET_BUNDLE_EXTRACT_BASE_DIR': str(HOME / 'game-event-agent/dotnet-bundle'),
        'GameEventAgent__Delivery__OAuth__ClientSecretFile': str(CONFIG / 'secrets/game-event-agent-client-secret')})
    active = pairs(run(['systemctl', 'show', AGENT, '--property=LoadState,ActiveState,SubState']))
    contract.require(set(active) == {'LoadState', 'ActiveState', 'SubState'} and active['LoadState'] == 'loaded')
    snapshot = contract.decode(run(['runuser', '-u', 'goldsrc', '--', 'env', '-i',
        *[key + '=' + value for key, value in environment.items()],
        INSTALL / digest / 'agent/GoldSrcOps.GameEventAgent', 'status', '--json']))
    return contract.project(snapshot, active['ActiveState'] == 'active' and active['SubState'] == 'running',
                            dt.datetime.now(contract.UTC))


def main():
    contract.require(os.geteuid() == 0 and len(sys.argv) == 1)
    try:
        value = capture()
    except (ValueError, KeyError, TypeError, OSError, OverflowError, subprocess.SubprocessError):
        value = {'schemaVersion': 1, 'available': False}
    print(json.dumps(value, separators=(',', ':'), allow_nan=False))


if __name__ == '__main__':
    def interrupted(_number, _frame):
        raise InterruptedError('Observation interrupted.')
    for number in (signal.SIGHUP, signal.SIGTERM):
        signal.signal(number, interrupted)
    try:
        main()
    except Exception:
        print('AGENT_OBSERVATION=refused', file=sys.stderr)
        sys.exit(1)
