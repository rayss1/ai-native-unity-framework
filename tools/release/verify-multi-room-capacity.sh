#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "Usage: $0 <host.json> <load.json> <wire.json> <output.json>" >&2
  exit 2
fi

host_json="$1"
load_json="$2"
wire_json="$3"
output_json="$4"

for input_file in "$host_json" "$load_json" "$wire_json"; do
  if [[ ! -s "$input_file" ]]; then
    echo "Required multi-room capacity input is missing or empty: $input_file" >&2
    exit 2
  fi
done

if ! command -v jq >/dev/null 2>&1; then
  echo "jq is required." >&2
  exit 2
fi

jq -e '
  .evidenceClass == "release-equivalent-host-core"
  and .roomCount == 2
  and .botsPerRoom == 64
  and .totalBotCapacity == 128
  and .sampleCount >= 18000
  and .elapsedSeconds >= 300
  and .tickP99Milliseconds <= 13.336
  and .tickP999Milliseconds <= 16
  and .slowTickPercentage <= 0.08
  and .gameplayP99Milliseconds <= 6.4
  and .gameplayAllocatedBytes == 0
  and .processMeasurementProcessorMilliseconds > 0
  and .processCpuUtilizationPercentage >= 0
  and .processCpuUtilizationPercentage <= 80
  and .processPeakWorkingSetBytes > 0
  and .gcTotalAvailableMemoryBytes > 0
  and (.processPeakWorkingSetBytes * 5) <= (.gcTotalAvailableMemoryBytes * 4)
  and .outerKcpMtu == 1150
  and (.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.fantasyCommit | test("^[0-9a-f]{40}$"))
  and (.protocolIdentity | test("^[0-9a-f]{64}$"))
' "$host_json" >/dev/null

jq -e '
  .botCount == 128
  and .roomCount == 2
  and .botsPerRoom == 64
  and .warmupSeconds >= 10
  and .measuredSeconds >= 300
  and .measuredInputFrames == (.botCount * .measuredSeconds * 60)
  and .measuredInputRateHz >= 59.5
  and .measuredInputRateHz <= 60.1
  and .measuredInputBatchesSent * 2 == .measuredInputFrames
  and .measuredInputBatchRateHz >= 29.5
  and .measuredInputBatchRateHz <= 30.1
  and .snapshotFrames > 0
  and .snapshotBytes > 0
  and .newestSnapshotTick > 0
  and .peakConnections == 128
  and .outerKcpMtu == 1150
  and (.sourceCommit | test("^[0-9a-f]{40}$"))
  and (.fantasyCommit | test("^[0-9a-f]{40}$"))
  and (.protocolIdentity | test("^[0-9a-f]{64}$"))
' "$load_json" >/dev/null

jq -e '
  .evidenceClass == "linux-loopback-netem-pcap"
  and .qualifiedSocketImpairment == true
  and .durationSeconds >= 60
  and .expectedClientCount == 128
  and .clientCount == 128
  and .headroomPercent == 20
  and .downstreamP95LimitKbps == 204.8
  and .upstreamP95LimitKbps == 51.2
  and .datagramPayloadP95LimitBytes == 960
  and .downstreamP95Kbps <= .downstreamP95LimitKbps
  and .upstreamP95Kbps <= .upstreamP95LimitKbps
  and .datagramPayloadP95Bytes <= .datagramPayloadP95LimitBytes
  and .maxDatagramPayloadBytes <= 1200
  and .packetCount > 0
  and .gatesPassed == true
' "$wire_json" >/dev/null

source_commit="$(jq -r '.sourceCommit' "$host_json")"
fantasy_commit="$(jq -r '.fantasyCommit' "$host_json")"
protocol_identity="$(jq -r '.protocolIdentity' "$host_json")"
jq -e --arg source "$source_commit" --arg fantasy "$fantasy_commit" --arg protocol "$protocol_identity" '
  .sourceCommit == $source
  and .fantasyCommit == $fantasy
  and .protocolIdentity == $protocol
' "$load_json" >/dev/null
jq -e --arg source "$source_commit" --arg fantasy "$fantasy_commit" --arg protocol "$protocol_identity" '
  .sourceCommit == $source
  and .fantasyCommit == $fantasy
  and .protocolIdentity == $protocol
' "$wire_json" >/dev/null

output_directory="$(dirname "$output_json")"
mkdir -p "$output_directory"
jq -n \
  --slurpfile host "$host_json" \
  --slurpfile load "$load_json" \
  --slurpfile wire "$wire_json" '
  {
    evidenceClass: "multi-room-capacity-candidate",
    sourceCommit: $host[0].sourceCommit,
    fantasyCommit: $host[0].fantasyCommit,
    protocolIdentity: $host[0].protocolIdentity,
    roomCount: $host[0].roomCount,
    botsPerRoom: $host[0].botsPerRoom,
    totalBotCount: $load[0].botCount,
    warmupSeconds: $load[0].warmupSeconds,
    measuredSeconds: $load[0].measuredSeconds,
    host: {
      sampleCount: $host[0].sampleCount,
      tickP99Milliseconds: $host[0].tickP99Milliseconds,
      tickP999Milliseconds: $host[0].tickP999Milliseconds,
      slowTickPercentage: $host[0].slowTickPercentage,
      gameplayP99Milliseconds: $host[0].gameplayP99Milliseconds,
      gameplayAllocatedBytes: $host[0].gameplayAllocatedBytes,
      processWorkingSetBytes: $host[0].processWorkingSetBytes,
      processPeakWorkingSetBytes: $host[0].processPeakWorkingSetBytes,
      processMeasurementProcessorMilliseconds: $host[0].processMeasurementProcessorMilliseconds,
      processCpuUtilizationPercentage: $host[0].processCpuUtilizationPercentage,
      managedHeapBytes: $host[0].managedHeapBytes,
      gcTotalCommittedBytes: $host[0].gcTotalCommittedBytes,
      gcTotalAvailableMemoryBytes: $host[0].gcTotalAvailableMemoryBytes,
      threadPoolThreadCount: $host[0].threadPoolThreadCount
    },
    load: {
      measuredInputFrames: $load[0].measuredInputFrames,
      measuredInputRateHz: $load[0].measuredInputRateHz,
      measuredInputBatchesSent: $load[0].measuredInputBatchesSent,
      measuredInputBatchRateHz: $load[0].measuredInputBatchRateHz,
      snapshotFrames: $load[0].snapshotFrames,
      snapshotBytes: $load[0].snapshotBytes,
      peakConnections: $load[0].peakConnections
    },
    wire: {
      durationSeconds: $wire[0].durationSeconds,
      clientCount: $wire[0].clientCount,
      pcapSha256: $wire[0].pcapSha256,
      downstreamP95Kbps: $wire[0].downstreamP95Kbps,
      upstreamP95Kbps: $wire[0].upstreamP95Kbps,
      datagramPayloadP95Bytes: $wire[0].datagramPayloadP95Bytes,
      maxDatagramPayloadBytes: $wire[0].maxDatagramPayloadBytes
    },
    gates: {
      headroomPercent: 20,
      tickP99LimitMilliseconds: 13.336,
      tickP999LimitMilliseconds: 16,
      gameplayP99LimitMilliseconds: 6.4,
      processCpuLimitPercentage: 80,
      processMemoryLimitFraction: 0.8,
      passed: true
    }
  }
' > "$output_json"

echo "Two-room capacity candidate gates passed."
