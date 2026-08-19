# Runtime successor evaluation image

This image publishes the pinned Fantasy example Server without creating a product Server Host. CI resolves the exact multi-platform digests for the SDK and runtime tags before the build, passes immutable `tag@sha256` references to Docker, and records those references with the parent, Fantasy, and protocol identities.

The container runs as the .NET base image's non-root `$APP_UID`. Fantasy logs are written to `/Logs`; configuration remains the application-owned `/app/Fantasy.config` and can be replaced through an explicit read-only mount during evaluation.

This artifact is internal evidence for proposed ADR-0012. It must not be published or promoted until the Fantasy license review is complete.
