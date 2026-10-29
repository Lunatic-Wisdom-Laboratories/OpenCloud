#!/usr/bin/env bash
# Builds and runs Dark Cloud 2 (games/dc2); the arguments are the same as games/dc2/run.sh.
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/games/dc2/run.sh" "$@"
