"""Synthetic closed-vault inputs for local and CI recovery checks."""

import struct

from check import bundle


def vault(entries):
    data = bytearray(struct.pack("<IHI", 0x6E564C54, 0x0200, len(entries)))
    for key, value in entries:
        data.extend(struct.pack("<IBH", 1700000000, len(key), len(value)))
        data.extend(key)
        data.extend(value)
    return data


def fixture(directory):
    source = directory / "vault"
    source.mkdir()
    key = b"v1:STEAM_0:1:989898"
    (source / (bundle.STEMS[0] + ".vault")).write_bytes(vault([(key, b"1 25 2")]))
    (source / (bundle.STEMS[1] + ".vault")).write_bytes(vault([
        (key, b"1 2 1 2"), (b"hud:" + key, b"1 1"),
        (b"streak:" + key, b"1 7"), (b"name:" + key, b"1 Control"),
    ]))
    path = directory / "synthetic.tar"
    data = bundle.create(source)
    path.write_bytes(data)
    return path, bundle.digest(data)
