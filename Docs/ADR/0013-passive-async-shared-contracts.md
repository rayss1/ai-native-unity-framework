# ADR-0013: Permit Passive Async Types in Shared Boundary Contracts

Status: Accepted
Date: 2026-08-19
Decision source: WS-15
Refines: [ADR-0002](0002-repository-layout-and-module-dependencies.md)

## Context

ADR-0006 and the public API catalog freeze `IRealtimeTransport` as a Unity/server boundary that uses `ValueTask`, `CancellationToken`, and `IAsyncDisposable`. ADR-0002's blanket prohibition on `System.Threading` in Shared would also reject those passive contract types, leaving the accepted transport port without a legal owner.

The restriction was intended to prevent Shared code from creating threads, timers, uncontrolled tasks, or hidden scheduling. A cancellation value passed through a boundary does not schedule work and is required for bounded shutdown and caller-controlled lifetime.

## Decision

Create `shared/realtime` as a Shared submodule compiled from one source set for Unity and `.NET Standard 2.1`. It owns engine- and transport-independent realtime contracts, including `IRealtimeTransport` and its value types.

Within `shared/realtime/Runtime` only, `System.Threading` and `System.Threading.Tasks` may be referenced solely for passive contract types: `CancellationToken`, `ValueTask`, and `IAsyncDisposable`. Thread creation, `CancellationTokenSource`, timers, `Task`, `Task.Run`, `TaskFactory`, `ThreadPool`, sleeps, sockets, and I/O remain forbidden. `shared/gameplay` retains the original prohibition unchanged.

Fantasy Session, KCP control blocks, sockets, native buffers, and framework disposal handles terminate in adapters and never appear in these contracts.

## Validation and rollback

Architecture validation applies the exception by exact path and rejects active scheduling constructs there. Unity EditMode and .NET contract tests compile the same source. If Unity compatibility fails, replace the offending passive surface with a project-owned equivalent value type; do not duplicate the contract source or expose Fantasy APIs.
