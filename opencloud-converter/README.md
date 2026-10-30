# opencloud-converter

One-time conversion of your own Dark Cloud 2 (Dark Chronicle) disc into the open formats the
engine ([`opencloud-godot`](../opencloud-godot/README.md)) reads. The engine never opens the
original files; everything that knows the retail formats lives here.

Needs the same tools as the engine: Godot 4.3 .NET and the .NET 8 SDK.

```sh
dotnet build                     # must finish with 0 errors
godot --headless --path . -- --source="/path/to/Dark Chronicle.iso" --out=/path/to/opencloud-data
```

| Option | Meaning |
| --- | --- |
| `--source=<path>` | Disc image (`.iso`), `DATA.HD4` (with `DATA.DAT` beside it) or a folder holding loose files, an `.iso` or `DATA.HD4`. Repeatable; later sources win. |
| `--out=<folder>` | Where the converted data goes. |
| `--zip=<file>` | Also pack the output folder into one ZIP the engine can mount. |
| `--only=<prefix>` | Convert only the original paths that start with it (`--only=chara/`). |

The run prints what it converted, what it skipped by extension and every file that failed, and
writes the same report to `opencloud-data.json` at the root of the output, which is also how the
engine recognises a converted folder. The exit code is 1 when a file failed.

## What is read

- the ISO 9660 file system of the disc, and `DATA.HD4`/`DATA.DAT`, the retail file index;
- PACK archives (`.chr`, `.pcp`, `.ipk`, `.mpk`, `.sky`...), at any nesting depth;
- TIM2 pictures and IMG/IM2/IM3 texture archives;
- MDS scenes and MDT models, `.wgt` vertex weights, `.bbp` bind poses and `.mot` motions;
- configuration scripts (`info.cfg`, `.map`, `comdat.cfg`), in text and binary encodings.

## What is written

Paths keep the original folder layout. Every JSON document starts with
`"format"` and `"version"`.

| Original | Converted |
| --- | --- |
| Character pack `x.chr` | `x.glb` + `x.model.json` (`opencloud.model`, kind `character`) |
| Costume pack (clothes, shoes...) | `x.glb` + `x.model.json` (kind `costume`) |
| Model `x.mds`, model pack `x.pcp` | `x.glb` (+ `.model.json` when it has frames to describe) |
| Texture file `x.img`/`.tm2`/`.im2`/`.im3` | `x.textures.json` (`opencloud.textures`) + `x.<n>.png` |
| Map `dir/x.map` with `x.ipk`, `x.mpk`, `x.sky` | `dir/x.map.json` (`opencloud.map`), `dir/x.ipk/<img>.textures.json`, `dir/x.mpk/<list>/list.json` (`opencloud.model_list`) + one GLB per model, `dir/x.sky/sky.json` (`opencloud.sky`) |
| `menu/cfg7/comdat.cfg` | `menu/cfg7/comdat.items.json` (`opencloud.items`) |

### Models

Each model is one glTF 2.0 binary file:

- every MDS frame is a glTF node with the frame's local transform, named after the frame;
- a character's frames are also the joints of a skin that holds all of them, so the skeleton
  keeps its full hierarchy; skinned visuals get their own skin over the bones they use, with the
  `.bbp` bind pose as inverse bind matrices;
- materials are PBR with metallic 0 and roughness 1 (unlit for skies), double-sided when the game draws
  both faces, `BLEND`/`MASK` from the texture's alpha, PNG images embedded;
- every `KEY name, start, end, step` interval of a motion is one animation named
  `m<slot>_<clip>`, sampled on the 30 frames-per-second motion timeline.

The companion `.model.json` keeps what glTF has no place for: the original frame names, the
deform (`VERTEX_ANIME`) frames, the scene-space base matrix of every bone, the game name, step,
slot and key index of every animation, and the nodes the game hides.

### Textures

A `.textures.json` lists the pictures of one texture file in order, with their game names (as
materials refer to them), the PNG file, whether it has transparency, and its U/V clamp flags.

### Maps

The `.map.json` holds the map information (texture and model lists, lighting sets, time
settings, sky) and the layout (part definitions with their pieces, placements, function
points). Each model list folder holds a `list.json` with every entry's name, type (drawn,
collision, camera collision, character), far clip and the GLB that holds it; collision models
are plain triangle meshes. A sky's `sky.json` holds the sky information and, per time band, the
sky and back-sky GLBs.

Not converted yet: map `.cfg` (water, draw-off areas, extra event points), `.efp` effects,
sounds, movies, events and the remaining game tables. They are listed in the report.
