#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 7 ]]; then
  echo "Usage: $0 <repository-root> <expected-source-sha> <run.json> <artifacts.json> <provenance.json> <soak-host.json> <telemetry-capacity.json>" >&2
  exit 2
fi

repository_root="$(cd "$1" && pwd)"
expected_source_sha="$2"
run_json="$3"
artifacts_json="$4"
provenance_json="$5"
soak_host_json="$6"
telemetry_capacity_json="$7"

for command_name in git jq; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "$command_name is required." >&2
    exit 2
  fi
done

for input_file in "$run_json" "$artifacts_json" "$provenance_json" "$soak_host_json" "$telemetry_capacity_json"; do
  if [[ ! -s "$input_file" ]]; then
    echo "Required qualification input is missing or empty: $input_file" >&2
    exit 2
  fi
done

if [[ ! "$expected_source_sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Expected source SHA must be a full lowercase 40-character commit ID." >&2
  exit 2
fi

actual_source_sha="$(git -C "$repository_root" rev-parse HEAD)"
if [[ "$actual_source_sha" != "$expected_source_sha" ]]; then
  echo "Checkout $actual_source_sha does not match expected source $expected_source_sha." >&2
  exit 1
fi

submodule_status="$(git -C "$repository_root" submodule status --recursive)"
if grep -Eq '^[-+U]' <<<"$submodule_status"; then
  echo "A submodule is uninitialized, conflicted, or differs from its gitlink." >&2
  exit 1
fi

fantasy_commit="$(git -C "$repository_root/server/vendor/Fantasy" rev-parse HEAD)"
if command -v sha256sum >/dev/null 2>&1; then
  protocol_identity="$(sha256sum "$repository_root/shared/schemas/ainative/v1/gameplay.proto" | cut -d ' ' -f 1)"
else
  protocol_identity="$(shasum -a 256 "$repository_root/shared/schemas/ainative/v1/gameplay.proto" | awk '{print $1}')"
fi

jq -e --arg source "$expected_source_sha" '
  .name == "Battle Host production validation"
  and .path == ".github/workflows/runtime-acceptance.yml"
  and (.event == "push" or .event == "workflow_dispatch")
  and .head_branch == "main"
  and .head_sha == $source
  and .status == "completed"
  and .conclusion == "success"
' "$run_json" >/dev/null

jq -e '
  ["runtime-acceptance-provenance", "runtime-acceptance-soak", "runtime-telemetry-capacity"] as $required
  | ([.artifacts[] | select(.expired == false) | .name] | unique) as $available
  | all($required[]; . as $name | $available | index($name) != null)
' "$artifacts_json" >/dev/null

jq -e --arg source "$expected_source_sha" --arg fantasy "$fantasy_commit" --arg protocol "$protocol_identity" '
  .parentCommit == $source
  and .fantasyCommit == $fantasy
  and .protocolSha256 == $protocol
  and (.sdkImage | test("^mcr\\.microsoft\\.com/dotnet/sdk:10\\.0\\.202-noble@sha256:[0-9a-f]{64}$"))
  and (.runtimeImage | test("^mcr\\.microsoft\\.com/dotnet/aspnet:10\\.0\\.[0-9]+-noble@sha256:[0-9a-f]{64}$"))
  and (.productImageId | test("^sha256:[0-9a-f]{64}$"))
  and .outerKcpMtu == 1150
' "$provenance_json" >/dev/null

jq -e --arg source "$expected_source_sha" --arg fantasy "$fantasy_commit" --arg protocol "$protocol_identity" '
  .evidenceClass == "release-equivalent-host-core"
  and .sourceCommit == $source
  and .fantasyCommit == $fantasy
  and .protocolIdentity == $protocol
  and .sampleCount >= 215900
  and .elapsedSeconds >= 3610
  and .tickP99Milliseconds <= 16.67
  and .tickP999Milliseconds <= 20
  and .slowTickPercentage <= 0.1
  and .gameplayP99Milliseconds <= 8
  and .gameplayAllocatedBytes == 0
  and .outerKcpMtu == 1150
' "$soak_host_json" >/dev/null

jq -e --arg source "$expected_source_sha" --arg fantasy "$fantasy_commit" --arg protocol "$protocol_identity" '
  .evidenceClass == "telemetry-capacity-comparison"
  and .sourceCommit == $source
  and .fantasyCommit == $fantasy
  and .protocolIdentity == $protocol
  and .botCount == 64
  and .warmupSeconds >= 10
  and .measuredSeconds >= 300
  and .exporterOutage.tickP99IncrementMilliseconds < .gates.tickP99IncrementLimitMilliseconds
  and .gates.tickP99IncrementLimitMilliseconds == 0.25
  and .exporterOutage.metricExportAttempts >= 1
  and .exporterOutage.metricExportFailures >= 1
  and .exporterOutage.traceExportAttempts >= 1
  and .exporterOutage.traceExportFailures >= 1
  and .exporterOutage.traceRecordsDropped == 0
  and .exporterOutage.projectMetricSeries >= 1
  and .exporterOutage.projectMetricSeries <= .exporterOutage.projectMetricSeriesLimit
  and .exporterOutage.projectMetricSeriesLimit == 16
  and .exporterOutage.projectMetricTagViolations == 0
  and .exporterOutage.projectMetricSeriesOverflow == 0
  and .baseline.processPeakWorkingSetBytes > 0
  and .exporterOutage.processPeakWorkingSetBytes > 0
  and .gates.boundedTraceQueue == true
  and .gates.taglessProjectMetrics == true
  and .gates.passed == true
' "$telemetry_capacity_json" >/dev/null

echo "Battle Host qualification passed for $expected_source_sha."
echo "Fantasy: $fantasy_commit"
echo "Protocol: $protocol_identity"
