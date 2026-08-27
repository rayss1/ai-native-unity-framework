# Battle Host Release Ledger

This directory retains the immutable manifest emitted by each successful Battle Host publication. GitHub Actions artifacts are supporting evidence and may expire; a deployment or rollback identity must remain reviewable from repository history.

Each `v<semantic-version>.json` file is copied byte-for-byte from the `battle-host-release-v<semantic-version>` artifact produced by the protected release workflow. Records are append-only:

- never edit, rename, or delete a published record;
- never reuse a version, source commit, source tag, or image digest;
- deploy the `image` digest, not either discovery tag;
- verify a new record before review with `tools/release/verify-battle-host-release.sh`;
- resolve an operator-supplied version with `tools/release/resolve-battle-host-release.sh`.

The ledger is an audit and deployment-selection input. Adding a record does not publish, deploy, restart, drain, or roll back a Host.

## Current canary pair

- Candidate: [`v0.2.0`](v0.2.0.json), immutable digest `sha256:f6fd37c7ab048a99327dbe7f4f0e60d5689cd0f2fbc6c28aca51592aa7d0bbb8`.
- Project-owner-designated fallback: [`v0.1.0`](v0.1.0.json), immutable digest `sha256:a350b8329d142a07026ac0f0bb28a67baf106cfae3fcb1e292f0cfe17fdb7d5c`.

Both records use Fantasy `f8bed0d464924f159d46498f1311206ea0694be8`, protocol identity `726a80d6a762913b87fe840f0be9086224598bcaadb0e4a7d4e3e44856c0b92c`, and configuration identity `fc0714bcbe7c8c673cf638506a45f3a4440585f4024ff78048434346ab8a66e4`. This designation does not deploy either image or approve a canary environment; the operator must still name the target, admission procedure, observation window, and drain/switch authority.
