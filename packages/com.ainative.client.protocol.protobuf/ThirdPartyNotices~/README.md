# Unity Protobuf dependencies

`Plugins/AiNative.Protocol.dll` is built from the one repository-owned set of generated C# files in `shared/generated/csharp`. `Plugins/Google.Protobuf.dll` is the unmodified NuGet 3.35.1 `lib/netstandard2.0` runtime (BSD-3-Clause; see the included upstream LICENSE). The exact upstream commit is `35cd01f9fe9afbeea38cc7b979a3b6bfcde82c03`.

`dependencies.lock.json` records normalized source SHA-256 hashes, artifact SHA-256 hashes and restored NuGet package SHA-512 identities, including transitive dependencies. Sources are normalized to LF in an ignored staging directory, paths are mapped, and debug symbols are omitted so the binary is reproducible across checkout paths and line-ending settings. The root SDK pin controls compiler selection. Assembly informational version excludes the changing Git commit ID; source identity is carried by the lock instead, so committing the packaged DLL does not invalidate its own hash.

Regenerate schemas using the existing `tools/ProtocolCodegen/ProtocolCodegen.csproj` (Grpc.Tools 2.83.0), then run `python tools/ProtocolCodegen/package_unity.py`. Verify without updating tracked artifacts using `python tools/ProtocolCodegen/package_unity.py --check`. CI checks generated-source drift before verifying the Unity binaries. Assemblies use explicit references so Google.Protobuf does not leak into prediction runtime.

Unity 6000.3 supplies System.Memory, System.Buffers, System.Numerics.Vectors and System.Runtime.CompilerServices.Unsafe. The pinned Fantasy vendor already includes an overridable Unsafe DLL (disabled for Editor); its exact hash is also locked, and no second copy is introduced. Do not copy these NuGet DLLs into Assets or packages: duplicate platform assemblies can break Mono/IL2CPP resolution. The pinned editor contains Memory/Buffers facades and Unsafe in NetStandard/EditorExtensions; Android support also provides Unsafe. Actual player compilation and device execution remain required acceptance checks, not guaranteed by packaging verification.

`Runtime/link.xml` preserves generated parsers/descriptors and Protobuf for IL2CPP. This is conservative until build-size measurements justify selective preservation.

Upgrade the package/version references, generation tool and lock together; run wire-compatibility, allocation, Unity and IL2CPP tests before release. Roll back the generated sources, binaries and dependency lock as one unit. No runtime format negotiation is introduced.
