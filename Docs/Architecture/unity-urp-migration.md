# Unity / URP migration

Baseline: Unity `6000.3.23f1` (`09d2ecc7fb28`), Universal RP `17.3.0`.
Authority: [ADR-0016](../ADR/0016-unity-6000-3-23-and-urp.md).

`client/UnityProject/Assets/AiNative.BattleClient/Rendering` contains the Universal Renderer and pipeline assets. Graphics and all six quality levels select that pipeline. The previously selected quality index 2 is preserved.

`Resources/BattleClient/Player.mat` and `Floor.mat` retain URP/Lit shaders and provide the greybox colors. The runtime uses shared material references without allocating material instances. The Built-in Standard always-included workaround is removed.

Unity generated the assets and project version through the installed editor. Recreate missing configuration from the repository root with:

```powershell
unity run client/UnityProject --editor-version 6000.3.23f1 --timeout 600 -- -executeMethod AiNative.Client.Editor.BattleClientRendering.ConfigureUrp
```

Both desktop build entry points validate pipeline assignments, renderer and materials. Existing assets are retained when configuration is run again; custom materials are not silently overwritten. Refer to [manual validation](unity-manual-validation.md) for clean-commit qualification.

## Development evidence

Working-tree migration artifacts are retained under `artifacts/unity-6000.3.23-urp`. Import, configuration, final Windows build and validation commands exited zero. The final run passed 51 EditMode and two PlayMode tests with zero failures/skips. Local reconnect advanced epoch 4 to 5. The graphics-enabled Tencent smoke advanced epoch 11 to 12 and acknowledgement 31 to 34, with zero dropped frames and no exception or shader error in its log.

The retained `greybox.png` was captured from the live Unity Editor camera using unity-cli and visually checked: blue character, dark floor, URP lighting, no magenta material. This is Editor rendering evidence; the separate Windows Player was also built and run with graphics enabled. The final Player and Unity 6000.3.23f1 Editor were opened for continued use.

Graphics-enabled smoke exposed a pre-existing shutdown cleanup issue: C# `is not null` accepted Unity objects whose native instance was already destroyed. Cleanup now uses Unity's null comparison, destroys the camera with its owning player, and clears owned references. The final smoke exits without that exception.

Architecture validation and PowerShell/Bash syntax checks passed. Unity serialized additional render-debug inputs and patch-version PlayerSettings fields; these generated settings are retained. Default Unity YAML empty scalars include trailing spaces, so whitespace review excludes blank-at-end-of-line for those generated assets.

The working-tree runner copy bypasses only the clean-checkout pre/postconditions so the uncommitted requested migration can be exercised. The checked-in qualification scripts still require a clean commit. This evidence is not exact-commit release qualification. Server and protocol code were not changed. macOS, Android/iOS IL2CPP, fixed Regional impairment, performance budgets and credentialed CI have not been rerun on this editor version.

The installed editor's package manifest selects URP 17.3.0, consistent with the [Unity Graphics changelog](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/CHANGELOG.md). Historical validation documents retain their original editor versions.
