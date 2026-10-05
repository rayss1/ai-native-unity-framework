using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using Google.Protobuf;

// Explicit acceptance scenario only. Uses real KCP sockets through a bounded UDP relay;
// no transport mock, shared API, wire message, or production fault switch is introduced.
internal static class TerminalDeliveryScenarios
{
    public static async Task RunAsync(FantasyServiceRuntime runtime, FailureScenarios.Fixture fixture,
        List<object> evidence, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("terminal-delivery-requires-windows");
        await RunCase(runtime, fixture, evidence, false, cancellation);
        await RunCase(runtime, fixture, evidence, true, cancellation);
    }

    private static async Task RunCase(FantasyServiceRuntime runtime, FailureScenarios.Fixture fixture,
        List<object> evidence, bool keepAcknowledgementsBlocked, CancellationToken cancellation)
    {
        string scenario = keepAcknowledgementsBlocked ? "terminal-delivery-bounded-timeout" : "terminal-delivery-delayed-ack";
        var events = new List<object>();
        // The mutable event list is retained even when a later assertion fails.
        evidence.Add(new { scenario = scenario + "-progress", events, stopwatchFrequency = Stopwatch.Frequency });
        void Record(string name) => events.Add(new { name, utc = DateTimeOffset.UtcNow, ticks = Stopwatch.GetTimestamp() });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        CancellationToken ct = deadline.Token;
        await using var a = await fixture.Prepare(ct);
        await using var b = await fixture.Prepare(ct);
        await fixture.Enqueue(a, ct);
        await fixture.Enqueue(b, ct);
        MatchReady readyA = await fixture.Ready(a.gate, a.login, a.request, cancellation: ct);
        MatchReady readyB = await fixture.Ready(b.gate, b.login, b.request, cancellation: ct);
        RoomAllocation allocation = readyA.Status.Allocation;
        Check(allocation.RoomId == readyB.Status.Allocation.RoomId, "same-room");
        int healthPort = allocation.NodeId switch { "battle-1" => 24106, "battle-2" => 24107, _ => throw new InvalidOperationException("unexpected-node") };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        async Task<JsonElement> Node()
        {
            using JsonDocument data = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/health/diagnostics", ct));
            return data.RootElement.GetProperty("queues").Clone();
        }
        JsonElement baseline = await Node();
        // This scenario owns a fresh topology run; reject accidental concurrent workloads.
        Check(baseline.GetProperty("liveRooms").GetInt32() == 1, "dedicated-scenario-room-required");
        await using var relayA = new DatagramRelay(allocation.Address);
        await using var relayB = new DatagramRelay(allocation.Address);
        await using var wireA = await runtime.ConnectBattleProbeAsync(relayA.Address, ct);
        await using var wireB = await runtime.ConnectBattleProbeAsync(relayB.Address, ct);
        await fixture.Join(wireA.Transport, readyA, ct);
        await fixture.Join(wireB.Transport, readyB, ct);
        await Task.WhenAll(fixture.FreshSnapshot(wireA.Transport, ct), fixture.FreshSnapshot(wireB.Transport, ct));
        Record("both-clients-joined");
        relayA.BlockClientDatagrams();
        relayB.BlockClientDatagrams();
        Record("client-to-server-barriers-closed");
        // No new input is needed: the configured real match timeout ends this two-player match.
        // KCP may receive/retransmit normally, but neither peer's transport ACK reaches Battle.
        async Task<Snapshot> Finished(IRealtimeTransport transport)
        {
            Snapshot snapshot;
            do { snapshot = await fixture.Receive<Snapshot>(transport, MessageId.Snapshot, 15, ct); }
            while (snapshot.MatchPhase != ArenaMatchPhase.ArenaMatchFinished);
            return snapshot;
        }
        var finals = await Task.WhenAll(Finished(wireA.Transport), Finished(wireB.Transport));
        Check(finals[0].RoomTick == finals[1].RoomTick, "same-terminal-tick");
        Record("both-clients-consumed-terminal-snapshot");
        JsonElement pending = await PollNode(Node, q => RoomState(q, allocation.RoomId) == "Pending" &&
            relayA.BlockedDatagrams > 0 && relayB.BlockedDatagrams > 0, TimeSpan.FromSeconds(3), ct);
        Check(wireA.Transport.State == TransportState.Connected && wireB.Transport.State == TransportState.Connected,
            "pending-terminal-connections-must-remain-open");
        Check(relayA.DisconnectDatagrams == 0 && relayB.DisconnectDatagrams == 0, "no-disconnect-before-drain");
        Check(Counter(pending, "terminalDelivered") == Counter(baseline, "terminalDelivered"), "pending-not-delivered");
        Check(Counter(pending, "terminalDeliveryFailures") == Counter(baseline, "terminalDeliveryFailures"), "pending-not-failed");
        Record("server-pending-with-acks-blocked");
        long pendingAt = Stopwatch.GetTimestamp();
        if (!keepAcknowledgementsBlocked)
        {
            relayA.AllowClientDatagrams();
            relayB.AllowClientDatagrams();
            Record("client-to-server-barriers-opened");
        }
        JsonElement released = await PollNode(Node, q => RoomState(q, allocation.RoomId) is null,
            TimeSpan.FromSeconds(15), ct);
        double pendingToReleaseSeconds = Stopwatch.GetElapsedTime(pendingAt).TotalSeconds;
        Record("server-room-released");
        long delivered = Counter(released, "terminalDelivered") - Counter(baseline, "terminalDelivered");
        long failures = Counter(released, "terminalDeliveryFailures") - Counter(baseline, "terminalDeliveryFailures");
        long timeouts = Counter(released, "terminalDeliveryTimeouts") - Counter(baseline, "terminalDeliveryTimeouts");
        Check(delivered == (keepAcknowledgementsBlocked ? 0 : 1), "delivered-counter-outcome");
        Check(failures == (keepAcknowledgementsBlocked ? 1 : 0), "failed-counter-outcome");
        Check(timeouts == (keepAcknowledgementsBlocked ? 1 : 0), "timeout-counter-outcome");
        await Poll(() => wireA.Transport.State == TransportState.Closed && wireB.Transport.State == TransportState.Closed &&
            relayA.DisconnectDatagrams > 0 && relayB.DisconnectDatagrams > 0, TimeSpan.FromSeconds(5), ct);
        Record("both-real-kcp-disconnects-observed");
        // A transport delivery timeout must not suppress the immutable durable result.
        SettlementReceipt? receipt = null;
        await PollAsync(async () =>
        {
            ServiceReply reply = await a.gate.CallAsync(ServiceMethods.SettlementStatus,
                new SettlementQuery { MatchId = allocation.MatchId }.ToByteArray(), a.login.SessionToken, ct);
            if (!reply.Success) return false;
            receipt = reply.Read(SettlementReceipt.Parser);
            return receipt.Confirmed && receipt.MatchId == allocation.MatchId;
        }, TimeSpan.FromSeconds(20), ct);
        foreach (var member in new[] { a, b })
        {
            PlayerProfile profile = (await member.gate.CallAsync(ServiceMethods.Profile,
                new ProfileRequest { PlayerId = member.login.PlayerId }.ToByteArray(), member.login.SessionToken, ct)).Read(PlayerProfile.Parser);
            Check(profile.Played == 1, "exactly-one-persisted-result-per-player");
        }
        Record("original-match-durable-settlement-confirmed");
        relayA.ThrowIfFaulted(); relayB.ThrowIfFaulted();
        evidence.Add(new
        {
            scenario, passed = true, allocation.RoomId, allocation.MatchId, allocation.NodeId,
            finalTick = finals[0].RoomTick, terminalSnapshotsConsumedBeforeDisconnect = true,
            acknowledgementBarrierRetained = keepAcknowledgementsBlocked,
            terminalDeliveredDelta = delivered, terminalFailureDelta = failures, terminalTimeoutDelta = timeouts,
            pendingToReleaseSeconds, pendingRoomState = "Pending",
            relayBlockedDatagrams = new[] { relayA.BlockedDatagrams, relayB.BlockedDatagrams },
            relayDisconnectDatagrams = new[] { relayA.DisconnectDatagrams, relayB.DisconnectDatagrams },
            durableResultConfirmed = receipt!.Confirmed, playersWithSingleSettlement = 2,
            stopwatchFrequency = Stopwatch.Frequency, events,
            scope = "Actual Windows .NET KCP transport acknowledgement/close ordering; not Unity application consumption or capacity qualification."
        });
    }

    private static long Counter(JsonElement node, string name) => node.GetProperty(name).GetInt64();

    private static string? RoomState(JsonElement node, string roomId)
    {
        foreach (JsonElement room in node.GetProperty("rooms").EnumerateArray())
            if (room.GetProperty("roomId").GetString() == roomId)
            {
                if (!room.GetProperty("finished").GetBoolean()) return "Active";
                return room.GetProperty("terminalDeliveryState").GetString() ?? "Unpublished";
            }
        return null;
    }

    private static async Task<JsonElement> PollNode(Func<Task<JsonElement>> read, Func<JsonElement, bool> predicate,
        TimeSpan timeout, CancellationToken ct)
    {
        JsonElement latest = default;
        await PollAsync(async () => { latest = await read(); return predicate(latest); }, timeout, ct);
        return latest;
    }

    private static Task Poll(Func<bool> predicate, TimeSpan timeout, CancellationToken ct)
        => PollAsync(() => Task.FromResult(predicate()), timeout, ct);

    private static async Task PollAsync(Func<Task<bool>> predicate, TimeSpan timeout, CancellationToken ct)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await predicate()) return;
            if (Stopwatch.GetElapsedTime(start) >= timeout) throw new TimeoutException("terminal-delivery-observation-timeout");
            await Task.Delay(10, ct); // Condition polling only; elapsed sleep is never success evidence.
        }
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException("terminal-delivery:" + reason); }

    private sealed class DatagramRelay : IAsyncDisposable
    {
        private readonly Socket front = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly Socket back = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource stop = new();
        private readonly Task upstream, downstream;
        private EndPoint? client;
        private int blocked;
        private long blockedDatagrams, disconnectDatagrams;
        public string Address => front.LocalEndPoint!.ToString()!;
        public long BlockedDatagrams => Interlocked.Read(ref blockedDatagrams);
        public long DisconnectDatagrams => Interlocked.Read(ref disconnectDatagrams);

        public DatagramRelay(string server)
        {
            try
            {
                front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                back.Connect(IPEndPoint.Parse(server));
                upstream = ForwardClient();
                downstream = ForwardServer();
            }
            catch { front.Dispose(); back.Dispose(); stop.Dispose(); throw; }
        }

        public void BlockClientDatagrams() => Volatile.Write(ref blocked, 1);
        public void AllowClientDatagrams() => Volatile.Write(ref blocked, 0);

        private async Task ForwardClient()
        {
            byte[] bytes = new byte[65535];
            while (!stop.IsCancellationRequested)
            {
                SocketReceiveFromResult received = await front.ReceiveFromAsync(bytes.AsMemory(), SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), stop.Token);
                EndPoint? prior = Volatile.Read(ref client);
                if (prior is not null && !prior.Equals(received.RemoteEndPoint)) throw new InvalidOperationException("relay-client-changed");
                Volatile.Write(ref client, received.RemoteEndPoint);
                if (Volatile.Read(ref blocked) != 0) { Interlocked.Increment(ref blockedDatagrams); continue; }
                await back.SendAsync(bytes.AsMemory(0, received.ReceivedBytes), SocketFlags.None, stop.Token);
            }
        }

        private async Task ForwardServer()
        {
            byte[] bytes = new byte[65535];
            while (!stop.IsCancellationRequested)
            {
                int count = await back.ReceiveAsync(bytes.AsMemory(), SocketFlags.None, stop.Token);
                EndPoint peer = Volatile.Read(ref client) ?? throw new InvalidOperationException("relay-missing-client");
                // Pinned Fantasy outer KCP control header: 0x07 + UInt32 channel ID.
                if (count == 5 && bytes[0] == 0x07) Interlocked.Increment(ref disconnectDatagrams);
                await front.SendToAsync(bytes.AsMemory(0, count), SocketFlags.None, peer, stop.Token);
            }
        }

        public void ThrowIfFaulted()
        {
            if (upstream.IsFaulted) upstream.GetAwaiter().GetResult();
            if (downstream.IsFaulted) downstream.GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await Task.WhenAll(upstream, downstream); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { front.Dispose(); back.Dispose(); stop.Dispose(); }
        }
    }
}
