# OpenCloud

Native PC and mobile ports of the Dark Cloud games, built from their decompiled code.
Dark Cloud 2 (Dark Chronicle) is supported now; Dark Cloud 1 comes later.

## Layout

- `engine/` is shared by every game: the build driver, the CMake rules, the platform layer
  (graphics, audio, input) and the asset tools.
- `games/dc2/` is Dark Cloud 2: the decompiled PS2 code in `ps2/`, the native port in `port/`,
  and its scripts, tools and docs.
- `games/dc1/` will hold Dark Cloud 1.

## Building

Place the PAL disc image `Dark Chronicle (PAL).iso` in `games/dc2/rom/pal/`.

- Windows: `build.bat` builds the Release port and `run.bat` builds and runs it.
- Linux and macOS: `./build.sh` and `./run.sh`.

Both take the same arguments as the scripts in `games/dc2/`; see `games/dc2/README.md` and
`games/dc2/docs/PC.md` for the configurations.

## Disclaimer

OpenCloud is in no way affiliated with Level-5 or Sony Interactive Entertainment or their
registered trademarks. It does not include the games' art, models, sound or video: those are
extracted at first start from a disc image you own.
