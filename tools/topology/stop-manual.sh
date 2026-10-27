#!/bin/sh
set -eu

# Resolve the entry relative to this wrapper and preserve every caller argument.
exec python3 "$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/manual.py" stop "$@"
