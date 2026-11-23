"""Exercise fail-closed public input preparation with synthetic downloads."""

from argparse import Namespace
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import prepare


class Response(io.BytesIO):
    def __init__(self, url, contents, disconnect=False):
        super().__init__(contents)
        self.url = url
        self.disconnect = disconnect

    def read(self, count=-1):
        if self.disconnect and self.tell():
            raise ConnectionResetError("fixture transport interrupted")
        if self.disconnect:
            count = min(count, 3)
        return super().read(count)


class PrepareFailureTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="player-recovery-prep-failure-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.helpers = prepare.engine_inputs()
        self.fixture_assets = {name: self.asset(item, payload) for name, item, payload in (
            ("zip", prepare.RELEASE, b"synthetic stable archive"),
            ("deb", prepare.LIBSTDCPP, b"synthetic compatibility package"),
        )}
        self.sdk = b"synthetic library"
        self.helpers.LOCK = self.root / "fixture-lock.json"
        self.helpers.LOCK.write_text(json.dumps({
            "assets": [],
            "images": {"steam": {"reference": "synthetic-sdk", "path": "/fixture/library.so",
                                 "sha256": self.hash(self.sdk)}},
        }), encoding="utf-8")
        self.output = self.root / "new-output"

    def hash(self, data):
        return hashlib.sha256(data).hexdigest()

    def asset(self, entry, payload):
        return {**entry, "sha256": self.hash(payload)}, payload

    def fake_docker(self, args, reason):
        verb = args[3]
        if verb == "cp":
            Path(args[-1]).write_bytes(self.sdk)
        elif verb not in {"create", "rm"}:
            self.fail("unexpected Docker command")
        return b""

    def run_case(self, *, interrupted=False, substituted=False):
        zip_asset, zip_payload = self.fixture_assets["zip"]
        deb_asset, deb_payload = self.fixture_assets["deb"]
        responses = {zip_asset["url"]: zip_payload, deb_asset["url"]: deb_payload}
        if substituted:
            responses[deb_asset["url"]] = b"unexpected substituted package"

        def get(url, *, timeout):
            self.assertEqual(60, timeout)
            return Response(url, responses[url], disconnect=interrupted and url == zip_asset["url"])

        with patch.object(prepare, "engine_inputs", return_value=self.helpers), \
             patch.object(prepare, "RELEASE", zip_asset), \
             patch.object(prepare, "LIBSTDCPP", deb_asset), \
             patch.object(self.helpers.urllib.request, "urlopen", side_effect=get), \
             patch.object(prepare, "docker_command", return_value=["docker", "--context", "fixture"]), \
             patch.object(prepare, "ensure_public_image"), \
             patch.object(prepare, "command", side_effect=self.fake_docker) as docker_calls, \
             patch.object(prepare.subprocess, "run") as build:
            if interrupted:
                with self.assertRaises(ConnectionResetError):
                    prepare.prepare(Namespace(output=self.output, docker_context="fixture"))
                self.assertFalse((self.output / "stable.zip").exists())
                docker_calls.assert_not_called()
            else:
                with self.assertRaisesRegex(ValueError, r"^Asset digest mismatch: lib32stdc\+\+\.deb"):
                    prepare.prepare(Namespace(output=self.output, docker_context="fixture"))
                verbs = [call.args[0][3] for call in docker_calls.call_args_list]
                self.assertEqual(["create", "cp", "rm"], verbs)
            build.assert_not_called()
        self.assertFalse((self.output / "engine-image-id").exists())
        self.assertEqual([], list(self.output.rglob("*.partial-*")))

    def test_interrupted_archive_is_discarded_before_build(self):
        self.run_case(interrupted=True)

    def test_substituted_compatibility_package_stops_before_build(self):
        self.run_case(substituted=True)


if __name__ == "__main__":
    unittest.main()
