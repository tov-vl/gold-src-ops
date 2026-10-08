#!/usr/bin/env python3
"""Bounded player-data transport. Runtime quiescence belongs to the capture guard."""

import argparse
import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import sys
import tarfile


STEMS = ("goldsrcops-player-stats-v1", "goldsrcops-player-preferences-v1")
NAMES = tuple(stem + ext for stem in STEMS for ext in (".vault", ".journal"))
MAX_FILE = 16 * 1024 * 1024
MAX_ARCHIVE = 4 * MAX_FILE + 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_path(path):
    path = Path(os.path.abspath(path))
    for part in (path, *path.parents):
        require(not part.is_symlink(), "Symbolic links are forbidden.")
    return path


def read_regular(path, maximum):
    path = safe_path(path)
    require(stat.S_ISREG(path.stat().st_mode), "A regular file is required.")
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
                         | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_BINARY", 0))
    with os.fdopen(descriptor, "rb") as stream:
        before = os.fstat(stream.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1,
                "A single regular file is required.")
        require(before.st_size <= maximum, "Input exceeds the size limit.")
        data = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
    # Windows path stat and handle stat can expose different creation times on
    # Docker-restored files. Unix ctime remains part of the mutation guard.
    identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns,
                          s.st_ctime_ns if os.name != "nt" else 0)
    require(len(data) <= maximum and identity(before) == identity(after)
            and identity(path.stat()) == identity(after),
            "Input changed during the read.")
    return data


def inventory(directory):
    directory = safe_path(directory)
    require(directory.is_dir(), "Vault directory is missing.")
    # Unrelated plugin vaults are deliberately not copied.
    files = {}
    for name in NAMES:
        path = directory / name
        if path.exists() or path.is_symlink():
            files[name] = read_regular(path, MAX_FILE)
        else:
            require(name.endswith(".journal"), "Both player vaults are required.")
            files[name] = None
    return files


def create(directory):
    files = inventory(directory)
    require(files == inventory(directory), "Vault set changed during capture.")
    manifest = {
        "schema": 1,
        "kind": "goldsrcops-player-data",
        "captured_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "files": [{"name": name, "present": data is not None,
                   "bytes": len(data) if data is not None else 0,
                   "sha256": digest(data) if data is not None else None}
                  for name, data in files.items()],
    }
    members = {"manifest.json": json.dumps(manifest, separators=(",", ":")).encode()}
    members.update({name: data for name, data in files.items() if data is not None})
    result = io.BytesIO()
    with tarfile.open(fileobj=result, mode="w", format=tarfile.USTAR_FORMAT) as archive:
        for name, data in members.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            info.mode = 0o600
            archive.addfile(info, io.BytesIO(data))
    require(files == inventory(directory), "Vault set changed before publication.")
    return result.getvalue()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate manifest key.")
        result[key] = value
    return result


def inspect(data):
    require(len(data) <= MAX_ARCHIVE, "Archive exceeds the size limit.")
    members = {}
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
        for member in archive:
            require(member.name in (*NAMES, "manifest.json") and member.name not in members,
                    "Unexpected or duplicate archive member.")
            require(member.type in (tarfile.REGTYPE, tarfile.AREGTYPE)
                    and not member.pax_headers and 0 <= member.size <= MAX_FILE,
                    "Only bounded regular archive members are accepted.")
            require(member.mode == 0o600 and member.uid == 0 and member.gid == 0,
                    "Unexpected archive metadata.")
            members[member.name] = archive.extractfile(member).read(MAX_FILE + 1)
            require(len(members[member.name]) == member.size, "Truncated archive member.")
    require("manifest.json" in members and len(members["manifest.json"]) <= 8192,
            "Manifest is missing or too large.")
    manifest = json.loads(members.pop("manifest.json"), object_pairs_hook=unique_object)
    require(set(manifest) == {"schema", "kind", "captured_utc", "files"}
            and type(manifest["schema"]) is int and manifest["schema"] == 1
            and manifest["kind"] == "goldsrcops-player-data", "Unknown manifest contract.")
    date = datetime.datetime.fromisoformat(manifest["captured_utc"])
    require(date.utcoffset() == datetime.timedelta(0), "Capture time must be UTC.")
    require(type(manifest["files"]) is list and len(manifest["files"]) == len(NAMES),
            "Unexpected inventory.")
    expected = set()
    for name, entry in zip(NAMES, manifest["files"]):
        require(set(entry) == {"name", "present", "bytes", "sha256"}
                and entry["name"] == name and type(entry["present"]) is bool
                and type(entry["bytes"]) is int, "Invalid file manifest.")
        if entry["present"]:
            expected.add(name)
            require(name in members and len(members[name]) == entry["bytes"]
                    and digest(members[name]) == entry["sha256"], "Payload integrity failed.")
        else:
            require(name.endswith(".journal") and entry["bytes"] == 0
                    and entry["sha256"] is None, "Invalid absent file.")
    require(set(members) == expected, "Archive and manifest inventory differ.")
    return manifest, members


def write_new(path, data):
    path = safe_path(path)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL
                         | getattr(os, "O_BINARY", 0), 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


def restore(data, target):
    manifest, files = inspect(data)
    target = safe_path(target)
    # Never merge into or replace an existing directory, including an empty one.
    target.mkdir(mode=0o700)
    for name, content in files.items():
        write_new(target / name, content)
    require(inventory(target) == {name: files.get(name) for name in NAMES},
            "Restored bytes differ.")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="action", required=True)
    capture = commands.add_parser("create")
    capture.add_argument("--vault-directory", required=True)
    capture.add_argument("--output", required=True)
    for action in ("inspect", "restore"):
        command = commands.add_parser(action)
        command.add_argument("--bundle", required=True)
        command.add_argument("--expected-sha256", required=True)
        if action == "restore":
            command.add_argument("--target", required=True)
    args = parser.parse_args()
    if args.action == "create":
        data = create(args.vault_directory)
        inspect(data)
        write_new(args.output, data)
        manifest, _ = inspect(read_regular(args.output, MAX_ARCHIVE))
    else:
        data = read_regular(args.bundle, MAX_ARCHIVE)
        require(digest(data) == args.expected_sha256, "Archive SHA-256 differs from the expected value.")
        manifest, _ = inspect(data)
        if args.action == "restore":
            manifest = restore(data, args.target)
    print(json.dumps({"result": "passed", "action": args.action, "sha256": digest(data),
                      "captured_utc": manifest["captured_utc"],
                      "files": sum(x["present"] for x in manifest["files"])}, separators=(",", ":")))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, tarfile.TarError, KeyError, TypeError):
        # Inputs can contain private identities or host paths. Do not echo exceptions.
        print("PLAYER_DATA_BUNDLE=failed; inspect private inputs and retain partial output", file=sys.stderr)
        sys.exit(1)
