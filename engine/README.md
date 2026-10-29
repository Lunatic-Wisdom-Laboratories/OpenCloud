# OpenCloud engine

The engine every OpenCloud game builds against: the Vulkan renderer, audio, input and platform layer, the
PS2 SDK replacement the decompiled games call into, the `port.h` macros, and the Rust data publisher that
extracts and converts a game's data from its disc image. A game (for example `games/dc2`) links it through the
CMake modules in `cmake/`.

## Layout

```
src/cpp/             C++ graphics, audio, platform, SDK and test sources
src/rust/            Rust data publisher sources (.rs only)
cmake/               CMake modules a game includes (OpenCloudPort, OpenCloudRust, OpenCloudFfmpeg, ...)
Cargo.toml           Rust package manifest
Cargo.lock           Locked Rust dependencies
include/             C++ headers and test fixtures
scripts/             Asset tools and the PORT_OVERRIDE helper (scripts/build/port_override.py)
tools/ffmpeg/        FFmpeg build tool
tests/               Python suites and CMake checks
```

## FFmpeg

OpenCloud ships no FFmpeg code. `tools/ffmpeg/oc-ffmpeg` (launcher `scripts/build/oc-ffmpeg`) builds FFmpeg 9.0.2 from the pinned ffmpeg.org tarball, checked against its SHA-256, with only the `mpeg2video` decoder and the `mpegvideo` parser:

```
--disable-everything --disable-programs --disable-doc --disable-network --disable-autodetect
--disable-avformat --disable-avfilter --disable-swscale --disable-swresample --disable-avdevice
--enable-decoder=mpeg2video --enable-parser=mpegvideo --enable-pic --disable-shared --enable-static
```

`--disable-x86asm` is added when `nasm` is absent. The build is LGPL: no `--enable-gpl`, `--enable-nonfree` or `--enable-version3`. It refuses to continue unless configure reports `License: LGPL version 2.1 or later`.

`oc-ffmpeg --out DIR [--jobs N]` installs `DIR/include/{libavcodec,libavutil}`, `DIR/lib/libavcodec.a`, `DIR/lib/libavutil.a`, `DIR/configure.log` and the stamp `DIR/oc-ffmpeg.json`. `--check` tests the stamp; an up-to-date `DIR` is skipped.

Per platform:

- Linux: build it with `scripts/build/oc-ffmpeg --out <game>/.opencloud/ffmpeg/linux-x64`, or point `OC_FFMPEG_DIR` at an LGPL FFmpeg tree.
- macOS: the game's `build.sh` runs `scripts/build/oc-ffmpeg --out .opencloud/ffmpeg/darwin-arm64`. It skips itself when the stamp is current.
- Windows: there is no POSIX configure. `tools/windows/acquire.ps1` downloads BtbN's `win64-lgpl-shared` build of the 7.1 release from a dated `autobuild-*` tag, checked against a pinned SHA-256, and the project passes `-DOC_FFMPEG_DIR`. `avcodec-61.dll`, `avutil-59.dll` and `swresample-5.dll` (which BtbN's `avcodec-61.dll` imports) ship beside the executable. Dynamic linking keeps LGPL relinking trivial; the static links on Linux and macOS are relinkable from the public sources.

CMake, from the project's `CMakeLists.txt`:

```cmake
include(${OC_ROOT}/cmake/OpenCloudFfmpeg.cmake)
oc_ffmpeg(<target> [PUBLIC|PRIVATE|INTERFACE])     # default PUBLIC
```

- `OC_FFMPEG_DIR` (cache, PATH): `$ENV{OC_FFMPEG_DIR}` when set, otherwise `.opencloud/ffmpeg/<os>-<arch>` under `CMAKE_SOURCE_DIR`, with `<os>` `linux`, `darwin` or `windows` and `<arch>` `x64` or `arm64`. Configure fails when `include/libavcodec/avcodec.h` is absent.
- Targets: `oc::ffmpeg` (interface), `oc::avcodec` and `oc::avutil` (static on Linux and macOS, shared on Windows).
- `OC_FFMPEG_RUNTIME_DLLS` (global property): on Windows, the DLL paths, for the consumer to copy beside its executable.

`OpenCloudFfmpeg.cmake` is not included by `CMakeLists.txt`. A game that does not call `oc_ffmpeg` links nothing of FFmpeg.

## port.h

`include/opencloud/port.h` lets one source tree serve the PS2 build and the port. The port defines `PORT`.

```c
#define PORT_INIT(...)    // PORT: = __VA_ARGS__      otherwise: nothing
#define PORT_RETURN(...)  // PORT: return __VA_ARGS__ otherwise: nothing
#define PORT_FIX(...)     // PORT: __VA_ARGS__        otherwise: nothing
#define PORT_OVERRIDE     // empty marker, read by scripts/build/port_override.py
```

Usage:

```c
#include "opencloud/port.h"

int count PORT_INIT(0);                       /* initialiser only in the port */
Vec origin PORT_INIT({0, 0, 0});              /* commas are fine inside the macro */
int Lookup(int i) { PORT_RETURN(table[i]); }  /* final return the PS2 code does not have */
PORT_FIX(if (index >= count) return 0;)       /* port-only code */
PORT_OVERRIDE void MGClearScreen(u_char r) { /* replaces the ps2/src definition */ }
```

Do not wrap `PORT_RETURN` in a PS2-visible `if`. The PS2 build then compiles that code. Under MWCC the expansions are empty apart from the stray `;` that `PORT_RETURN(...);` and `PORT_FIX(...);` leave. `tests/port_h/run.sh MWCC_EXE` checks that the macro uses compile to the same code as their hand-written PS2 text, and that a PORT build takes the values the macros give.

`#include "opencloud/port.h"` resolves through an `-i` directory: `-i <root>/tools/opencloud/include`. Angle-bracket includes do not search `-i` directories, so they need `MWCIncludes`.

## Shared port code

`src/cpp/` is the host code that every PC port shares. Its include root is `include/`: consumers write `#include "gfx/gfx.hpp"` and `#include "platform/clock.hpp"`.

| Directory | Contents |
|---|---|
| `gfx/` | Vulkan renderer: display lists, pipelines, readback, `shaders/` (compiled to headers by the consumer) |
| `audio/` | PCM WAV banks, mixer, reverb, versioned JSON musical/effect sequences, WAV/FLAC/Vorbis decoding |
| `platform/` | Host services: window, input, display, clock, files, paths, settings, glyphs, overlay, TrueType font, memory, audio output, image files |
| `platform/arena` | Per-owner arena blocks over `platform/memory`: zeroed, 64-byte aligned, 4x headroom, a guard page after each, named overflow aborts, a debug check that a block lies above 4 GiB, `WidenNear` and `PortImagePointer` for truncated pointers |
| `sce/` | PS2 SDK replacements (eekernel, eekernel_threads, libcdvd, libgraph, libmc, libpad, libvu0, sifdev, sifrpc). `eekernel_threads` runs kernel threads and semaphores as cooperative fibers on the main OS thread, and a switch inside `ClockPump` aborts; `sce/unimplemented.cpp` defines `Ps2Unimplemented`, behind `PS2_UNIMPLEMENTED()` in `opencloud/port.h` |
| `sce/stubs/` | Stubs for libdev, libdma, libgraph, libmpeg, libpkt, libsdr, modmsin, sifdma |
| `tests/` | GoogleTest cases and fixtures, compiled into each game's test executable |

- `gfx/`, `audio/` and `platform/` never include a game header and are built without the game's `port.h`.
- `sce/` is compiled by each consumer with its own `ps2/include/sce` on the include path and its `port.h` force-included. The SDK family selects which `sce` files are built.
- The game provides:
  - `SettingsGet()` and `SettingsChange(const HostSettings &)`, the definitions behind `settings.hpp`. The game calls `SettingsSetEnvPrefix` to name its environment variables (`<prefix>_<name>`).
  - `PathsConfigure(PathsConfig)`: the per-user directory name and the data and save environment variables.
  - `WindowSetIdentity()`: the window title and icon.
  - `gfx::RendererConfig`: `app_name`, `logical_width` and `logical_height`, and `display_aspect`.
- Shared tests run after the game's `main()` has set its identity. They must not assume a particular game.
- On Android the window is fixed fullscreen in landscape (`WindowSetMode` changes nothing), and the game draws its own on-screen gamepad: it passes the gamepad's state to `InputSetTouchOverlay`, which pad 0 takes while `InputTouchOverlayVisible()`, true after an `InputPoll` that found no gamepad, keyboard or mouse in use.
- Behaviour change (main after f1b8e80): the flanks that carry a full-frame `Draw2D` rectangle to the edge of a window wider or taller than the logical frame always sample with clamped wrap (`gfx/README.md`, "Fill"). A full-frame draw of a repeating texture used to tile into the flanks and now repeats its edge texels there, which removes the seam at the frame's border. Output at the logical aspect is unchanged; only non-4:3 windows differ, and only for such draws.

### Diagnostics

The host code reports through `opencloud/log.hpp` (`platform/log.cpp`). Only `PathsConsumeArgs`' usage errors (`--data needs a directory`), printed before a host can install a sink, go straight to stderr.

- `oc::Log(level, subsystem, message)` and `oc::Logf(level, subsystem, format, args...)` (`std::format`) send one line to the sink installed with `oc::SetLogSink`. Without a sink each line goes to stderr as `<subsystem>: <message>`, as one write. The sink is an atomic function pointer, so `Log` takes no lock and may be called from any thread, the audio callback and pipeline workers included; a sink must be safe the same way and must not log back into OpenCloud. `nullptr` restores stderr.
- `oc::Fatal(subsystem, message, unimplemented)` logs the line at `oc::LogLevel::Fatal`, flushes stderr, and calls the hook installed with `oc::SetFatalHook`, once per process. The hook normally writes a crash report and does not return; if it returns, the caller's own exit follows. `oc::Abort(subsystem, message)` is `Fatal` with `unimplemented` false, then `std::abort()`.
- `Ps2Unimplemented` calls `Fatal` with `unimplemented` true, the call's `<file>:<line>` as the subsystem and `<function>: PlayStation 2 code is not implemented on PC` as the message, then aborts. `gfx::Fatal` (and so `gfx::Check`) and the window's SDL failures call `Fatal` and exit with status 1. Arena overflows (subsystem `arena <name>`), failed arena mappings, fiber creation failures and `eekernel` deadlocks call `Abort`.
- `<prefix>_AUDIO_TRACE` lines (`audio::Trace`) go through `Log` at `Debug`, formatted into a stack buffer, so a trace line costs no allocation on the audio callback.

CMake entry points are in `cmake/OpenCloudPort.cmake`:

| Name | Purpose |
|---|---|
| `oc_port_host_library(<target>)` | Builds `gfx/`, `audio/` and `platform/` as one host library |
| `oc_port_sce_sources(<out-var> LIBS <family>...)` | Sets `<out-var>` to the `sce/` sources for the SDK families named |
| `oc_port_tests(<target>)` | Builds the shared tests into the consumer's test executable |
| `OC_PORT_VENDOR_DIRS` | Include directories of the vendored single-header libraries (stb, dr_libs) |

## First-run data setup

The Rust `opencloud_core` library exports `setup_data(title, data_root)` through its
`cxx` bridge. Call it on the main thread before creating the game's window, only
when the game's data-version check reports missing or outdated data. It returns
`true` after successful extraction/publication and `false` if the user closes
setup before starting. Window initialization failures cross the bridge as errors.
The game should recheck its data marker before continuing startup.

Setup provides an editable source path, **Choose image**, **Choose folder**, a
progress bar and **Okay**. The source can be a disc folder or an ISO image; a folder
holding no `DATA.DAT` and a single `.iso` file stands for that image. The pickers are native
on Windows and macOS, and uses the XDG desktop portal on Linux; Linux installations
need a portal backend for their desktop. The setup window uses eframe/egui with
OpenGL and supports X11 and Wayland. Linux build dependencies are included in the
toolchain image (`pkg-config`, X11, Wayland, xkbcommon and EGL development files).

Extraction runs on a worker thread. `extract_with_progress` reports planned work
for the ISO read, asset planning, concurrent texture, model, audio, movie, script
and retained-asset conversion, ZIP streaming, final archive verification and
transactional publication. Model publication has the largest progress share because
it determines the conversion tail; the ETA is withheld when critical model work
stops advancing. Percentages measure completed work, not estimated elapsed time.
Concurrent callbacks are coalesced monotonically by the window. Only successful
publication reaches 100%; then setup closes and
returns to the caller automatically. Errors allow retry. Closing is disabled
while extraction is active so publishing is never abandoned mid-operation.

Run `cargo test --locked` and `cargo clippy --all-targets --locked -- -D warnings`
from this directory to check the Rust library. Retail-data tests are opt-in.

Consumers can include `cmake/OpenCloudRust.cmake` to import `opencloud_core` and
`opencloud_bridge`, including the native GUI system libraries. On Windows, after
initializing FFmpeg, `cmake/OpenCloudWindowsRuntime.cmake` provides
`oc_windows_runtime(target)` to copy the SDL, FFmpeg, compiler/Rust and Vulkan
runtime DLLs beside each executable on every build. `INSTALL` also installs those
DLLs and emits a runtime package list; `EXECUTABLES` adds named executable targets
to that list. The Vulkan loader download
is pinned and hash-checked when `OC_VULKAN_RUNTIME_DIR` is not supplied. These
rules copy program dependencies only; they never include retail game assets.

## Models and actions

`src/rust/models.rs` converts the game's models, skeletons and motions into glTF with
gameplay sidecars, and `src/rust/actions.rs` names their actions. Every CFG `KEY`
interval becomes one glTF animation named in English from
`assets/motion_names.json`; conversion fails when a motion name has no entry. A
motion that only drives other bundles' models is published into the models the
game's scripts and code load it onto, never as an animation-only glTF; the scan of
compiled scripts is in `src/rust/native_scripts.rs`. Each glTF keeps its binary data
in one `<file>.gltf.bin` beside it. Only ChronicleTwo uses these
modules; Chronicle's pinned revision does not contain the Rust publisher. Changes
to the published format bump `data_version.json`.
