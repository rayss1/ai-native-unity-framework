# Fantasy License Project-Owner Approval

Status: Approved by project owner
Date: 2026-08-24
WS-26 extensions: 2026-08-31 and 2026-09-01
Scope: Fantasy fork `rayss1/Fantasy` at `f8bed0d464924f159d46498f1311206ea0694be8`; tracked packages `Fantasy-Net` `2026.1.1003` and `Fantasy.Unity` `2026.1.1001`; the project-owned Battle Host composition; and Windows x64 plus macOS ARM64 client use and distribution through `com.ainative.client.fantasy`

## Recorded approval

The project owner provided the following written decision:

> 我以项目负责人身份接受 Fantasy 当前许可证及其限制，确认项目相关主体不是被禁用实体，并批准商业使用、修改和分发；相关法律风险由项目方承担。

English record: the project owner accepts the current Fantasy license and its restrictions, confirms that the project and its related entities are not the prohibited entity, approves commercial use, modification, and distribution, and accepts the associated legal risk on behalf of the project.

For WS-26, the project owner explicitly extended that same acceptance to `Fantasy.Unity` `2026.1.1001` from the same approved commit and to Windows client use and distribution on 2026-08-31. On 2026-09-01 the project owner additionally approved macOS client use and distribution for that same version and commit. These extensions do not approve another Fantasy commit, package version, platform, license text, or distribution model.

## Conditions and controls

- Preserve the Fantasy copyright and license text in source and substantial distributions.
- Retain the exact Fantasy license and `packages/com.ainative.client.fantasy/THIRD-PARTY-NOTICES.md` beside approved Windows and macOS client distributions that contain Fantasy.Unity; each Player build/packaging path must fail if either notice cannot be staged.
- Keep the fork and every consumed package pinned to an exact reviewed commit/version.
- Do not remove or weaken the entity-specific restriction when redistributing the licensed work.
- Reopen legal review if the copyright holder, license text, prohibited-entity relationship, distribution model, or adopted upstream baseline changes.
- This record is project-owner risk acceptance for the stated scope; it does not claim to be an independent legal opinion or grant rights beyond the current license.

This approval closes the ADR-0012 project legal gate for the pinned baseline. Release owners remain responsible for preserving notices and verifying that future artifacts stay within this scope.
