# ai-native-unity-framework
An AI-native full-stack Unity game development framework for autonomous AI agents, covering client, server, shared modules, development tools, testing, build pipelines, and automation.

The repository contains the first production vertical-slice foundation: one Shared Gameplay source set compiled by Unity and .NET, bounded client prediction/reconciliation primitives, project-owned realtime/protocol contracts, a .NET 10 Fantasy-backed Battle Host, cross-runtime vectors, deterministic replay/load evidence, a production container contract, and a manifest-derived architecture validator.

## Requirements

- .NET SDK 10.0.202. The repository `global.json` pins the exact SDK servicing version.
- Unity 6000.3.9f1 for local package import and EditMode tests.

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
tools/run-unity-manual-validation.sh
```

The required 14-test result and evidence bundle are defined in [Manual Unity Shared-Vector Validation](Docs/Architecture/unity-manual-validation.md).

The equivalent direct invocation on Windows is:

```powershell
if (-not $env:ALLUSERSPROFILE) {
  $env:ALLUSERSPROFILE = $env:PROGRAMDATA
}

& 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe' `
  -batchmode -nographics `
  -projectPath "$PWD/client/UnityProject" `
  -runTests -testPlatform EditMode `
  -testResults "$PWD/client/UnityProject/TestResults/editmode.xml" `
  -logFile "$PWD/client/UnityProject/Logs/editmode.log"
```

The architecture-check command returns `0` for a valid repository, `1` for architecture violations, and `2` for invalid arguments, configuration, or unreadable inputs. Use `--format json` for machine-readable diagnostics.

## Current layout

- `client/UnityProject`: minimal Unity composition project and local package manifest.
- `shared`: UPM/.NET Standard 2.1 Gameplay and realtime contracts, Protobuf schemas/generated code, and dual-runtime tests.
- `server/src/Hosts/AiNative.BattleHost`: production `net10.0` composition root with health, drain, replay, and Fantasy KCP startup.
- `server/src/Modules`: project-owned Fantasy and protocol adapters; Fantasy runtime types terminate at this boundary.
- `tools/ArchitectureCheck`: architecture graph and forbidden-API validator.
- `server/vendor/Fantasy`: pinned, opaque production vendor source; only the approved Server adapter/composition projects consume its tracked package.
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
