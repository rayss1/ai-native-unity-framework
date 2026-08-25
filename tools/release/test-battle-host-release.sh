#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
verifier="$repository_root/tools/release/verify-battle-host-release.sh"
resolver="$repository_root/tools/release/resolve-battle-host-release.sh"
published="$repository_root/infrastructure/battle-host/releases/v0.1.0.json"
fixture_dir="$(mktemp -d)"
cleanup() { rm -rf "$fixture_dir"; }
trap cleanup EXIT

expect_failure() {
  case_name="$1"
  manifest="$2"
  if "$verifier" "$repository_root" "$manifest" > "$fixture_dir/$case_name.log" 2>&1; then
    echo "Release-ledger negative case unexpectedly passed: $case_name" >&2
    exit 1
  fi
}

"$verifier" "$repository_root" "$published"
resolved="$($resolver "$repository_root" 0.1.0)"
expected="$(jq -er '.image' "$published")"
test "$resolved" = "$expected"

mkdir -p "$fixture_dir/wrong-tree" "$fixture_dir/wrong-fantasy" "$fixture_dir/wrong-protocol" \
  "$fixture_dir/wrong-config" "$fixture_dir/mutable-image" "$fixture_dir/wrong-tags" "$fixture_dir/duplicate"

jq '.sourceTree = "0000000000000000000000000000000000000000"' "$published" > "$fixture_dir/wrong-tree/v0.1.0.json"
expect_failure wrong-tree "$fixture_dir/wrong-tree/v0.1.0.json"

jq '.fantasyCommit = "0000000000000000000000000000000000000000"' "$published" > "$fixture_dir/wrong-fantasy/v0.1.0.json"
expect_failure wrong-fantasy "$fixture_dir/wrong-fantasy/v0.1.0.json"

jq '.protocolIdentity = "0000000000000000000000000000000000000000000000000000000000000000"' "$published" > "$fixture_dir/wrong-protocol/v0.1.0.json"
expect_failure wrong-protocol "$fixture_dir/wrong-protocol/v0.1.0.json"

jq '.configSha256 = "0000000000000000000000000000000000000000000000000000000000000000"' "$published" > "$fixture_dir/wrong-config/v0.1.0.json"
expect_failure wrong-config "$fixture_dir/wrong-config/v0.1.0.json"

jq '.image = "ghcr.io/rayss1/ai-native-battle-host:v0.1.0"' "$published" > "$fixture_dir/mutable-image/v0.1.0.json"
expect_failure mutable-image "$fixture_dir/mutable-image/v0.1.0.json"

jq '.tags[1] = "ghcr.io/rayss1/ai-native-battle-host:latest"' "$published" > "$fixture_dir/wrong-tags/v0.1.0.json"
expect_failure wrong-tags "$fixture_dir/wrong-tags/v0.1.0.json"

cp "$published" "$fixture_dir/duplicate/v0.1.0.json"
jq '
  .version = "0.1.1"
  | .tags[0] = "ghcr.io/rayss1/ai-native-battle-host:v0.1.1"
' "$published" > "$fixture_dir/duplicate/v0.1.1.json"
expect_failure duplicate-source "$fixture_dir/duplicate/v0.1.1.json"

if "$resolver" "$repository_root" 9.9.9 > "$fixture_dir/missing-version.log" 2>&1; then
  echo "Unknown release version unexpectedly resolved." >&2
  exit 1
fi

echo "Battle Host release-ledger contract tests passed."
