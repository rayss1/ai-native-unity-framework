# ADR-0008: Own the Content Pipeline and Keep Code Hot Update Optional

Status: Accepted
Date: 2026-08-13
Decision source: WS-11

## Context

The project needs vendor-neutral content delivery, recoverable mobile updates, and a stable gameplay-facing API. Addressables is outside the baseline. Downloaded managed code adds AOT, recovery, and store-policy risk.

## Decision

The project owns a custom asset pipeline behind `IAssetService`. Stable logical asset IDs resolve through a signed, versioned manifest to platform/quality artifacts with content hashes and dependencies. Downloads are chunked and resumable; verification completes before an atomic active-manifest switch. Cache quota/eviction never removes the active or designated rollback set.

The bootstrap, version check, networking contracts, manifest verification, asset recovery, activation, and rollback path are AOT-capable and shipped with the application. Gameplay/UI use asset handles and cancellation through the port; they never depend on bundle, CDN, storage, or cache implementation types.

HybridCLR is an optional `hotupdate.hybridclr` package. It is absent by default and may contain only an approved Gameplay/UI scope. Core contracts, bootstrap, recovery, and security boundaries remain AOT. A resource/config-only path and normal store release path remain supported.

Manifest and code compatibility are explicit: minimum app version, schema/protocol range, content cohort, and rollback target are validated before activation. Staged rollout is controlled server-side but activation is locally atomic.

## Consequences

The project owns substantial build, signing, cache, rollout, and recovery tooling. It gains provider independence and deterministic rollback. HybridCLR cannot become an implicit core dependency.

## Validation, migration, and rollback

- Content Spike passes interrupted/resumed download, corrupted chunk, bad signature, dependency cycle, disk-full, process-kill-during-activation, cache pressure, staged rollout, and offline restart scenarios.
- Activation passes only when the new manifest and all required artifacts verify; any failure restarts with the last-known-good set.
- HybridCLR adoption requires Android/iOS IL2CPP, stripping, generic supplement, upgrade, downgrade, and cold-start recovery tests plus a release-specific store-policy review.
- Format migration uses dual-read/old-write first, then new-write after fleet compatibility. Rollback reselects the prior signed manifest; disabling the optional package returns to AOT/resource-only releases.
