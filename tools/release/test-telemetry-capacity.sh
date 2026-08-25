#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
verifier="$repository_root/tools/release/verify-telemetry-capacity.sh"
fixture_dir="$(mktemp -d)"
cleanup() { rm -rf "$fixture_dir"; }
trap cleanup EXIT

write_host() {
  output="$1"
  exporter_configured="$2"
  tick_p99="$3"
  attempts="$4"
  failures="$5"
  tag_violations="$6"
  series_overflow="$7"
  jq -n \
    --argjson configured "$exporter_configured" \
    --argjson tickP99 "$tick_p99" \
    --argjson attempts "$attempts" \
    --argjson failures "$failures" \
    --argjson tagViolations "$tag_violations" \
    --argjson seriesOverflow "$series_overflow" '
    {
      evidenceClass: "release-equivalent-host-core",
      sourceCommit: "1111111111111111111111111111111111111111",
      fantasyCommit: "2222222222222222222222222222222222222222",
      protocolIdentity: "3333333333333333333333333333333333333333333333333333333333333333",
      sampleCount: 18000,
      elapsedSeconds: 310,
      tickP99Milliseconds: $tickP99,
      tickP999Milliseconds: 2,
      slowTickPercentage: 0,
      gameplayP99Milliseconds: 1,
      gameplayAllocatedBytes: 0,
      runtime: "10.0.4",
      operatingSystem: "fixture",
      processorCount: 4,
      processWorkingSetBytes: 100000000,
      processPeakWorkingSetBytes: 110000000,
      processTotalProcessorMilliseconds: 20000,
      managedHeapBytes: 10000000,
      gcTotalCommittedBytes: 20000000,
      threadPoolThreadCount: 4,
      outerKcpMtu: 1150,
      telemetryExporterConfigured: $configured,
      telemetryMetricExportAttempts: $attempts,
      telemetryMetricExportFailures: $failures,
      telemetryTraceExportAttempts: $attempts,
      telemetryTraceExportFailures: $failures,
      telemetryTraceRecordsDropped: 0,
      projectMetricSeries: 7,
      projectMetricSeriesLimit: 16,
      projectMetricTagViolations: $tagViolations,
      projectMetricSeriesOverflow: $seriesOverflow
    }
    ' > "$output"
}

jq -n '{
  botCount: 64,
  peakConnections: 64,
  warmupSeconds: 10,
  measuredSeconds: 300,
  loadElapsedSeconds: 310,
  measuredInputFrames: 1152000,
  measuredInputRateHz: 60,
  measuredInputBatchesSent: 576000,
  measuredInputBatchRateHz: 30,
  snapshotFrames: 38400,
  outerKcpMtu: 1150
}' > "$fixture_dir/load.json"

write_host "$fixture_dir/baseline.json" false 1.00 0 0 0 0
write_host "$fixture_dir/outage.json" true 1.24 3 3 0 0

verify() {
  "$verifier" \
    "$fixture_dir/baseline.json" \
    "$1" \
    "$fixture_dir/load.json" \
    "$fixture_dir/load.json" \
    "$fixture_dir/result.json"
}

expect_failure() {
  case_name="$1"
  input="$2"
  if verify "$input" > "$fixture_dir/$case_name.log" 2>&1; then
    echo "Telemetry capacity negative case unexpectedly passed: $case_name" >&2
    exit 1
  fi
}

verify "$fixture_dir/outage.json"
jq -e '.gates.passed == true and .exporterOutage.tickP99IncrementMilliseconds == 0.24' \
  "$fixture_dir/result.json" >/dev/null

jq '.tickP99Milliseconds = 1.25' "$fixture_dir/outage.json" > "$fixture_dir/delta-at-limit.json"
expect_failure delta-at-limit "$fixture_dir/delta-at-limit.json"

jq '.telemetryMetricExportFailures = 0' "$fixture_dir/outage.json" > "$fixture_dir/no-export-failure.json"
expect_failure no-export-failure "$fixture_dir/no-export-failure.json"

jq '.projectMetricTagViolations = 1' "$fixture_dir/outage.json" > "$fixture_dir/tag-violation.json"
expect_failure tag-violation "$fixture_dir/tag-violation.json"

jq '.projectMetricSeriesOverflow = 1' "$fixture_dir/outage.json" > "$fixture_dir/series-overflow.json"
expect_failure series-overflow "$fixture_dir/series-overflow.json"

echo "Telemetry capacity contract tests passed."
