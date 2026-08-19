using System.Text.Json;
using NUnit.Framework;

namespace AiNative.Tools.ArchitectureCheck.Tests;

public sealed class ArchitectureValidatorTests
{
    private string _fixtureRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "ainative-architecture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    [Test]
    public void ValidFixtureAllowsClientAndToolsToDependOnSharedAndEditorOnRuntime()
    {
        Write("shared/gameplay/Runtime/AiNative.Gameplay.asmdef", Asmdef("AiNative.Gameplay"));
        Write("client/Runtime/AiNative.Client.asmdef", Asmdef("AiNative.Client", "AiNative.Gameplay"));
        Write("client/Editor/AiNative.Client.Editor.asmdef", Asmdef("AiNative.Client.Editor", "AiNative.Client"));
        Write("tools/Tool.csproj", Project("AiNative.Tools.Tool", "../shared/gameplay/Gameplay.Shared.csproj"));
        Write("shared/gameplay/Gameplay.Shared.csproj", Project("AiNative.Gameplay"));
        WriteSolution("shared/gameplay/Gameplay.Shared.csproj", "tools/Tool.csproj");

        ArchitectureValidationResult result = Validate();

        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void UnityManifestCanReferenceAnInternalSharedPackage()
    {
        Write("shared/gameplay/package.json", JsonSerializer.Serialize(new
        {
            name = "com.ainative.gameplay",
            version = "0.1.0",
            dependencies = new Dictionary<string, string>(),
        }));
        Write("client/UnityProject/Packages/manifest.json", JsonSerializer.Serialize(new
        {
            dependencies = new Dictionary<string, string>
            {
                ["com.ainative.gameplay"] = "file:../../../shared/gameplay",
            },
        }));

        ArchitectureValidationResult result = Validate();

        Assert.That(result.Diagnostics, Is.Empty);
    }

    [TestCase("bin")]
    [TestCase("obj")]
    public void GeneratedDotNetOutputInsideUnityPackageIsReported(string directoryName)
    {
        Write("shared/gameplay/package.json", JsonSerializer.Serialize(new
        {
            name = "com.ainative.gameplay",
            version = "0.1.0",
        }));
        Write($"shared/gameplay/Tests/{directoryName}/generated.dll", string.Empty);

        ArchitectureValidationResult result = Validate();

        ArchitectureDiagnostic diagnostic = result.Diagnostics.Single(item => item.Code == "ARC007");
        Assert.That(diagnostic.File, Is.EqualTo($"shared/gameplay/Tests/{directoryName}"));
        Assert.That(diagnostic.Source, Is.EqualTo("shared/gameplay/package.json"));
        Assert.That(diagnostic.Target, Is.EqualTo($"shared/gameplay/Tests/{directoryName}"));
    }

    [Test]
    public void ForbiddenLayerDependencyIsReported()
    {
        Write("client/Client.csproj", Project("AiNative.Client"));
        Write("shared/Shared.csproj", Project("AiNative.Shared", "../client/Client.csproj"));
        WriteSolution("client/Client.csproj", "shared/Shared.csproj");

        AssertCode("ARC001");
    }

    [Test]
    public void DependencyCycleIsReported()
    {
        Write("tools/A/A.csproj", Project("AiNative.Tools.A", "../B/B.csproj"));
        Write("tools/B/B.csproj", Project("AiNative.Tools.B", "../A/A.csproj"));
        WriteSolution("tools/A/A.csproj", "tools/B/B.csproj");

        AssertCode("ARC002");
    }

    [Test]
    public void RuntimeToEditorReferenceIsReported()
    {
        Write("client/Runtime/AiNative.Client.asmdef", Asmdef("AiNative.Client", "AiNative.Client.Editor"));
        Write("client/Editor/AiNative.Client.Editor.asmdef", Asmdef("AiNative.Client.Editor"));

        AssertCode("ARC003");
    }

    [TestCase("using UnityEngine;")]
    [TestCase("using System.IO;")]
    [TestCase("using System.Threading.Tasks;")]
    [TestCase("class Bad { object Value => System.IO.File.OpenRead(\"state\"); }")]
    [TestCase("class Bad { object Value => new System.Random(); }")]
    [TestCase("class Bad { object Value => System.DateTime.UtcNow; }")]
    [TestCase("class Bad { int Value => System.Environment.TickCount; }")]
    [TestCase("#if FOO\nclass Bad { }\n#endif")]
    public void SharedForbiddenApiIsReported(string source)
    {
        Write("shared/gameplay/Runtime/Bad.cs", source);

        AssertCode("ARC004");
    }

    [Test]
    public void MissingInternalReferenceIsReported()
    {
        Write("client/Runtime/AiNative.Client.asmdef", Asmdef("AiNative.Client", "AiNative.Missing"));

        AssertCode("ARC005");
    }

    [Test]
    public void SolutionDriftIsReported()
    {
        Write("tools/Tool.csproj", Project("AiNative.Tools.Tool"));
        Write("AiNative.sln", string.Empty);

        AssertCode("ARC006");
    }

    [Test]
    public void InitializedFantasySubmoduleIsTreatedAsAnOpaqueVendorBoundary()
    {
        Write("server/vendor/Fantasy/Fantasy.sln", string.Empty);
        Write(
            "server/vendor/Fantasy/Fantasy.Packages/Fantasy.Net/Fantasy.Net.csproj",
            Project("Fantasy.Net", "../../../../client/Forbidden.csproj"));
        Write("server/vendor/Fantasy/Shared/Bad.cs", "using UnityEngine; class Bad { }");

        ArchitectureValidationResult result = Validate();

        Assert.That(result.Diagnostics, Is.Empty);
    }

    private ArchitectureValidationResult Validate()
    {
        string rules = Path.Combine(AppContext.BaseDirectory, "architecture-rules.json");
        return new ArchitectureValidator().Validate(_fixtureRoot, rules);
    }

    private void AssertCode(string code)
    {
        ArchitectureValidationResult result = Validate();
        Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain(code));
    }

    private void WriteSolution(params string[] projects)
    {
        string content = string.Join(
            Environment.NewLine,
            projects.Select((path, index) =>
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"P{index}\", \"{path.Replace('/', '\\')}\", \"{{00000000-0000-0000-0000-{index:D12}}}\"{Environment.NewLine}EndProject"));
        Write("AiNative.sln", content);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Project(string assemblyName, string? projectReference = null)
    {
        string reference = projectReference is null
            ? string.Empty
            : $"<ItemGroup><ProjectReference Include=\"{projectReference}\" /></ItemGroup>";
        return $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup>{reference}</Project>";
    }

    private static string Asmdef(string name, params string[] references) => JsonSerializer.Serialize(new
    {
        name,
        references,
    });
}
