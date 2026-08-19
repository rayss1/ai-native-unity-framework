# ai-native-unity-framework
An AI-native full-stack Unity game development framework for autonomous AI agents, covering client, server, shared modules, development tools, testing, build pipelines, and automation.

The repository currently contains the first verifiable skeleton: one Shared Gameplay source set compiled by both Unity and .NET, cross-runtime contract tests, and a manifest-derived architecture validator. Server and vertical-slice runtime modules are intentionally deferred until they own a real artifact.

## Requirements

- .NET SDK 9.0.300 or a later 9.0 feature band. The repository `global.json` selects the installed 9.0 SDK.
- .NET 8 and .NET 9 runtimes for the compatibility test matrix.
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
dotnet run --project tools/ArchitectureCheck -c Release --no-build -- --root . --format text
```

Run the Unity package tests from the repository root on Windows:

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
- `shared/gameplay`: UPM package, .NET Standard 2.1 project, shared Runtime source, and dual-runtime tests.
- `tools/ArchitectureCheck`: architecture graph and forbidden-API validator.
- `server/vendor/Fantasy`: pinned, opaque evaluation source; production projects must not reference it while ADR-0012 is Proposed.
- `Docs`: accepted ADRs and architecture contracts.

## Documentation

- [Architecture and Technology Baseline](Docs/Architecture/technology-baseline.md)
- [First Vertical Slice Architecture Contracts](Docs/Architecture/architecture-contracts.md)
- [Architecture Decision Record Index](Docs/ADR/README.md)
- [Decision Alternatives and Trade-offs](Docs/Architecture/decision-tradeoffs.md)
- [Module Dependency Matrix](Docs/Architecture/dependency-matrix.md)
- [Public API Contract Catalog](Docs/Architecture/public-api-contracts.md)
- [First Vertical Slice Performance Budgets](Docs/Architecture/performance-budgets.md)
- [Architecture Risk Register](Docs/Architecture/risk-register.md)
