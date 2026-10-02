#!/bin/sh
set -eu
exec dotnet "$(cat /app/entrypoint-name)" --pid "${AINATIVE_PROCESS_ID:?required}" -m Release
