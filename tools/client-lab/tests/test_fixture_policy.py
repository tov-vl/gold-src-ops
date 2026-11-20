"""Offline fixture/tunnel policy tests. No Docker process, socket or game is started."""
import ast
import base64
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("lab_fixture", ROOT / "lab_fixture.py")
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
tunnel = {"__name__": "policy_test"}
exec(compile((ROOT / "templates/fixture/udp_tunnel.py.in").read_text(), "udp_tunnel.py", "exec"), tunnel)
runner_source = (ROOT / "templates/fixture/run_fixture.py.in").read_text(encoding="utf-8")
runner_tree = ast.parse(runner_source.replace("{{LAN_REJOIN}}", "1").replace("{{GUNS_READINESS}}", "1"))
vacancy = {}
vacancy_function = next(node for node in runner_tree.body if isinstance(node, ast.FunctionDef) and node.name == "vacant_info")
exec(compile(ast.Module(body=[vacancy_function], type_ignores=[]), "run_fixture.py", "exec"), vacancy)
spec = importlib.util.spec_from_file_location("verify_lan_rejoin", ROOT / "verify_lan_rejoin.py")
rejoin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rejoin)


class FixturePolicyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="client-lab-policy-")
        self.root = Path(self.temporary.name)
        self.identity = "20000000-0000-4000-8000-000000000001"
        self.name = "gso-client-lab-" + self.identity.replace("-", "")
        self.plan = {"schema": 1, "id": self.identity, "language": "ru", "container": self.name, "network": self.name + "-net", "image": fixture.IMAGE,
                     "assets": {"cacheDirectory": r"C:\LabAssets\cache", "runtimeDirectory": r"C:\LabAssets\runtime", "steamSdkPath": r"C:\LabAssets\steamclient.so"}}

    def tearDown(self):
        self.temporary.cleanup()

    def read(self):
        (self.root / "plan.json").write_text(json.dumps(self.plan))
        return fixture.read_plan(self.root)

    def test_exact_local_plan(self):
        self.assertEqual(self.read()["container"], self.name)

    def test_lan_rejoin_requires_empty_matching_a2s_info(self):
        header = b"\xff\xff\xff\xffI0"
        fields = b"FixtureName\0de_dust2\0cstrike\0Counter-Strike\0"
        tail = b"\x0a\0\0\x04\0dle\0"
        empty = header + fields + tail
        vacant = vacancy["vacant_info"]
        self.assertTrue(vacant(empty, "FixtureName"))
        for packet in (empty.replace(b"de_dust2", b"cs_office"), empty[:-1],
                       header + fields + b"\x0a\0\x01\x04\0" + tail[5:],
                       header + fields + b"\x0a\0\0\x04\x02" + tail[5:],
                       b"\xff\xff\xff\xffA1234"):
            with self.subTest(packet=packet):
                self.assertFalse(vacant(packet, "FixtureName"))
        self.assertFalse(vacant(empty, "OtherFixture"))

    def test_lan_rejoin_evidence_requires_unique_ordered_events(self):
        expected = ("round", "disconnect", "rejoin", "spawn")
        self.assertTrue(rejoin.one_ordered_sequence(expected, expected))
        for events in (("round", "rejoin", "spawn"), ("round", "rejoin", "disconnect", "spawn"),
                       ("round", "disconnect", "rejoin", "rejoin", "spawn")):
            self.assertFalse(rejoin.one_ordered_sequence(events, expected))

    def test_rejects_mutated_image_target_and_extra_fields(self):
        for field, replacement in (("image", "latest"), ("container", "unrelated-service"), ("network", "bridge"), ("language", "de"), ("id", "--all")):
            original = self.plan[field]
            self.plan[field] = replacement
            with self.assertRaises(ValueError):
                self.read()
            self.plan[field] = original
        self.plan["command"] = "unrelated"
        with self.assertRaises(ValueError):
            self.read()

    def test_rejects_asset_mount_injection_and_traversal(self):
        for value in (r"C:\assets,readonly=false", r"C:\assets\..\Windows", r"\\host\share", "--privileged"):
            self.plan["assets"]["cacheDirectory"] = value
            with self.assertRaises(ValueError):
                self.read()

    def test_claim_never_overwritten(self):
        path = self.root / "claim.json"
        fixture.write_new(path, {"first": True})
        with self.assertRaises(FileExistsError):
            fixture.write_new(path, {"second": True})
        self.assertEqual(json.loads(path.read_text()), {"first": True})

    def test_stop_refuses_another_container_before_runtime_mutation(self):
        self.read()
        (self.root / "container.json").write_text(json.dumps({"id": "a" * 64}))
        calls = []
        original = fixture.docker

        def fake(*args, **_kwargs):
            calls.append(args)
            return json.dumps([{"Name": "/unrelated", "Image": fixture.IMAGE, "Config": {"Labels": {"purpose": "client-lab"}}}])

        fixture.docker = fake
        try:
            with self.assertRaises(ValueError):
                fixture.stop(self.root, self.plan)
        finally:
            fixture.docker = original
        self.assertEqual(calls, [("inspect", "a" * 64)])

    def test_tunnel_round_trip_and_limits(self):
        self.assertEqual(tunnel["decode"](tunnel["encode"](7, b"packet")), (7, b"packet"))
        for flow, data in ((8, b"x"), (-1, b"x"), (True, b"x"), (0, b""), (0, b"x" * 4097)):
            with self.assertRaises(ValueError):
                tunnel["encode"](flow, data)
        for value in ({"id": True, "data": "eA=="}, {"id": 0, "data": "!"}, {"id": 0, "data": "eA==", "extra": True}, {"id": 0, "data": base64.b64encode(b"x" * 4097).decode()}):
            with self.assertRaises(ValueError):
                tunnel["decode"](json.dumps(value).encode())

    def test_evidence_failure_still_stops_only_the_verified_fixture(self):
        self.read()
        (self.root / "container.json").write_text(json.dumps({"id": "a" * 64}))
        calls = []
        running = [True]
        original = fixture.docker

        def fake(*args, **_kwargs):
            calls.append(args)
            if args[0] == "inspect":
                return json.dumps([{"Name": "/" + self.name, "Image": fixture.IMAGE,
                                    "Config": {"Labels": {"purpose": "client-lab"}}, "State": {"Running": running[0]}}])
            if args[0] == "exec":
                raise subprocess.CalledProcessError(1, "mock")
            if args[0] == "stop":
                running[0] = False
            return ""

        fixture.docker = fake
        try:
            result = fixture.stop(self.root, self.plan)
        finally:
            fixture.docker = original
        self.assertTrue(result["fixtureRemoved"])
        self.assertFalse(result["fixtureEvidenceComplete"])
        self.assertIn(("stop", "-t", "15", "a" * 64), calls)
        self.assertEqual(calls[-1], ("rm", "a" * 64))


if __name__ == "__main__":
    unittest.main()
