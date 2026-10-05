using System.Reflection;
using global::Fantasy.Network;

namespace AiNative.Server.Fantasy;

internal enum FantasySendDrainStatus { Pending, Drained, Failed }

internal interface IFantasySendDrain
{
    Task<FantasySendDrainStatus> CheckSendDrainAsync();
}

internal interface IFantasySendDrainDispatcher
{
    bool TryPostSendDrainCheck(Action action);
    bool TryGetPendingKcpSends(out uint count);
}

internal sealed partial class FantasySessionSender
{
    private readonly object _sendDrainCheckGate = new();
    private TaskCompletionSource<FantasySendDrainStatus>? _pendingSendDrainCheck;

    public Task<FantasySendDrainStatus> CheckSendDrainAsync()
    {
        lock (_sendDrainCheckGate)
        {
            try
            {
                if (IsClosed || _dispatcher is not IFantasySendDrainDispatcher observer)
                    return Task.FromResult(FantasySendDrainStatus.Failed);
                if (_pendingSendDrainCheck is { } pending) return pending.Task;
                var completion = new TaskCompletionSource<FantasySendDrainStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingSendDrainCheck = completion;
                try
                {
                    if (!observer.TryPostSendDrainCheck(() => CheckOnScene(observer, completion)))
                        CompleteSendDrainCheck(completion, FantasySendDrainStatus.Failed);
                }
                catch { CompleteSendDrainCheck(completion, FantasySendDrainStatus.Failed); }
                return completion.Task;
            }
            catch { return Task.FromResult(FantasySendDrainStatus.Failed); }
        }
    }

    private void CheckOnScene(IFantasySendDrainDispatcher observer, TaskCompletionSource<FantasySendDrainStatus> completion)
    {
        FantasySendDrainStatus result = FantasySendDrainStatus.Failed;
        try
        {
            // FIFO scene execution places this after earlier drains, including their actual Send calls.
            // The scheduled flag also detects a drain posted behind this check by a concurrent producer.
            if (!IsClosed)
            {
                if (Volatile.Read(ref _drainScheduled) != 0 || PendingOutboundPackets != 0 ||
                    !_outbound.IsEmpty || HasPendingSnapshot()) result = FantasySendDrainStatus.Pending;
                else if (observer.TryGetPendingKcpSends(out uint count))
                    result = count == 0 ? FantasySendDrainStatus.Drained : FantasySendDrainStatus.Pending;
                if (IsClosed) result = FantasySendDrainStatus.Failed;
            }
        }
        catch { result = FantasySendDrainStatus.Failed; }
        CompleteSendDrainCheck(completion, result);
    }

    private void CompleteSendDrainCheck(TaskCompletionSource<FantasySendDrainStatus> completion, FantasySendDrainStatus result)
    {
        lock (_sendDrainCheckGate)
        {
            completion.TrySetResult(result);
            if (ReferenceEquals(_pendingSendDrainCheck, completion)) _pendingSendDrainCheck = null;
        }
    }

    private void FailPendingSendDrainCheck()
    {
        lock (_sendDrainCheckGate)
        {
            _pendingSendDrainCheck?.TrySetResult(FantasySendDrainStatus.Failed);
            _pendingSendDrainCheck = null;
        }
    }
}

// This inspection is specific to the pinned Fantasy fork, kept out of shared/public contracts.
// Reads are permitted only on the owning Scene thread, after the adapter dispatch barrier.
internal static class FantasyKcpSendDrainObservation
{
    private static readonly PropertyInfo? Channel;
    private static readonly Type? KcpChannelType;
    private static readonly PropertyInfo? Kcp;
    private static readonly PropertyInfo? WaitSendCount;

    static FantasyKcpSendDrainObservation()
    {
        try
        {
            Channel = typeof(Session).GetProperty("Channel", BindingFlags.Instance | BindingFlags.NonPublic);
            KcpChannelType = typeof(Session).Assembly.GetType("Fantasy.Network.KCP.KCPServerNetworkChannel", throwOnError: false);
            Kcp = KcpChannelType?.GetProperty("Kcp", BindingFlags.Instance | BindingFlags.Public);
            WaitSendCount = Kcp?.PropertyType.GetProperty("WaitSendCount", BindingFlags.Instance | BindingFlags.Public);
        }
        catch { /* Contract unavailable: all checks fail closed. */ }
    }

    internal static bool IsContractAvailable => Channel?.GetMethod is not null &&
        KcpChannelType is not null && Kcp?.GetMethod is not null &&
        WaitSendCount?.GetMethod is not null && WaitSendCount.PropertyType == typeof(uint);

    internal static bool TryRead(Session session, out uint count)
    {
        count = 0;
        try
        {
            if (!IsContractAvailable || session.IsDisposed) return false;
            object? channel = Channel!.GetValue(session);
            if (channel is null || !KcpChannelType!.IsInstanceOfType(channel)) return false;
            object? kcp = Kcp!.GetValue(channel);
            if (kcp is null || WaitSendCount!.GetValue(kcp) is not uint pending) return false;
            count = pending;
            return true;
        }
        catch { return false; }
    }
}
