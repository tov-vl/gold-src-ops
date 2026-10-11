#!/usr/bin/env python3
"""Acceptance rejects stale, incomplete, malformed and unhealthy runtime replies."""
import importlib.util
import io
from pathlib import Path
import socket
import tempfile
import threading
import unittest

spec = importlib.util.spec_from_file_location('probe', Path(__file__).resolve().parents[2]/'ops/gameserver/addon-runtime-probe.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)
GOOD = '''WEAPON_SELECTION_STATUS version=0.24.0 enabled=1 persistent_stats=1
PLAYER_PREFERENCES_STATUS version=0.24.0 storage=nvault errors=0
PLAYER_STATS_STATUS version=0.24.0 storage=nvault errors=0
PLAYER_LEADERBOARD_STATUS version=0.24.0 ready=1 errors=0
'''


class ProbeTests(unittest.TestCase):
    def test_secret_input_is_bounded_and_rejects_command_delimiters(self):
        self.assertEqual(b'fixture', p.read_password(io.BytesIO(b'fixture\n')))
        for secret in (b'', b'x'*129, b'x'*128+b'\ny', b'quote"', b'a;b', b'a\nb', b'a\\b'):
            with self.subTest(length=len(secret)), self.assertRaises(ValueError):
                p.read_password(io.BytesIO(secret))

    def test_valid_and_wrong_loaded_version(self):
        p.verify(GOOD, '0.24.0')
        with self.assertRaises(ValueError): p.verify(GOOD, '0.23.0')

    def test_incomplete_duplicate_and_mixed_version(self):
        for text in (GOOD.split('\n', 1)[1], GOOD+GOOD, GOOD.replace('ready=1', 'ready=1 ready=1'),
                     GOOD.replace('PLAYER_STATS_STATUS version=0.24.0', 'PLAYER_STATS_STATUS version=0.23.0')):
            with self.subTest(text=text), self.assertRaises(ValueError): p.verify(text, '0.24.0')

    def test_storage_errors_disabled_and_runtime_errors(self):
        for text in (GOOD.replace('storage=nvault', 'storage=unavailable'), GOOD.replace('errors=0', 'errors=1'),
                     GOOD.replace('enabled=1', 'enabled=0'), GOOD.replace('ready=1', 'ready=0'), GOOD+'Run time error 10'):
            with self.subTest(text=text), self.assertRaises(ValueError): p.verify(text, '0.24.0')

    def test_error_log_and_symlink_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            p.errors_absent(root)
            (root/'error_today.log').write_text('runtime failure')
            with self.assertRaises(ValueError): p.errors_absent(root)
            (root/'error_today.log').unlink()
            (root/'error_today.log').symlink_to(root/'absent')
            with self.assertRaises(ValueError): p.errors_absent(root)

    def exchange(self, challenge, response):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
            server.bind(('127.0.0.1', 0))
            server.settimeout(3)
            requests = []
            def respond():
                request, peer = server.recvfrom(4096)
                requests.append(request)
                server.sendto(challenge, peer)
                if response is not None:
                    request, peer = server.recvfrom(4096)
                    requests.append(request)
                    server.sendto(response, peer)
            thread = threading.Thread(target=respond)
            thread.start()
            try:
                return p.query(server.getsockname(), b'fixture', p.COMMANDS[0])
            finally:
                thread.join(timeout=4)
                self.assertFalse(thread.is_alive())
                self.assertEqual(p.HEADER+b'challenge rcon\n', requests[0])

    def test_rcon_fresh_challenge_and_response(self):
        self.assertEqual(GOOD, self.exchange(p.HEADER+b'challenge rcon 123\n', p.HEADER+b'l'+GOOD.encode()+b'\0'))

    def test_rcon_malformed_challenge_header_and_oversized_reply(self):
        with self.assertRaises(ValueError): self.exchange(b'invalid', None)
        for packet in (b'invalid', p.HEADER+b'l'+b'x'*8193):
            with self.assertRaises(ValueError): self.exchange(p.HEADER+b'challenge rcon -1\n', packet)


if __name__ == '__main__': unittest.main(verbosity=2)
