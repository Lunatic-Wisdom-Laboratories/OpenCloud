# opencloud-godot

Native reimplementation of the Dark Cloud 2 (Dark Chronicle) engine in Godot 4 (.NET 8 / C#).
The repository holds code only: the engine reads the user's own game data at run time.

## Requirements

- Godot 4.3 .NET (mono) editor
- .NET 8 SDK

```sh
dotnet build          # must finish with 0 errors
godot --path . -- --data=/path/to/data      # opens the asset viewer
godot --path . res://src/Scenes/MapViewer.tscn -- --data=/path/to/data --map=s01   # map viewer
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
| `src/Core/Scripts` | `GameScript`: the CFG script reader (text and `BIN` encodings) |
| `src/Core/IO` | `VirtualFileSystem` and its sources: directory, ZIP, ISO, `DATA.HD4`/`DATA.DAT`, PACK |
| `src/Assets/Textures` | TIM2 decoder, IMG/IM2/IM3 archives, `TextureLoader` (→ `ImageTexture`), `TextureLibrary` |
| `src/Assets/Models` | MDS/MDT parsers, `MdsModelLoader` (→ `Skeleton3D` + `MeshInstance3D` + `Skin`), character packs (`info.cfg`, `.wgt` weights, `.bbp` bind poses, `.mot` motions → `AnimationPlayer`), `GltfModelLoader` for `data.zip` |
| `src/Assets/Maps` | Map scripts (`MapInfo`, `MapLayout`), PCP model lists, sky packs, `MapLoader` (→ placed parts, collision bodies, sky) |
| `src/Rendering` | Material factory, free camera, `MapLightingRig` (map lighting sets → environment and lights) |
| `src/Game/Items` | `ItemCatalog`: the common item table (`menu/cfg7/comdat.cfg`), item types and families |
| `src/Game/Save` | `GlobalSaveState` (shared world state), `SaveOperation`, `Inventory`, `PlayerLocalState`, `PlayerRoster` |
| `src/Net` | `NetworkManager` (ENet co-op session) and `SaveSync` (host-authoritative save replication) |
| `src/Scenes` | `AssetViewer` (main scene), `MapViewer`, `LoaderSmokeTest` and `NetSyncSmokeTest` (headless checks) |
| `tools` | `make_test_data.py`: synthetic sample data, no game content |

## Asset viewer

Loads Max (`chara/c01_base.chr`, or the converted `chara/c01_base.gltf`; his outfit
`mainchr/c01fuku1` is added when present) and browses every `.chr`, `.mds`, `.pcp`, `.gltf`
and `.glb` in the mounted data.

PgDn/PgUp browse · Home Max · F frame · B bone markers · R reload · F1 help ·
right mouse look · WASD move · Q/E down/up · Shift fast · wheel speed.

Animations are listed on the right (click one, or Up/Down): Space pauses, Left/Right step one
motion frame while paused, `-`/`=` halve/double the speed, T toggles playing at each action's
`KEY` step. Models without motions of their own (Max's outfit) follow the clips of the first
animated model wherever bone names match.

## Map viewer

Lists every `map/**/*.map` of the mounted data (the retail `.iso`, `DATA.HD4` or an extracted
folder) and shows the chosen one with its lighting, sky and time of day. Start on a map with
`-- --map=<name or path>` (`s01` is `map/s/s01/s01.map`, as `GetMapPath` builds it), or press Tab
in the asset viewer.

PgDn/PgUp or click the list: map · F frame · R reload · C collision · P function points ·
H pieces the script hides · L map lighting on/off · `[` `]` game clock -/+ 1 h · Tab asset viewer ·
F1 help. The camera is the asset viewer's.

## Maps

A map is loaded as `CScene::LoadMap` does, from the files sharing its stem:

- `.map`: one script read twice. The map information tags (`IMG`, `PCP`, `LIGHT_SET` blocks with
  `BGCOLOR`, `AMBIENT`, `LIGHT`, `FOG`..., `TIME_FLAG`, `SKY_INFO`) say which textures and model
  lists to load and how to light the map; the layout tags define parts (`PARTS` ... `PARTS_END`,
  each a list of `PIECE`s) and place them (`MAP_PARTS` ... `MAP_PARTS_END`).
- `.ipk`: the IMG texture files the map names. `.mpk`: the PCP model lists, each a PACK whose
  `info.cfg` (`MDS`, `TYPE`, `FAR_CLIP`, `MDS_END`) says whether a model is drawn, walkable collision
  (type 1), camera collision (type 2 → 3) or a character. Pieces find their model by name, searching
  the lists loaded last first, as the retail slot order does.
- Frames are placed as `mgCFrame::GetLocalMatrix` places them: the frame's own matrix is scaled,
  rotated about X, then Y, then Z (radians), then moved. A placement copies its part without the
  pieces that have a `PIECE_COL_TYPE` (`CMapParts::Copy`).
- Pieces show by their show flags and `PIECE_TIME` (`CheckTime`) at the map's time: the game clock
  when `TIME_FLAG` enables it, else the fixed time, else noon. The lighting set is the active one, or
  the one of the current time band on timed maps. Light and ambient colours are on the GS scale
  (128 = 1.0); pieces of `LIGHT_FLAG` parts only take the ambient.
- Collision models become `StaticBody3D`s (layer 1 walk, layer 2 camera) with concave shapes.
- `.sky` (or `map/def.sky`): per time band a back sky and a sky model drawn at the camera, spinning,
  and mirrored below the horizon, with each band's own IMG.
- Function points (events, lights, sounds...) are shown as markers scaled to their trigger radius.

Not yet: the add-map of `map/cmn` (needs the map name table), `.cfg` (water, draw-off areas,
occlusion), `.efp` effects, camera tables, `PIECE_MATERIAL` colours, point lights, blending
between time lighting sets, sun and moon, LOD and far clipping.

## Characters, skinning and motion

A character pack (`.chr`) is read the way the game reads it, through its `info.cfg`:

- `MODEL` names the MDS scene. Every MDS object is a bone of one `Skeleton3D`.
- The frames named by `VERTEX_ANIME` are skinned with `<model>.wgt` (up to 32 bones per model,
  four influences per vertex, weights in percent, renormalised as `CreateVertexWeight` does);
  every other model moves rigidly with its bone.
- `MOTION slot, "x.mot", "x.bbp"` loads a motion: the `.bbp` holds the bind pose (scene-space
  matrices that replace the rest pose as the skin's base), the `.mot` the rotation, translation
  and scale key lists. Each `KEY name, start, end, step` interval becomes one looping
  `Animation` in library `m<slot>` of the model's `AnimationPlayer`, with the original name
  (`game_name` meta) and step (`step` meta). One motion frame is 1/30 s.
- Keys are evaluated as `MotionProc` does (shorter-arc slerp with its snap thresholds, lerp,
  scale after rotation); bones with scale keys are resampled at every key and decomposed.

Costumes are put on the way `LoadSkin` does it (`MdsModelLoader.ApplySkin`): the costume pack's
`info.cfg` is read with the skin tags (`MODEL`, `MOTION slot, ?, "x.bbp", "x.wgt"`), its visuals on
the character's deform frames are skinned to the character's skeleton with bones matched by name,
and each costume frame replaces what the character's frame of the same name drew (Max's rig
placeholder triangles). The viewer dresses Max in `mainchr/c01fuku1.chr` this way.

Not yet: vertex/shape motion
(`SHAPE_ANIME`, key types 12/20/21), material, visibility and camera keys, motion blending,
sequences (`SEQ`), cloth.

## Headless check

```sh
python3 tools/make_test_data.py /tmp/oc-test
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/retail \
    --model=chara/c01_base.chr --model=chara/c99_binary.chr --texture=menu/sample.tm2
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/converted/data.zip \
    --model=chara/c01_base.gltf
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/retail --map=t01
```

## Co-op save state

The save record is split by owner:

- `GlobalSaveState` is what every player shares: story flags (0x800 bits) and counters (0x80),
  per-map flags (256 maps x 128 bits), clock and story progress, Georama part counts, money
  (0..999999), medals (0..999), the shared 150-place item board (138 or 144 places plus
  overflow, with the stacking, owned-limit and unique-item rules) and the status and equipment
  of Max and Monica.
- `PlayerLocalState` is one player's: peer id, slot, controlled character (slot 0 Max, slot 1
  Monica), map and position, and their own options. `PlayerRoster` holds one per player.

Every change to the shared state is a `SaveOperation` (`AddMoney`, `AddItem`, `RemoveItem`,
`SetBitFlag`, `SetMapFlag`...) applied with `GlobalSaveState.Apply`, which is deterministic.
`NetworkManager` (ENet, port 7777, two players by default):

- the host applies operations, numbers them and replays them to every peer on reliable channel 1;
  a client's `Submit` goes to the host, which validates it, and the client's state changes when
  the host's copy comes back;
- every 5 s the host sends its revision and a SHA-256-based digest; a client that differs, or
  misses a revision, asks for a full snapshot;
- joining checks the protocol version and the item table fingerprint (both players need the
  same game data), sends the roster and a snapshot, and raises `PlayerJoined(peerId, slot,
  character)` on every peer (`PlayerLeft` when they go).

```sh
godot --headless --path . res://src/Scenes/NetSyncSmokeTest.tscn   # no game data needed
```

The check runs a host and a client in one process over localhost: inventory rules, sync from
both sides, a deliberately broken client copy repaired by the digest check, and leaving.

## Format references

Formats are reimplemented from reverse-engineering notes on the retail game; no code from other
projects is reused.
