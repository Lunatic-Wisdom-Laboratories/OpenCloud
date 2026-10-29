#!/usr/bin/env bash
# Builds Dark Cloud 2 (games/dc2); the arguments are the same as games/dc2/build.sh.
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/games/dc2/build.sh" "$@"
