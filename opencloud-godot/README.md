# opencloud-godot

Native reimplementation of the Dark Cloud 2 (Dark Chronicle) engine in Godot 4 (.NET 8 / C#).
The repository holds code only: the engine reads the user's own game data at run time.

## Requirements

- Godot 4.3 .NET (mono) editor
- .NET 8 SDK

```sh
dotnet build          # must finish with 0 errors
godot --path . -- --data=/path/to/data      # opens the asset viewer
```

## Game data

Point the engine at any of these with `-- --data=<path>` (repeatable; later paths override
earlier ones), the `OPENCLOUD_DATA` environment variable, or `data/paths` in
`user://opencloud.cfg` (the viewer saves working command-line paths there):

| Source | What is read |
| --- | --- |
| Disc image (`.iso`) | ISO 9660 files, plus the files packed in its `DATA.HD4`/`DATA.DAT` |
| `DATA.HD4` (with `DATA.DAT` beside it) | The retail file index |
| `data.zip` | The extractor's archive: ZIP64, Zstandard (method 93) and stored entries, CRC-checked |
| A folder | Loose files; a folder that holds `data.zip`, `DATA.HD4` or an `.iso` mounts those too |

Paths are case-insensitive, accept `\` and device prefixes (`cdrom0:\CHARA\C01_BASE.CHR;1`), and can
reach into PACK archives: `chara/c01_base.chr/c01_base.mds`.

## Layout

| Folder | Contents |
| --- | --- |
| `src/Core` | Binary helpers, Shift-JIS names, data path discovery |
| `src/Core/IO` | `VirtualFileSystem` and its sources: directory, ZIP, ISO, `DATA.HD4`/`DATA.DAT`, PACK |
| `src/Assets/Textures` | TIM2 decoder, IMG/IM2/IM3 archives, `TextureLoader` (→ `ImageTexture`), `TextureLibrary` |
| `src/Assets/Models` | MDS/MDT parsers, `MdsModelLoader` (→ `Skeleton3D` + `MeshInstance3D`), `GltfModelLoader` for `data.zip` |
| `src/Rendering` | Material factory, free camera |
| `src/Scenes` | `AssetViewer` (main scene) and `LoaderSmokeTest` (headless check) |
| `tools` | `make_test_data.py`: synthetic sample data, no game content |

## Asset viewer

Loads Max (`chara/c01_base.chr`, or the converted `chara/c01_base.gltf`; his outfit
`mainchr/c01fuku1` is added when present) and browses every `.chr`, `.mds`, `.pcp`, `.gltf`
and `.glb` in the mounted data.

PgDn/PgUp browse · Home Max · F frame · B bone markers · R reload · F1 help ·
right mouse look · WASD move · Q/E down/up · Shift fast · wheel speed.

## Headless check

```sh
python3 tools/make_test_data.py /tmp/oc-test
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/retail \
    --model=chara/c01_base.chr --texture=menu/sample.tm2
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/converted/data.zip \
    --model=chara/c01_base.gltf
```

## Format references

Formats are reimplemented from reverse-engineering notes on the retail game; no code from other
projects is reused.
