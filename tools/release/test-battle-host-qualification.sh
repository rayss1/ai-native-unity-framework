#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
source_sha="$(git -C "$repository_root" rev-parse HEAD)"
fantasy_commit="$(git -C "$repository_root/server/vendor/Fantasy" rev-parse HEAD)"
if command -v sha256sum >/dev/null 2>&1; then
  protocol_identity="$(sha256sum "$repository_root/shared/schemas/ainative/v1/gameplay.proto" | cut -d ' ' -f 1)"
else
  protocol_identity="$(shasum -a 256 "$repository_root/shared/schemas/ainative/v1/gameplay.proto" | awk '{print $1}')"
fi
verifier="$repository_root/tools/release/verify-battle-host-qualification.sh"
fixture_dir="$(mktemp -d)"
cleanup() { rm -rf "$fixture_dir"; }
trap cleanup EXIT

jq -n --arg source "$source_sha" '{
  name: "Battle Host production validation",
  path: ".github/workflows/runtime-acceptance.yml",
  event: "push",
  head_branch: "main",
  head_sha: $source,
  status: "completed",
  conclusion: "success"
}' > "$fixture_dir/run.json"

jq -n '{artifacts: [
  {name: "runtime-acceptance-provenance", expired: false},
  {name: "runtime-acceptance-soak", expired: false}
]}' > "$fixture_dir/artifacts.json"

jq -n \
  --arg source "$source_sha" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" \
  '{
    parentCommit: $source,
    fantasyCommit: $fantasy,
    protocolSha256: $protocol,
    sdkImage: "mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:0000000000000000000000000000000000000000000000000000000000000000",
    runtimeImage: "mcr.microsoft.com/dotnet/aspnet:10.0.4-noble@sha256:1111111111111111111111111111111111111111111111111111111111111111",
    productImageId: "sha256:2222222222222222222222222222222222222222222222222222222222222222",
    outerKcpMtu: 1150
  }' > "$fixture_dir/provenance.json"

jq -n \
  --arg source "$source_sha" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" \
  '{
    evidenceClass: "release-equivalent-host-core",
    sourceCommit: $source,
    fantasyCommit: $fantasy,
    protocolIdentity: $protocol,
    sampleCount: 216000,
    elapsedSeconds: 3610,
    tickP99Milliseconds: 1,
    tickP999Milliseconds: 2,
    slowTickPercentage: 0,
    gameplayP99Milliseconds: 0.1,
    gameplayAllocatedBytes: 0,
    outerKcpMtu: 1150
  }' > "$fixture_dir/soak-host.json"

verify() {
  "$verifier" \
    "$repository_root" \
    "$source_sha" \
    "$1" \
    "$2" \
    "$3" \
    "$4"
}

expect_failure() {
  case_name="$1"
  shift
  if verify "$@" > "$fixture_dir/$case_name.log" 2>&1; then
    echo "Qualification negative case unexpectedly passed: $case_name" >&2
    exit 1
  fi
}

verify \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json"

jq '.event = "workflow_dispatch"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-dispatch.json"
verify \
  "$fixture_dir/run-dispatch.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json"

jq '.head_sha = "ffffffffffffffffffffffffffffffffffffffff"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-wrong-sha.json"
expect_failure wrong-source \
  "$fixture_dir/run-wrong-sha.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json"

jq '.event = "pull_request"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-pull-request.json"
expect_failure pull-request-run \
  "$fixture_dir/run-pull-request.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json"

jq '(.artifacts[] | select(.name == "runtime-acceptance-soak") | .expired) = true' \
  "$fixture_dir/artifacts.json" > "$fixture_dir/artifacts-expired.json"
expect_failure expired-soak \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts-expired.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json"

jq '.tickP99Milliseconds = 16.68' \
  "$fixture_dir/soak-host.json" > "$fixture_dir/soak-over-budget.json"
expect_failure over-budget \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-over-budget.json"

echo "Battle Host qualification contract tests passed."
