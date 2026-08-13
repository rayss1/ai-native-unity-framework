using System.Text.Json;
using AiNative.Tools.ArchitectureCheck;

return Run(args);

static int Run(string[] arguments)
{
    try
    {
        (string root, string format) = ParseArguments(arguments);
        ArchitectureValidationResult result = new ArchitectureValidator().Validate(root);
        WriteResult(result, format);
        return result.IsValid ? 0 : 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"ARCHCHECK_ERROR: {exception.Message}");
        return 2;
    }
}

static (string Root, string Format) ParseArguments(string[] arguments)
{
    string? root = null;
    string format = "text";

    for (int index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--root" when index + 1 < arguments.Length:
                root = arguments[++index];
                break;
            case "--format" when index + 1 < arguments.Length:
                format = arguments[++index];
                break;
            default:
                throw new ArgumentException($"Unknown or incomplete argument: {arguments[index]}");
        }
    }

    if (root is null)
    {
        throw new ArgumentException("Required argument is missing: --root <repo>");
    }

    if (format is not ("text" or "json"))
    {
        throw new ArgumentException("--format must be 'text' or 'json'.");
    }

    return (root, format);
}

static void WriteResult(ArchitectureValidationResult result, string format)
{
    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));
        return;
    }

    foreach (ArchitectureDiagnostic diagnostic in result.Diagnostics)
    {
        string edge = diagnostic.Source is null ? string.Empty : $" [{diagnostic.Source} -> {diagnostic.Target}]";
        Console.WriteLine($"{diagnostic.Code} {diagnostic.File}: {diagnostic.Message}{edge}");
    }

    Console.WriteLine(result.IsValid
        ? "Architecture validation passed."
        : $"Architecture validation failed with {result.Diagnostics.Count} violation(s).");
}
