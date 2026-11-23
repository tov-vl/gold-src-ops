"""Prepare public inputs for the isolated 0.40.0 player-data recovery lab."""

import argparse
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid

from check import HERE, PAIR_SHA256, ROOT, command, docker_command, require


RELEASE = {
    "name": "stable.zip",
    "url": "https://github.com/tov-vl/gold-src-ops/releases/download/player-storage-v0.40.0/player-storage-v0.40.0-rc.2.zip",
    "sha256": PAIR_SHA256,
}
LIBSTDCPP = {
    "name": "lib32stdc++.deb",
    "url": "https://archive.debian.org/debian/pool/main/g/gcc-8/lib32stdc++6_8.3.0-6_amd64.deb",
    "sha256": "df69b110822b39f39600cd6ee889f4d7736b2105a7d9c8a100ce5f98d978abf0",
}
RESTIC_IMAGE = "restic/restic@sha256:136600b6ff6843d61d355f7f71f460a166429f35de6fd11b568fece3c9a4d510"


def engine_inputs():
    spec = importlib.util.spec_from_file_location("engine_ci", ROOT / "tools/smoke/engine-ci.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def ensure_public_image(docker, reference):
    inspection = subprocess.run(docker + ["image", "inspect", reference], capture_output=True,
                                timeout=30, check=False)
    if inspection.returncode != 0:
        subprocess.run(docker + ["pull", "--platform=linux/amd64", reference],
                       stdout=sys.stderr, timeout=300, check=True)
    data = json.loads(command(docker + ["image", "inspect", reference], "public_image_unavailable"))[0]
    require(reference in data["RepoDigests"] and data["Os"] == "linux"
            and data["Architecture"] == "amd64", "public_image_identity_mismatch")


def prepare(args):
    docker = docker_command(args.docker_context)
    helpers = engine_inputs()
    lock = json.loads(helpers.LOCK.read_bytes())
    destination = args.output.resolve()
    destination.mkdir()
    archives = destination / "assets"
    archives.mkdir()
    helpers.download(RELEASE, destination)
    for asset in lock["assets"]:
        helpers.download(asset, archives)

    steam = lock["images"]["steam"]
    ensure_public_image(docker, steam["reference"])
    ensure_public_image(docker, RESTIC_IMAGE)
    container = "recovery-public-sdk-" + uuid.uuid4().hex
    command(docker + ["create", "--name", container, "--pull", "never", "--network", "none",
                      "--entrypoint", "/bin/true", steam["reference"]], "steam_client_source_unavailable")
    try:
        path = destination / "steamclient.so"
        command(docker + ["cp", container + ":" + steam["path"], str(path)], "steam_client_copy_failed")
        helpers.verify(path, steam["sha256"])
    finally:
        command(docker + ["rm", "-fv", container], "steam_client_source_cleanup_failed")

    # The build context contains only the verified public compatibility package.
    with tempfile.TemporaryDirectory(prefix="recovery-image-inputs-") as temporary:
        context = Path(temporary).resolve()
        helpers.download(LIBSTDCPP, context)
        iidfile = destination / "engine-image-id"
        subprocess.run(docker + ["build", "--platform=linux/amd64", "--network", "none", "--no-cache",
                                 "--provenance=false", "--iidfile", str(iidfile),
                                 "-f", str(HERE / "Dockerfile"), str(context)],
                       stdout=sys.stderr, timeout=600, check=True)
    image_id = iidfile.read_text(encoding="ascii").strip()
    require(re.fullmatch(r"sha256:[a-f0-9]{64}", image_id), "built_image_id_invalid")
    listing = json.loads(command(docker + ["image", "inspect", image_id], "built_image_unavailable"))[0]
    require(listing["Id"] == image_id and listing["Architecture"] == "amd64"
            and listing["Os"] == "linux", "built_image_identity_mismatch")
    return {"imageId": image_id, "stableZip": RELEASE["name"], "assets": "assets",
            "steamClient": "steamclient.so", "resticImage": RESTIC_IMAGE}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="A directory that does not exist yet.")
    parser.add_argument("--docker-context", required=True)
    print(json.dumps(prepare(parser.parse_args()), indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
