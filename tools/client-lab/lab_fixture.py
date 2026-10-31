"""Explicit one-attempt fixture lifecycle; no provisioning, downloads or client dispatch."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

IMAGE = "sha256:9bffd1d5a9dbecceaee569911ffba6bd5e28326be7879dd7cbeed90b6d7f4574"


def read_json(path):
    if path.is_symlink() or path.stat().st_size > 65536:
        raise ValueError("Invalid local metadata")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def read_plan(root):
    root = root.resolve(strict=True)
    plan = read_json(root / "plan.json")
    if set(plan) != {"schema", "id", "language", "container", "network", "image", "assets"}:
        raise ValueError("Unexpected plan shape")
    if plan["schema"] != 1 or plan["language"] not in ("ru", "en") or not re.fullmatch(r"[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}", plan["id"]):
        raise ValueError("Unexpected run identity")
    expected = "gso-client-lab-" + plan["id"].replace("-", "")
    if plan["container"] != expected or plan["network"] != expected + "-net" or plan["image"] != IMAGE:
        raise ValueError("Unexpected fixture identity")
    if set(plan["assets"]) != {"cacheDirectory", "runtimeDirectory", "steamSdkPath"}:
        raise ValueError("Unexpected assets")
    for value in plan["assets"].values():
        if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z]:\\[A-Za-z0-9 _().\\-]+", value) or ".." in value.split("\\"):
            raise ValueError("Unsafe local asset path")
    return plan


def docker(*args, timeout=40):
    return subprocess.run(["docker", *args], capture_output=True, text=True, encoding="utf-8", timeout=timeout, check=True).stdout.strip()


def write_new(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)


def start(root, plan):
    write_new(root / "start-claimed.json", {"oneAttempt": True, "utc": dt.datetime.now(dt.timezone.utc).isoformat()})
    if docker("image", "inspect", IMAGE, "--format", "{{.Id}}") != IMAGE:
        raise ValueError("Pinned local image missing")
    if docker("ps", "-aq", "--filter", "name=^/" + plan["container"] + "$"):
        raise ValueError("Fixture identity already exists")
    mounts = [(root, "/runner", True), (root / "inputs", "/inputs", True), (root / "evidence", "/evidence", False),
              (Path(plan["assets"]["cacheDirectory"]), "/assets", True), (Path(plan["assets"]["runtimeDirectory"]), "/runtime", True),
              (Path(plan["assets"]["steamSdkPath"]), "/sdk/steamclient.so", True)]
    for source, _, _ in mounts:
        if not source.exists() or source.is_symlink() or "," in str(source):
            raise ValueError("Missing or linked local input")
    network = docker("network", "create", "--internal", "--label", "purpose=client-lab", plan["network"])
    write_new(root / "network.json", {"id": network, "name": plan["network"]})
    args = ["run", "-d", "--name", plan["container"], "--network", plan["network"], "--cpus", "2", "--memory", "1536m", "--pids-limit", "128",
            "--security-opt", "no-new-privileges:true", "--restart", "no", "--no-healthcheck", "--label", "purpose=client-lab"]
    for source, target, readonly in mounts:
        args += ["--mount", "type=bind,source=" + str(source) + ",target=" + target + (",readonly" if readonly else "")]
    args += ["--entrypoint", "python3", IMAGE, "-B", "/runner/run_fixture.py"]
    container = docker(*args)
    write_new(root / "container.json", {"id": container, "name": plan["container"], "image": IMAGE, "network": plan["network"]})
    return {"status": "started", "container": container, "publishedPorts": False, "clientDispatched": False}


def relay(root, _plan):
    if os.name != "nt":
        raise ValueError("LAN relay controller requires Windows")
    if read_json(root / "evidence/runtime.json")["status"] != "ready":
        raise ValueError("Fixture is not ready")
    write_new(root / "relay-start-claimed.json", {"oneAttempt": True})
    write_new(root / "relay-lease.json", {"expiresUtc": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(minutes=25)).isoformat()})
    with (root / "relay-private.log").open("xb") as log:
        process = subprocess.Popen([sys.executable, "-B", str(root / "udp_relay.py"), "--lan"], cwd=root, stdout=log, stderr=log,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
    write_new(root / "relay-process.json", {"pid": process.pid})
    return {"status": "relay_start_requested", "pid": process.pid, "clientDispatched": False}


def stop(root, plan):
    (root / "stop-lan").touch(exist_ok=True)
    evidence_failure = "fixture_not_running"
    receipt_path = root / "container.json"
    if receipt_path.exists():
        receipt = read_json(receipt_path)
        if not re.fullmatch(r"[a-f0-9]{64}", receipt["id"]):
            raise ValueError("Invalid retained container identity")
        inspected = json.loads(docker("inspect", receipt["id"]))[0]
        if inspected["Name"] != "/" + plan["container"] or inspected["Image"] != IMAGE or inspected["Config"]["Labels"].get("purpose") != "client-lab":
            raise ValueError("Container identity changed")
        if inspected["State"]["Running"]:
            try:
                script = "from pathlib import Path; p=Path('/tmp/private/server-private.log'); b=p.read_bytes() if p.exists() else b''; print(b[-65536:].decode('utf-8','replace'),end='')"
                (root / "evidence/server-final.log").write_text(docker("exec", "--user", "1000", receipt["id"], "python3", "-c", script), encoding="utf-8")
                errors = "import json; from pathlib import Path; p=Path('/tmp/fixture/cstrike/addons/amxmodx/logs'); print(json.dumps({'errorFiles': len(list(p.glob('error_*')))}))"
                write_new(root / "evidence/amxx-errors.json", json.loads(docker("exec", "--user", "1000", receipt["id"], "python3", "-c", errors)))
                evidence_failure = None
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                evidence_failure = type(error).__name__
            finally:
                # Evidence loss must not leave an identified owned fixture running.
                docker("stop", "-t", "15", receipt["id"])
        if json.loads(docker("inspect", receipt["id"]))[0]["State"]["Running"]:
            raise ValueError("Container did not stop")
        docker("rm", receipt["id"])
    network_path = root / "network.json"
    if network_path.exists():
        network = read_json(network_path)
        if not re.fullmatch(r"[a-f0-9]{64}", network["id"]):
            raise ValueError("Invalid retained network identity")
        inspected = json.loads(docker("network", "inspect", network["id"]))[0]
        if inspected["Name"] != plan["network"] or inspected["Labels"].get("purpose") != "client-lab" or inspected["Containers"]:
            raise ValueError("Network identity or occupancy changed")
        docker("network", "rm", network["id"])
    if (root / "relay-start-claimed.json").exists():
        for _ in range(30):
            status = root / "relay-lan.json"
            if status.exists() and read_json(status)["status"] in ("stopped", "expired", "failed", "tunnel_exited"):
                break
            time.sleep(0.2)
        else:
            raise ValueError("Relay stop is not confirmed")
    result = {"fixtureRemoved": True, "gameCleanupRequiresIndependentResult": True, "evidenceRetained": True,
              "fixtureEvidenceComplete": evidence_failure is None, "evidenceFailure": evidence_failure}
    write_new(root / "cleanup.json", result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("plan", "start", "relay", "stop"))
    parser.add_argument("--run-directory", type=Path, required=True)
    parser.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    root = args.run_directory.resolve(strict=True)
    plan = read_plan(root)
    if args.operation != "plan" and not args.execute:
        raise ValueError("Explicit --execute is required for local runtime effects")
    result = plan if args.operation == "plan" else {"start": start, "relay": relay, "stop": stop}[args.operation](root, plan)
    print(json.dumps(result))


if __name__ == "__main__":
    main()
