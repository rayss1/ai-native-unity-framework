#!/usr/bin/env python3
"""Rebuild or verify Unity protocol binaries using pinned NuGet and generated sources."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PACKAGE = ROOT / "packages/com.ainative.client.protocol.protobuf"
STAGE = ROOT / "artifacts/unity-protocol"
VERSIONS = {"Google.Protobuf": "3.35.1", "Grpc.Tools": "2.83.0"}

def digest(data):
    return hashlib.sha256(data).hexdigest()

def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail on binary, source or dependency drift; do not update the package")
    args = parser.parse_args()
    for project_path in ["shared/protocol/Protocol.Shared.csproj", "tools/ProtocolCodegen/ProtocolCodegen.csproj"]:
        for reference in ET.parse(ROOT / project_path).iter("PackageReference"):
            name = reference.get("Include")
            if name in VERSIONS and reference.get("Version") != VERSIONS[name]:
                raise RuntimeError("Pinned dependency mismatch in " + project_path + ": " + name)
    STAGE.mkdir(parents=True, exist_ok=True)
    sources = sorted((ROOT / "shared/generated/csharp").rglob("*.cs"))
    source_hashes = {}
    for source in sources:
        relative = source.relative_to(ROOT).as_posix()
        content = source.read_text(encoding="utf-8-sig").replace("\r\n", "\n").encode("utf-8")
        target = STAGE / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        source_hashes[relative] = digest(content)
    # Explicit source items prevent removed schemas from surviving in the staging directory.
    compile_items = "".join('<Compile Include="' + s.relative_to(ROOT).as_posix() + '" />' for s in sources)
    project = STAGE / "UnityProtocol.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
        '<TargetFramework>netstandard2.1</TargetFramework><AssemblyName>AiNative.Protocol</AssemblyName>'
        '<EnableDefaultCompileItems>false</EnableDefaultCompileItems><DebugType>none</DebugType>'
        '<Deterministic>true</Deterministic><ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>'
        '<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>'
        '<Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings>'
        '<PathMap>$(MSBuildProjectDirectory)=/_/</PathMap>'
        '</PropertyGroup><ItemGroup>' + compile_items +
        '<PackageReference Include="Google.Protobuf" Version="3.35.1" />'
        '</ItemGroup></Project>', encoding="utf-8")
    run("dotnet", "build", str(project), "-c", "Release", "-t:Rebuild", "--nologo")
    assets = json.loads((ROOT / "artifacts/obj/UnityProtocol/project.assets.json").read_text(encoding="utf-8"))
    package_roots = [Path(p) for p in assets["packageFolders"]]
    runtime = next((p / "google.protobuf/3.35.1/lib/netstandard2.0/Google.Protobuf.dll" for p in package_roots
                    if (p / "google.protobuf/3.35.1/lib/netstandard2.0/Google.Protobuf.dll").exists()), None)
    if runtime is None:
        raise RuntimeError("Pinned Google.Protobuf runtime was not restored")
    binaries = {
        "AiNative.Protocol.dll": ROOT / "artifacts/bin/UnityProtocol/Release/netstandard2.1/AiNative.Protocol.dll",
        "Google.Protobuf.dll": runtime,
    }
    dependencies = {name: {"sha512": item["sha512"], "packaged": name.startswith("Google.Protobuf/")}
                    for name, item in sorted(assets["libraries"].items()) if item["type"] == "package"}
    inputs = [ROOT / "shared/protocol/Protocol.Shared.csproj", ROOT / "tools/ProtocolCodegen/ProtocolCodegen.csproj", ROOT / "global.json", Path(__file__).resolve()]
    inputs += sorted((ROOT / "shared/schemas").rglob("*.proto"))
    for source in inputs:
        source_hashes[source.relative_to(ROOT).as_posix()] = digest(source.read_text(encoding="utf-8-sig").replace("\r\n", "\n").encode("utf-8"))
    vendor_unsafe = ROOT / "server/vendor/Fantasy/Fantasy.Packages/Fantasy.Unity/Runtime/Plugins/Other/System.Runtime.CompilerServices.Unsafe.dll"
    if not vendor_unsafe.exists():
        raise RuntimeError("Initialize the pinned Fantasy submodule before packaging")
    vendor_hash = digest(vendor_unsafe.read_bytes())
    lock = {"vendorProvided": {vendor_unsafe.relative_to(ROOT).as_posix(): vendor_hash}, "format": 1, "versions": VERSIONS, "targetFramework": "netstandard2.1", "runtimeAsset": "lib/netstandard2.0/Google.Protobuf.dll",
            "dependencies": dependencies, "unityProvided": ["System.Memory", "System.Buffers", "System.Numerics.Vectors", "System.Runtime.CompilerServices.Unsafe"],
            "sourcesSha256": source_hashes, "artifactsSha256": {name: digest(path.read_bytes()) for name, path in binaries.items()}}
    lock_text = json.dumps(lock, indent=2, sort_keys=True) + "\n"
    lock_path = PACKAGE / "ThirdPartyNotices~/dependencies.lock.json"
    plugins = PACKAGE / "Plugins"
    if args.check:
        if not lock_path.exists() or lock_path.read_text(encoding="utf-8") != lock_text:
            raise RuntimeError("Unity protocol dependency/source/artifact lock drift; regenerate with package_unity.py")
        for name, path in binaries.items():
            if not (plugins / name).exists() or (plugins / name).read_bytes() != path.read_bytes():
                raise RuntimeError("Unity protocol binary drift: " + name)
    else:
        plugins.mkdir(parents=True, exist_ok=True)
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        for name, path in binaries.items():
            shutil.copyfile(path, plugins / name)
        lock_path.write_text(lock_text, encoding="utf-8", newline="\n")
    # Duplicate runtime dependencies can silently change assembly resolution in Unity.
    for base in [ROOT / "packages", ROOT / "client/UnityProject/Assets", ROOT / "shared/gameplay", ROOT / "shared/realtime", ROOT / "server/vendor/Fantasy/Fantasy.Packages/Fantasy.Unity"]:
        for dll in base.rglob("*.dll"):
            if any(part in {"bin", "obj"} or part.endswith("~") for part in dll.parts):
                continue
            if dll.name in binaries and dll.parent != plugins:
                raise RuntimeError("Duplicate protocol assembly: " + str(dll))
            if dll.name.removesuffix(".dll") in lock["unityProvided"] and dll != vendor_unsafe:
                raise RuntimeError("Do not bundle Unity-provided assembly: " + str(dll))
    print("Unity Protobuf artifacts " + ("verified" if args.check else "packaged") + "; no duplicate framework assemblies.")

if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
