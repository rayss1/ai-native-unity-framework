# Battle Host production image

This directory owns the Linux x64 production container contract for `AiNative.BattleHost`. The multi-stage build uses immutable .NET SDK/runtime image references supplied by CI, publishes the `net10.0` Host, runs as the base image's non-root user, and records the source, Fantasy, and protocol identities as OCI labels and environment values.

The image listens on HTTP port `8080` and Fantasy KCP UDP port `22000`. It contains one application-owned default `Fantasy.config`; production deployment must replace it with a reviewed read-only mount. Evaluation-only administrative endpoints stay disabled unless `AINATIVE_ENABLE_EVALUATION_ENDPOINTS=true` is explicitly supplied by a validation environment.

Use the Compose contract only with an immutable image digest and an explicit configuration path:

```bash
AINATIVE_BATTLE_HOST_IMAGE='registry.example/ainative/battle-host@sha256:<digest>' \
AINATIVE_FANTASY_CONFIG='/absolute/path/Fantasy.config' \
docker compose -f infrastructure/battle-host/compose.yaml up -d
```

The production validation workflow builds and probes this Dockerfile but does not push an image. Publishing and promotion remain explicit release operations.
