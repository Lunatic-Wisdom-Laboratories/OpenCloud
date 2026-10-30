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
godot --path . res://src/Scenes/TownScene.tscn -- --data=/path/to/data --map=s01   # walk as Max
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
| `src/Gameplay` | `PlayerCharacter` (town controller), `CharacterMotion` (motions by game name), `TownCamera`, `PadInput`, `PlayerModels` (dressed player rigs), `PlayerSync` (30 Hz network sync) |
| `src/Scenes` | `AssetViewer` (main scene), `MapViewer`, `TownScene` and `LoaderSmokeTest` (headless check) |
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

## Walking in town

`TownScene` loads a map and puts Max at its `CHARA_POS`, dressed as `SetupMainUnit` dresses him:
his field rig `mainchr/c01_base2` (the town rig `chara/c01_base` keeps a placeholder where the
shoes go, so it is only the fallback), clothes and shoes as costumes, the cap on the `hat` frame. Models go through `GameModelLoader`, so converted glTF files are picked up too.

WASD/arrows or the left stick move (Shift walks on the keyboard) · Q/E or L1/R1, J/L or the right
stick turn the camera · I/K raise and lower it · C or L2 swings it behind Max · R back to the start ·
Tab map viewer · F1 help.

The controller follows the town loop's `CharaControl` and `EditMoveChara`, at the game's 30 Hz:

- The stick is calibrated like the original pad (dead zone 50 of 128, the rest stretched to full)
  and turned by the camera angle. Full stick is 5 units a game frame, 0.7 of that on maps under 800
  units across. The facing turns at most 0.3 rad a frame; under 0.8 stick strength Max walks
  (`歩き`, step 0.1 + strength / 0.8), above it he runs (`走り`), and stands (`立ち`) with no input.
- Gravity is 0.6 a frame down to -10. Ground is searched from 10 above the feet, 20 down
  (`MoveCheck`/`GetFootPoly` against the walk collision): ledges up to 10 are stepped onto, and
  after more than three airborne frames he plays `落下中`. Landing faster than 5 plays `着地` and
  holds him for up to 20 frames. Walls stop a 13-unit cylinder that starts above the step height.
- Motions change as `SetMotion` does: a motion already playing keeps its frame, a new one blends in
  over about 4.5 frames, a step is motion frames per game frame.
- The model is drawn between the last two game frames, and the camera follows the drawn model,
  so the 30 Hz logic looks smooth.

The camera is `CCameraControl` with the town parameters: it trails the look-at point (Max plus 30
up) between 100 and 160 units away, at a height the right stick moves between -15 and 40 that
drifts back to -15..20, eye and look-at point closing 1/4 and 1/3 of their gap a frame. It is kept
in front of camera collision and 30 above the ground.

Not yet: water (wading speed, `水中走り`, ripples), foot sounds and dust, events (doors, ladders,
treasure, talk), fixed camera areas, the georama edit mode, Monica's equipment.

### Players and co-op

Every player is a `PlayerCharacter` owned by a network peer (its multiplayer authority), named
`Players/Player<peer id>` on every peer. `TownScene.SpawnPlayer(peerId, slot)` adds one (slot 0
Max, 1 Monica) and `RemovePlayer(peerId)` takes it away; the co-op session layer calls them when
players join and leave. Only the owner runs the controller. Its `PlayerSync` sends position,
facing and motion once a game frame (30 Hz) with unreliable ordered RPCs on transfer channel 2,
through whatever peer the scene tree's multiplayer API holds; the other peers draw that player
three game frames in the past, interpolating between the states received.

For testing the sync without the session layer, `--net-host=<port>` and
`--net-join=<address>:<port>` open a bare two-player ENet session, and `--net-report=<seconds>`
prints every player's position after that long and quits.

## Headless check

```sh
python3 tools/make_test_data.py /tmp/oc-test
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/retail \
    --model=chara/c01_base.chr --model=chara/c99_binary.chr --texture=menu/sample.tm2
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/converted/data.zip \
    --model=chara/c01_base.gltf
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/retail --map=t01
godot --headless --path . res://src/Scenes/TownScene.tscn -- --data=/tmp/oc-test/retail --map=t01 --selftest=60
```

The town self-test holds the stick forward for 60 game frames and fails unless Max moved and
stands on the synthetic map's floor.

## Format references

Formats are reimplemented from reverse-engineering notes on the retail game; no code from other
projects is reused.
