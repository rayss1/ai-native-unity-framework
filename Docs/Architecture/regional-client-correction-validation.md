# Regional Real-Client Correction Validation

Status: WS-27 candidate; exact-commit evidence required
Platform: macOS Apple Silicon ARM64 Mono Player
Decision source: ADR-0005 and `performance-budgets.md`

## Purpose

This gate measures the actual composed Unity Player, project prediction adapter, Fantasy Unity KCP transport, and exact-source Battle Host under the frozen Regional impairment profile. Synthetic load and codec tests remain useful Server evidence, but cannot establish player-visible correction magnitude or frequency.

## Frozen profile and budgets

The Colima bridge applies the profile symmetrically so Player-to-Host and Host-to-Player traffic each receive `50 +/- 10 ms` delay with 25% correlation, 1% random loss, 0.5% duplication, and 1% reordering with 50% correlation. Together the directional delay models the 100 ms Regional RTT budget. The script refuses a pre-existing unexpected qdisc, records both active configurations and statistics, requires observed packet loss, and restores the bridge/interface qdisc classes during cleanup.

After ten seconds of connected prediction warm-up, the Player resets only its bounded diagnostics window and measures for at least 60 seconds. A pass requires:

- at least 1,000 matched/corrected reconciliation samples;
- local correction P95 at or below 250 mm and P99 at or below 750 mm;
- no more than two corrections above 250 mm per player-minute;
- zero prediction-history misses, dropped prediction inputs, and dropped application input frames;
- a live session, nonzero epoch/acknowledgement/Tick, exact configured profile, and normal Player/Host exit.

The adapter stores one exact-millimetre bucket from 0 through 8,192 mm plus one bounded overflow bucket. The overflow percentile resolves conservatively to the exact maximum observed correction. Histogram allocation occurs only when the adapter is constructed; recording and percentile reads do not grow collections.

## Execution and evidence

Run the complete clean-commit command documented in [Unity macOS Validation](unity-manual-validation.md):

```bash
tools/run-unity-manual-validation.sh
```

The reviewed evidence must include `regional-correction.json`, Regional Player stdout/stderr, ingress and egress qdisc state before/during/after measurement, qdisc statistics, exact source/tree/Fantasy/protocol/configuration/Unity/image identities, the Player binary hash, Host shutdown evidence, and the enclosing `hashes.sha256` manifest.

## Interpretation

A pass closes the local macOS Mono Regional correction magnitude/frequency gate for one exact commit and the deterministic test movement only. It does not prove physical WAN behavior, game-specific physics prediction, correction smoothing or visual quality, Windows, Android/iOS IL2CPP, mobile radios, production room-density defaults, deployment rollback, or a real Linux environment canary. Any affected source, package, profile, threshold, image, Unity, or Fantasy change requires a new exact-commit run.
