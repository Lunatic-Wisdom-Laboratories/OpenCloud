#!/usr/bin/env python3
"""Writes synthetic game data for exercising the loaders without the retail game.

Nothing here comes from the game: the files are tiny hand-made samples of each container and
format the engine reads (TIM2, IMG, MDT/MDS, PACK, DATA.HD4/DATA.DAT and a ZIP64 data.zip with
Zstandard entries holding a glTF), placed at the paths the asset viewer looks for Max.

    python3 tools/make_test_data.py <output-dir>

produces <output-dir>/retail/DATA.HD4 + DATA.DAT and <output-dir>/converted/data.zip. Writing
data.zip needs the `zstandard` module (pip install zstandard).
"""
import base64
import json
import os
import struct
import sys
import zlib


def align(data: bytes, boundary: int) -> bytes:
    return data + b"\0" * (-len(data) % boundary)


# --- TIM2 ---------------------------------------------------------------------------------

def tim2_picture(width, height, image_type, pixels, clut=b"", clut_type=0, clut_colours=0):
    header_size = 0x30
    total = header_size + len(pixels) + len(clut)
    return struct.pack(
        "<IIIHHBBBBHHQQII",
        total, len(clut), len(pixels), header_size, clut_colours,
        0, 1, clut_type, image_type, width, height, 0, 0, 0, 0,
    ) + pixels + clut


def tim2(*pictures):
    return b"TIM2" + struct.pack("<BBH8x", 4, 0, len(pictures)) + b"".join(pictures)


def checker_tim2():
    """16x16 8-bit indexed checkerboard of logical indices 0 (white) and 9 (red).

    The CLUT is stored in CSM1 order, which swaps entries 8-15 and 16-23 of every 32, so the
    red colour for logical index 9 is written at position 17.
    """
    pixels = bytes(9 if ((x // 4) + (y // 4)) % 2 else 0 for y in range(16) for x in range(16))
    clut = bytearray(256 * 4)
    clut[0:4] = bytes([255, 255, 255, 0x80])
    clut[17 * 4:17 * 4 + 4] = bytes([255, 0, 0, 0x80])
    return tim2(tim2_picture(16, 16, 5, pixels, bytes(clut), clut_type=3, clut_colours=256))


def alpha_tim2():
    """8x8 32-bit picture, blue at half GS alpha (0x40 -> 0x80 after scaling)."""
    pixels = bytes([0, 0, 255, 0x40]) * 64
    return tim2(tim2_picture(8, 8, 3, pixels))


def img_archive(entries):
    body = bytearray(b"IMG\0" + struct.pack("<I8x", len(entries)))
    data_offset = 0x10 + 0x30 * len(entries)
    blobs = bytearray()
    for name, blob in entries:
        offset = data_offset + len(blobs)
        body += name.encode().ljust(32, b"\0") + struct.pack("<I12x", offset)
        blobs += align(blob, 16)
    return bytes(body + blobs)


# --- MDT / MDS ----------------------------------------------------------------------------

def vec4s(values):
    return b"".join(struct.pack("<4f", *v) for v in values)


def material(name, diffuse=(1, 1, 1, 1)):
    return struct.pack("<4f4f4ff", *diffuse, 0.5, 0.5, 0.5, 1, 0, 0, 0, 0, 0) + name.encode().ljust(32, b"\0") + struct.pack("<i2f", 0, 0, 0)


def mdt():
    positions = [(-0.5, 0, 0, 1), (0.5, 0, 0, 1), (-0.5, 1, 0, 1), (0.5, 1, 0, 1), (0, 1.2, 0, 1)]
    normals = [(0, 0, 1, 0)]
    uvs = [(0, 1, 0, 0), (1, 1, 0, 0), (0, 0, 0, 0), (1, 0, 0, 0), (0.5, 0.5, 0, 0)]
    colours = [(128, 128, 128, 128), (128, 64, 0, 128)]
    materials = [material("tex_checker"), material("tex_alpha")]

    faces = bytearray(struct.pack("<iiii", 0, 0x10, 2, 0))
    # Triangle strip (GS prim 4): v, n, uv per vertex.
    faces += struct.pack("<III", 4, 4, 0)
    for v in range(4):
        faces += struct.pack("<3i", v, 0, v)
    # Triangle list (GS prim 3) with per-vertex colours: v, n, uv, colour.
    faces += struct.pack("<III", 3 | 0x100, 3, 1)
    for v, c in ((2, 0), (3, 0), (4, 1)):
        faces += struct.pack("<4i", v, 0, v, c)

    sections = [vec4s(positions), vec4s(normals), vec4s(colours), bytes(align(bytes(faces), 16)), vec4s(uvs), b"".join(materials)]
    offsets = []
    cursor = 0x40
    for section in sections:
        offsets.append(cursor)
        cursor += len(section)
    header = b"MDT\0" + struct.pack(
        "<15i", 0x40, cursor,
        len(positions), offsets[0], len(normals), offsets[1], len(colours), offsets[2],
        len(faces), offsets[3], len(uvs), offsets[4], len(materials), offsets[5], 0,
    )
    return header + b"".join(sections)


def mds():
    identity = (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0)
    records = []
    model = mdt()
    model_offset = 0x10 + 0x70 * 2
    for index, (name, parent, translation, has_model) in enumerate(
        [("root", -1, (0, 0, 0), False), ("body--x", 0, (0, 0.5, 0), True)]
    ):
        records.append(struct.pack("<ii", 0, 0x70) + name.encode().ljust(32, b"\0")
                       + struct.pack("<ii", model_offset if has_model else 0, parent)
                       + struct.pack("<16f", *identity, *translation, 1))
    return struct.pack("<iiIi", 0, 0, len(records), 0x10) + b"".join(records) + model


# --- PACK and DATA.HD4 / DATA.DAT ---------------------------------------------------------

def pack(members):
    table_size = 0x4C * (len(members) + 1)
    data_start = (table_size + 0x7F) & ~0x7F
    table = bytearray()
    blobs = bytearray()
    for index, (name, blob) in enumerate(members):
        entry_at = index * 0x4C
        offset = data_start + len(blobs) - entry_at
        table += name.encode().ljust(64, b"\0") + struct.pack("<iii", offset, len(blob), 0x4C)
        blobs += align(blob, 0x80)
    table += b"\0" * 0x4C
    return bytes(align(bytes(table), 0x80)) + bytes(blobs)


def data_dat(files):
    table_size = 12 * len(files)
    names = bytearray()
    data = bytearray()
    records = bytearray()
    for name, blob in files:
        records += struct.pack("<iii", table_size + len(names), len(blob), len(data) // 2048)
        names += name.replace("/", "\\").encode() + b"\0"
        data += align(blob, 2048)
    return bytes(records + names), bytes(data)


# --- data.zip (ZIP64, Zstandard, 64-byte aligned payloads) ---------------------------------

def png_rgba(width, height, rgba):
    raw = b"".join(b"\0" + rgba[y * width * 4:(y + 1) * width * 4] for y in range(height))

    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b"")


def gltf_triangle():
    positions = struct.pack("<9f", -0.5, 0, 0, 0.5, 0, 0, 0, 1, 0)
    uvs = struct.pack("<6f", 0, 1, 1, 1, 0.5, 0)
    binary = positions + uvs
    document = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"name": "c01_base", "nodes": [0]}],
        "nodes": [{"name": "body", "mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1}, "material": 0}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}, "doubleSided": True}],
        "textures": [{"source": 0}],
        "images": [{"uri": "../textures/chara/c01%20base_0.png"}],
        "buffers": [{"uri": "c01_base.gltf.bin", "byteLength": len(binary)}],
        "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 36}, {"buffer": 0, "byteOffset": 36, "byteLength": 24}],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [-0.5, 0, 0], "max": [0.5, 1, 0]},
            {"bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC2"},
        ],
    }
    return json.dumps(document, separators=(",", ":")).encode(), binary


def write_zip64(path, entries):
    import zstandard

    out = bytearray()
    central = bytearray()
    for name, payload in entries:
        stored = name.lower().endswith((".png", ".mp4"))
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
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    root = sys.argv[1]
    retail = os.path.join(root, "retail")
    converted = os.path.join(root, "converted")
    os.makedirs(retail, exist_ok=True)
    os.makedirs(converted, exist_ok=True)

    textures = img_archive([("tex_checker", checker_tim2()), ("tex_alpha", alpha_tim2())])
    character = pack([("c01_base.mds", mds()), ("c01_base.img", textures)])
    header, data = data_dat([("chara/c01_base.chr", character), ("menu/sample.tm2", checker_tim2())])
    with open(os.path.join(retail, "DATA.HD4"), "wb") as handle:
        handle.write(header)
    with open(os.path.join(retail, "DATA.DAT"), "wb") as handle:
        handle.write(data)

    document, binary = gltf_triangle()
    checker = bytes([255, 255, 255, 255, 255, 0, 0, 255] * 2 + [255, 0, 0, 255, 255, 255, 255, 255] * 2)
    write_zip64(os.path.join(converted, "data.zip"), [
        ("chara/c01_base.gltf", document),
        ("chara/c01_base.gltf.bin", binary),
        ("textures/chara/c01 base_0.png", png_rgba(2, 2, checker)),
        ("gameplay/readme.json", b'{"note":"synthetic"}'),
    ])
    print(f"wrote {retail} and {converted}")


if __name__ == "__main__":
    main()
