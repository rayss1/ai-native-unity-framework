using AiNative.Realtime;
using AiNative.Server.Fantasy;

namespace AiNative.Server.Battle;

internal enum TerminalDeliveryState { Pending, Drained, Failed, TimedOut }

internal readonly record struct TerminalDatagram(TransportChannel Channel, byte[] Payload);

// Owned by the network pump. Only the outcome is read by the durable-result loop.
// No timer callback, background loop or blocking wait enters the Worker Tick.
internal sealed class TerminalRoomDelivery(TimeProvider clock, TerminalRoomDelivery.Peer[] peers)
{
    internal sealed class Peer(IRealtimeTransport transport, TerminalDatagram[] packets)
    {
        internal readonly IRealtimeTransport Transport = transport;
        internal readonly TerminalDatagram[] Packets = packets;
        internal int Next;
        internal Task<FantasySendDrainStatus>? Check;
        internal bool Drained;
    }

    private readonly long _started = clock.GetTimestamp();
    private int _state;
    internal TerminalDeliveryState State => (TerminalDeliveryState)Volatile.Read(ref _state);
    internal void Fail() => Complete(TerminalDeliveryState.Failed);

    internal void Pump()
    {
        if (State != TerminalDeliveryState.Pending) return;
        if (clock.GetElapsedTime(_started) >= TimeSpan.FromSeconds(5))
        { Complete(TerminalDeliveryState.TimedOut); return; }
        try
        {
            bool allDrained = true;
            foreach (Peer peer in peers)
            {
                if (peer.Drained) continue;
                if (peer.Transport.State != TransportState.Connected)
                { Complete(TerminalDeliveryState.Failed); return; }
                while (peer.Next < peer.Packets.Length)
                {
                    TerminalDatagram packet = peer.Packets[peer.Next];
                    ValueTask<SendResult> sending = peer.Transport.SendAsync(packet.Channel, packet.Payload);
                    // The fixed Fantasy adapter returns immediate admission, never network completion.
                    if (!sending.IsCompletedSuccessfully)
                    { Complete(TerminalDeliveryState.Failed); return; }
                    SendResult sent = sending.Result;
                    if (sent.Status == SendStatus.WouldBlock) break;
                    if (sent.Status != SendStatus.Accepted || sent.AcceptedBytes != packet.Payload.Length)
                    { Complete(TerminalDeliveryState.Failed); return; }
                    peer.Next++;
                }
                if (peer.Next != peer.Packets.Length) { allDrained = false; continue; }
                if (peer.Transport is not IFantasySendDrain drain)
                { Complete(TerminalDeliveryState.Failed); return; }
                peer.Check ??= drain.CheckSendDrainAsync();
                if (!peer.Check.IsCompleted) { allDrained = false; continue; }
                if (!peer.Check.IsCompletedSuccessfully || peer.Check.Result == FantasySendDrainStatus.Failed)
                { _ = peer.Check.Exception; Complete(TerminalDeliveryState.Failed); return; }
                if (peer.Check.Result == FantasySendDrainStatus.Drained) peer.Drained = true;
                else { peer.Check = null; allDrained = false; }
            }
            if (allDrained) Complete(TerminalDeliveryState.Drained);
        }
        catch { Complete(TerminalDeliveryState.Failed); }
    }

    private void Complete(TerminalDeliveryState outcome) => Volatile.Write(ref _state, (int)outcome);
}
