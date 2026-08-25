#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 <baseline-host.json> <outage-host.json> <baseline-load.json> <outage-load.json> <output.json>" >&2
  exit 2
fi

baseline_host="$1"
outage_host="$2"
baseline_load="$3"
outage_load="$4"
output_file="$5"

for input_file in "$baseline_host" "$outage_host" "$baseline_load" "$outage_load"; do
  if [[ ! -s "$input_file" ]]; then
    echo "Required telemetry capacity input is missing or empty: $input_file" >&2
    exit 2
  fi
done

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required." >&2
  exit 2
fi

jq -e '
  .evidenceClass == "release-equivalent-host-core"
  and .sampleCount >= 1700
  and .elapsedSeconds >= 39
  and .tickP99Milliseconds <= 16.67
  and .tickP999Milliseconds <= 20
  and .slowTickPercentage <= 0.1
  and .gameplayP99Milliseconds <= 8
  and .gameplayAllocatedBytes == 0
  and .outerKcpMtu == 1150
  and .processorCount > 0
  and .processWorkingSetBytes > 0
  and .processPeakWorkingSetBytes >= .processWorkingSetBytes
  and .processTotalProcessorMilliseconds > 0
  and .managedHeapBytes > 0
  and .gcTotalCommittedBytes > 0
  and .threadPoolThreadCount > 0
  and .projectMetricSeries >= 0
  and .projectMetricSeries <= .projectMetricSeriesLimit
  and .projectMetricSeriesLimit == 16
  and .projectMetricTagViolations == 0
  and .projectMetricSeriesOverflow == 0
' "$baseline_host" >/dev/null

jq -e '
  .evidenceClass == "release-equivalent-host-core"
  and .sampleCount >= 1700
  and .elapsedSeconds >= 39
  and .tickP99Milliseconds <= 16.67
  and .tickP999Milliseconds <= 20
  and .slowTickPercentage <= 0.1
  and .gameplayP99Milliseconds <= 8
  and .gameplayAllocatedBytes == 0
  and .outerKcpMtu == 1150
  and .processorCount > 0
  and .processWorkingSetBytes > 0
  and .processPeakWorkingSetBytes >= .processWorkingSetBytes
  and .processTotalProcessorMilliseconds > 0
  and .managedHeapBytes > 0
  and .gcTotalCommittedBytes > 0
  and .threadPoolThreadCount > 0
  and .telemetryExporterConfigured == true
  and .telemetryMetricExportAttempts >= 1
  and .telemetryMetricExportFailures >= 1
  and .telemetryTraceExportAttempts >= 1
  and .telemetryTraceExportFailures >= 1
  and .telemetryTraceRecordsDropped == 0
  and .projectMetricSeries >= 1
  and .projectMetricSeries <= .projectMetricSeriesLimit
  and .projectMetricSeriesLimit == 16
  and .projectMetricTagViolations == 0
  and .projectMetricSeriesOverflow == 0
' "$outage_host" >/dev/null

jq -e '
  .botCount == 64
  and .peakConnections == 64
  and .warmupSeconds >= 10
  and .measuredSeconds >= 30
  and .loadElapsedSeconds >= 40
  and .measuredInputFrames == (.botCount * .measuredSeconds * 60)
  and .measuredInputRateHz >= 59.5
  and .measuredInputRateHz <= 60.1
  and (.measuredInputBatchesSent * 2) == .measuredInputFrames
  and .measuredInputBatchRateHz >= 29.5
  and .measuredInputBatchRateHz <= 30.1
  and .snapshotFrames > 0
  and .outerKcpMtu == 1150
' "$baseline_load" >/dev/null

jq -e '
  .botCount == 64
  and .peakConnections == 64
  and .warmupSeconds >= 10
  and .measuredSeconds >= 30
  and .loadElapsedSeconds >= 40
  and .measuredInputFrames == (.botCount * .measuredSeconds * 60)
  and .measuredInputRateHz >= 59.5
  and .measuredInputRateHz <= 60.1
  and (.measuredInputBatchesSent * 2) == .measuredInputFrames
  and .measuredInputBatchRateHz >= 29.5
  and .measuredInputBatchRateHz <= 30.1
  and .snapshotFrames > 0
  and .outerKcpMtu == 1150
' "$outage_load" >/dev/null

jq -e -n \
  --slurpfile baseline "$baseline_host" \
  --slurpfile outage "$outage_host" '
    ($baseline[0].sourceCommit == $outage[0].sourceCommit)
    and ($baseline[0].fantasyCommit == $outage[0].fantasyCommit)
    and ($baseline[0].protocolIdentity == $outage[0].protocolIdentity)
    and ($baseline[0].telemetryExporterConfigured == false)
    and ($baseline[0].telemetryMetricExportAttempts == 0)
    and ($baseline[0].telemetryMetricExportFailures == 0)
    and ($baseline[0].telemetryTraceExportAttempts == 0)
    and ($baseline[0].telemetryTraceExportFailures == 0)
    and ($baseline[0].telemetryTraceRecordsDropped == 0)
    and (($outage[0].tickP99Milliseconds - $baseline[0].tickP99Milliseconds) < 0.25)
  ' >/dev/null

mkdir -p "$(dirname "$output_file")"
jq -n \
  --slurpfile baseline "$baseline_host" \
  --slurpfile outage "$outage_host" \
  --slurpfile baselineLoad "$baseline_load" \
  --slurpfile outageLoad "$outage_load" '
  {
    evidenceClass: "telemetry-capacity-comparison",
    sourceCommit: $baseline[0].sourceCommit,
    fantasyCommit: $baseline[0].fantasyCommit,
    protocolIdentity: $baseline[0].protocolIdentity,
    botCount: 64,
    warmupSeconds: $baselineLoad[0].warmupSeconds,
    measuredSeconds: $baselineLoad[0].measuredSeconds,
    baseline: {
      tickP99Milliseconds: $baseline[0].tickP99Milliseconds,
      tickP999Milliseconds: $baseline[0].tickP999Milliseconds,
      processWorkingSetBytes: $baseline[0].processWorkingSetBytes,
      processPeakWorkingSetBytes: $baseline[0].processPeakWorkingSetBytes,
      processTotalProcessorMilliseconds: $baseline[0].processTotalProcessorMilliseconds,
      managedHeapBytes: $baseline[0].managedHeapBytes,
      gcTotalCommittedBytes: $baseline[0].gcTotalCommittedBytes,
      threadPoolThreadCount: $baseline[0].threadPoolThreadCount
    },
    exporterOutage: {
      tickP99Milliseconds: $outage[0].tickP99Milliseconds,
      tickP999Milliseconds: $outage[0].tickP999Milliseconds,
      tickP99IncrementMilliseconds: ([$outage[0].tickP99Milliseconds - $baseline[0].tickP99Milliseconds, 0] | max),
      metricExportAttempts: $outage[0].telemetryMetricExportAttempts,
      metricExportFailures: $outage[0].telemetryMetricExportFailures,
      traceExportAttempts: $outage[0].telemetryTraceExportAttempts,
      traceExportFailures: $outage[0].telemetryTraceExportFailures,
      traceRecordsDropped: $outage[0].telemetryTraceRecordsDropped,
      projectMetricSeries: $outage[0].projectMetricSeries,
      projectMetricSeriesLimit: $outage[0].projectMetricSeriesLimit,
      projectMetricTagViolations: $outage[0].projectMetricTagViolations,
      projectMetricSeriesOverflow: $outage[0].projectMetricSeriesOverflow,
      processWorkingSetBytes: $outage[0].processWorkingSetBytes,
      processPeakWorkingSetBytes: $outage[0].processPeakWorkingSetBytes,
      processTotalProcessorMilliseconds: $outage[0].processTotalProcessorMilliseconds,
      managedHeapBytes: $outage[0].managedHeapBytes,
      gcTotalCommittedBytes: $outage[0].gcTotalCommittedBytes,
      threadPoolThreadCount: $outage[0].threadPoolThreadCount
    },
    runtime: $outage[0].runtime,
    operatingSystem: $outage[0].operatingSystem,
    processorCount: $outage[0].processorCount,
    gates: {
      tickP99IncrementLimitMilliseconds: 0.25,
      projectMetricSeriesLimit: 16,
      boundedTraceQueue: true,
      taglessProjectMetrics: true,
      passed: true
    }
  }
  ' > "$output_file"

echo "Telemetry outage and one-room capacity gates passed."
