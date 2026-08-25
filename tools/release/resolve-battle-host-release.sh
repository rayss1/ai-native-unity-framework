#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <repository-root> <semantic-version>" >&2
  exit 2
fi

repository_root="$(cd "$1" && pwd)"
version="$2"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
  echo "Version must be a semantic version without a leading v." >&2
  exit 2
fi

manifest="infrastructure/battle-host/releases/v$version.json"
"$repository_root/tools/release/verify-battle-host-release.sh" "$repository_root" "$manifest" >/dev/null
jq -er '.image' "$repository_root/$manifest"
