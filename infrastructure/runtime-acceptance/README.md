# Runtime acceptance candidate image

This internal-only image publishes the gated WS-16 Battle Host with the exact Fantasy package stored in the pinned submodule. CI resolves immutable .NET SDK and ASP.NET runtime image digests, builds for Linux x64, runs as the base image's non-root user, probes readiness/drain and graceful SIGTERM, and records source, Fantasy, protocol, and image identities.

The workflow does not push or otherwise distribute this image. Legal approval, licensed Unity validation, real KCP room integration, replay/reconnect/impairment evidence, and the qualified 64-bot soak remain release gates. The image must not be promoted while ADR-0012 is Proposed.
