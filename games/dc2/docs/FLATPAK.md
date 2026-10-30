# Flatpak

The Linux release of the PC port (`docs/PC.md`) is a Flatpak,
`io.github.lunatic_wisdom_laboratories.OpenCloud`. It holds `opencloud` and `dc2data` on the
freedesktop 26.08 runtime, whose SDL3 and Vulkan loader it uses. No game data is in it: the first start asks for
your own PAL disc and extracts it.

## Installing the bundle

CI (`.github/workflows/pc.yml`) uploads `opencloud.flatpak` with every
build. The runtime comes from Flathub:

```sh
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user opencloud.flatpak
flatpak run io.github.lunatic_wisdom_laboratories.OpenCloud
```

It also appears in the desktop's application list as OpenCloud.

## The first start

With no current game data, the game opens OpenCloud's data setup window
(`docs/PC.md`, "Game data and first run"), which takes a Dark Chronicle disc
image or a folder holding `DATA.HD4`, `DATA.DAT`, `SOUND.HD3`, `SOUND.DAT` and
`MOVIE`. Inside the Flatpak its file selector is the desktop's file-chooser
portal, so the game reads the chosen file without any permission to your
files. It extracts and converts the disc into the data directory, then
continues into the game. Closing the window instead exits with status 3 and
prints the `dc2data` command below.

### From a shell

`dc2data` does the same from a script. It needs read access to the folder
holding the image, granted for that one command:

```sh
flatpak run --filesystem="$HOME/Games:ro" --command=dc2data io.github.lunatic_wisdom_laboratories.OpenCloud \
    extract "$HOME/Games/Dark Chronicle (PAL).iso" ~/.var/app/io.github.lunatic_wisdom_laboratories.OpenCloud/data/opencloud/dc2/data
```

## Where things live

Inside the sandbox `XDG_DATA_HOME` is `~/.var/app/io.github.lunatic_wisdom_laboratories.OpenCloud/data`,
so:

| | |
|---|---|
| game data | `~/.var/app/io.github.lunatic_wisdom_laboratories.OpenCloud/data/opencloud/dc2/data` and `port_data` beside it |
| saves and the pipeline cache | `~/.var/app/io.github.lunatic_wisdom_laboratories.OpenCloud/data/opencloud/dc2/saves` |

`config.json` and `logs/` live one level above `saves/`, in
`~/.var/app/io.github.lunatic_wisdom_laboratories.OpenCloud/data/opencloud/dc2`.

`config.json` is described in `docs/PC.md`. `flatpak uninstall --delete-data
io.github.lunatic_wisdom_laboratories.OpenCloud` removes both. The usual options still apply:
`flatpak run io.github.lunatic_wisdom_laboratories.OpenCloud --window 1920x1080`.

## Controls

Keyboard, mouse and gamepads as in `docs/PC.md`.

## Permissions

| finish-arg | why |
|---|---|
| `--socket=wayland` | the window, on Wayland |
| `--socket=fallback-x11` | the window on an X11 session; ignored when Wayland is there |
| `--share=ipc` | X11 shared memory, without which fallback-x11 is slow or broken |
| `--device=dri` | the GPU, for Vulkan and the data setup window's OpenGL |
| `--socket=pulseaudio` | sound, through PipeWire's or PulseAudio's PulseAudio socket |
| `--device=all` | gamepads: `/dev/input`, and `/dev/hidraw*` for those SDL reads through HIDAPI `--device=input` covers only `/dev/input` |

There is no network and no filesystem access. The disc reaches the game
through the file-chooser portal, which every Flatpak may call without a
`--talk-name`.

## Building

```sh
port/flatpak.sh
```

builds `opencloud.flatpak` in the repository root from the working tree,
installing the runtime, the SDK and the llvm22 extension from Flathub when
they are missing. `INSTALL=1` also installs the bundle for this user;
`CLEAN=1` discards flatpak-builder's cache first. By hand, the same is:

```sh
flatpak remote-add --user --if-not-exists flathub https://dl.flathub.org/repo/flathub.flatpakrepo
flatpak install --user flathub org.freedesktop.Platform//26.08 org.freedesktop.Sdk//26.08 \
    org.freedesktop.Sdk.Extension.llvm22//26.08
flatpak-builder --repo=repo build-dir port/flatpak/io.github.lunatic_wisdom_laboratories.OpenCloud.yml
flatpak build-bundle repo opencloud.flatpak io.github.lunatic_wisdom_laboratories.OpenCloud
```

(`flatpak-builder --install-deps-from=flathub ...` installs the three for
you.) Run from the repository root; `build-dir/`, `repo/`, `.flatpak-builder/`
and `opencloud.flatpak` are ignored by git. `flatpak-builder --user --install
build-dir port/flatpak/io.github.lunatic_wisdom_laboratories.OpenCloud.yml` installs the build
directly instead of through a bundle.

The manifest (`port/flatpak/io.github.lunatic_wisdom_laboratories.OpenCloud.yml`) builds the port
and what it needs for the build alone; SDL3 and the Vulkan headers come from the
SDK:

- **nlohmann/json**, the header from its release archive.
- **FFmpeg 9.0.2**, static format, codec, resampling and scaling libraries for
  MP4 playback, plus the FFmpeg command for lossless MPEG-2/ALAC movie extraction,
  configured by `../../engine/tools/ffmpeg/oc-ffmpeg` (LGPL only).
- **Rust 1.99.0**, the official archive OpenCloud's image installs, newer than
  the SDK's `rust-stable` extension.
- **cxxbridge 1.0.205**, cxx's code generator, built from the vendored crates;
  Corrosion uses it because its version is cxx's.
- **The port**, `PLATFORM=PC`, Release, with clang 22 from the
  `org.freedesktop.Sdk.Extension.llvm22` extension (the SDK's own compiler is
  GCC, and the port is built with clang everywhere) and OpenCloud's Rust data
  setup built by that Rust through Corrosion 0.6.1, the release `OpenCloudRust.cmake` pins. Shaders are
  compiled with the SDK's glslang, which targets `vulkan1.4`. The SDK's default
  compiler flags are replaced: they add `_GLIBCXX_ASSERTIONS`,
  `-Werror=format-security` and `-g`, none of which the game is built with
  elsewhere.

The build has no network, so every Rust crate in `../../engine/Cargo.lock` is
downloaded beforehand: `port/flatpak/cargo-sources.json` lists them, and
`port/flatpak/cargo_sources.py` writes it again from the lock, which it must be
whenever the OpenCloud pin changes the lock. The single-file decoders
`OpenCloudPort.cmake` would otherwise fetch are given through
`OC_PORT_VENDOR_DIRS`.

The app module's source is the working tree (`type: dir`); CI swaps it for a
`git` source at the commit under test. Vulkan validation layers are not
shipped. The bundle carries no Vulkan driver of its own: the runtime's Mesa
(`org.freedesktop.Platform.GL.default`) or the host's NVIDIA driver extension
provides it.
