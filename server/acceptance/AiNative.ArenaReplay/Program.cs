using System.Text.Json;
using AiNative.Server.Battle;
// Server-side qualification executable: retains Server dependency ownership under ADR-0002.
if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: ArenaReplay <file.anar> <source-identity> <fantasy-identity> <protocol-identity> <configuration-identity>");
    return 2;
}
try
{
    var verified = ArenaReplayVerifier.Verify(args[0], new(args[1], args[2], args[3], args[4]));
    Console.WriteLine(JsonSerializer.Serialize(verified));
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
{
    Console.Error.WriteLine("Arena replay rejected: " + error.Message);
    return 1;
}
