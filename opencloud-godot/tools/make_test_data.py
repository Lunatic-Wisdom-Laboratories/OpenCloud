#!/usr/bin/env python3
"""Writes synthetic game data for exercising the loaders without the retail game.

Nothing here comes from the game: the files are tiny hand-made samples of each container and
format the converter reads (TIM2, IMG, MDT/MDS, MOT/WGT/BBP, CFG, PACK, DATA.HD4/DATA.DAT),
placed at the paths the asset viewer looks for Max, plus a costume, a small map
(map/t/t01/t01.*), a loose texture and an item table.

    python3 tools/make_test_data.py <output-dir>

produces <output-dir>/retail/DATA.HD4 + DATA.DAT, ready for opencloud-converter.
"""
import os
import struct
import sys


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


UPRIGHT_QUAD = [(-0.5, 0, 0, 1), (0.5, 0, 0, 1), (-0.5, 1, 0, 1), (0.5, 1, 0, 1), (0, 1.2, 0, 1)]
# A 1000 x 1000 floor at y = 0 (the strip uses the first four corners).
GROUND_QUAD = [(-500, 0, -500, 1), (500, 0, -500, 1), (-500, 0, 500, 1), (500, 0, 500, 1), (0, 0, 0, 1)]


def mdt(positions=UPRIGHT_QUAD):
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


FRAMES = [
    # name, parent, translation, has model
    ("root", -1, (0, 0, 0), False),
    ("body--x", 0, (0, 0.5, 0), True),
    ("hip", 0, (0, 0.5, 0), False),
    ("arm", 2, (0, 0.5, 0), False),
]


# The costume names the same frames in another order, so its bones must be matched by name.
COSTUME_FRAMES = [
    ("root", -1, (0, 0, 0), False),
    ("hip", 0, (0, 0.5, 0), False),
    ("arm", 1, (0, 0.5, 0), False),
    ("body", 0, (0, 0.5, 0), True),
]


def mds(frames=FRAMES, positions=UPRIGHT_QUAD):
    identity = (1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0)
    records = []
    model = mdt(positions)
    model_offset = 0x10 + 0x70 * len(frames)
    for name, parent, translation, has_model in frames:
        records.append(struct.pack("<ii", 0, 0x70) + name.encode().ljust(32, b"\0")
                       + struct.pack("<ii", model_offset if has_model else 0, parent)
                       + struct.pack("<16f", *identity, *translation, 1))
    return struct.pack("<iiIi", 0, 0, len(records), 0x10) + b"".join(records) + model


# --- Skinning and motion -------------------------------------------------------------------

def key_lists(lists):
    """Weight/motion container: 0x20 header {frame, target, type, 0, count, more} + 0x20 keys."""
    out = bytearray()
    for index, (frame, target, kind, keys) in enumerate(lists):
        more = 1 if index + 1 < len(lists) else 0
        out += struct.pack("<iiiiII8x", frame, target, kind, 0, len(keys), more)
        for key_frame, value in keys:
            out += struct.pack("<I12x4f", key_frame, *value)
    return bytes(out)


def weights():
    """The body model (frame 1): bottom vertices on the hip, the top ones blended with the arm."""
    return key_lists([
        (1, 2, 20, [(0, (100, 0, 0, 0)), (1, (100, 0, 0, 0)), (2, (40, 0, 0, 0)), (3, (40, 0, 0, 0))]),
        (1, 3, 20, [(2, (60, 0, 0, 0)), (3, (60, 0, 0, 0)), (4, (100, 0, 0, 0))]),
    ])


def costume_weights():
    """The costume body (its frame 3) weighted to its hip (1) and arm (2)."""
    return key_lists([
        (3, 1, 20, [(0, (100, 0, 0, 0)), (1, (100, 0, 0, 0)), (2, (40, 0, 0, 0)), (3, (40, 0, 0, 0))]),
        (3, 2, 20, [(2, (60, 0, 0, 0)), (3, (60, 0, 0, 0)), (4, (100, 0, 0, 0))]),
    ])


def costume_info_cfg():
    """Read with the skin tags: MOTION slot, unused, bind pose, weights."""
    return b'MODEL "c01f.mds";\nMOTION 0, "", "c01f.bbp", "c01f.wgt";\n'


def bind_pose(frames=FRAMES):
    """Scene-space matrix of every frame in the rest pose."""
    world = []
    for name, parent, translation, _ in frames:
        base = world[parent][3] if parent >= 0 else (0, 0, 0)
        world.append(((1, 0, 0, 0), (0, 1, 0, 0), (0, 0, 1, 0),
                      (base[0] + translation[0], base[1] + translation[1], base[2] + translation[2], 1)))
    return b"".join(struct.pack("<16f", *[c for row in m for c in row]) for m in world)


def motion():
    """Arm swings about Z (keys are scalar-first quaternions), hip bobs; frames 0-20."""
    import math

    def about_z(degrees):
        half = math.radians(degrees) / 2
        return (math.cos(half), 0, 0, math.sin(half))

    return key_lists([
        (3, 0, 0, [(0, about_z(0)), (10, about_z(60)), (20, about_z(0))]),
        (2, 0, 2, [(0, (0, 0.5, 0, 0)), (10, (0, 0.7, 0, 0)), (20, (0, 0.5, 0, 0))]),
    ])


def info_cfg():
    text = """// synthetic character
V2;
NAME "test";
VERTEX_ANIME "body";
MODEL "c01_base.mds";
MOTION 0, "c01_base.mot", "c01_base.bbp", "";
KEY_START;
KEY "\u7acb\u3061", 0, 10, 1.0;
KEY "walk", 10, 20, 0.5;
KEY "\u8d70\u308a", 0, 20, 1.0;
KEY_END;
MOTION_END;
"""
    return text.encode("cp932")


def item_table():
    # COM item, type, list, active, stack, max, icon texture, icon, message, file, attribute
    text = """// synthetic item table
COMINIT;
COM 1, 1, 0, 1, 1, 1, 0, 0, 0, "", 0;
COM 2, 22, 0, 1, 9, 99, 0, 1, 0, "", 0;
COM 3, 5, 0, 1, 1, 1, 0, 2, 0, "fuku1", 64;
"""
    return text.encode("cp932")


CHARACTER_TAGS = [
    "V2", "NAME", "IMG", "IMG_END", "BODY_SIZE", "SCALE", "MATERIAL_ANIME", "POLY_NUM", "MODEL",
    "SHADOW_MODEL", "OBJECT_NAME", "OBJECT_NAME2", "MOTION", "SHADOW_MOTION", "OUTLINE",
    "VERTEX_ANIME", "SHAPE_ANIME", "KEY_START", "KEY", "KEY_END",
]


def info_cfg_binary():
    """The same script in the "BIN" encoding: s16 tag index, s16 count, type bytes, values."""
    def command(tag, *args):
        out = bytearray(struct.pack("<hh", CHARACTER_TAGS.index(tag), len(args)))
        for arg in args:
            out.append(3 if isinstance(arg, str) else 2 if isinstance(arg, float) else 1)
        out += b"\0" * (-len(out) % 4)
        for arg in args:
            if isinstance(arg, str):
                out += align(arg.encode("cp932") + b"\0", 4)
            else:
                out += struct.pack("<f" if isinstance(arg, float) else "<i", arg)
        return bytes(out)

    return b"BIN\0" + b"".join([
        command("V2"),
        command("VERTEX_ANIME", "body"),
        command("MODEL", "c01_base.mds"),
        command("MOTION", 0, "c01_base.mot", "c01_base.bbp", ""),
        command("KEY_START"),
        command("KEY", "\u7acb\u3061", 0, 10, 1.0),
        command("KEY_END"),
    ]) + struct.pack("<h", -1)


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


# --- map ---------------------------------------------------------------------------------

MAP_SCRIPT = """// synthetic map: one part definition placed twice
IMG "t01.img";
PCP "t01_0.pcp";
ACTIVE_LIGHT_SET 0;
TIME_FLAG 1, 0;
SKY_INFO 1, 0.0, 30.0;
LIGHT_SET 0;
 FOV;
 BGCOLOR 40, 60, 90;
 AMBIENT 64, 64, 64;
 LIGHT 0, 0.0, 1.0, 0.0, 64, 64, 64, 1;
 FOG_ENABLE 1;
 FOG 100.0, 1000.0, 200, 200, 255;
LIGHT_SET_END;
CHARA_POS 0.0, 0.0, 0.0;
PARTS "house";
 LIGHT_FLAG 0, 0;
 PIECE "box.mds";
  PIECE_POS 10.0, 0.0, 0.0;
  PIECE_ROT 0.0, 1.5707964, 0.0;
 PIECE_END;
 PIECE "lamp.mds";
  PIECE_NAME "night_lamp";
  PIECE_TIME 18.0, 6.0;
 PIECE_END;
 PIECE "floor_col.mds";
 PIECE_END;
 PIECE "box.mds";
  PIECE_COL_TYPE 1;
 PIECE_END;
 FUNC_POINT 1;
  FUNC_DATA "event", 1;
  FUNC_NAME "door";
  FUNC_POS 0.0, 0.0, 5.0, 0.0, 0.0, 0.0, 15.0, 15.0, 15.0;
  FUNC_DATA_END;
 FUNC_POINT_END;
PARTS_END;
PARTS "ground";
 PIECE "ground_col.mds";
 PIECE_END;
PARTS_END;
MAP_PARTS "house_a";
 PARTS_NAME "house";
 PARTS_POS 100.0, 0.0, 0.0;
 PARTS_ROT 0.0, 3.1415927, 0.0;
MAP_PARTS_END;
MAP_PARTS "house_b", 0;
 PARTS_NAME "house";
 PARTS_SCALE 2.0, 2.0, 2.0;
MAP_PARTS_END;
MAP_PARTS "ground";
 PARTS_NAME "ground";
MAP_PARTS_END;
MAP_PARTS "nothing";
 PARTS_NAME "no_such_part";
MAP_PARTS_END;
"""

MODEL_LIST = """MDS "box.mds"; TYPE 0; MDS_END;
MDS "lamp.mds"; MDS_END;
MDS "floor_col.mds"; TYPE 1; FAR_CLIP 500.0, 1; MDS_END;
MDS "ground_col.mds"; TYPE 1; MDS_END;
"""

SKY_INFO = """SKY_IMG 0, "sky0.img";
SKY_MDS 0, "sky0.mds", 1.0;
SKYB_MDS 0, "skyb0.mds", 0.5;
SKY_ANIME 0, "arm", 2.0;
"""


def map_files(textures):
    model_list = pack([
        ("info.cfg", MODEL_LIST.encode()),
        ("box.mds", mds()),
        ("lamp.mds", mds()),
        ("floor_col.mds", mds()),
        ("ground_col.mds", mds(positions=GROUND_QUAD)),
    ])
    sky = pack([
        ("info.cfg", SKY_INFO.encode()),
        ("sky0.mds", mds()),
        ("skyb0.mds", mds()),
        ("sky0.img", textures),
    ])
    return [
        ("map/t/t01/t01.map", MAP_SCRIPT.encode()),
        ("map/t/t01/t01.ipk", pack([("t01.img", textures)])),
        ("map/t/t01/t01.mpk", pack([("t01_0.pcp", model_list)])),
        ("map/t/t01/t01.sky", sky),
    ]


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    root = sys.argv[1]
    retail = os.path.join(root, "retail")
    os.makedirs(retail, exist_ok=True)

    textures = img_archive([("tex_checker", checker_tim2()), ("tex_alpha", alpha_tim2())])
    character = pack([
        ("info.cfg", info_cfg()),
        ("c01_base.mds", mds()),
        ("c01_base.wgt", weights()),
        ("c01_base.mot", motion()),
        ("c01_base.bbp", bind_pose()),
        ("c01_base.img", textures),
    ])
    binary_character = pack([(name, info_cfg_binary() if name == "info.cfg" else blob) for name, blob in [
        ("info.cfg", b""), ("c01_base.mds", mds()), ("c01_base.wgt", weights()),
        ("c01_base.mot", motion()), ("c01_base.bbp", bind_pose()), ("c01_base.img", textures),
    ]])
    costume = pack([
        ("info.cfg", costume_info_cfg()),
        ("c01f.mds", mds(COSTUME_FRAMES)),
        ("c01f.wgt", costume_weights()),
        ("c01f.bbp", bind_pose(COSTUME_FRAMES)),
        ("c01f.img", textures),
    ])
    header, data = data_dat([("chara/c01_base.chr", character), ("mainchr/c01fuku1.chr", costume), ("chara/c99_binary.chr", binary_character), ("menu/sample.tm2", checker_tim2()), ("menu/cfg7/comdat.cfg", item_table())] + map_files(textures))
    with open(os.path.join(retail, "DATA.HD4"), "wb") as handle:
        handle.write(header)
    with open(os.path.join(retail, "DATA.DAT"), "wb") as handle:
        handle.write(data)

    print(f"wrote {retail}")


if __name__ == "__main__":
    main()
