#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <repository-root> <release-manifest.json>" >&2
  exit 2
fi

repository_root="$(cd "$1" && pwd)"
manifest_path="$2"
if [[ "$manifest_path" != /* ]]; then
  manifest_path="$repository_root/$manifest_path"
fi
manifest_path="$(cd "$(dirname "$manifest_path")" && pwd)/$(basename "$manifest_path")"
ledger_dir="$(dirname "$manifest_path")"

for command_name in git jq; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "$command_name is required." >&2
    exit 2
  fi
done

if [[ ! -s "$manifest_path" ]]; then
  echo "Release manifest is missing or empty: $manifest_path" >&2
  exit 2
fi

hash_stream() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum | awk '{print $1}'
  else
    shasum -a 256 | awk '{print $1}'
  fi
}

version="$(jq -er '.version' "$manifest_path")"
source_commit="$(jq -er '.sourceCommit' "$manifest_path")"
source_tree="$(jq -er '.sourceTree' "$manifest_path")"
fantasy_commit="$(jq -er '.fantasyCommit' "$manifest_path")"
protocol_identity="$(jq -er '.protocolIdentity' "$manifest_path")"
config_identity="$(jq -er '.configSha256' "$manifest_path")"
image="$(jq -er '.image' "$manifest_path")"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
  echo "Release version is not a supported semantic version: $version" >&2
  exit 1
fi

if [[ "$(basename "$manifest_path")" != "v$version.json" ]]; then
  echo "Release manifest filename must be v$version.json." >&2
  exit 1
fi

if [[ ! "$source_commit" =~ ^[0-9a-f]{40}$ ||
      ! "$source_tree" =~ ^[0-9a-f]{40}$ ||
      ! "$fantasy_commit" =~ ^[0-9a-f]{40}$ ||
      ! "$protocol_identity" =~ ^[0-9a-f]{64}$ ||
      ! "$config_identity" =~ ^[0-9a-f]{64}$ ]]; then
  echo "Release source, tree, Fantasy, protocol, or configuration identity has an invalid shape." >&2
  exit 1
fi

if [[ ! "$image" =~ ^ghcr\.io/rayss1/ai-native-battle-host@sha256:[0-9a-f]{64}$ ]]; then
  echo "Release image must be the project GHCR image selected by immutable digest." >&2
  exit 1
fi

version_tag="ghcr.io/rayss1/ai-native-battle-host:v$version"
source_tag="ghcr.io/rayss1/ai-native-battle-host:sha-$source_commit"
jq -e --arg version_tag "$version_tag" --arg source_tag "$source_tag" '
  (.tags | type == "array" and length == 2)
  and (.tags | index($version_tag) != null)
  and (.tags | index($source_tag) != null)
  and (.sdkImage | test("^mcr\\.microsoft\\.com/dotnet/sdk:10\\.0\\.202-noble@sha256:[0-9a-f]{64}$"))
  and (.runtimeImage | test("^mcr\\.microsoft\\.com/dotnet/aspnet:10\\.0\\.[0-9]+-noble@sha256:[0-9a-f]{64}$"))
  and (.qualifiedRun | test("^https://github\\.com/rayss1/ai-native-unity-framework/actions/runs/[1-9][0-9]*$"))
  and (.qualificationProvenanceSha256 | test("^[0-9a-f]{64}$"))
  and (.qualificationSoakSha256 | test("^[0-9a-f]{64}$"))
  and (.version == "0.1.0" or (.qualificationTelemetryCapacitySha256 | test("^[0-9a-f]{64}$")))
  and (.attestationUrl | test("^https://github\\.com/rayss1/ai-native-unity-framework/attestations/[1-9][0-9]*$"))
  and (.publishedAtUtc | fromdateiso8601 | type == "number")
' "$manifest_path" >/dev/null

if ! git -C "$repository_root" cat-file -e "$source_commit^{commit}" 2>/dev/null; then
  echo "Release source commit is not present in repository history: $source_commit" >&2
  exit 1
fi

if ! git -C "$repository_root" merge-base --is-ancestor "$source_commit" HEAD; then
  echo "Release source commit is not an ancestor of the current checkout." >&2
  exit 1
fi

actual_tree="$(git -C "$repository_root" show -s --format=%T "$source_commit")"
actual_fantasy="$(git -C "$repository_root" ls-tree "$source_commit" -- server/vendor/Fantasy | awk '$1 == "160000" && $2 == "commit" {print $3}')"
actual_protocol="$(git -C "$repository_root" show "$source_commit:shared/schemas/ainative/v1/gameplay.proto" | hash_stream)"
actual_config="$(git -C "$repository_root" show "$source_commit:server/src/Hosts/AiNative.BattleHost/Fantasy.config" | hash_stream)"

if [[ "$actual_tree" != "$source_tree" ||
      "$actual_fantasy" != "$fantasy_commit" ||
      "$actual_protocol" != "$protocol_identity" ||
      "$actual_config" != "$config_identity" ]]; then
  echo "Release manifest does not match the recorded source tree, Fantasy gitlink, protocol, or configuration." >&2
  exit 1
fi

ledger_manifests=()
while IFS= read -r ledger_manifest; do
  ledger_manifests+=("$ledger_manifest")
done < <(find "$ledger_dir" -maxdepth 1 -type f -name 'v*.json' -print | sort)
if [[ ${#ledger_manifests[@]} -eq 0 ]]; then
  echo "Release ledger contains no manifests." >&2
  exit 1
fi

duplicate_check="$(jq -s '
  {
    versions: ([.[].version] | length == (unique | length)),
    images: ([.[].image] | length == (unique | length)),
    sources: ([.[].sourceCommit] | length == (unique | length)),
    tags: ([.[].tags[]] | length == (unique | length))
  }
  | all(.[]; . == true)
' "${ledger_manifests[@]}")"
if [[ "$duplicate_check" != "true" ]]; then
  echo "Release ledger reuses a version, digest, source commit, or tag." >&2
  exit 1
fi

echo "Battle Host release ledger verified: v$version"
echo "Image: $image"
echo "Source: $source_commit"
echo "Fantasy: $fantasy_commit"
