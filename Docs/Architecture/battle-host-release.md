# Battle Host Release Procedure

Status: Controlled publication procedure for the first vertical slice
Last updated: 2026-08-25

This procedure implements the immutable-image, provenance, and rollback requirements in [ADR-0010](../ADR/0010-observability-and-deployment.md) and [ADR-0012](../ADR/0012-server-runtime-successor.md). It publishes only the Linux x64 Battle Host image; it does not deploy a process, mutate an environment, or introduce an orchestrator.

## Release identity

A release is the tuple of:

- semantic version;
- exact parent commit and Git tree;
- exact Fantasy gitlink/package;
- protocol and application-configuration SHA-256 identities;
- immutable SDK and runtime base-image digests;
- qualified GitHub Actions run;
- GHCR image digest, embedded BuildKit provenance/SBOM, and GitHub artifact attestation.

Tags are discovery aliases, not deployment identities. Deployments use `ghcr.io/rayss1/ai-native-battle-host@sha256:<digest>`. The workflow creates `sha-<40-character-commit>` first and promotes `v<semantic-version>` only after the published digest passes hardened smoke and attestation verification. It never creates or updates `latest`.

## Qualification gate

The manual release workflow accepts only the current `main` HEAD. The supplied production-validation run must:

1. be the `Battle Host production validation` workflow triggered by `push` or manual dispatch on `main`;
2. be completed successfully for the exact release commit;
3. retain unexpired provenance and 60-minute soak artifacts;
4. match the checked-out Fantasy gitlink and protocol hash;
5. pass the qualified Tick, duration, allocation, and MTU assertions.

The repository-owned `tools/release/verify-battle-host-qualification.sh` applies the same checks locally and in the publication workflow. Product CI executes positive and negative contract fixtures for source identity, artifact expiry, and performance-budget rejection.

If the current `main` commit has no production-validation run because its changes were outside that workflow's path filter, manually dispatch `Battle Host production validation` on `main`. Non-pull-request runs execute the full qualified soak; pull requests still require the explicit `qualified-soak` label.

## Publication

Before the first release, configure the GitHub `battle-host-release` environment with the desired required reviewers. A repository maintainer then opens **Actions > Battle Host release publication > Run workflow** on `main` and supplies:

- `version`: semantic version without a leading `v`;
- `qualified_run_id`: the exact successful main production-validation run;
- `confirmation`: the literal `PUBLISH`.

The workflow uses only the repository `GITHUB_TOKEN`; no personal access token is stored. Its write permissions are scoped to packages, attestations, and the short-lived OIDC identity. Publication is serialized, and any existing version or source tag causes a hard failure rather than an overwrite.

## Verification and deployment

Verify an image before deployment:

```bash
gh attestation verify \
  oci://ghcr.io/rayss1/ai-native-battle-host@sha256:<digest> \
  --repo rayss1/ai-native-unity-framework
```

Deploy the exact digest with a reviewed read-only configuration:

```bash
AINATIVE_BATTLE_HOST_IMAGE='ghcr.io/rayss1/ai-native-battle-host@sha256:<digest>' \
AINATIVE_FANTASY_CONFIG='/absolute/path/Fantasy.config' \
docker compose -f infrastructure/battle-host/compose.yaml up -d
```

The release manifest artifact records every identity needed to reproduce or audit the image. Registry publication does not imply that the image was deployed.

## Rollback and failure containment

- Keep the previous qualified digest and compatible configuration throughout rollout.
- Drain the new Host, then point Compose at the previous digest; never retag the previous artifact as the failed version.
- Protocol changes remain additive during the rollback window, so the prior Host decoder continues to accept deployed traffic.
- A failure before `v<version>` promotion can leave only a source-qualified `sha-<commit>` image. Investigate it as an unpromoted artifact; do not reuse or overwrite the tag.
- A failure after version promotion requires a new version after correction. Published version/source tags remain immutable and auditable.
- Changing the Fantasy baseline, license scope, protocol compatibility, or release distribution model reopens the corresponding ADR/legal gates.
