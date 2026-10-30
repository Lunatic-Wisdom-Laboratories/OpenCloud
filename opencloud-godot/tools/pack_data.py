#!/usr/bin/env python3
"""Packs a converted data folder into data.zip the way large archives are best read: ZIP64,
entries 64-byte aligned, PNG and GLB stored, everything else Zstandard compressed (method 93).

    python3 tools/pack_data.py <converted-folder> <data.zip>

Needs the `zstandard` module (pip install zstandard). The engine also reads plain deflate ZIPs,
such as the converter's own --zip output.
"""
import os
import struct
import sys
import zlib


def write_zip64(path, entries):
    import zstandard

    out = bytearray()
    central = bytearray()
    for name, payload in entries:
        stored = name.lower().endswith((".png", ".glb"))
        data = payload if stored else zstandard.ZstdCompressor(level=1, write_content_size=True).compress(payload)
        method, version = (0, 45) if stored else (93, 63)
        crc = zlib.crc32(payload)
        encoded = name.encode()
        local_offset = len(out)
        zip64 = struct.pack("<HHQQ", 1, 16, len(payload), len(data))
        fixed = 30 + len(encoded) + len(zip64)
        padding = -(local_offset + fixed) % 64
        if padding and padding < 4:
            padding += 64
        pad = struct.pack("<HH", 0xD935, padding - 4) + b"\0" * (padding - 4) if padding else b""
        out += struct.pack("<IHHHHHIIIHH", 0x04034B50, version, 0x800, method, 0, 0x21, crc,
                           0xFFFFFFFF, 0xFFFFFFFF, len(encoded), len(zip64) + len(pad)) + encoded + zip64 + pad
        assert len(out) % 64 == 0
        out += data
        extra = struct.pack("<HHQQQ", 1, 24, len(payload), len(data), local_offset)
        central += struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, version, version, 0x800, method, 0, 0x21, crc,
                               0xFFFFFFFF, 0xFFFFFFFF, len(encoded), len(extra), 0, 0, 0, 0, 0xFFFFFFFF) + encoded + extra
    directory_offset = len(out)
    out += central
    zip64_end = len(out)
    out += struct.pack("<IQHHIIQQQQ", 0x06064B50, 44, 63, 63, 0, 0, len(entries), len(entries), len(central), directory_offset)
    out += struct.pack("<IIQI", 0x07064B50, 0, zip64_end, 1)
    out += struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, 0xFFFF, 0xFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0)
    with open(path, "wb") as handle:
        handle.write(out)


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    folder, target = sys.argv[1], sys.argv[2]
    entries = []
    for directory, _, files in os.walk(folder):
        for name in sorted(files):
            path = os.path.join(directory, name)
            with open(path, "rb") as handle:
                entries.append((os.path.relpath(path, folder).replace(os.sep, "/"), handle.read()))
    entries.sort()
    write_zip64(target, entries)
    print(f"wrote {target} ({len(entries)} entries)")


if __name__ == "__main__":
    main()
