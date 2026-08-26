#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
verifier="$repository_root/tools/release/verify-multi-room-capacity.sh"
fixture_dir="$(mktemp -d)"
trap 'rm -rf "$fixture_dir"' EXIT

source_commit="$(printf '1%.0s' {1..40})"
fantasy_commit="$(printf '2%.0s' {1..40})"
protocol_identity="$(printf '3%.0s' {1..64})"

jq -n \
  --arg source "$source_commit" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" '
  {
    evidenceClass: "release-equivalent-host-core",
    roomCount: 2,
    botsPerRoom: 64,
    totalBotCapacity: 128,
    sampleCount: 18000,
    elapsedSeconds: 310,
    tickP99Milliseconds: 3,
    tickP999Milliseconds: 5,
    slowTickPercentage: 0,
    gameplayP99Milliseconds: 1,
    gameplayAllocatedBytes: 0,
    processWorkingSetBytes: 200000000,
    processPeakWorkingSetBytes: 300000000,
    processTotalProcessorMilliseconds: 100000,
    processMeasurementProcessorMilliseconds: 80000,
    processCpuUtilizationPercentage: 20,
    managedHeapBytes: 20000000,
    gcTotalCommittedBytes: 30000000,
    gcTotalAvailableMemoryBytes: 536870912,
    threadPoolThreadCount: 8,
    outerKcpMtu: 1150,
    sourceCommit: $source,
    fantasyCommit: $fantasy,
    protocolIdentity: $protocol
  }
' > "$fixture_dir/host.json"

jq -n \
  --arg source "$source_commit" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" '{
  botCount: 128,
  roomCount: 2,
  botsPerRoom: 64,
  warmupSeconds: 10,
  measuredSeconds: 300,
  measuredInputFrames: 2304000,
  measuredInputRateHz: 60,
  measuredInputBatchesSent: 1152000,
  measuredInputBatchRateHz: 30,
  snapshotFrames: 768000,
  snapshotBytes: 600000000,
  newestSnapshotTick: 19000,
  peakConnections: 128,
  outerKcpMtu: 1150,
  sourceCommit: $source,
  fantasyCommit: $fantasy,
  protocolIdentity: $protocol
}' > "$fixture_dir/load.json"

jq -n \
  --arg source "$source_commit" \
  --arg fantasy "$fantasy_commit" \
  --arg protocol "$protocol_identity" '
  {
    evidenceClass: "linux-loopback-netem-pcap",
    qualifiedSocketImpairment: true,
    pcapSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    durationSeconds: 60,
    expectedClientCount: 128,
    clientCount: 128,
    headroomPercent: 20,
    downstreamP95LimitKbps: 204.8,
    upstreamP95LimitKbps: 51.2,
    datagramPayloadP95LimitBytes: 960,
    downstreamP95Kbps: 180,
    upstreamP95Kbps: 45,
    datagramPayloadP95Bytes: 800,
    maxDatagramPayloadBytes: 917,
    packetCount: 1000,
    sourceCommit: $source,
    fantasyCommit: $fantasy,
    protocolIdentity: $protocol,
    gatesPassed: true
  }
' > "$fixture_dir/wire.json"

"$verifier" \
  "$fixture_dir/host.json" \
  "$fixture_dir/load.json" \
  "$fixture_dir/wire.json" \
  "$fixture_dir/output.json"
jq -e '.evidenceClass == "multi-room-capacity-candidate" and .gates.passed == true' \
  "$fixture_dir/output.json" >/dev/null

expect_failure() {
  local case_name="$1"
  local host="$2"
  local load="$3"
  local wire="$4"
  if "$verifier" "$host" "$load" "$wire" "$fixture_dir/rejected.json" >/dev/null 2>&1; then
    echo "Multi-room capacity negative case unexpectedly passed: $case_name" >&2
    exit 1
  fi
}

jq '.tickP99Milliseconds = 13.337' "$fixture_dir/host.json" > "$fixture_dir/slow-host.json"
expect_failure "tick headroom" "$fixture_dir/slow-host.json" "$fixture_dir/load.json" "$fixture_dir/wire.json"

jq '.processPeakWorkingSetBytes = 429496730' "$fixture_dir/host.json" > "$fixture_dir/memory-host.json"
expect_failure "memory headroom" "$fixture_dir/memory-host.json" "$fixture_dir/load.json" "$fixture_dir/wire.json"

jq '.roomCount = 1' "$fixture_dir/load.json" > "$fixture_dir/one-room-load.json"
expect_failure "room identity" "$fixture_dir/host.json" "$fixture_dir/one-room-load.json" "$fixture_dir/wire.json"

jq '.sourceCommit = "ffffffffffffffffffffffffffffffffffffffff"' \
  "$fixture_dir/load.json" > "$fixture_dir/wrong-source-load.json"
expect_failure "source identity" "$fixture_dir/host.json" "$fixture_dir/wrong-source-load.json" "$fixture_dir/wire.json"

jq '.downstreamP95Kbps = 204.9 | .gatesPassed = false' "$fixture_dir/wire.json" > "$fixture_dir/wide-wire.json"
expect_failure "wire headroom" "$fixture_dir/host.json" "$fixture_dir/load.json" "$fixture_dir/wide-wire.json"

echo "Multi-room capacity contract tests passed."
