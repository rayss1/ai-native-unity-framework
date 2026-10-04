using System.Text.Json;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Control;
using AiNative.Server.Backend;
using AiNative.Server.Fantasy;
using AiNative.Server.Hosting;
using AiNative.Server.Protocol;
using Google.Protobuf;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

List<object> evidence = [];
string output = Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_REPORT") ?? "acceptance.json";
AcceptanceOptions options = AcceptanceOptions.Load(Environment.GetEnvironmentVariable);
if (Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_MODE") == "qualification")
    _ = ArenaCapacityOptions.Load(Environment.GetEnvironmentVariable);
using CancellationTokenSource deadline = new(options.Deadline);
CancellationToken ct = deadline.Token;
ServiceRole role = Enum.Parse<ServiceRole>(Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_ROLE") ?? "Client");
HostSettings settings = HostSettings.Load(role, args);
await using FantasyServiceRuntime runtime = new(settings.SceneId, role, settings.Authentication);
runtime.SetHandler(new RejectHandler());
Task run = runtime.RunAsync(ct);
int exit = 0;
try
{
    await Task.WhenAny(runtime.WaitUntilReadyAsync(ct), run); if (run.IsCompleted) await run; await runtime.WaitUntilReadyAsync(ct);
    if (role == ServiceRole.Client && Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_MODE") is { Length: > 0 } mode) { await FailureScenarios.RunAsync(runtime, mode, evidence, ct); }
    else if (role == ServiceRole.Battle)
    {
        MatchResult result = MatchResult.Parser.ParseFrom(File.ReadAllBytes(HostSettings.Required("AINATIVE_ACCEPTANCE_RESULT_FILE")));
        SettlementReceipt prior = (await runtime.CallAsync(new(ServiceRole.Player), ServiceMethods.SettlementStatus, new SettlementQuery { MatchId = result.MatchId }, ct)).Read(SettlementReceipt.Parser);
        Check(prior.Confirmed, "prior-receipt-confirmed");
        for (int i = 0; i < 2; i++)
        {
            SettlementReceipt receipt = (await runtime.CallAsync(new(ServiceRole.Player), ServiceMethods.Settle, result, ct)).Read(SettlementReceipt.Parser);
            Check(receipt.Confirmed && receipt.PayloadHash == prior.PayloadHash, "duplicate-receipt-same-hash");
        }
        await using PostgresPlayerStore database = new(HostSettings.Required("AINATIVE_POSTGRES_CONNECTION_STRING"));
        foreach (PlayerResult player in result.Players)
        {
            PlayerProfile? profile = await database.ProfileAsync(player.PlayerId, ct);
            Check(profile?.Played == 1 && profile.Won == (player.Won ? 1 : 0) && profile.Kills == player.Kills, "duplicate-postgres-counters-once");
        }
        evidence.Add(new { scenario = "signed-internal-duplicate-settlement", passed = true, prior.MatchId, prior.PayloadHash, playersWithSingleSettlement = result.Players.Count });
    }
    else
    {
    await using FantasyBackendProbe gateA = await runtime.ConnectGateProbeAsync(options.GateAddress, ct);
    await using FantasyBackendProbe gateB = await runtime.ConnectGateProbeAsync(options.GateAddress, ct);
    var a = await Queue(gateA); var b = await Queue(gateB);
    evidence.Add(new { scenario = "tcp-register-login-party-ready-queue", passed = true, players = 2 });
    ServiceReply privateCall = await gateA.CallAsync(ServiceMethods.Settle, new MatchResult().ToByteArray(), a.login.SessionToken, ct);
    Check(!privateCall.Success, "client-private-settlement-forbidden");
    ServiceReply foreignProfile = await gateA.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = b.login.PlayerId }.ToByteArray(), a.login.SessionToken, ct);
    Check(!foreignProfile.Success, "foreign-profile-forbidden");
    evidence.Add(new { scenario = "public-client-authorization", passed = true });
    MatchReady readyA = await Ready(gateA, a.login, a.request);
    MatchReady readyB = await Ready(gateB, b.login, b.request);
    Check(readyA.Status.Allocation.RoomId == readyB.Status.Allocation.RoomId, "two-parties-same-room");
    evidence.Add(new { scenario = "tcp-account-party-match", passed = true, room = readyA.Status.Allocation.RoomId });
    if (Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_BACKEND_ONLY") == "true")
        evidence.Add(new { scenario = "kcp-reconnect-settlement", skipped = "explicit-backend-only" });
    else
    {
        await using var battleA = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
        await using var battleB = await runtime.ConnectBattleProbeAsync(readyB.Status.Allocation.Address, ct);
        var first = await Join(battleA.Transport, readyA); var second = await Join(battleB.Transport, readyB);
        await using SnapshotInputLoop inputA = new(battleA.Transport, ct);
        await using SnapshotInputLoop inputB = new(battleB.Transport, ct);
        Snapshot snapshot;
        do { snapshot = await inputA.ReadAsync(TimeSpan.FromSeconds(10)); } while (snapshot.LastProcessedInputSequence < 1);
        evidence.Add(new { scenario = "kcp-input-ack", passed = true, snapshot.RoomTick, snapshot.LastProcessedInputSequence });
        string? restartSignal = Environment.GetEnvironmentVariable("AINATIVE_GATE_RESTART_SIGNAL");
        if (restartSignal is not null)
        {
            File.WriteAllText(restartSignal, "active");
            DateTime restartDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(restartSignal + ".done")) { if (DateTime.UtcNow > restartDeadline) throw new TimeoutException("gate-restart-harness"); await Task.Delay(100, ct); }
            Snapshot continued = await inputA.ReadAsync(TimeSpan.FromSeconds(5));
            Check(continued.RoomTick > snapshot.RoomTick, "battle-continues-during-gate-restart");
            evidence.Add(new { scenario = "gate-restart-battle-continues", passed = true, continued.RoomTick });
            await using FantasyBackendProbe reconnectedGate = await runtime.ConnectGateProbeAsync(options.GateAddress, ct);
            PlayerProfile restoredProfile = (await reconnectedGate.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = a.login.PlayerId }.ToByteArray(), a.login.SessionToken, ct)).Read(PlayerProfile.Parser);
            MatchReady restoredMatch = (await reconnectedGate.CallAsync(ServiceMethods.MatchStatus, new MatchQuery { RequestId = a.request }.ToByteArray(), a.login.SessionToken, ct)).Read(MatchReady.Parser);
            Check(restoredProfile.PlayerId == a.login.PlayerId && restoredMatch.Status.Allocation?.RoomId == readyA.Status.Allocation.RoomId, "gate-reconnect-public-profile-match-restored");
            evidence.Add(new { scenario = "gate-reconnect-public-profile-match-restored", passed = true, restoredProfile.PlayerId, room = restoredMatch.Status.Allocation!.RoomId });
        }
        await inputA.DisposeAsync();
        await using var replacement = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
        var resumed = await Join(replacement.Transport, readyA);
        await using SnapshotInputLoop replacementInput = new(replacement.Transport, ct);
        Check(resumed.EntityId == first.EntityId, "reconnect-same-entity");
        evidence.Add(new { scenario = "reconnect", passed = true, resumed.EntityId });
        await using var invalid = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
        await Send(invalid.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "acceptance", EntryTicket = readyA.EntryTicket, GlobalRoomId = "wrong-room" });
        bool rejected = false;
        try { await Receive<LoginResponse>(invalid.Transport, MessageId.LoginResponse, TimeSpan.FromSeconds(2)); } catch (TimeoutException) { rejected = true; }
        Check(rejected, "wrong-room-ticket-no-response");
        evidence.Add(new { scenario = "wrong-room-ticket", passed = true });
        string? activeSignal = Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_ACTIVE_SIGNAL");
        if (activeSignal is not null)
        {
            RoomAllocation active = readyA.Status.Allocation;
            // Write metadata atomically; never include session tokens or tickets.
            File.WriteAllText(activeSignal + ".tmp", JsonSerializer.Serialize(new { active.RoomId, active.MatchId, active.NodeId, active.BootEpoch, playerIds = active.PlayerIds.ToArray() }));
            File.Move(activeSignal + ".tmp", activeSignal, true);
        }
        string? battleRestartSignal = Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_BATTLE_RESTART_SIGNAL");
        if (battleRestartSignal is not null)
        {
            while (!File.Exists(battleRestartSignal)) await Task.Delay(100, ct);
            await using var stale = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
            await Send(stale.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "acceptance", EntryTicket = readyA.EntryTicket, GlobalRoomId = readyA.Status.Allocation.RoomId });
            bool fenced = false;
            try { await Receive<LoginResponse>(stale.Transport, MessageId.LoginResponse, TimeSpan.FromSeconds(2)); } catch (TimeoutException) { fenced = true; }
            Check(fenced, "old-boot-ticket-no-login");
            evidence.Add(new { scenario = "battle-restart-old-ticket-fenced", passed = true, readyA.Status.Allocation.RoomId, readyA.Status.Allocation.BootEpoch });
            await replacementInput.StopAfterVerifiedPeerLossAsync();
            await inputB.StopAfterVerifiedPeerLossAsync();
            // The old battle cannot finish. The harness must require this exact expected
            // termination plus a fresh match; a generic failed client is never a pass.
            throw new InvalidOperationException("expected-room-lost-after-battle-restart");
        }
        if (Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_EXPIRED_TICKET") == "true")
        {
            using var claims = JsonDocument.Parse(Convert.FromBase64String(readyA.EntryTicket.Split('.')[0]));
            long expiry = claims.RootElement.GetProperty("Expiry").GetInt64();
            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= expiry) await Task.Delay(500, ct);
            Snapshot active = await replacementInput.ReadAsync(TimeSpan.FromSeconds(10));
            Check(active.MatchPhase == ArenaMatchPhase.ArenaMatchActive, "expiry-tested-in-live-room");
            await using var expired = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
            await Send(expired.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "acceptance", EntryTicket = readyA.EntryTicket, GlobalRoomId = readyA.Status.Allocation.RoomId });
            bool expiryRejected = false;
            try { await Receive<LoginResponse>(expired.Transport, MessageId.LoginResponse, TimeSpan.FromSeconds(2)); } catch (TimeoutException) { expiryRejected = true; }
            Check(expiryRejected, "expired-ticket-no-response");
            evidence.Add(new { scenario = "expired-player-issued-ticket-live-room", passed = true, expiryUnixSeconds = expiry, active.RoomTick });
        }
        do { snapshot = await replacementInput.ReadAsync(TimeSpan.FromSeconds(30)); } while (snapshot.MatchPhase != ArenaMatchPhase.ArenaMatchFinished);
        await inputB.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Check(inputB.SentInputs > 0 && replacementInput.SentInputs > 0 && inputB.LastSnapshot?.MatchPhase == ArenaMatchPhase.ArenaMatchFinished && inputB.LastSnapshot.LastProcessedInputSequence > 0 && snapshot.LastProcessedInputSequence > 0, "both-players-continuous-input-through-finish");
        evidence.Add(new { scenario = "snapshot-driven-continuous-input", passed = true, firstConnectionInputs = inputA.SentInputs, secondPlayerInputs = inputB.SentInputs, secondPlayerFinishedTick = inputB.LastSnapshot!.RoomTick, secondPlayerAcknowledgedSequence = inputB.LastSnapshot.LastProcessedInputSequence, replacementInputs = replacementInput.SentInputs, replacementAcknowledgedSequence = snapshot.LastProcessedInputSequence, finishedRoomTick = snapshot.RoomTick, inputHz = 10, deadlineSeconds = options.Deadline.TotalSeconds });
        await using FantasyBackendProbe profileGate = await runtime.ConnectGateProbeAsync(options.GateAddress, ct);
        await AcceptanceSettlementPolling.WaitAsync(async token =>
        {
            ServiceReply reply = await profileGate.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = a.login.PlayerId }.ToByteArray(), a.login.SessionToken, token);
            return reply.Success ? reply.Read(PlayerProfile.Parser) : null;
        }, ct);
        await Task.Delay(1500, ct);
        PlayerProfile stable = (await profileGate.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = a.login.PlayerId }.ToByteArray(), a.login.SessionToken, ct)).Read(PlayerProfile.Parser);
        Check(stable.Played == 1, "settlement-remains-once");
        evidence.Add(new { scenario = "finished-postgres-profile", passed = true, stable.Played, stable.Won, stable.Kills });
        string? resultFile = Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_RESULT_FILE");
        if (resultFile is not null)
        {
            RoomAllocation allocation = readyA.Status.Allocation;
            MatchResult result = new() { MatchId = allocation.MatchId, RoomId = allocation.RoomId, NodeId = allocation.NodeId, BootEpoch = allocation.BootEpoch, Completion = "Finished" };
            foreach (string player in allocation.PlayerIds)
            {
                uint entity = player == a.login.PlayerId ? first.EntityId : second.EntityId;
                PlayerState state = snapshot.Players.Single(x => x.EntityId == entity);
                result.Players.Add(new PlayerResult { PlayerId = player, Won = entity == snapshot.LeaderEntityId, Kills = (int)state.Kills });
            }
            File.WriteAllBytes(resultFile, result.ToByteArray());
            File.WriteAllText(resultFile + ".node", allocation.NodeId);
        }
    }
}
}
catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; evidence.Add(new { scenario = "run", passed = false, error = ex.GetType().Name, message = ex.Message }); }
finally
{
    File.WriteAllText(output, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, exit, evidence }, new JsonSerializerOptions { WriteIndented = true }));
    deadline.Cancel(); try { await run.WaitAsync(TimeSpan.FromSeconds(3)); } catch (OperationCanceledException) { } catch (TimeoutException) { } catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ":" + ex.Message); }
}
return exit;

async Task<(LoginResult login, string request)> Queue(FantasyBackendProbe gate)
{
    var account = new AccountRequest { Username = "accept_" + Guid.NewGuid().ToString("N")[..16], Password = Guid.NewGuid().ToString("N") };
    LoginResult login = (await gate.CallAsync(ServiceMethods.RegisterAccount, account.ToByteArray(), ct: ct)).Read(LoginResult.Parser);
    await using FantasyBackendProbe loginConnection = await runtime.ConnectGateProbeAsync(options.GateAddress, ct);
    login = (await loginConnection.CallAsync(ServiceMethods.Login, account.ToByteArray(), ct: ct)).Read(LoginResult.Parser);
    PartyState party = (await gate.CallAsync(ServiceMethods.PartyCreate, new Empty().ToByteArray(), login.SessionToken, ct)).Read(PartyState.Parser);
    party = (await gate.CallAsync(ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray(), login.SessionToken, ct)).Read(PartyState.Parser);
    string request = Guid.NewGuid().ToString("N");
    (await gate.CallAsync(ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = request }.ToByteArray(), login.SessionToken, ct)).Read(MatchStatus.Parser);
    return (login, request);
}
async Task<MatchReady> Ready(FantasyBackendProbe gate, LoginResult login, string request)
{
    for (int i = 0; i < 100; i++)
    {
        MatchReady ready = (await gate.CallAsync(ServiceMethods.MatchStatus, new MatchQuery { RequestId = request }.ToByteArray(), login.SessionToken, ct)).Read(MatchReady.Parser);
        if (ready.EntryTicket.Length > 0) return ready;
        if (ready.Status.Failure.Length > 0) throw new InvalidOperationException("match-failure:" + ready.Status.Failure);
        await Task.Delay(200, ct);
    }
    throw new TimeoutException("match-ready-timeout");
}
async Task<JoinRoomResponse> Join(IRealtimeTransport transport, MatchReady ready)
{
    await Send(transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "acceptance", EntryTicket = ready.EntryTicket, GlobalRoomId = ready.Status.Allocation.RoomId });
    LoginResponse login = await Receive<LoginResponse>(transport, MessageId.LoginResponse, TimeSpan.FromSeconds(10));
    Check(login.GlobalRoomId == ready.Status.Allocation.RoomId && login.BootEpoch == ready.Status.Allocation.BootEpoch, "admission-allocation");
    await Send(transport, MessageId.JoinRoomRequest, new JoinRoomRequest { SessionId = login.SessionId, RequestedRoom = 1 });
    var joined = await Receive<JoinRoomResponse>(transport, MessageId.JoinRoomResponse, TimeSpan.FromSeconds(10));
    Check(joined.TickRate == 60 && joined.EntityId != 0, "join-tickrate-entity"); return joined;
}
async Task Send(IRealtimeTransport transport, MessageId id, IMessage message)
{
    byte[] bytes = new byte[1200];
    Check(RealtimeProtocolCodec.TryEncode(id, message, bytes, out var channel, out int size), "encode");
    Check((await transport.SendAsync(channel, bytes.AsMemory(0, size), ct)).Status == SendStatus.Accepted, "send");
}
async Task<T> Receive<T>(IRealtimeTransport transport, MessageId id, TimeSpan timeout) where T : IMessage
{
    DateTime end = DateTime.UtcNow + timeout; byte[] bytes = new byte[1200];
    while (DateTime.UtcNow < end)
    {
        while (transport.TryReceive(bytes, out var packet))
            if (packet.IsComplete && RealtimeProtocolCodec.TryDecode(bytes.AsSpan(0, packet.WrittenBytes), out var decoded) == ProtocolDecodeStatus.Accepted && decoded.MessageId == id) return (T)decoded.Message;
        await Task.Delay(10, ct);
    }
    throw new TimeoutException("receive-" + id);
}
void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
sealed class RejectHandler : IServiceHandler
{
    public ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.FromResult(ServiceReply.Reject("forbidden"));
}

public sealed record AcceptanceOptions(string GateAddress, TimeSpan Deadline)
{
    public static AcceptanceOptions Load(Func<string, string?> environment)
    {
        string address = environment("AINATIVE_ACCEPTANCE_GATE_ADDRESS") ?? "127.0.0.1:23001";
        string raw = environment("AINATIVE_ACCEPTANCE_DEADLINE_SECONDS") ?? "240";
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > 86400)
            throw new ArgumentException("AINATIVE_ACCEPTANCE_DEADLINE_SECONDS must be an integer from 1 to 86400.");
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("AINATIVE_ACCEPTANCE_GATE_ADDRESS cannot be blank.");
        return new(address, TimeSpan.FromSeconds(seconds));
    }
}

public static class AcceptanceSettlementPolling
{
    public static async Task<PlayerProfile> WaitAsync(Func<CancellationToken, Task<PlayerProfile?>> probe, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlayerProfile? profile = await probe(cancellationToken);
            if (profile?.Played == 1) return profile;
            if (profile?.Played > 1) throw new InvalidOperationException("settlement-count-exceeds-one");
            await Task.Delay(300, cancellationToken);
        }
    }
}

/// <summary>One reader per transport; keeps the newest snapshot in a bounded mailbox.</summary>
public sealed class SnapshotInputLoop : IAsyncDisposable
{
    readonly IRealtimeTransport transport;
    readonly CancellationTokenSource stop;
    readonly Channel<Snapshot> snapshots = Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true });
    int disposed;
    long sentInputs;
    Snapshot? lastObservedSnapshot;
    public long SentInputs => Interlocked.Read(ref sentInputs);
    public Snapshot? LastSnapshot => Volatile.Read(ref lastObservedSnapshot);
    public Task Completion { get; }
    public SnapshotInputLoop(IRealtimeTransport transport, CancellationToken cancellationToken)
    {
        this.transport = transport;
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = PumpAsync();
    }
    async Task PumpAsync()
    {
        byte[] receive = new byte[1200], send = new byte[1200];
        Snapshot? latest = null;
        uint sequence = 0;
        long lastSnapshot = 0, nextSend = 0;
        Exception? failure = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // A busy peer cannot starve cancellation or input sends.
                for (int budget = 0; budget < 64 && !stop.IsCancellationRequested && transport.TryReceive(receive, out var packet); budget++)
                {
                    if (!packet.IsComplete || RealtimeProtocolCodec.TryDecode(receive.AsSpan(0, packet.WrittenBytes), out var decoded) != ProtocolDecodeStatus.Accepted || decoded.Message is not Snapshot snapshot) continue;
                    if (latest is not null && snapshot.RoomTick < latest.RoomTick) continue;
                    latest = snapshot; Volatile.Write(ref lastObservedSnapshot, snapshot); sequence = Math.Max(sequence, snapshot.LastProcessedInputSequence);
                    lastSnapshot = Stopwatch.GetTimestamp(); snapshots.Writer.TryWrite(snapshot);
                    if (snapshot.MatchPhase == ArenaMatchPhase.ArenaMatchFinished) return;
                }
                long now = Stopwatch.GetTimestamp();
                if (latest is not null && now >= nextSend && Stopwatch.GetElapsedTime(lastSnapshot).TotalSeconds < 1)
                {
                    InputCommand input = new() { RoomTick = checked(latest.RoomTick + 2), Sequence = checked(++sequence), MoveXMilli = 1000 };
                    if (!RealtimeProtocolCodec.TryEncode(MessageId.InputCommand, input, send, out var channel, out int length))
                        throw new InvalidOperationException("continuous-input-send");
                    SendResult result = await transport.SendAsync(channel, send.AsMemory(0, length), stop.Token);
                    if (result.Status is SendStatus.Closed or SendStatus.Faulted) throw new PeerLossSendException();
                    if (result.Status != SendStatus.Accepted) throw new InvalidOperationException("continuous-input-send");
                    Interlocked.Increment(ref sentInputs); nextSend = now + Stopwatch.Frequency / 10;
                }
                await Task.Delay(10, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; throw; }
        finally { snapshots.Writer.TryComplete(failure); }
    }
    public async Task<Snapshot> ReadAsync(TimeSpan timeout)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        wait.CancelAfter(timeout);
        try { return await snapshots.Reader.ReadAsync(wait.Token); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { throw new TimeoutException("receive-Snapshot"); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        try { await Completion; } finally { stop.Dispose(); }
    }

    internal async ValueTask StopAfterVerifiedPeerLossAsync()
    {
        try { await DisposeAsync(); }
        catch (PeerLossSendException) { }
    }

    sealed class PeerLossSendException : InvalidOperationException
    {
        public PeerLossSendException() : base("continuous-input-send") { }
    }
}
