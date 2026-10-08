"""Bounded policy and private state shared by the weekly player backup."""
import contextlib
import datetime as dt
import fcntl
import json
import os
from pathlib import Path
import re
import socket
import stat
import subprocess
from zoneinfo import ZoneInfo

MOSCOW = ZoneInfo("Europe/Moscow")
UTC = dt.timezone.utc
MAX_BUNDLE = 65 * 1024 * 1024
MAX_CYCLES = 52


def require(condition, message="Player backup guard failed."):
    if not condition:
        raise ValueError(message)


def now():
    return dt.datetime.now(UTC)


def weekly_slot(moment):
    require(moment.tzinfo is not None)
    local = moment.astimezone(MOSCOW)
    if local.weekday() == 6 and local.hour == 4 and local.minute < 5:
        return local.date().isoformat()
    return None


def valid_slot(value):
    require(re.fullmatch(r"\d{4}-\d{2}-\d{2}", value) is not None)
    require(dt.date.fromisoformat(value).weekday() == 6)
    return value


def private_path(path, directory=False):
    path = Path(path)
    require(path.is_absolute())
    require(not any(p.is_symlink() for p in (path, *path.parents)))
    info = path.stat()
    require(info.st_uid == os.geteuid() and info.st_mode & 0o077 == 0)
    require(stat.S_ISDIR(info.st_mode) if directory else
            stat.S_ISREG(info.st_mode) and info.st_nlink == 1)
    return path


def read_json(path):
    path = private_path(path)
    require(path.stat().st_size <= 16384)
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, data, replace=False):
    path = Path(path)
    private_path(path.parent, directory=True)
    temporary = path.with_name(path.name + ".new") if replace else path
    with temporary.open("x", encoding="utf-8") as output:
        os.chmod(temporary, 0o600)
        json.dump(data, output, separators=(",", ":"))
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    if replace:
        if path.exists() or path.is_symlink():
            private_path(path)
        os.replace(temporary, path)
    descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


@contextlib.contextmanager
def lock(path, shared=False):
    parent = Path(path).parent
    if shared:
        info = parent.stat()
        require(not any(p.is_symlink() for p in (parent, *parent.parents)))
        require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.geteuid() and info.st_mode & 0o022 == 0)
    else:
        private_path(parent, directory=True)
    descriptor = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        if shared:
            info = os.fstat(descriptor)
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
                    and info.st_uid == os.geteuid() and info.st_mode & 0o027 == 0)
        else:
            private_path(path)
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


class Ledger:
    def __init__(self, root):
        self.root = private_path(root, directory=True)

    @property
    def pending(self):
        return self.root / "pending.json"

    def claim(self, slot):
        valid_slot(slot)
        # Any interrupted side effect blocks future weeks until reconciliation.
        require(not self.pending.exists() and not self.pending.is_symlink(),
                "An earlier attempt requires reconciliation.")
        cycle = self.root / slot
        if cycle.exists() or cycle.is_symlink():
            existing = read_json(cycle / "result.json")
            require(existing["slot"] == slot and existing["state"] in
                    ("skipped-busy", "skipped-window", "captured", "verified"))
            return cycle, existing
        require(len(list(self.root.glob("????-??-??"))) < MAX_CYCLES,
                "Review private evidence capacity before another cycle.")
        require(__import__("shutil").disk_usage(self.root).free >= 4 * MAX_BUNDLE)
        cycle.mkdir(mode=0o700)
        record = {"schema": 1, "slot": slot, "state": "started",
                  "startedUtc": now().isoformat()}
        write_json(cycle / "result.json", record)
        write_json(self.pending, record)
        return cycle, None

    def finish(self, cycle, record):
        write_json(cycle / "result.json", record, replace=True)
        private_path(self.pending).unlink()


def run(arguments, timeout=30, maximum=16384):
    # Only fixed local programs and the restricted SSH dispatcher are called.
    result = subprocess.run(arguments, capture_output=True, timeout=timeout, check=False)
    require(result.returncode == 0, "A bounded backup command failed.")
    require(len(result.stdout) <= maximum, "Backup command output exceeds its bound.")
    return result.stdout


def parse_info(packet):
    require(len(packet) >= 6 and packet[:4] == b"\xff\xff\xff\xff" and len(packet) <= 4096)
    kind, offset = packet[4], 5

    def string():
        nonlocal offset
        end = packet.find(b"\0", offset)
        require(end >= offset and end - offset <= 255)
        value = packet[offset:end].decode("latin1")
        offset = end + 1
        return value

    if kind == 0x6D:
        string()
    else:
        require(kind == 0x49)
        offset += 1
    name, map_name, folder, description = (string() for _ in range(4))
    if kind == 0x49:
        offset += 2
        require(len(packet) >= offset + 3)
        players, maximum, bots = packet[offset:offset + 3]
    else:
        require(len(packet) >= offset + 7)
        players, maximum = packet[offset:offset + 2]
        mod = packet[offset + 6]
        require(mod in (0, 1))
        offset += 7
        if mod:
            string()
            string()
            offset += 11
        require(len(packet) >= offset + 2)
        bots = packet[offset + 1]
    require(players <= maximum and bots <= players)
    return name, folder, players, bots


def probe(config):
    request = b"\xff\xff\xff\xffTSource Engine Query\0"
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as connection:
        connection.settimeout(3)
        connection.connect((config["host"], config["port"]))
        connection.send(request)
        packet = connection.recv(4097)
        if packet[:5] == b"\xff\xff\xff\xffA":
            require(len(packet) == 9)
            connection.send(request + packet[5:])
            packet = connection.recv(4097)
    name, folder, players, bots = parse_info(packet)
    require(name == config["expectedName"] and folder == "cstrike")
    return players == 0 and bots == 0


def freshness(root, moment=None):
    root = private_path(root, directory=True)
    require(not (root / "pending.json").exists() and not (root / "pending.json").is_symlink(), "Backup attempt requires reconciliation.")
    record = read_json(root / "last-success.json")
    require(record["schema"] == 1 and record["state"] == "verified")
    require(record["workload"] == "goldsrcops-player-data-v1")
    captured = dt.datetime.fromisoformat(record["capturedUtc"])
    require(captured.tzinfo is not None)
    age = ((moment or now()) - captured).total_seconds()
    require(-300 <= age <= 8 * 86400, "Player backup is older than eight days.")
    require(re.fullmatch(r"[a-f0-9]{64}", record["bundleSha256"]) is not None)
    require(re.fullmatch(r"[a-f0-9]{64}", record["snapshotId"]) is not None)
    return {"state": "fresh", "ageHours": round(max(age, 0) / 3600, 2)}
