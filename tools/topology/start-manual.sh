#!/bin/sh
set -eu

# Replace the wrapper so the Python supervisor receives terminal signals directly.
exec python3 "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/manual.py" start "$@"
