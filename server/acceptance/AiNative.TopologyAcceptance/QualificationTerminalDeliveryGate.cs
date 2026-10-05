using System.Text.Json;

internal static class QualificationTerminalDeliveryGate
{
    internal static bool Passes(JsonElement report)
    {
        var nodes = report.GetProperty("nodes").EnumerateArray().ToArray();
        return nodes.Length == 2 && nodes.Select(node => node.GetProperty("node").GetString()).Order()
            .SequenceEqual(new[] { "battle-1", "battle-2" }) &&
            nodes.All(node => node.GetProperty("terminalDeliveryFailures").GetInt64() == 0 &&
                node.GetProperty("terminalDeliveryTimeouts").GetInt64() == 0);
    }
}
