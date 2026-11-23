"""Read the bounded closed-vault format and prepare an in-memory engine check."""

import sys
from pathlib import Path
import struct

import bundle


PRIVATE = Path("/private")
ARCHIVE = PRIVATE / "copy.tar"
EXPECTED = PRIVATE / "expectations.tsv"
TARGET = Path("/server/cstrike/addons/amxmodx/data/vault")
MAX_ROWS = 4096


class VaultRejected(ValueError):
    """Closed-input rejection safe to report without record contents."""


def records(data):
    bundle.require(len(data) >= 10, "Missing nVault header.")
    magic, version, count = struct.unpack_from("<IHI", data)
    bundle.require(magic == 0x6E564C54 and version == 0x0200, "Unsupported nVault header.")
    if count > MAX_ROWS:
        raise VaultRejected("vault_record_limit_exceeded")
    offset, keys = 10, set()
    for _ in range(count):
        bundle.require(offset + 7 <= len(data), "Missing nVault record.")
        stamp, key_length, value_length = struct.unpack_from("<IBH", data, offset)
        offset += 7
        end = offset + key_length + value_length
        bundle.require(0 < key_length <= 63 and 0 < value_length <= 127 and end <= len(data),
                       "Unbounded or truncated nVault record.")
        key = data[offset:offset + key_length]
        value = data[offset + key_length:end]
        bundle.require(key not in keys and b"\0" not in key + value, "Invalid nVault record.")
        keys.add(key)
        offset = end
        yield stamp, key, value
    bundle.require(offset == len(data), "Trailing nVault bytes.")


def expectations(raw, *, mismatch):
    _, files = bundle.inspect(raw)
    if any(data for name, data in files.items() if name.endswith(".journal")):
        raise VaultRejected("nonempty_journal")
    rows = []
    for index, stem in enumerate(bundle.STEMS):
        for stamp, key, value in records(files[stem + ".vault"]):
            rank = 0
            if index == 0:
                parts = value.decode("ascii").split(" ")
                bundle.require(len(parts) == 3 and parts[0] == "1" and parts[1].isdigit(),
                               "Invalid stats record framing.")
                kills = int(parts[1])
                rank = sum(kills >= threshold for threshold in (25, 100, 250, 500))
            if mismatch and not rows:
                # Change one byte without changing the width of the test record.
                value = (b"!" if value[:1] != b"!" else b"?") + value[1:]
            # Pawn stores native nVault timestamps in signed, 32-bit cells.
            if stamp >= 2**31:
                stamp -= 2**32
            rows.append(f"{index} {stamp} {key.hex()} {value.hex()} {rank}\n")
            if len(rows) > MAX_ROWS:
                raise VaultRejected("vault_record_limit_exceeded")
    return (str(len(rows)) + "\n" + "".join(rows)).encode("ascii")


def main():
    action, digest, mode = sys.argv[1:]
    bundle.require(action in {"receive", "restore", "compare"} and mode in {"verify", "mismatch"},
                   "Unknown recovery operation.")
    if action == "receive":
        raw = sys.stdin.buffer.read(bundle.MAX_ARCHIVE + 1)
        bundle.require(bundle.digest(raw) == digest, "Input SHA-256 mismatch.")
        rows = expectations(raw, mismatch=mode == "mismatch")
        bundle.write_new(ARCHIVE, raw)
        bundle.write_new(EXPECTED, rows)
    else:
        raw = bundle.read_regular(ARCHIVE, bundle.MAX_ARCHIVE)
        bundle.require(bundle.digest(raw) == digest, "Readback SHA-256 mismatch.")
        if action == "restore":
            bundle.restore(raw, TARGET)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VaultRejected as error:
        print(f"RECOVERY_INPUT={error}")
        raise SystemExit(1) from None
    except (OSError, ValueError, KeyError):
        print("RECOVERY_INPUT=rejected")
        raise SystemExit(1) from None
