# Presentation Correction Smoothing

Status: WS-28 implemented; exact-main macOS ARM64 Mono gate passed
Last updated: 2026-09-03
Decision source: ADR-0005, ADR-0007, and WS-28

## Scope

WS-28 adds a Client-layer visual correction primitive without changing Shared Gameplay, protocol bytes, server authority, prediction history, Fantasy, or Battle Host behavior. The simulation accepts every authoritative reconciliation immediately. Only the rendered X/Z position carries a temporary residual.

The current implementation uses a 100 ms linear decay for corrections at or below 250 mm. Successive small corrections compose with the remaining residual so a new Snapshot does not introduce an extra visual jump. A correction or accumulated residual above 250 mm snaps immediately. Authoritative-ahead and history-miss results also snap because continuity is no longer trustworthy. Entering reconnect, a terminal fault, or a new presentation epoch clears residual state.

The smoother is engine-neutral code in `com.ainative.client.prediction`. The Battle Client session owns its lifecycle, while `BattleClientCompositionRoot.LateUpdate` supplies Unity render delta time and maps millimetres to metres. Remote interpolation remains a separate future adapter.

## Invariants and gates

- authoritative and predicted `KinematicState` changes before presentation smoothing;
- the smoother cannot mutate input sequence, acknowledgement, history, connection epoch, or protocol state;
- zero delta preserves the pre-correction displayed position;
- the residual reaches exactly zero by 100 ms and does not overshoot;
- residual never remains active above the 250 mm snap boundary;
- reconnect/fault state cannot carry a residual into a new transport epoch;
- steady-state `Advance` allocates zero managed bytes;
- invalid negative, NaN, or infinite render delta fails explicitly.

The exact-commit Unity gate requires 44/44 EditMode tests, including five engine-neutral smoother tests and one composed-session test, plus the existing two real-KCP PlayMode tests, ARM64 Mono reconnect Player smoke, and Regional correction measurement. After warm-up, the Regional result must observe at least one smoothed correction, zero presentation snaps, and a final residual no greater than 250 mm. A passing bundle proves the bounded algorithm and composed macOS path only.

## Recorded exact-main evidence

Exact `main` source `b8d4228c20cd9bf05054a956c5cc168711bfdff9` (tree `2241de28438df0a56fa354d42130741f92a978f8`) passed [.NET run 33755796726](https://github.com/rayss1/ai-native-unity-framework/actions/runs/33755796726) with a zero-warning build, 97/97 tests, dependency audit, generated-protocol check, Battle Host publish, and architecture validation. Fantasy remained pinned to `f8bed0d464924f159d46498f1311206ea0694be8`; protocol and configuration identities remained `3cb86e21687e65af0e0d409d9186384d0f959fd6aa873eb9e1cd0cb39c77d37d` and `fc0714bcbe7c8c673cf638506a45f3a4440585f4024ff78048434346ab8a66e4`.

The reviewed Unity `6000.3.9f1` revision `7a9955a4f2fa` bundle passed 44/44 EditMode and 2/2 real-KCP PlayMode tests. The ARM64 Mono Player completed login, join, input acknowledgement, forced reconnect, epoch `4 -> 5`, acknowledgement `30 -> 31`, and zero dropped input frames. Under the symmetric Regional profile it recorded 1,221 reconciliation samples, correction P95/P99 `9/10 mm`, maximum `12 mm`, zero corrections above `250 mm`, 1,207 smoothed presentation corrections, zero presentation snaps, and a final residual of `1 mm`. The qdiscs observed 168 packet drops. The Host exited zero after draining rooms and KCP without forced termination.

The retained local bundle is `artifacts/unity-macos/b8d4228c20cd9bf05054a956c5cc168711bfdff9/`; its hash-manifest SHA-256 is `496da779c7449f2a6ffb1e59d3a37d99f34dba2ae6f73b40d6f3187569fb2604`. Every manifest entry, staged notice, and the `arm64` Player executable were independently rechecked. This closes the bounded WS-28 macOS composition gate for that exact commit, not the product-quality gates below.

## Non-goals and rollback

WS-28 does not establish representative game feel, remote interpolation, animation smoothing, camera behavior, game-specific Unity/Jolt physics prediction, Windows, or Android/iOS IL2CPP quality. Those require representative scenes and devices. Rollback removes the smoother from the application composition and renders the latest predicted state directly; server authority, protocol, transport, and replay remain unchanged.
