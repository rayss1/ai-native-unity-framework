# ai-native-unity-framework
An AI-native full-stack Unity game development framework for autonomous AI agents, covering client, server, shared modules, development tools, testing, build pipelines, and automation.

The repository contains the first production vertical-slice foundation: one Shared Gameplay source set compiled by Unity and .NET, a Unity-ready bounded client prediction/protocol adapter, a dedicated Fantasy KCP client transport and Battle Client composition candidate, project-owned realtime/protocol contracts, a .NET 10 Fantasy-backed Battle Host, cross-runtime vectors, deterministic replay/load evidence, a production container contract, and a manifest-derived architecture validator.

## Requirements

- .NET SDK 10.0.202. The repository `global.json` pins the exact SDK servicing version.
- Unity 6000.3.9f1 revision `7a9955a4f2fa` with Mac Build Support (Mono) for local package import, EditMode/PlayMode tests, and the Apple Silicon ARM64 smoke build.
- Colima/Docker with Linux x64 emulation and a macOS-reachable VM address for the fixed-image Battle Host used by the macOS Unity gate. Start the Apple Silicon profile with `--vz-rosetta --network-address`; TCP-only SSH port forwarding cannot carry the real KCP/UDP gate.

## Clone and initialize dependencies

For a new checkout, initialize the pinned vendor source at clone time:

```bash
git clone --recurse-submodules https://github.com/rayss1/ai-native-unity-framework.git
```

For an existing checkout:

```bash
git submodule update --init --recursive
```

Maintainers update Fantasy only to an explicitly reviewed commit; the submodule does not follow a floating branch:

```bash
git -C server/vendor/Fantasy fetch origin
git -C server/vendor/Fantasy checkout <reviewed-commit-sha>
git add server/vendor/Fantasy
```

## Validate

```powershell
dotnet restore AiNative.sln
dotnet build AiNative.sln -c Release --no-restore
dotnet test AiNative.sln -c Release --no-build --no-restore
dotnet publish server/src/Hosts/AiNative.BattleHost/AiNative.BattleHost.csproj -c Release -f net10.0 --no-restore
dotnet run --project tools/ArchitectureCheck -c Release --no-build -- --root . --format text
```

Until credentialed Unity CI is restored, run the Unity package tests manually on the exact commit under review. On macOS:

```bash
colima start --vm-type vz --vz-rosetta --arch aarch64 --cpus 4 --memory 8 --network-address
tools/run-unity-manual-validation.sh
```

The macOS script is the complete WS-26/WS-27 desktop and real-client correction gate. It builds the exact-source Host with fixed .NET images, verifies exactly 38 EditMode and 2 real-KCP PlayMode tests, builds an ARM64 Mono Player, runs the reconnect smoke, applies the symmetric Regional network profile, measures correction percentiles for 60 seconds after warm-up, and retains the evidence bundle. The complete contracts are defined in [Unity macOS Validation](Docs/Architecture/unity-manual-validation.md) and [Regional Real-Client Correction Validation](Docs/Architecture/regional-client-correction-validation.md).

The Windows entry point remains available as a future supplemental platform gate, but it no longer blocks WS-26:

```powershell
tools/run-unity-windows-validation.ps1
```

Both scripts require a clean checkout so every result identifies one exact commit. A passing current macOS bundle includes the local Regional real-client correction profile, but does not imply Windows, Universal/x86_64, Android/iOS IL2CPP, production deployment, or a real Linux environment canary. Retain and review the generated bundle before updating milestone status.

The architecture-check command returns `0` for a valid repository, `1` for architecture violations, and `2` for invalid arguments, configuration, or unreadable inputs. Use `--format json` for machine-readable diagnostics.

## Current layout

- `client/UnityProject`: minimal Unity composition project and local package manifest.
- `packages/com.ainative.client.prediction`: Unity-ready input/Snapshot/reconnect adapter over project-owned Shared contracts.
- `packages/com.ainative.client.fantasy`: the sole Client Fantasy namespace boundary, implementing bounded Fantasy KCP behind `IRealtimeTransport` with pinned `Fantasy.Unity` and retained third-party notices.
- `shared`: UPM/.NET Standard 2.1 Gameplay and realtime contracts, Protobuf schemas/generated code, and dual-runtime tests.
- `server/src/Hosts/AiNative.BattleHost`: production `net10.0` composition root with health, drain, replay, and Fantasy KCP startup.
- `server/src/Modules`: project-owned Fantasy and protocol adapters; Fantasy runtime types terminate at this boundary.
- `tools/ArchitectureCheck`: architecture graph and forbidden-API validator.
- `server/vendor/Fantasy`: pinned, opaque vendor source; approved Server adapter/composition projects consume `Fantasy-Net`, while only the dedicated Client transport consumes `Fantasy.Unity` from the same commit.
- `infrastructure/battle-host`: non-root Linux production image and Compose deployment contract.
- `Docs`: accepted ADRs and architecture contracts.

## Battle Host container

CI builds and validates the production Dockerfile without publishing it. The manual release workflow admits only an exact qualified `main` run, publishes the Linux x64 image to GHCR with SBOM/provenance and a GitHub artifact attestation, and records source, Fantasy, protocol, configuration, SDK/runtime, and image identities. Operators deploy an immutable digest and mount a reviewed configuration:

```bash
AINATIVE_BATTLE_HOST_IMAGE="$(tools/release/resolve-battle-host-release.sh "$PWD" 0.1.0)" \
AINATIVE_FANTASY_CONFIG='/absolute/path/Fantasy.config' \
docker compose -f infrastructure/battle-host/compose.yaml up -d
```

The repository-retained [release ledger](infrastructure/battle-host/releases/README.md) maps an operator-selected version to its verified immutable digest. Publishing never happens on a normal push or pull request. See [Battle Host Release Procedure](Docs/Architecture/battle-host-release.md) for the explicit release and rollback gate.

## Documentation

- [Architecture and Technology Baseline](Docs/Architecture/technology-baseline.md)
- [First Vertical Slice Architecture Contracts](Docs/Architecture/architecture-contracts.md)
- [Architecture Decision Record Index](Docs/ADR/README.md)
- [Decision Alternatives and Trade-offs](Docs/Architecture/decision-tradeoffs.md)
- [Module Dependency Matrix](Docs/Architecture/dependency-matrix.md)
- [Public API Contract Catalog](Docs/Architecture/public-api-contracts.md)
- [Client Prediction and Reconciliation Baseline](Docs/Architecture/client-prediction-baseline.md)
- [First Vertical Slice Performance Budgets](Docs/Architecture/performance-budgets.md)
- [Architecture Risk Register](Docs/Architecture/risk-register.md)
