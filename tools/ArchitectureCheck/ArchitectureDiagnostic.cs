namespace AiNative.Tools.ArchitectureCheck;

public sealed record ArchitectureDiagnostic(
    string Code,
    string File,
    string Message,
    string? Source = null,
    string? Target = null);

public sealed record ArchitectureValidationResult(
    IReadOnlyList<ArchitectureDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}
