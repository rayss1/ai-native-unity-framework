using System.Security.Cryptography;
using AiNative.Protocol.Backend.V1;
using Google.Protobuf;

namespace AiNative.Server.Control;

public static class ResultPayload
{
    public static MatchResult Canonicalize(MatchResult result)
    {
        MatchResult canonical = result.Clone();
        canonical.Players.Clear();
        canonical.Players.Add(result.Players.OrderBy(x => x.PlayerId, StringComparer.Ordinal).Select(x => x.Clone()));
        return canonical;
    }
    public static string Hash(MatchResult result) => Convert.ToHexString(SHA256.HashData(Canonicalize(result).ToByteArray()));
}
