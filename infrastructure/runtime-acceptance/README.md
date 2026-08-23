# Runtime acceptance candidate image

This internal-only image publishes the gated WS-16 Battle Host with the exact Fantasy package stored in the pinned submodule. CI resolves immutable .NET SDK and ASP.NET runtime image digests, builds for Linux x64, runs as the base image's non-root user, proves a real Fantasy KCP Login/Join/Input/Snapshot/Reconnect loopback, probes readiness/drain and graceful SIGTERM, and records source, Fantasy, protocol, and image identities.

The image listens on HTTP port `8080` and Fantasy KCP UDP port `22000`. `Fantasy.config` is application-owned and the published image contains exactly one default copy. Evaluation deployments can replace it without rebuilding the image by mounting a reviewed file read-only at `/app/Fantasy.config`; port and topology changes must remain consistent with the exposed UDP mapping and recorded provenance.

The workflow does not push or otherwise distribute this image. Legal approval, exact-final-commit Unity evidence, impairment/backpressure and deterministic replay evidence, and the qualified 60-minute 64-bot soak remain release gates. The image must not be promoted while ADR-0012 is Proposed.
