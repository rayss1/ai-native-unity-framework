# Topology Arena replay evidence (ANAR v1)

Status: implemented internal evidence format; complements ADR-0015 without changing ANRP versions 1 or 2.

## Capture and ownership

A file describes one global room/allocation lifetime. The actual ArenaRoom accepts joins and inputs, retains ordered pending commands, handles connection supersession, and produces a hash after every Tick. The worker records only successful Join, accepted Input and ClearPending mutations, followed by each resulting Tick/hash. Rejected inputs and network packets do not mutate the simulation and are not replay records. Reconnect owner replacement clears pending commands and is recorded even when no new entity is created.

The capture queue is a preallocated single-producer/single-consumer value ring. `AINATIVE_ARENA_REPLAY_CAPACITY` defaults to 4096 records per room; valid range is 1–1,000,000. Worker Tick performs no serialization, file operations, waits, locks or managed allocation for capture. Pumping and file operations run in the off-Tick host service. A full ring permanently changes capture status to Overflow; gameplay continues and its evidence is explicitly incomplete. `TopologyBattleEngine.IncompleteReplayCount` records incomplete/faulted captures.

The off-Tick writer creates a hashed-identity `.anar.pending` path with CreateNew, drains a bounded number of records per pump, writes a terminal footer and flushes the file to disk before closing and renaming it. Successful captures become `.anar`; overflow or aborted captures become `.anar.incomplete`. Verification by path rejects unpublished/incomplete extensions. This prevents a footer buffered before a failed durable flush from being mistaken for published evidence. A process crash leaves an unpublished or truncated capture which cannot verify. Rename/directory durability across sudden power loss is platform-dependent; a missing file is a verification failure.

Replay persistence failures set `ReplayHealthy = false`, begin draining the worker pool, prevent room creation and make node reports/readiness unhealthy. Existing rooms continue Tick. The error is permanent for that process; restart/repair is required. Files remain under the configured directory; room identity cannot choose its filesystem path. Completed-room streams remain in the off-Tick capture registry until finalized, independently of room release.

## Configuration and identities

Set `AINATIVE_ARENA_REPLAY_PATH` to enable topology capture. An absent/blank value disables it; readiness therefore does not claim replay evidence when capture was not configured. Enabled capture requires nonempty `AINATIVE_SOURCE_COMMIT`, `AINATIVE_FANTASY_COMMIT`, `AINATIVE_PROTOCOL_IDENTITY` and `AINATIVE_CONFIGURATION_IDENTITY`. Match length comes from `AINATIVE_MATCH_LENGTH_TICKS` and is recorded in the header.

The header also includes global room, allocation, match, node and boot epoch identities, gameplay/map fingerprint, and a literal event-ordering contract. `ArenaReplayVerifier.GameplayFingerprint` hashes ArenaRoom + Shared ArenaGameplay text encoded as UTF-8 without BOM and normalized to LF, independent of checkout line endings. The source drift test checks the constant. Update it and retained evidence whenever these rules/map sources change; the verifier rejects a different fingerprint. It is an identity check, not a cryptographic signature proving artifact provenance.

Internal constructor extension:

```
TopologyBattleEngine(gateway, tickets, outbox,
    int matchLength = ArenaRoom.MatchLengthTicks,
    string? replayDirectory = null,
    ArenaReplayIdentity? replayIdentity = null,
    int replayCapacity = 4096,
    int maxReplayFiles = 1024,
    long maxReplayBytes = 2147483648L)
```

## Binary encoding

All numeric fields are little-endian. Text is strict UTF-8, prefixed by uint16 byte length, bounded to 4096 bytes and nonempty. Unknown versions/records fail closed.

Header: uint32 magic `0x52414E41` (ANAR), uint16 version 1, eleven strings in this order: room, allocation, match, node, boot epoch, source, Fantasy, protocol, configuration, gameplay/map fingerprint, ordering. The ordering string is `mutations-at-current-tick;advance-one;hash-every-tick`. Int32 match length follows.

Every record starts with byte kind and uint64 room Tick:

| Kind | Following fields | Semantics |
|---|---|---|
| 1 Join | uint32 assigned entity | At current Tick; verifier must assign exactly that entity. |
| 2 Input | uint32 entity, uint32 sequence, uint64 client Tick, int32 move X/Z, int32 yaw/pitch deltas, uint32 buttons, byte weapon | At current Tick; ArenaRoom must accept it. |
| 3 ClearPending | uint32 entity | At current Tick; existing entity's pending queue is cleared. |
| 4 Tick | uint64 authoritative state hash | Exactly current Tick + 1; advance once then compare complete hash. |
| 255 Footer | uint64 final hash, int64 record count, byte capture status | Tick/hash/count must match exact reconstructed state; status must be Complete (1); EOF must immediately follow. |

Capture status values: Capturing 0, Complete 1, Overflow 2, PersistenceFailure 3, Aborted 4. No dropped records are tolerated. The verifier rejects reordered/missing Ticks, invalid lifecycle or input, unknown entities, hash/identity drift, incomplete status, truncation and trailing bytes. It instantiates the same ArenaRoom implementation and match length; it does not use the legacy synthetic model.

## Runnable verification and compatibility

```
dotnet run --project server/acceptance/AiNative.ArenaReplay -- <file.anar> <source> <fantasy> <protocol> <configuration>
```

Success prints JSON with RoomId, MatchId, FinalTick, FinalHash and Records, and exits 0. Rejection exits 1; usage errors exit 2. All four expected identities must be supplied, so verification cannot silently bless a drifted header.

Legacy `--verify-replay` continues to read ANRP v1/v2 using the existing BattleReplayVerifier. ANAR is deliberately separate: synthetic bot captures cannot express Arena's joins, movement/combat state and queue-clearing semantics. No gameplay or backend protobuf schema changes are required.

## Validation boundaries

Server.Battle tests cover real Arena joins, ordered accepted inputs, idle Tick hashing, reconnect ClearPending, terminal capture publication after room release, tampered input/hash, identities, truncation, trailing bytes, permanent overflow, filesystem failure and warm producer/owner Tick zero managed bytes. The CLI was exercised against a retained capture from authenticated injected-transport topology gameplay; that fixture proves replay correctness, not live network transport security/performance. Legacy Arena protocol tests verify actual consumed input acknowledgements and event draining on non-snapshot Ticks. Existing synthetic v1/v2 tests remain unchanged.

Unity, IL2CPP/AOT and live KCP replay sessions remain unrun in this environment. No worker capacity or production latency qualification follows from these correctness tests.

## Process diagnostics

Topology Host enforces positive worker/density values and a checked product of at most 16 rooms before gateway construction, keeping bounded control inventories within their wire budget. `GET /health/diagnostics` returns off-Tick `workers = pool.Performance()` snapshots (workerId, tickCount, overBudgetTicks, allocatedBytes, lastTickMicros, firstSampleTick, recentTickMicros), process-wide GC generation counts/total allocated bytes/heap size, private/working-set memory, and replay health/incomplete capture count. Capacity tools must sample the actual process endpoint; isolated warmed Tick allocation tests do not replace this operational evidence.

## Bounded retained storage and admission

`AINATIVE_ARENA_REPLAY_MAX_FILES` defaults to 1024 and `AINATIVE_ARENA_REPLAY_MAX_BYTES` to 2 GiB; both must be positive. Startup scans the configured directory outside Tick and counts only filenames matching 64 lowercase hex characters followed by `.anar`, `.anar.pending` or `.anar.incomplete`. It counts existing published, unfinished and failed files. Unknown files are untouched and excluded. No valid replay is automatically deleted; retention/archival is an explicit operational action.

Before room creation, admission reserves one file and a complete lifetime byte budget. This is outside the owner Tick, as is all accounting/completion. Room Tick has no quota locks, filesystem access or allocation. The byte reservation is:

```
45088 header bytes + 26 footer bytes
+ (matchLength + 2701) * 137 records-per-Tick * 46 bytes-per-record
```

A Tick processes at most 128 commands. Each command emits at most one record, except each of at most eight first joins can emit both Join and ClearPending. The Tick hash adds one record, hence 137. Every record conservatively receives the largest 46-byte Input budget, even though lifecycle and hash records are smaller. The waiting timeout plus uninterrupted Active match lifetime bounds total Ticks. Eleven strictly bounded UTF-8 header strings supply the fixed header maximum. The writer checks remaining reserved bytes before every header/record/footer write and cannot grow beyond the claimed budget.

On finalization, the one retained file continues counting against the file quota, and actual on-disk size replaces its byte reservation. Canceled captures are finalized as incomplete evidence and remain counted. Persistence failure retains the conservative claim and closes admission; restart accounts the actual retained file size. Quota exhaustion returns `replay-storage-quota` from engine creation and reports `ReplayCanAdmit=false`, while `ReplayHealthy=true` distinguishes healthy full storage from I/O failure. It does not drain existing gameplay. Readiness and node availability consider quota admission. Diagnostics add replay `canAdmit`, `files` and `accountedBytes` (existing bytes plus active reservations).

With the default 36000-Tick match, an empty 2 GiB quota can reserve eight room captures concurrently. This is a safe accounting limit, not measured disk utilization or production capacity. Increase the explicitly configured quota when qualifying larger room density; no fallback silently disables capture.
