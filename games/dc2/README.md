# Dark Cloud 2

Dark Chronicle / Dark Cloud 2 in OpenCloud: the game's decompiled code (`ps2/src`, `ps2/include`) and the
native port built from it (`port/`), against the shared engine in `../../engine`.

## Building and running

Place the PAL disc image `Dark Chronicle (PAL).iso` in `rom/pal/`; its data is extracted to `port/run/data`
the first time the game runs.

- Windows: `build.bat` builds the Release port into `port/build/windows-x64-release`; `build.bat windows-x64`
  builds Debug. `run.bat` builds, extracts the data and starts the game. The toolchain is downloaded to
  `.opencloud/windows` on first use. PowerShell 7 (`pwsh`) must be on PATH.
- Linux and macOS: `./build.sh` and `./run.sh` do the same with the CMake presets in `CMakePresets.json`
  (`linux-x64-release`, `macos-arm64-release` and their Debug variants), using the host's compilers and libraries.
- `port/flatpak.sh` packages the Linux port as a Flatpak; `docs/FLATPAK.md` covers it.
