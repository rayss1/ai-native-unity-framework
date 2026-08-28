#!/usr/bin/env bash
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
editor_path="${UNITY_EDITOR_PATH:-/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity}"

if [[ ! -x "$editor_path" ]]; then
  echo "Unity Editor was not found at: $editor_path" >&2
  echo "Set UNITY_EDITOR_PATH to the Unity 6000.3.9f1 executable." >&2
  exit 2
fi

if [[ -n "$(git -C "$repo_root" status --porcelain --untracked-files=all)" ]]; then
  echo "The worktree is dirty. Commit, stash, or remove changes so the evidence identifies an exact revision." >&2
  exit 2
fi

editor_version="$("$editor_path" -version 2>/dev/null | tail -n 1 | tr -d '\r')"
if [[ "$editor_version" != "6000.3.9f1" ]]; then
  echo "Expected Unity 6000.3.9f1, found: $editor_version" >&2
  exit 2
fi

editor_app="${editor_path%/Contents/MacOS/Unity}"
if [[ -f "$editor_app/Contents/Info.plist" ]]; then
  editor_revision="$(plutil -extract UnityBuildNumber raw "$editor_app/Contents/Info.plist")"
  if [[ "$editor_revision" != "7a9955a4f2fa" ]]; then
    echo "Expected Unity revision 7a9955a4f2fa, found: $editor_revision" >&2
    exit 2
  fi
else
  editor_revision="unavailable"
fi

commit="$(git -C "$repo_root" rev-parse HEAD)"
evidence_dir="${1:-$repo_root/artifacts/unity-manual/$commit}"
mkdir -p "$evidence_dir"

{
  echo "commit=$commit"
  echo "validated_at_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "host=$(uname -a)"
  echo "editor_version=$editor_version"
  echo "editor_revision=$editor_revision"
  sed -n '1,2p' "$repo_root/client/UnityProject/ProjectSettings/ProjectVersion.txt"
  echo "manifest_sha256=$(shasum -a 256 "$repo_root/client/UnityProject/Packages/manifest.json" | awk '{print $1}')"
  echo "packages_lock_sha256=$(shasum -a 256 "$repo_root/client/UnityProject/Packages/packages-lock.json" | awk '{print $1}')"
  echo "client_prediction_package_sha256=$(shasum -a 256 "$repo_root/packages/com.ainative.client.prediction/package.json" | awk '{print $1}')"
  echo "acceptance_vector_sha256=$(shasum -a 256 "$repo_root/shared/test-vectors/acceptance-64-bot-v1.json" | awk '{print $1}')"
  git -C "$repo_root" submodule status --recursive
} > "$evidence_dir/metadata.txt"

set +e
"$editor_path" \
  -batchmode \
  -nographics \
  -projectPath "$repo_root/client/UnityProject" \
  -runTests \
  -testPlatform EditMode \
  -testResults "$evidence_dir/editmode.xml" \
  -logFile "$evidence_dir/editmode.log"
unity_exit=$?
set -e

if [[ $unity_exit -ne 0 ]]; then
  echo "Unity exited with code $unity_exit. Inspect $evidence_dir/editmode.log" >&2
  exit "$unity_exit"
fi

if [[ ! -s "$evidence_dir/editmode.xml" ]]; then
  echo "Unity did not produce editmode.xml." >&2
  exit 1
fi

if ! command -v xmllint >/dev/null 2>&1; then
  echo "xmllint is required to verify the NUnit result." >&2
  exit 2
fi

passed="$(xmllint --xpath 'string(/test-run/@passed)' "$evidence_dir/editmode.xml")"
failed="$(xmllint --xpath 'string(/test-run/@failed)' "$evidence_dir/editmode.xml")"
skipped="$(xmllint --xpath 'string(/test-run/@skipped)' "$evidence_dir/editmode.xml")"
result="$(xmllint --xpath 'string(/test-run/@result)' "$evidence_dir/editmode.xml")"

{
  echo "result=$result"
  echo "passed=$passed"
  echo "failed=$failed"
  echo "skipped=$skipped"
} | tee "$evidence_dir/summary.txt"

if [[ "$result" != "Passed" || "$passed" != "22" || "$failed" != "0" || "$skipped" != "0" ]]; then
  echo "Expected exactly 22 passed, 0 failed, and 0 skipped EditMode tests." >&2
  exit 1
fi

echo "Manual Unity validation passed. Evidence: $evidence_dir"
