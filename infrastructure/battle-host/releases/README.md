# Battle Host Release Ledger

This directory retains the immutable manifest emitted by each successful Battle Host publication. GitHub Actions artifacts are supporting evidence and may expire; a deployment or rollback identity must remain reviewable from repository history.

Each `v<semantic-version>.json` file is copied byte-for-byte from the `battle-host-release-v<semantic-version>` artifact produced by the protected release workflow. Records are append-only:

- never edit, rename, or delete a published record;
- never reuse a version, source commit, source tag, or image digest;
- deploy the `image` digest, not either discovery tag;
- verify a new record before review with `tools/release/verify-battle-host-release.sh`;
- resolve an operator-supplied version with `tools/release/resolve-battle-host-release.sh`.

The ledger is an audit and deployment-selection input. Adding a record does not publish, deploy, restart, drain, or roll back a Host.
