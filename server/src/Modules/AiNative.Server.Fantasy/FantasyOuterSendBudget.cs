using System.Collections;
using System.Reflection;
using global::Fantasy.Network;
using Fantasy.Network.TCP;

namespace AiNative.Server.Fantasy;

// The pinned TCP implementation otherwise uses an unbounded private send queue. Keep this vendor-specific
// inspection inside the adapter; an incompatible upgrade fails closed rather than silently losing the bound.
internal static class FantasyOuterSendBudget
{
    private static readonly PropertyInfo Channel = typeof(Session).GetProperty("Channel", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("fantasy-channel-budget-contract-changed");
    private static readonly FieldInfo Buffers = typeof(TCPServerNetworkChannel).GetField("_sendBuffers", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("fantasy-tcp-budget-contract-changed");
    public static bool Check(Session session)
    {
        if (session.IsDisposed) return false;
        if (Channel.GetValue(session) is not TCPServerNetworkChannel channel) return false;
        if (Buffers.GetValue(channel) is not ICollection queue || queue.Count >= 128)
        { session.Dispose(); return false; }
        return true;
    }
}
