# opencloud-godot

Native reimplementation of the Dark Cloud 2 (Dark Chronicle) engine in Godot 4 (.NET 8 / C#).
The repository holds code only. The engine reads only converted, open-format data: run
[`opencloud-converter`](../opencloud-converter/README.md) once on your own disc first.

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

Point the engine at the converter's output with `-- --data=<path>` (repeatable; later paths
override earlier ones, so a datapack mounted last replaces files), the `OPENCLOUD_DATA`
environment variable, or `data/paths` in `user://opencloud.cfg` (the viewer saves working
command-line paths there):

| Source | What is read |
| --- | --- |
| A converted folder | Loose files (holds the converter's `opencloud-data.json`); a `data.zip` inside it mounts too |
| `data.zip` | A converted folder packed as ZIP (deflate, or ZIP64 with Zstandard method 93 / stored entries), CRC-checked |

A disc image, `DATA.HD4` or a folder holding them is refused with a message pointing to the
converter. Paths are case-insensitive and accept `\`.

What the engine reads (see the converter's README for the full description):

| File | Format |
| --- | --- |
| `*.glb` + `*.model.json` | glTF 2.0 binary models; the companion holds what glTF cannot: bone frame names, deform frames, bind matrices, the game names and `KEY` steps of the animations, hidden nodes |
| `*.textures.json` + `*.png` | The pictures of one texture file with their game names and clamp/alpha flags |
| `*.map.json`, `list.json`, `sky.json` | Map information and layout, model lists, sky packs |
| `menu/cfg7/comdat.items.json` | The common item table |

## Layout

| Folder | Contents |
| --- | --- |
| `src/Core` | Binary helpers, Shift-JIS names, data path discovery |
| `src/Core/IO` | `VirtualFileSystem` and its sources: directory and ZIP |
| `src/Assets/Textures` | `TextureDocument` (PNG + JSON → `ImageTexture`), `TextureLibrary` |
| `src/Assets/Models` | `ModelLoader` (GLB + `.model.json` → `Skeleton3D`, `Skin`, `AnimationPlayer` with libraries `m<slot>`), costumes (`ApplySkin`), `ModelDocument`, `ModelMeta` |
| `src/Assets/Maps` | `MapDocument` (map information, layout, model lists, sky), `MapLoader` (→ placed parts, collision bodies, sky) |
| `src/Rendering` | Material factory, free camera, `MapLightingRig` (map lighting sets → environment and lights) |
| `src/Game/Items` | `ItemCatalog`: the common item table (`menu/cfg7/comdat.items.json`), item types and families |
| `src/Game/Save` | `GlobalSaveState` (shared world state), `SaveOperation`, `Inventory`, `PlayerLocalState`, `PlayerRoster` |
| `src/Net` | `NetworkManager` (ENet co-op session) and `SaveSync` (host-authoritative save replication) |
| `src/Gameplay` | `PlayerCharacter` (town controller), `CharacterMotion` (motions by game name), `TownCamera`, `PadInput`, `PlayerModels` (dressed player rigs), `PlayerSync` (30 Hz network sync) |
| `src/Scenes` | `AssetViewer` (main scene), `MapViewer`, `TownScene`, `LoaderSmokeTest` and `NetSyncSmokeTest` (headless checks) |
| `tools` | `make_test_data.py`: synthetic original-format sample data for the converter, no game content; `pack_data.py`: packs a converted folder into a ZIP64/Zstandard `data.zip` |

## Asset viewer

Loads Max (`mainchr/c01_base2.glb`, else `chara/c01_base.glb`; his outfit `mainchr/c01fuku1.glb`
is added when present) and browses every `.glb` and `.gltf` in the mounted data.

PgDn/PgUp browse · Home Max · F frame · B bone markers · R reload · F1 help ·
right mouse look · WASD move · Q/E down/up · Shift fast · wheel speed.

Animations are listed on the right (click one, or Up/Down): Space pauses, Left/Right step one
motion frame while paused, `-`/`=` halve/double the speed, T toggles playing at each action's
`KEY` step. Models without motions of their own (Max's outfit) follow the clips of the first
animated model wherever bone names match.

## Map viewer

Lists every converted map (`map/**/*.map.json`) of the mounted data and shows the chosen one with its lighting, sky and time of day. Start on a map with
`-- --map=<name or path>` (`s01` is `map/s/s01/s01.map.json`, as `GetMapPath` builds it), or press Tab
in the asset viewer.

PgDn/PgUp or click the list: map · F frame · R reload · C collision · P function points ·
H pieces the script hides · L map lighting on/off · `[` `]` game clock -/+ 1 h · Tab town mode on this map ·
F1 help. The camera is the asset viewer's.

## Maps

The converter reads a map as `CScene::LoadMap` does, from the original files sharing its stem,
and writes `<stem>.map.json` with its models and textures beside it; the engine builds it from
that:

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

The converter reads a character pack (`.chr`) the way the game reads it, through its `info.cfg`,
and writes it as one GLB with its `.model.json`:

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

Costumes are put on the way `LoadSkin` does it (`ModelLoader.ApplySkin`): the converter reads the
costume pack's `info.cfg` with the skin tags (`MODEL`, `MOTION slot, ?, "x.bbp", "x.wgt"`) and
writes it skinned to its own frames (kind `costume`); the engine re-skins its visuals on
the character's deform frames are skinned to the character's skeleton with bones matched by name,
and each costume frame replaces what the character's frame of the same name drew (Max's rig
placeholder triangles). The viewer dresses Max in `mainchr/c01fuku1.glb` this way.

Not yet: vertex/shape motion
(`SHAPE_ANIME`, key types 12/20/21), material, visibility and camera keys, motion blending,
sequences (`SEQ`), cloth.

## Walking in town

`TownScene` loads a map and puts Max at its `CHARA_POS`, dressed as `SetupMainUnit` dresses him in
town, but on his field rig `mainchr/c01_base2` (the town rig `chara/c01_base` is the fallback;
the cap hangs upside down on its `hat` frame), clothes and shoes
(`c01sh001`, else `c01sh002`) as costumes, the cap on the `hat` frame. The HUD lists what was
loaded and any piece that was not found.

WASD/arrows or the left stick move (Shift walks on the keyboard) · Q/E or L1/R1, J/L or the right
stick turn the camera · I/K raise and lower it · C or L2 swings it behind Max · R back to the start ·
Tab asset viewer · F1 help. Tab in the map viewer opens this scene on the map being shown, so Tab
cycles asset viewer → map viewer → town → asset viewer.

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
godot --headless --path ../opencloud-converter -- --source=/tmp/oc-test/retail --out=/tmp/oc-test/out --zip=/tmp/oc-test/data.zip
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/out \
    --model=chara/c01_base.glb --model=chara/c99_binary.glb --skin=mainchr/c01fuku1.glb \
    --texture=menu/sample.textures.json --map=t01 --items
godot --headless --path . res://src/Scenes/LoaderSmokeTest.tscn -- --data=/tmp/oc-test/data.zip --map=t01
godot --headless --path . res://src/Scenes/TownScene.tscn -- --data=/tmp/oc-test/out --map=t01 --selftest=60
```

Pointing the smoke test at `/tmp/oc-test/retail` must fail: the engine refuses original data.

The town self-test holds the stick forward for 60 game frames and fails unless Max moved and
stands on the synthetic map's floor.

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
