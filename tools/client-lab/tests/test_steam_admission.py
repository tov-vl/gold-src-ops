import io
from pathlib import Path
from types import SimpleNamespace
import unittest

path = Path(__file__).resolve().parents[1] / "templates/fixture/steam_admission.py.in"
steam = {}
exec(compile(path.read_bytes(), str(path), "exec"), steam)


def record(userid, account, event):
    return f'L 11/22/2026 - 00:00:00: "Player<{userid}><{account}><>" {event}\n'.encode()


class SteamAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.pipe = io.BytesIO()
        self.server = SimpleNamespace(process=SimpleNamespace(stdin=self.pipe))
        self.admitted = set()
        self.status = {}

    def observe(self, log):
        steam["observe"](self.server, log, self.admitted, self.status, lambda: None)

    def test_waits_for_engine_approval_and_join_readiness(self):
        self.observe(record(5, "STEAM_0:1:123", "entered the game"))
        self.observe(record(5, "STEAM_0:1:123", "STEAM USERID validated"))
        self.assertEqual(b"", self.pipe.getvalue())
        both = (record(5, "STEAM_0:1:123", "entered the game")
                + record(5, "STEAM_0:1:123", "STEAM USERID validated"))
        self.observe(both)
        self.observe(both)
        self.assertEqual(b"gso_fixture_steam_allow_join 5\n", self.pipe.getvalue())
        self.assertEqual({"steamAdmissions": 1, "observerDisconnected": False}, self.status)

    def test_new_connection_same_account_and_exact_first_inventory(self):
        one = record(5, "STEAM_0:1:123", "entered the game") + record(5, "STEAM_0:1:123", "STEAM USERID validated")
        two = record(6, "STEAM_1:1:123", "entered the game") + record(6, "STEAM_1:1:123", "STEAM USERID validated")
        self.observe(one)
        with self.assertRaisesRegex(RuntimeError, "rejoin window"):
            self.observe(one + two)
        self.status["vacantForRejoin"] = True
        observed = (b"FIXTURE_STEAM_OBSERVE_DISCONNECT observed=1\n"
                    b"FIXTURE_STEAM_OBSERVE_SPAWN authorized=1 same_account=1 new_userid=1 inventory=0x14000000\n")
        self.observe(one + two + observed)
        self.assertEqual(2, self.status["steamAdmissions"])
        self.assertEqual(0x14000000, self.status["steamSpawnInventory"])

    def test_other_account_refused(self):
        first = record(5, "STEAM_0:1:123", "STEAM USERID validated")
        with self.assertRaisesRegex(RuntimeError, "unexpected account"):
            self.observe(first + record(6, "STEAM_1:1:124", "STEAM USERID validated"))
        self.assertEqual(b"", self.pipe.getvalue())

    def test_private_console_check_uses_real_status(self):
        log = (b"prefix\nGSO_PRIVATE_STEAM_STATUS \n"
               b"hostname:  Test server\nmap     :  de_dust2 at: 0 x, 0 y, 0 z\n"
               b"players :  0 active (4 max)\nGSO_PRIVATE_STEAM_END \n")
        self.server.log_checkpoint = lambda: 7
        self.server.read_private_log = lambda: log
        self.assertTrue(steam["is_vacant"](self.server, "Test server"))
        self.assertFalse(steam["is_vacant"](self.server, "Other server"))
