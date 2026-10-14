"""Bounded GoldSrc status transport shared by fixture and control-plane probes."""
import re
import socket
import time

HEADER = b'\xff' * 4
COMMANDS = ('goldsrcops_weapons_status', 'goldsrcops_stats_status')


def require(value):
    if not value:
        raise ValueError('Acceptance contract failed.')


def query(address, password, command):
    require(command in COMMANDS or re.fullmatch(r'goldsrcops_player_lookup [a-f0-9]{16} (?:[A-F0-9]{2}){1,31}', command))
    deadline = time.monotonic() + 4
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
        client.connect(address)
        client.settimeout(2)
        client.send(HEADER + b'challenge rcon\n')
        challenge = client.recv(4096)
        match = re.fullmatch(rb'\xff{4}challenge rcon (-?\d+)\s*\x00?', challenge)
        require(match is not None)
        client.send(HEADER + b'rcon ' + match[1] + b' "' + password + b'" ' + command.encode('ascii') + b'\n')
        output = bytearray()
        while True:
            remaining = deadline - time.monotonic()
            require(remaining > 0)
            client.settimeout(min(0.3 if output else 2, remaining))
            try:
                packet = client.recv(8193)
            except socket.timeout:
                require(bool(output))
                break
            require(len(packet) <= 8192 and packet.startswith(HEADER + b'l'))
            output.extend(packet[5:].rstrip(b'\x00'))
            require(len(output) <= 16384)
        text = output.decode('utf-8')
        require(not re.search(r'Bad rcon_password|banned|No password set', text, re.I))
        return text


def verify(text, version):
    require(re.fullmatch(r'\d+\.\d+\.\d+', version) is not None)
    require(not re.search(r'Run time error|Load fails|bad load|ML_NOTFOUND|Bad rcon_password', text, re.I))
    required = {
        'WEAPON_SELECTION_STATUS': {'enabled': '1', 'persistent_stats': '1'},
        'PLAYER_PREFERENCES_STATUS': {'storage': 'nvault', 'errors': '0'},
        'PLAYER_STATS_STATUS': {'storage': 'nvault', 'errors': '0'},
        'PLAYER_LEADERBOARD_STATUS': {'ready': '1', 'errors': '0'},
    }
    for prefix, expected in required.items():
        lines = [line for line in text.splitlines() if line.startswith(prefix + ' ')]
        require(len(lines) == 1)
        pairs = [token.split('=', 1) for token in lines[0].split()[1:]]
        require(all(len(pair) == 2 for pair in pairs) and len(dict(pairs)) == len(pairs))
        fields = dict(pairs)
        require(fields.get('version') == version and all(fields.get(k) == v for k, v in expected.items()))


def read_password(source):
    raw = source.read(130)
    require(len(raw) <= 129)
    password = raw.rstrip(b'\r\n')
    require(1 <= len(password) <= 128 and all(32 < c < 127 and c not in (34, 59, 92) for c in password))
    return password
