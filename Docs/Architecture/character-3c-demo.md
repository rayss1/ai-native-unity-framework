# Local character 3C demo

Status: Local prototype; not an authoritative movement replacement

## Boundary

`com.ainative.client.character` owns reusable Unity-local input, movement, camera and
animation presentation. `Assets/AiNative.CharacterDemo` owns scene composition, V-bot,
weapon bindings and generated animation/material assets. The application references
the package; no package references application art. Shared, Server and the existing
60 Hz arena prediction contracts are unchanged (ADR-0002, ADR-0005, ADR-0007).

CharacterController is an explicitly local motor. Future network integration must supply
movement state from the authoritative/predicted path and adapt presentation; it cannot
silently run this motor alongside ArenaMovement or treat PhysX as cross-runtime deterministic.
Rollback removes the local package manifest/testables entries and the demo scene; original
art, battle scenes and network state are unaffected.

## Resource audit

- `VBOT2.0_Custom.prefab`: valid Humanoid Avatar, four skinned meshes, no missing scripts;
  original Animator has no controller and enables root motion. Dedicated demo instance
  uses the supplied Invector@ShooterMelee graph with root motion off.
- `VBOT_LOD Variant.prefab`: 13 missing scripts and no usable Animator found; excluded.
- Art animation inventory: 14 FBX, 12 standalone clips, seven controllers, eight masks.
  Basic free movement supplies idle/walk/run/sprint/crouch. Basic actions supplies jump,
  falling, landing and roll. Shooter supplies strafe, rifle poses and fire; melee uses
  WeakAttack_UnarmedC. Ladder, vault, reload, hit reactions and other combat clips are unused.
- The demo uses an application-owned copy of `Art/Animations/Shooter/Invector@ShooterMelee.controller`,
  preserving all eight layers and synchronized motion overrides. Missing Invector Behaviour
  references are removed from the working graph; the local animation driver supplies its
  parameters and dispatches jump, roll and fixed unarmed C attacks. Original art stays unchanged.
  The builder retains the working controller GUID on rebuild; upstream graph updates require
  explicitly refreshing that copy. Unused bow/throw/sword branches retain source limitations.
- Clip copies remove events that call unavailable Invector components. Horizontal root translation
  is extracted (Bake Into Pose XZ off) and discarded by `applyRootMotion=false`. The previous
  generator baked XZ into the pose, causing the mesh to travel then snap back each loop even
  though Animator root motion was disabled. Original Y/rotation and loop settings are preserved.
- Imported skeleton scale is .01. Weapon attachment preserves metre world scale and uses
  a pose-calibrated socket plus left-hand IK. Original materials are preserved; the demo
  creates URP copies where conversion is needed.

## Local behavior

Rifle mode now fires visible projectiles at 70 m/s (100 m maximum range), with unlimited
ammunition and 25 damage per click. Camera-centre aiming selects the destination; muzzle
segment casts enforce walls and exclude the owner. An overlap check prevents shooting
out of a wall enclosing the muzzle. Three 100 HP training targets at the far end of the
course flash on hit, show HP, turn gray on defeat and reset after three seconds.
The HUD shows shot/hit counts and the last impact. Melee remains animation-only.
This is local PhysX damage, not a server-authoritative combat implementation.

### Rifle validation (2026-10-15)

- Character package PlayMode: 24/24 passed, including six rifle/action tests for thin
  colliders, wall obstruction, muzzle overlap, owner exclusion, damage/death/reset,
  disable cleanup and grounded action gating.
- Real Invector rig animation regression: 1/1 passed. ArchitectureCheck passed.
- Windows Development Player: smoke exited 0 and logged `CHARACTER_DEMO_SMOKE_PASS`;
  it drives an accepted motor shot through the configured camera/muzzle into a real
  scene target and asserts exactly 25 HP damage, alongside movement/jump/melee animation.
- Evidence: `artifacts/character-demo/rifle-playmode-results.json`,
  `rifle-animation-results.json`, `rifle-build-result.json`, `rifle-player-smoke.log`
  and `rifle-player-exit.txt`. These are local worktree checks, not clean release evidence.
- BattleHost publication succeeded, but automatic approval review blocked starting
  the local host process with only `blocked by policy` supplied as the reason.
  No new live network test result is claimed. Character projectiles/HP are not wired
  to the authoritative arena protocol; network combat remains separate integration work.
- Visual tracer readability, final weapon pose and interactive aiming feel still require
  human acceptance; the Player smoke uses `-nographics`.

The Input System 1.20.1 Gameplay/UI maps share a per-reader action asset. Gameplay is
disabled on cursor release/focus loss; edges clear immediately. Recapture waits until
the UI click is released. Mouse delta is sampled once per render update. One local player
and one explicitly wired InputSystemUIInputModule are supported by the demo.

Roll wins simultaneous attack and locks its initial direction; melee and roll reject
mode changes and action reentry. Grounded/coyote jump and buffered jump use seconds.
Crouch expansion requires capsule clearance. Only presentation is affected by animations;
movement collisions always go through CharacterController. Aim walking defaults to 1.8 m/s
to match the available walk clips. The camera retracts immediately on collision and restores
smoothly, excluding character layer 2. A stalled frame caps simulation delta at .05 seconds.

See the package README for controls, generator, tests and remaining compatibility limits.

## Verification observed on 2026-10-14

Number-key follow-up: corrected Input System paths from `digit1/2/3` to `1/2/3` and
regenerated the wrapper. The package PlayMode suite passed 18/18, including keyboard
press/release through the reader into motor mode changes. Live Editor keyboard injection
confirmed Shooter mode and Invector `MoveSet_ID=1`. Evidence: `number-key-results.json`
under `artifacts/character-demo`. The Player build below predates this binding-only fix.
After staging new Unity assets, plain `git diff --cached --check` reports Unity-serialized
empty-value trailing spaces; the check with `core.whitespace=-blank-at-eol` passes.

Follow-up regression: `AiNative.CharacterDemo.PlayModeTests` runs the actual Invector rig
through multiple running loops and checks model-to-capsule drift and discontinuities.
It passed after changing the controller and correcting horizontal root extraction (1/1).
The package PlayMode suite also passed again (15/15), as did architecture validation and
`git diff --check`. The replacement Windows Development Player built successfully with zero
errors and five warnings, then exited 0 with `CHARACTER_DEMO_SMOKE_PASS`. Evidence:
`artifacts/character-demo/invector-regression-results.json`, `followup-playmode-results.json`,
`invector-build-result.json` and `invector-player-smoke.log`. An initial broad `AiNative`
test filter also selected network tests and was cancelled; only the targeted results above
are claimed. Interactive visual acceptance of the replacement graph remains outstanding.
The following records describe verification of the initial implementation:

Local dirty working tree, Windows, Unity 6000.3.23f1, Input System 1.20.1:

- Unity compilation: passed. EditMode: 1 passed; PlayMode: 15 passed, zero failures/skips.
  These cover input lifecycle, recovery-click isolation, collision, action mutual exclusion,
  low ceilings, camera obstruction/self exclusion and 30/60/120 Hz command-driven movement.
- `dotnet run --project tools/ArchitectureCheck -- --root .`: passed; `git diff --check`: passed.
- Official input-wrapper regeneration produced identical SHA-256 before and after.
- StandaloneWindows64 Development Player: succeeded, zero errors, five warnings. The warnings
  concern disabled Player Pipeline debugging and pre-existing `defaultDamageEffect` Resources
  content (disabled particle module and missing scripts); that imported content was not modified.
- Built Player launched with `-batchmode -nographics --character-demo-smoke`: exit 0 and
  `CHARACTER_DEMO_SMOKE_PASS` for composition, grounded movement, jump, rifle and melee.
- Editor camera captures checked material rendering and Humanoid idle/rifle/aim poses. Separate
  hand-local hip/aim sockets were calibrated because the source poses use different hand rotations.

Local evidence: `artifacts/character-demo/playmode-results.json`, `build-summary.json`,
`player-smoke.log`, and pose captures. These ignored local outputs are not a clean-commit release
qualification. No performance budget claim is made; no mobile/IL2CPP, full network suite or
interactive Player graphics qualification was run. Sustained animation/foot sliding and feel
across frame rates still need human playtesting; frame-rate checks above are automated motor tests.
