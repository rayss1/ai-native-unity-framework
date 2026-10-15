# Local character 3C

Unity 6000.3 / Input System 1.20.1. A local-only PhysX prototype for keyboard and mouse;
this package does not implement server authority, prediction or networking.
The package contains reusable components; the application owns art and scene composition.

## Try it

Open `Assets/AiNative.CharacterDemo/Scenes/CharacterDemo.unity` by itself and press Play.
Use **AiNative > Character Demo > Build** to regenerate the dedicated demo artifacts.
The generator refuses to run while any open scene is dirty. It never edits original art
or existing battle scenes. Generated artifacts are owned by the builder: edit its source
or create a separate variant if you need changes to survive rebuilding.

| Input | Action |
| --- | --- |
| WASD / mouse | Move / orbit camera |
| Left Shift / Left Ctrl | Sprint / walk |
| Space / C / Left Alt | Jump / toggle crouch / roll |
| 1 / 2 / 3 | Free / rifle / unarmed melee |
| Right mouse / left mouse | Aim in rifle mode / action |
| Escape | Release cursor; click outside UI and release to resume |

The course contains a 30-degree ramp, 20-cm steps, 1.25-m low ceiling, walls and jump
platforms and three training targets. Falling below -12 m respawns the character. Rifle
shots launch visible local projectiles (70 m/s, 100 m range, 25 damage). Targets have 100 HP
and reset three seconds after defeat. Press 2, aim with RMB and click LMB once per shot.
Walls block projectiles. Ammunition is unlimited; melee remains animation-only.

## Composition and lifecycle

Add `CharacterController`, `CharacterMotor` and `CharacterInputReader` to a unit-scale root.
Put the root and every character/weapon child on Ignore Raycast (layer 2); the default
environment masks exclude this layer. Keep collidable level geometry on Default.
Configure the motor and `CharacterOrbitCamera` with explicit references, and place
`CharacterAnimationDriver` on the same object as the Humanoid Animator for IK callbacks.
Use an independent camera object. `CharacterDemoHud` is an optional development overlay.

`CharacterAnimationDriver` targets the supplied Invector@ShooterMelee parameter/state contract.
The demo builds an eight-layer working copy, removes missing Invector Behaviour references,
and uses independent clips with horizontal Bake Into Pose disabled. Animator root motion
stays off; otherwise baked horizontal pose movement can visibly snap back at loop boundaries.
Original art is retained. Run `AiNative.CharacterDemo.PlayModeTests` for the real-rig loop regression.

`CharacterInputReader.ConfigureUi` must run before Awake, or be serialized by the builder.
Each reader owns one generated `CharacterControls` instance. Gameplay is enabled only
while captured; UI stays enabled while the component is enabled. Disable clears commands,
unsubscribes and releases the cursor; destroy disposes the actions and UI references.
The supported sample is one local player/EventSystem. Input updates dynamically, never
by legacy `UnityEngine.Input`; pointer deltas are degrees-per-pixel and are not multiplied
by delta time. The first click after releasing the cursor is reserved for recapture.

Movement consumes one input frame per Update. `Simulate(frame, yawDegrees, deltaSeconds)`
is also available for local command-driven tests, must run on the Unity main thread, and
must not be called in addition to automatic Update in the same frame. Distances are metres,
speed metres/second, time seconds. Root motion is disabled. Delta time is capped at .05 s
to bound a stalled frame; this demo does not promise wall-clock catch-up after stalls.

Regenerate the checked-in input wrapper through **AiNative > Character Demo > Regenerate
Input Wrapper** after changing `Runtime/Input/CharacterControls.inputactions`. The generator
uses Unity's pinned InputActionCodeGenerator. No reflection or runtime code generation is used.

## Validation and compatibility

Run the `AiNative.Client.Character.EditMode.Tests` and `AiNative.Client.Character.PlayMode.Tests`
assemblies in Unity Test Runner. Input tests use Unity.InputSystem.TestFramework. Editor
menu **Build Windows Player** creates only the demo at `artifacts/character-demo/CharacterDemo.exe`
without replacing project build scene settings.

The built Player accepts `--character-demo-smoke` to run a finite, local movement/action
smoke and exit with 0 on success (1 on a failed assertion). For CI-style verification use
`-batchmode -nographics --character-demo-smoke -logFile <absolute-path>`; require both exit 0
and `CHARACTER_DEMO_SMOKE_PASS` in the log. This does not validate Player rendering.

The battle application's keyboard, mouse and UI paths also use Input System as of
2026-10-15. The character demo remains a separate local scene; open it alone.
Its motor, projectiles and HP are not connected to the authoritative Arena simulation.
No Shared/Server contracts or global timestep change.
Input System 1.20.1 is pinned in package.json; its license is the Unity Companion License
(see the installed package LICENSE.md). Upgrades require regenerating the wrapper and
rerunning input lifecycle tests; rollback restores the previous dependency pin and wrapper.
No other third-party runtime dependency is introduced. Imported Invector/V-bot assets remain
application-owned, with their existing provenance/licensing; this package does not redistribute them.
