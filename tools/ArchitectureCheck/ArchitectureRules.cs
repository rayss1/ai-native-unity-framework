using System.Text.Json;

namespace AiNative.Tools.ArchitectureCheck;

public sealed class ArchitectureRules
{
    public required string[] IgnoredDirectoryNames { get; init; }

    public required string[] IgnoredPathPrefixes { get; init; }

    public required string[] UnityPackageForbiddenDirectoryNames { get; init; }

    public required Dictionary<string, string> LayerRoots { get; init; }

    public required Dictionary<string, string[]> AllowedDependencies { get; init; }

    public required string[] InternalReferencePrefixes { get; init; }

    public required string[] SharedForbiddenNamespacePrefixes { get; init; }

    public static ArchitectureRules Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ArchitectureRules>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException($"Architecture rules are empty: {path}");
    }
}
