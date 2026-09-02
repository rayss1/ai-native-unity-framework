# Presentation Correction Smoothing

Status: WS-28 implementation candidate; exact-commit macOS evidence pending
Last updated: 2026-09-02
Decision source: ADR-0005, ADR-0007, and WS-28

## Scope

WS-28 adds a Client-layer visual correction primitive without changing Shared Gameplay, protocol bytes, server authority, prediction history, Fantasy, or Battle Host behavior. The simulation accepts every authoritative reconciliation immediately. Only the rendered X/Z position carries a temporary residual.

The current candidate uses a 100 ms linear decay for corrections at or below 250 mm. Successive small corrections compose with the remaining residual so a new Snapshot does not introduce an extra visual jump. A correction or accumulated residual above 250 mm snaps immediately. Authoritative-ahead and history-miss results also snap because continuity is no longer trustworthy. Entering reconnect, a terminal fault, or a new presentation epoch clears residual state.

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

## Non-goals and rollback

WS-28 does not establish representative game feel, remote interpolation, animation smoothing, camera behavior, game-specific Unity/Jolt physics prediction, Windows, or Android/iOS IL2CPP quality. Those require representative scenes and devices. Rollback removes the smoother from the application composition and renders the latest predicted state directly; server authority, protocol, transport, and replay remain unchanged.
