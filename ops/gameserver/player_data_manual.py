"""Read-only validation of retained manual backup evidence; no remote operations."""
import datetime as dt
import hashlib
import os
from pathlib import Path
import re
import stat

MAX_COPIES = 52


def require(condition):
    if not condition:
        raise ValueError("Invalid manual player backup evidence.")


def bounded_hash(path):
    with os.fdopen(os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK), 'rb') as stream:
        info = os.fstat(stream.fileno())
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                and info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0)
        raw = stream.read(16385)
    require(len(raw) <= 16384)
    return hashlib.sha256(raw).hexdigest()


def read_success(root, moment, reader, private):
    """Read bounded, retained evidence; never replace or reinterpret a weekly slot."""
    registry = Path(root) / "manual-copies"
    if not registry.exists() and not registry.is_symlink():
        return None
    private(registry, directory=True)
    latest = None
    with os.scandir(registry) as entries:
        for index, entry in enumerate(entries):
            require(index < MAX_COPIES and re.fullmatch(r"[a-f0-9]{64}", entry.name) is not None)
            directory = private(Path(entry.path), directory=True)
            names = []
            with os.scandir(directory) as files:
                for number, child in enumerate(files):
                    require(number < 4)
                    names.append(child.name)
            require(set(names) == {
                "success.json", "archive.json", "recovery.json", "manifest.json"})
            record = reader(directory / "success.json")
            require(record["schema"] == 1 and record["state"] == "verified"
                    and record["source"] == "reviewed-manual-copy"
                    and record["workload"] == "goldsrcops-player-data-v1"
                    and record["bundleSha256"] == entry.name)
            require(re.fullmatch(r"[a-f0-9]{64}", record["snapshotId"]) is not None)
            captured = dt.datetime.fromisoformat(record["capturedUtc"])
            registered = dt.datetime.fromisoformat(record["registeredUtc"])
            require(captured.tzinfo is not None and registered.tzinfo is not None
                    and 0 < captured.timestamp() <= registered.timestamp() <= moment.timestamp() + 300)
            operations = []
            for name, action in (("archive", "Archive"), ("recovery", "Recover")):
                path = directory / (name + ".json")
                operation = reader(path)
                require(bounded_hash(path) == record[name + "Sha256"])
                require(operation["schemaVersion"] == 1 and operation["state"] == "verified"
                        and operation["action"] == action
                        and all(operation[key] == record[key] for key in ("snapshotId", "bundleSha256")))
                started = dt.datetime.fromisoformat(operation["startedUtc"])
                completed = dt.datetime.fromisoformat(operation["completedUtc"])
                require(started.tzinfo is not None and completed.tzinfo is not None
                        and captured <= started <= completed <= registered)
                operations.append((started, completed))
            require(operations[0][1] <= operations[1][0])
            manifest_path = directory / "manifest.json"
            manifest = reader(manifest_path)
            require(bounded_hash(manifest_path) == record["manifestSha256"]
                    and manifest["schema"] == 1 and manifest["kind"] == "goldsrcops-player-data"
                    and manifest["captured_utc"] == record["capturedUtc"])
            if latest is None or captured > dt.datetime.fromisoformat(latest["capturedUtc"]):
                latest = record
    return latest
