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

List<object> evidence = [];
string output = Environment.GetEnvironmentVariable("AINATIVE_ACCEPTANCE_REPORT") ?? "acceptance.json";
using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(240));
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
    await using FantasyBackendProbe gateA = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
    await using FantasyBackendProbe gateB = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
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
        Snapshot snapshot = await Receive<Snapshot>(battleA.Transport, MessageId.Snapshot, TimeSpan.FromSeconds(10));
        await Send(battleA.Transport, MessageId.InputCommand, new InputCommand { RoomTick = snapshot.RoomTick + 2, Sequence = 1, MoveXMilli = 1000 });
        do { snapshot = await Receive<Snapshot>(battleA.Transport, MessageId.Snapshot, TimeSpan.FromSeconds(10)); } while (snapshot.LastProcessedInputSequence < 1);
        evidence.Add(new { scenario = "kcp-input-ack", passed = true, snapshot.RoomTick, snapshot.LastProcessedInputSequence });
        string? restartSignal = Environment.GetEnvironmentVariable("AINATIVE_GATE_RESTART_SIGNAL");
        if (restartSignal is not null)
        {
            File.WriteAllText(restartSignal, "active");
            DateTime restartDeadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(restartSignal + ".done")) { if (DateTime.UtcNow > restartDeadline) throw new TimeoutException("gate-restart-harness"); await Task.Delay(100, ct); }
            Snapshot continued = await Receive<Snapshot>(battleA.Transport, MessageId.Snapshot, TimeSpan.FromSeconds(5));
            Check(continued.RoomTick > snapshot.RoomTick, "battle-continues-during-gate-restart");
            evidence.Add(new { scenario = "gate-restart-battle-continues", passed = true, continued.RoomTick });
        }
        await using var replacement = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
        var resumed = await Join(replacement.Transport, readyA);
        Check(resumed.EntityId == first.EntityId, "reconnect-same-entity");
        evidence.Add(new { scenario = "reconnect", passed = true, resumed.EntityId });
        await using var invalid = await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address, ct);
        await Send(invalid.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "acceptance", EntryTicket = readyA.EntryTicket, GlobalRoomId = "wrong-room" });
        bool rejected = false;
        try { await Receive<LoginResponse>(invalid.Transport, MessageId.LoginResponse, TimeSpan.FromSeconds(2)); } catch (TimeoutException) { rejected = true; }
        Check(rejected, "wrong-room-ticket-no-response");
        evidence.Add(new { scenario = "wrong-room-ticket", passed = true });
        do { snapshot = await Receive<Snapshot>(replacement.Transport, MessageId.Snapshot, TimeSpan.FromSeconds(30)); } while (snapshot.MatchPhase != ArenaMatchPhase.ArenaMatchFinished);
        await using FantasyBackendProbe profileGate = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
        PlayerProfile profile;
        do { await Task.Delay(300, ct); profile = (await profileGate.CallAsync(ServiceMethods.Profile, new ProfileRequest { PlayerId = a.login.PlayerId }.ToByteArray(), a.login.SessionToken, ct)).Read(PlayerProfile.Parser); } while (profile.Played != 1);
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
    await using FantasyBackendProbe loginConnection = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
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
