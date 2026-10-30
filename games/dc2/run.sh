#!/usr/bin/env bash
# Builds and runs the port on Linux or macOS: ./run.sh [preset] [game arguments].
# The game data is extracted to port/run/data from the PAL disc image in rom/pal/ when it is missing or stale.
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
case $preset in
    linux-x64) build=port/build/pc ;;
    linux-x64-release) build=port/build/pc-release ;;
    *) build=port/build/$preset ;;
esac
"$root/build.sh" "$preset" --target opencloud dc2data

# The retail image by name, else any disc image in rom/pal, else an extracted disc.
source="rom/pal/Dark Chronicle (PAL).iso"
images=(rom/pal/*.iso)
[ -e "$source" ] || source=${images[0]}
[ -e "$source" ] || source=rom/pal/extracted/iso
data=port/run/data
if [ -e "$source" ]; then
    "$build/dc2data" current "$source" "$data" || "$build/dc2data" extract "$source" "$data"
elif [ -f port/run/data.zip ]; then
    "$build/dc2data" check "$data"
else
    echo "run.sh: no PAL disc found in rom/pal; the game starts without data and its file loads fail" >&2
fi

# Script runs log at debug to the console (docs/LOGGING.md); a DC2_LOG of the user's own still wins.
export DC2_LOG="${DC2_LOG:-debug}"
exec "$build/opencloud" --run-dir "$root/port/run" "$@"
