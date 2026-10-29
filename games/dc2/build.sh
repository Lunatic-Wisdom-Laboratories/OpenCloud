#!/usr/bin/env bash
# Builds the port with CMake on Linux or macOS: ./build.sh [preset] [cmake --build arguments].
# The preset is linux-x64-release or macos-arm64-release by default; linux-x64 and macos-arm64 are the Debug builds.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
cd "$root"
case $(uname -s) in
    Darwin) preset=macos-arm64-release ;;
    *) preset=linux-x64-release ;;
esac
case ${1:-} in
    linux-x64|linux-x64-release|macos-arm64|macos-arm64-release) preset=$1; shift ;;
esac
# The macOS builds link the minimal LGPL FFmpeg built on the host; oc-ffmpeg skips itself when its stamp is current.
case $preset in
    macos-*) "$root/../../engine/scripts/build/oc-ffmpeg" --out .opencloud/ffmpeg/darwin-arm64 ;;
esac
cmake --preset "$preset"
cmake --build --preset "$preset" "$@"
