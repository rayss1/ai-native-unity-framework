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
  {name: "runtime-acceptance-soak", expired: false},
  {name: "runtime-telemetry-capacity", expired: false}
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
    roomCount: 1,
    botsPerRoom: 64,
    totalBotCapacity: 64,
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

jq -n \
  --arg source "$source_sha" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" \
  '{
    evidenceClass: "telemetry-capacity-comparison",
    roomCount: 1,
    botsPerRoom: 64,
    sourceCommit: $source,
    fantasyCommit: $fantasy,
    protocolIdentity: $protocol,
    botCount: 64,
    warmupSeconds: 10,
    measuredSeconds: 300,
    baseline: {
      processPeakWorkingSetBytes: 100000000
    },
    exporterOutage: {
      tickP99IncrementMilliseconds: 0.1,
      metricExportAttempts: 30,
      metricExportFailures: 30,
      traceExportAttempts: 30,
      traceExportFailures: 30,
      traceRecordsDropped: 0,
      projectMetricSeries: 7,
      projectMetricSeriesLimit: 16,
      projectMetricTagViolations: 0,
      projectMetricSeriesOverflow: 0,
      processPeakWorkingSetBytes: 110000000
    },
    gates: {
      tickP99IncrementLimitMilliseconds: 0.25,
      boundedTraceQueue: true,
      taglessProjectMetrics: true,
      passed: true
    }
  }' > "$fixture_dir/telemetry-capacity.json"

verify() {
  "$verifier" \
    "$repository_root" \
    "$source_sha" \
    "$1" \
    "$2" \
    "$3" \
    "$4" \
    "$5"
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
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.event = "workflow_dispatch"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-dispatch.json"
verify \
  "$fixture_dir/run-dispatch.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.head_sha = "ffffffffffffffffffffffffffffffffffffffff"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-wrong-sha.json"
expect_failure wrong-source \
  "$fixture_dir/run-wrong-sha.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.event = "pull_request"' \
  "$fixture_dir/run.json" > "$fixture_dir/run-pull-request.json"
expect_failure pull-request-run \
  "$fixture_dir/run-pull-request.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '(.artifacts[] | select(.name == "runtime-acceptance-soak") | .expired) = true' \
  "$fixture_dir/artifacts.json" > "$fixture_dir/artifacts-expired.json"
expect_failure expired-soak \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts-expired.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.tickP99Milliseconds = 16.68' \
  "$fixture_dir/soak-host.json" > "$fixture_dir/soak-over-budget.json"
expect_failure over-budget \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-over-budget.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.roomCount = 2 | .totalBotCapacity = 128' \
  "$fixture_dir/soak-host.json" > "$fixture_dir/soak-room-drift.json"
expect_failure room-drift \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-room-drift.json" \
  "$fixture_dir/telemetry-capacity.json"

jq '.exporterOutage.tickP99IncrementMilliseconds = 0.25' \
  "$fixture_dir/telemetry-capacity.json" > "$fixture_dir/telemetry-at-limit.json"
expect_failure telemetry-at-limit \
  "$fixture_dir/run.json" \
  "$fixture_dir/artifacts.json" \
  "$fixture_dir/provenance.json" \
  "$fixture_dir/soak-host.json" \
  "$fixture_dir/telemetry-at-limit.json"

echo "Battle Host qualification contract tests passed."
