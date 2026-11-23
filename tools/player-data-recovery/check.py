#!/usr/bin/env python3
"""Verify a previously recovered player-data TAR with the stable 0.40.0 engine pair."""

import argparse
import importlib.util
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tarfile
import time
import uuid
import zipfile


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
BUNDLE_TOOL = ROOT / "ops/gameserver/player-data-bundle.py"
PAIR_REVISION = "353f67ab887e0184974ee7ae18d849c45da80350"
PAIR_SHA256 = "cf3515f7990b509de2210b59e208b4d53df80a20235dd6a9bf00d9a562e3d59a"
PAIR_FILES = (
    "plugins/goldsrcops_weapon_selection.amxx",
    "modules/nvault_amxx_i386.so",
    "data/lang/goldsrcops-player-menu.txt",
    "configs/plugins/goldsrcops-weapon-selection.cfg",
)
RUN_TIMEOUT = 180
TMPFS = {
    "/server": "rw,exec,nosuid,size=1g",
    "/private": "rw,noexec,nosuid,size=96m",
    "/tmp": "rw,noexec,nosuid,size=32m",
}
MEMORY_BYTES = 2 * 1024**3
ROW_MARKER = re.compile(
    r"RECOVERY_EPOCH=(\d+) (passed|failed) rows=(\d+) stats=(\d+) preferences=(\d+)"
    r" hud=(\d+) streak=(\d+) names=(\d+) writes=(\d+) failures=(\d+)"
)
COUNTS = ("rows", "stats", "preferences", "hud", "streak", "names", "writes", "failures")
INPUT_REJECTIONS = {
    "RECOVERY_INPUT=rejected": "closed_vault_rejected",
    "RECOVERY_INPUT=nonempty_journal": "nonempty_journal",
    "RECOVERY_INPUT=vault_record_limit_exceeded": "vault_record_limit_exceeded",
}


def load_bundle_tool():
    spec = importlib.util.spec_from_file_location("player_data_bundle", BUNDLE_TOOL)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


bundle = load_bundle_tool()


class CheckError(Exception):
    """A fixed error code safe to put in the operator report."""


def require(condition, code):
    if not condition:
        raise CheckError(code)


def command(args, failure, *, timeout=35):
    result = subprocess.run(args, capture_output=True, timeout=timeout, check=False)
    require(result.returncode == 0, failure)
    return result.stdout


def stage_inputs(args, kit):
    """Stage public release and runtime inputs only, after verifying their hashes."""
    release = bundle.read_regular(args.stable_zip, 32 * 1024**2)
    require(bundle.digest(release) == PAIR_SHA256, "stable_zip_hash_mismatch")
    with zipfile.ZipFile(io.BytesIO(release)) as package:
        manifest = json.loads(package.read("pair/manifest.json"))
        require(manifest["addonVersion"] == "0.40.0", "stable_pair_version_mismatch")
        payload = {entry["path"]: entry["sha256"] for entry in manifest["payload"]}
        for path in PAIR_FILES:
            relative = "cstrike/addons/amxmodx/" + path
            data = package.read("pair/" + relative)
            require(bundle.digest(data) == payload[relative], "stable_pair_payload_mismatch")
            (kit / Path(path).name).write_bytes(data)

    for name in ("goldsrcops_weapon_selection.sma", "goldsrcops-process-storage.inc"):
        revision_path = PAIR_REVISION + ":samples/amxmodx-weapon-selection/" + name
        data = command(["git", "-C", str(ROOT), "cat-file", "blob", revision_path],
                       "stable_source_unavailable")
        (kit / name).write_bytes(data)

    lock = json.loads((ROOT / "tools/smoke/engine-ci-lock.json").read_bytes())
    for asset in lock["assets"]:
        archive = args.assets / asset["name"]
        data = bundle.read_regular(archive, 64 * 1024**2)
        require(bundle.digest(data) == asset["sha256"], "runtime_asset_hash_mismatch")
        destination = kit / asset["directory"]
        destination.mkdir()
        if asset["name"].endswith(".zip"):
            with zipfile.ZipFile(io.BytesIO(data)) as source:
                require(all((destination / name).resolve().is_relative_to(destination)
                            for name in source.namelist()), "runtime_asset_path_rejected")
                source.extractall(destination)
        else:
            with tarfile.open(fileobj=io.BytesIO(data)) as source:
                source.extractall(destination, filter="data")
    steam = bundle.read_regular(args.steam_client, 64 * 1024**2)
    require(bundle.digest(steam) == lock["images"]["steam"]["sha256"], "steam_client_hash_mismatch")
    (kit / "steamclient.so").write_bytes(steam)
    shutil.copyfile(BUNDLE_TOOL, kit / "bundle.py")
    for name in ("recovery.sma", "load.py", "run.sh"):
        shutil.copyfile(HERE / name, kit / name)


def docker_command(context):
    endpoint = json.loads(command(["docker", "context", "inspect", context], "docker_context_unavailable"))
    host = endpoint[0]["Endpoints"]["docker"]["Host"]
    require(re.fullmatch(r"unix:///(?!/)[^?#]+|npipe:////\./pipe/[\w.-]+", host),
            "docker_context_not_local")
    return ["docker", "--context", context]


def run_engine(args, kit, raw, digest, result, *, mismatch=False):
    docker = docker_command(args.docker_context)
    image = json.loads(command(docker + ["image", "inspect", args.engine_image_id],
                               "engine_image_unavailable"))[0]
    require(image["Id"] == args.engine_image_id and image["Os"] == "linux"
            and image["Architecture"] == "amd64" and not image["Config"].get("Volumes"),
            "engine_image_rejected")

    # The IID is immutable, the bind mount contains only verified public inputs,
    # and the TAR is sent through attached stdin after inspecting the container.
    name = "player-recovery-" + uuid.uuid4().hex
    create = ["create", "--name", name, "--pull", "never", "--interactive",
              "--network", "none", "--no-healthcheck", "--log-driver", "none",
              "--user", "0:0", "--read-only", "--cpus", "2", "--memory", str(MEMORY_BYTES),
              "--pids-limit", "256", "--security-opt", "no-new-privileges", "--ulimit", "core=0"]
    for path, options in TMPFS.items():
        create += ["--tmpfs", f"{path}:{options}"]
    create += ["--mount", f"type=bind,source={kit},target=/kit,readonly",
               "--entrypoint", "/usr/bin/timeout", args.engine_image_id,
               "--signal=TERM", "--kill-after=5", str(RUN_TIMEOUT), "bash", "/kit/run.sh",
               digest, "mismatch" if mismatch else "verify"]
    list_container = docker + ["container", "ls", "-aq", "--no-trunc", "--filter", f"name=^/{name}$"]
    try:
        created = command(docker + create, "container_create_failed")
    except subprocess.TimeoutExpired:
        if command(list_container, "container_create_unreconciled").strip():
            command(docker + ["rm", "-fv", name], "container_cleanup_failed")
            require(not command(list_container, "container_cleanup_unverified").strip(),
                    "container_cleanup_unverified")
            result["containerRemoved"] = True
        raise

    try:
        cid = created.decode("ascii").strip()
        require(re.fullmatch(r"[a-f0-9]{64}", cid), "container_identity_rejected")
        inspected = json.loads(command(docker + ["inspect", cid], "container_inspect_failed"))[0]
        host = inspected["HostConfig"]
        mounts = inspected["Mounts"]
        require(inspected["Id"] == cid and inspected["Image"] == args.engine_image_id
                and inspected["Name"] == "/" + name and host["NetworkMode"] == "none"
                and not host["PortBindings"] and not host["PublishAllPorts"] and not host["Privileged"]
                and not host["Devices"] and host["ReadonlyRootfs"] and host["Tmpfs"] == TMPFS
                and host["Memory"] == MEMORY_BYTES and host["NanoCpus"] == 2_000_000_000
                and host["PidsLimit"] == 256 and host["LogConfig"]["Type"] == "none"
                and host["SecurityOpt"] == ["no-new-privileges"]
                and set((m["Type"], m["Destination"], m["RW"]) for m in mounts)
                == {("bind", "/kit", False)}, "container_isolation_rejected")
        result["isolationVerified"] = True
        result["containerStarted"] = True
        attached = subprocess.run(docker + ["start", "-ai", cid], input=raw, capture_output=True,
                                  timeout=RUN_TIMEOUT + 25, check=False)
        lines = attached.stdout.decode("utf-8", errors="replace").splitlines()
        state = json.loads(command(docker + ["inspect", cid], "container_inspect_failed"))[0]["State"]
        require(not state["Running"] and not state["OOMKilled"], "container_did_not_complete")
        require(state["ExitCode"] != 124, "engine_timed_out")
        result["stockPairOpened"] = lines.count("RECOVERY_STABLE_PAIR=passed") == 1
        for line in lines:
            if line in INPUT_REJECTIONS:
                raise CheckError(INPUT_REJECTIONS[line])
            phase = re.fullmatch(r"RECOVERY_PHASE_FAILED=(input|setup|compile|stock|engine)", line)
            if phase:
                result["failureStage"] = phase[1]
            marker = ROW_MARKER.fullmatch(line)
            if marker:
                epoch, outcome, *counts = marker.groups()
                result["epochs"].append({"epoch": int(epoch), "result": outcome,
                                         **dict(zip(COUNTS, map(int, counts), strict=True))})
        if "RECOVERY_CHECK=mismatch" in lines and any(e["result"] == "failed" for e in result["epochs"]):
            raise CheckError("engine_mismatch")
        require(attached.returncode == 0 and state["ExitCode"] == 0 and result["stockPairOpened"]
                and lines.count("RECOVERY_CHECK=passed") == 1, "engine_check_failed")
        rows = result["epochs"]
        require([e["epoch"] for e in rows] == [1, 2]
                and all(e["result"] == "passed" and e["writes"] == e["failures"] == 0
                        and e["rows"] == sum(e[k] for k in COUNTS[1:6]) for e in rows)
                and all(rows[0][key] == rows[1][key] for key in COUNTS), "engine_evidence_rejected")
    finally:
        command(docker + ["rm", "-fv", name], "container_cleanup_failed")
        require(not command(list_container, "container_cleanup_unverified").strip(),
                "container_cleanup_unverified")
        result["containerRemoved"] = True


def check(args, *, mismatch=False):
    started = time.monotonic()
    result = {"schema": 1, "result": "failed", "addonVersion": "0.40.0",
              "inputHashVerified": False, "vaultHashesVerified": False,
              "isolationVerified": False, "containerStarted": False, "containerRemoved": False,
              "stockPairOpened": False, "epochs": []}
    try:
        require(re.fullmatch(r"[a-f0-9]{64}", args.expected_sha256), "expected_sha256_invalid")
        raw = bundle.read_regular(args.bundle, bundle.MAX_ARCHIVE)
        require(bundle.digest(raw) == args.expected_sha256, "input_hash_mismatch")
        result["inputHashVerified"] = True
        bundle.inspect(raw)
        result["vaultHashesVerified"] = True
        require(re.fullmatch(r"sha256:[a-f0-9]{64}", args.engine_image_id), "engine_image_id_invalid")
        with tempfile.TemporaryDirectory(prefix="player-recovery-") as directory:
            kit = Path(directory).resolve()
            stage_inputs(args, kit)
            run_engine(args, kit, raw, args.expected_sha256, result, mismatch=mismatch)
        result["result"] = "passed"
    except CheckError as error:
        result["reason"] = str(error)
    except subprocess.TimeoutExpired:
        result["reason"] = "operation_timed_out"
    except (OSError, ValueError, KeyError, TypeError, IndexError, tarfile.TarError, zipfile.BadZipFile):
        result["reason"] = "input_or_runtime_contract_failed"
    result["elapsedSeconds"] = round(time.monotonic() - started, 3)
    return result


def add_runtime_arguments(parser):
    parser.add_argument("--stable-zip", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--steam-client", type=Path, required=True)
    parser.add_argument("--engine-image-id", required=True)
    parser.add_argument("--docker-context", required=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--expected-sha256", required=True)
    add_runtime_arguments(parser)
    result = check(parser.parse_args())
    print(json.dumps(result, indent=2))
    return 0 if result["result"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
