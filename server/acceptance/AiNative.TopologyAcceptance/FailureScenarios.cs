using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Protocol.Backend.V1;
using AiNative.Protocol.V1;
using AiNative.Realtime;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using AiNative.Server.Fantasy;
using AiNative.Server.Hosting;
using AiNative.Server.Protocol;
using Google.Protobuf;
using Npgsql;

// Qualification-only cleanup. Party IDs and original identity are retained; unknown mutation replies never count as success.
internal static class QualificationPartyCleanup
{
    public static async Task LeaveAsync(string playerId, string partyId, string requestId,
        Func<string, IMessage, CancellationToken, Task<ServiceReply>> call, TimeProvider clock)
    {
        long started = clock.GetTimestamp();
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        int leaveAttempts = 0;
        Exception? lastStatusFailure = null;
        while (clock.GetElapsedTime(started) < timeout)
        {
            ServiceReply reply;
            try { reply = await call(ServiceMethods.PartyGet, new PartyCommand { PartyId = partyId }, CancellationToken.None); }
            catch (Exception failure) when (failure is TimeoutException or ServiceException)
            { throw new InvalidOperationException("qualification-party-cleanup-unconfirmed", failure); }
            if (reply.Error == "party_not_found") return;
            if (!reply.Success) throw new InvalidOperationException("qualification-party-cleanup-unconfirmed");
            PartyState party = reply.Read(PartyState.Parser);
            if (party.PartyId != partyId || party.Members.Count != 1 || party.Members[0].PlayerId != playerId)
                throw new InvalidOperationException("qualification-party-cleanup-identity-conflict");
            if (party.QueueRequestId.Length > 0)
            {
                if (party.QueueRequestId != requestId) throw new InvalidOperationException("qualification-party-cleanup-request-conflict");
                try
                {
                    var statusReply = await call(ServiceMethods.MatchStatus, new MatchQuery { RequestId = requestId }, CancellationToken.None);
                    if (!statusReply.Success)
                    {
                        lastStatusFailure = new InvalidOperationException("match-status-rejected:" + statusReply.Error);
                        // Gate issues a ticket for cached ready status. Coordinator can already be
                        // Released before Match's next pump clears that status and Lobby's queue.
                        // A rejected ticket proves neither party absence nor permission to cancel.
                        if (statusReply.Error != "invalid_allocation")
                            throw new InvalidOperationException("qualification-party-cleanup-unconfirmed", lastStatusFailure);
                        if (clock.GetElapsedTime(started) < timeout) await Task.Delay(250);
                        continue;
                    }
                    var status = statusReply.Read(MatchReady.Parser).Status;
                    // An allocated room must finish through its original battle and settlement; never cancel it here.
                    if (status.State == "queued")
                    {
                        var cancelled = await call(ServiceMethods.QueueCancel, new MatchQuery { RequestId = requestId }, CancellationToken.None);
                        if (!cancelled.Success) throw new InvalidOperationException("qualification-party-cleanup-unconfirmed");
                    }
                }
                catch (Exception failure) when (failure is TimeoutException or ServiceException)
                { throw new InvalidOperationException("qualification-party-cleanup-unconfirmed", failure); }
            }
            else
            {
                if (++leaveAttempts > 3) throw new InvalidOperationException("qualification-party-cleanup-unconfirmed");
                try { await call(ServiceMethods.PartyLeave, new PartyCommand { PartyId = partyId, ExpectedVersion = party.Version }, CancellationToken.None); }
                catch (Exception failure) when (failure is TimeoutException or ServiceException) { /* Verify the uncertain mutation through PartyGet below. */ }
                // Even a successful reply is followed by an authoritative absence check, using the same party ID.
                continue;
            }
            if (clock.GetElapsedTime(started) < timeout) await Task.Delay(250);
        }
        throw new InvalidOperationException("qualification-party-cleanup-unconfirmed", lastStatusFailure);
    }
}

internal static class FailureScenarios
{
    public static async Task RunAsync(FantasyServiceRuntime runtime, string mode, List<object> evidence, CancellationToken ct)
    {
        var fixture = new Fixture(runtime, evidence, ct);
        if (mode == "qualification") { await new ArenaCapacityRunner(runtime, fixture, evidence, ct).RunAsync(); return; }
        if (mode is "capacity" or "bots") { await fixture.CapacityAndBots(mode == "bots"); return; }
        if (mode == "party-notifications") { await fixture.PartyNotifications(); return; }
        await using Pair pair = await fixture.CreatePair();
        var original = pair.ReadyA.Status.Allocation;
        Snapshot before = await fixture.FreshSnapshot(pair.A.Transport);
        if (mode == "backend-restarts")
        {
            fixture.BeginKeepAlive(pair);
            RoomAllocation previous = await fixture.DatabaseRoom(original.RoomId);
            await fixture.Signal("restart", "coordinator");
            RoomAllocation recovered = await fixture.DatabaseRoom(original.RoomId);
            Fixture.Check(recovered.RoomId == previous.RoomId && recovered.NodeId == previous.NodeId && recovered.BootEpoch == previous.BootEpoch && recovered.PlayerIds.SequenceEqual(previous.PlayerIds) && recovered.CoordinatorEpoch != previous.CoordinatorEpoch && recovered.State == "Ready", "coordinator-rebuild-identity-epoch");
            Snapshot after = await fixture.FreshSnapshot(pair.A.Transport); Fixture.Check(after.RoomTick > before.RoomTick, "coordinator-restart-keeps-battle");
            evidence.Add(new { scenario = "coordinator-restart-rebuild", passed = true, original.RoomId, oldCoordinatorEpoch = previous.CoordinatorEpoch, newCoordinatorEpoch = recovered.CoordinatorEpoch, after.RoomTick });
            var pendingMatch = await fixture.Queue();
            await fixture.Signal("restart", "match");
            var stale = (await pendingMatch.gate.CallAsync(ServiceMethods.MatchStatus, new MatchQuery { RequestId = pendingMatch.request }.ToByteArray(), pendingMatch.login.SessionToken, ct)).Read(MatchReady.Parser);
            Fixture.Check(stale.Status.State == "failed" && stale.Status.Failure == "queue_lost", "match-restart-old-queue-lost");
            var party = (await pendingMatch.gate.CallAsync(ServiceMethods.PartyGet, new PartyCommand { PartyId = pendingMatch.partyId }.ToByteArray(), pendingMatch.login.SessionToken, ct)).Read(PartyState.Parser);
            Fixture.Check(party.QueueRequestId.Length == 0 && party.Members.All(x => !x.Ready), "match-restart-party-reconfirmation");
            party = (await pendingMatch.gate.CallAsync(ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray(), pendingMatch.login.SessionToken, ct)).Read(PartyState.Parser);
            string replacementRequest = Guid.NewGuid().ToString("N");
            (await pendingMatch.gate.CallAsync(ServiceMethods.PartyQueue, new PartyQueueRequest { PartyId = party.PartyId, ExpectedVersion = party.Version, RequestId = replacementRequest }.ToByteArray(), pendingMatch.login.SessionToken, ct)).Read(MatchStatus.Parser);
            var cancelled = (await pendingMatch.gate.CallAsync(ServiceMethods.QueueCancel, new MatchQuery { RequestId = replacementRequest }.ToByteArray(), pendingMatch.login.SessionToken, ct)).Read(MatchStatus.Parser);
            Fixture.Check(cancelled.State == "cancelled", "reconfirmed-queue-cancelled"); await pendingMatch.gate.DisposeAsync();
            evidence.Add(new { scenario = "independent-match-restart-party-reconfirmation", passed = true, stale.Status.Failure, party.Version });
            var pendingLobby = await fixture.Queue();
            await fixture.Signal("restart", "lobby");
            var surviving = (await pendingLobby.gate.CallAsync(ServiceMethods.MatchStatus, new MatchQuery { RequestId = pendingLobby.request }.ToByteArray(), pendingLobby.login.SessionToken, ct)).Read(MatchReady.Parser);
            Fixture.Check(surviving.Status.State == "queued", "lobby-restart-preserves-match-queue");
            cancelled = (await pendingLobby.gate.CallAsync(ServiceMethods.QueueCancel, new MatchQuery { RequestId = pendingLobby.request }.ToByteArray(), pendingLobby.login.SessionToken, ct)).Read(MatchStatus.Parser);
            Fixture.Check(cancelled.State == "cancelled", "lobby-restart-roster-authorized-cancel"); await pendingLobby.gate.DisposeAsync();
            evidence.Add(new { scenario = "independent-lobby-restart-match-queue-status-cancel", passed = true, surviving.Status.State, cancellation = cancelled.State });
            after = await fixture.FreshSnapshot(pair.A.Transport); Fixture.Check(after.RoomTick > before.RoomTick, "lobby-match-restart-keeps-battle");
            // Fresh identities prevent double allocation of players still in the original room.
            await using Pair reconfirmed = await fixture.CreatePair();
            Fixture.Check(reconfirmed.ReadyA.Status.Allocation.RoomId != original.RoomId, "fresh-party-rematch");
            evidence.Add(new { scenario = "active-battle-survives-independent-backend-restarts", passed = true, newRoom = reconfirmed.ReadyA.Status.Allocation.RoomId, after.RoomTick });
            await fixture.FinishAndProfile(pair);
        }
        else if (mode == "player-outage")
        {
            await fixture.Signal("stop", "player");
            Snapshot finished = await fixture.Finished(pair.A.Transport);
            string path = Path.Combine(HostSettings.Required("AINATIVE_ACCEPTANCE_RUN_DIRECTORY"), "outbox", original.NodeId, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(original.MatchId))) + ".result");
            await fixture.Until(() => File.Exists(path), "durable-result-on-disk", 10);
            MatchResult durable = MatchResult.Parser.ParseFrom(await File.ReadAllBytesAsync(path, ct));
            Fixture.Check(durable.MatchId == original.MatchId && durable.Completion == "Finished", "outbox-result-identity");
            await using var database = new PostgresPlayerStore(HostSettings.Required("AINATIVE_POSTGRES_CONNECTION_STRING"));
            var offline = await database.ProfileAsync(pair.LoginA.PlayerId, ct); Fixture.Check(offline?.Played == 0, "no-settlement-during-player-outage");
            evidence.Add(new { scenario = "player-down-durable-outbox", passed = true, finished.RoomTick, durableBytes = new FileInfo(path).Length, durablePlayers = durable.Players.Count });
            await fixture.Signal("start", "player");
            await fixture.ProfileOnce(pair);
            await fixture.Until(() => !File.Exists(path), "outbox-confirmation-removes-file", 15);
            evidence.Add(new { scenario = "player-recovery-outbox-retry", passed = true });
        }
        else if (mode == "battle-crash")
        {
            await fixture.Signal("restart", original.NodeId);
            RoomAllocation lost = await fixture.DatabaseRoom(original.RoomId); Fixture.Check(lost.State == "Lost", "old-allocation-lost");
            await using var stale = await runtime.ConnectBattleProbeAsync(original.Address, ct);
            await fixture.Send(stale.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "failure-acceptance", EntryTicket = pair.ReadyA.EntryTicket, GlobalRoomId = original.RoomId });
            bool rejected = false; try { await fixture.Receive<LoginResponse>(stale.Transport, MessageId.LoginResponse, 2); } catch (TimeoutException) { rejected = true; }
            Fixture.Check(rejected, "old-boot-ticket-no-login");
            await using Pair replacement = await fixture.CreatePair();
            RoomAllocation next = replacement.ReadyA.Status.Allocation;
            if (next.NodeId == original.NodeId) Fixture.Check(next.BootEpoch != original.BootEpoch, "new-boot-epoch");
            evidence.Add(new { scenario = "battle-crash-old-allocation-ticket-fenced", passed = true, original.NodeId, oldBoot = original.BootEpoch, lost.State, replacementNode = next.NodeId, replacementBoot = next.BootEpoch });
            await fixture.FinishAndProfile(replacement);
        }
        else if (mode == "expired-ticket")
        {
            fixture.BeginKeepAlive(pair);
            using var claims = JsonDocument.Parse(Convert.FromBase64String(pair.ReadyA.EntryTicket.Split('.')[0]));
            long expiry = claims.RootElement.GetProperty("Expiry").GetInt64();
            while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= expiry) { await Task.Delay(1000, ct); await fixture.FreshSnapshot(pair.A.Transport); }
            Snapshot active = await fixture.FreshSnapshot(pair.A.Transport);
            Fixture.Check(active.MatchPhase == ArenaMatchPhase.ArenaMatchActive, "expiry-tested-in-live-room");
            await using var expired = await runtime.ConnectBattleProbeAsync(original.Address, ct);
            await fixture.Send(expired.Transport, MessageId.LoginRequest, new LoginRequest { ProtocolMajor = 1, ClientBuild = "expiry-acceptance", EntryTicket = pair.ReadyA.EntryTicket, GlobalRoomId = original.RoomId });
            bool rejected = false; try { await fixture.Receive<LoginResponse>(expired.Transport, MessageId.LoginResponse, 2); } catch (TimeoutException) { rejected = true; }
            Fixture.Check(rejected, "actually-expired-player-issued-ticket");
            evidence.Add(new { scenario = "expired-player-issued-ticket-live-room", passed = true, expiryUnixSeconds = expiry, active.RoomTick });
            await fixture.FinishAndProfile(pair);
        }
        else throw new InvalidOperationException("unknown-failure-mode");
    }

    internal sealed class Fixture(FantasyServiceRuntime runtime, List<object> evidence, CancellationToken ct)
    {
        int signal;
        public static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); }
        public async Task Signal(string action, params string[] services)
        {
            string directory = HostSettings.Required("AINATIVE_FAULT_SIGNALS"); int number = ++signal;
            string request = Path.Combine(directory, number + ".request.json");
            File.WriteAllText(request, JsonSerializer.Serialize(new { action, services }));
            string done = Path.Combine(directory, number + ".done.json");
            await Until(() => File.Exists(done), "fault-harness-timeout-" + action, 35);
            using var json = JsonDocument.Parse(File.ReadAllText(done)); Check(json.RootElement.GetProperty("status").GetString() == "done", "fault-harness-failed");
        }
        public async Task Until(Func<bool> predicate, string name, int seconds)
        { var end = DateTime.UtcNow.AddSeconds(seconds); while (!predicate()) { if (DateTime.UtcNow > end) throw new TimeoutException(name); await Task.Delay(100, ct); } }
        public void BeginKeepAlive(Pair pair)
        {
            pair.KeepAliveTask = Task.Run(async () =>
            {
                byte[] bytes = new byte[1200]; ulong tick = 0; uint sequence = 0;
                try
                {
                    while (!pair.KeepAliveStop.IsCancellationRequested)
                    {
                        while (pair.B.Transport.TryReceive(bytes, out var packet))
                            if (packet.IsComplete && RealtimeProtocolCodec.TryDecode(bytes.AsSpan(0, packet.WrittenBytes), out var decoded) == ProtocolDecodeStatus.Accepted && decoded.Message is Snapshot snapshot) { tick = snapshot.RoomTick; sequence = Math.Max(sequence,snapshot.LastProcessedInputSequence); }
                        if (tick > 0)
                        {
                            var input = new InputCommand { RoomTick = tick + 2, Sequence = ++sequence };
                            await Send(pair.A.Transport, MessageId.InputCommand, input); await Send(pair.B.Transport, MessageId.InputCommand, input);
                        }
                        await Task.Delay(500, pair.KeepAliveStop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) when (pair.A.Transport.State != TransportState.Connected || pair.B.Transport.State != TransportState.Connected) { }
            }, ct);
        }
        public async Task<RoomAllocation> DatabaseRoom(string room)
        {
            await using var source = NpgsqlDataSource.Create(HostSettings.Required("AINATIVE_POSTGRES_CONNECTION_STRING"));
            await using var command = source.CreateCommand("SELECT payload FROM coordinator.rooms WHERE room_id=$1"); command.Parameters.AddWithValue(room);
            return RoomAllocation.Parser.ParseFrom((byte[])(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("room-not-persisted")));
        }
        public async Task<(FantasyBackendProbe gate, LoginResult login, string request, string partyId)> Queue()
        {
            PreparedMember member = await Prepare(ct);
            try { await Enqueue(member, ct); return (member.gate, member.login, member.request, member.partyId); }
            catch { await member.DisposeAsync(); throw; }
        }
        public sealed record PreparedMember(FantasyBackendProbe gate, LoginResult login, string request, string partyId, ulong partyVersion,
            Func<PreparedMember, Task>? cleanup = null) : IAsyncDisposable
        {
            readonly object ownership = new();
            Task? disposal, transportDisposal;
            public ValueTask DisposeTransportAsync()
            { lock (ownership) return new(transportDisposal ??= gate.DisposeAsync().AsTask()); }
            public ValueTask DisposeAsync()
            { lock (ownership) return new(disposal ??= DisposeCoreAsync()); }
            async Task DisposeCoreAsync()
            { try { if (cleanup is not null) await cleanup(this); } finally { await DisposeTransportAsync(); } }
        }
        public async Task<PreparedMember> Prepare(CancellationToken token, bool cleanupParty = false)
        {
            token.ThrowIfCancellationRequested();
            // Await the bounded connection attempt before observing cancellation, so a late probe always has an owner.
            var gate = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", CancellationToken.None);
            LoginResult? login = null;
            PartyState? party = null;
            string request = Guid.NewGuid().ToString("N");
            try
            {
                token.ThrowIfCancellationRequested();
                var account = new AccountRequest { Username = "fault_" + Guid.NewGuid().ToString("N")[..16], Password = Guid.NewGuid().ToString("N") };
                login = (await gate.CallAsync(ServiceMethods.RegisterAccount, account.ToByteArray(), ct:token)).Read(LoginResult.Parser);
                await using var loginConnection = await runtime.ConnectGateProbeAsync("127.0.0.1:23001",CancellationToken.None);
                token.ThrowIfCancellationRequested();
                login = (await loginConnection.CallAsync(ServiceMethods.Login,account.ToByteArray(),ct:token)).Read(LoginResult.Parser);
                // Cancellation of the probe waiter cannot cancel an already accepted backend mutation.
                // Qualification awaits its bounded reply before transferring or cleaning the known party.
                CancellationToken mutation = cleanupParty ? CancellationToken.None : token;
                token.ThrowIfCancellationRequested();
                try { party = (await gate.CallAsync(ServiceMethods.PartyCreate, new Empty().ToByteArray(), login.SessionToken, mutation)).Read(PartyState.Parser); }
                catch (Exception failure) when (cleanupParty && failure is TimeoutException or ServiceException)
                { throw new InvalidOperationException("qualification-party-create-outcome-unknown", failure); }
                token.ThrowIfCancellationRequested();
                party = (await gate.CallAsync(ServiceMethods.PartyReady, new PartyCommand { PartyId = party.PartyId, ExpectedVersion = party.Version, Ready = true }.ToByteArray(), login.SessionToken, mutation)).Read(PartyState.Parser);
                token.ThrowIfCancellationRequested();
                return new(gate, login, request, party.PartyId, party.Version, cleanupParty ? CleanupPreparedParty : null);
            }
            catch
            {
                try
                {
                    if (cleanupParty && party is not null && login is not null)
                        await CleanupPreparedParty(new(gate, login, request, party.PartyId, party.Version));
                }
                finally { await gate.DisposeAsync(); }
                throw;
            }
        }
        public async Task KeepPreparedAlive(PreparedMember member, CancellationToken token)
        {
            // Await the whole bounded call. A cancelled response waiter would still leave an RPC in flight at handoff.
            var party = (await member.gate.CallAsync(ServiceMethods.PartyGet,
                new PartyCommand { PartyId = member.partyId }.ToByteArray(), member.login.SessionToken, CancellationToken.None)).Read(PartyState.Parser);
            Check(member.login.ExpiresUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() && party.PartyId == member.partyId &&
                party.Version == member.partyVersion && party.QueueRequestId.Length == 0 && party.Members.Count == 1 &&
                party.Members[0].PlayerId == member.login.PlayerId && party.Members[0].Ready, "qualification-prepared-party-changed");
        }
        async Task CleanupPreparedParty(PreparedMember member)
        {
            // The original Gate can expire during a match. This fresh connection uses the original session and identity.
            await using var gate = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", CancellationToken.None);
            await QualificationPartyCleanup.LeaveAsync(member.login.PlayerId, member.partyId, member.request,
                async (method, payload, token) => await gate.CallAsync(method, payload.ToByteArray(), member.login.SessionToken, token), TimeProvider.System);
        }
        public async Task Enqueue(PreparedMember member, CancellationToken token)
        {
            (await member.gate.CallAsync(ServiceMethods.PartyQueue,
                new PartyQueueRequest { PartyId = member.partyId, ExpectedVersion = member.partyVersion, RequestId = member.request }.ToByteArray(), member.login.SessionToken, token)).Read(MatchStatus.Parser);
        }
        public async Task<MatchReady> Ready(FantasyBackendProbe gate, LoginResult login, string request, bool allowFailure = false, CancellationToken? cancellation = null)
        {
            CancellationToken token = cancellation ?? ct;
            for(int i=0;i<150;i++) {
                var ready=(await gate.CallAsync(ServiceMethods.MatchStatus,new MatchQuery{RequestId=request}.ToByteArray(),login.SessionToken,token)).Read(MatchReady.Parser);
                if(ready.EntryTicket.Length>0) return ready;
                if(ready.Status.State=="failed" || ready.Status.Failure.Length>0) { if(allowFailure)return ready; throw new InvalidOperationException("match-failure:"+ready.Status.Failure); }
                await Task.Delay(200,token);
            }
            throw new TimeoutException("allocation-not-ready");
        }
        public async Task<Pair> CreatePair()
        {
            var a=await Queue(); var b=await Queue(); var readyA=await Ready(a.gate,a.login,a.request);var readyB=await Ready(b.gate,b.login,b.request);
            Check(readyA.Status.Allocation.RoomId==readyB.Status.Allocation.RoomId,"same-room");
            var wireA=await runtime.ConnectBattleProbeAsync(readyA.Status.Allocation.Address,ct); var wireB=await runtime.ConnectBattleProbeAsync(readyB.Status.Allocation.Address,ct);
            var entityA=await Join(wireA.Transport,readyA);var entityB=await Join(wireB.Transport,readyB);
            return new(a.gate,b.gate,a.login,b.login,a.request,b.request,readyA,readyB,wireA,wireB,entityA.EntityId,entityB.EntityId);
        }
        public async Task<JoinRoomResponse> Join(IRealtimeTransport transport,MatchReady ready,CancellationToken? cancellation = null)
        {
            await Send(transport,MessageId.LoginRequest,new LoginRequest{ProtocolMajor=1,ClientBuild="failure-acceptance",EntryTicket=ready.EntryTicket,GlobalRoomId=ready.Status.Allocation.RoomId},cancellation);
            var login=await Receive<LoginResponse>(transport,MessageId.LoginResponse,10,cancellation);
            Check(login.BootEpoch==ready.Status.Allocation.BootEpoch,"join-boot-identity");
            await Send(transport,MessageId.JoinRoomRequest,new JoinRoomRequest{SessionId=login.SessionId,RequestedRoom=1},cancellation);
            return await Receive<JoinRoomResponse>(transport,MessageId.JoinRoomResponse,10,cancellation);
        }
        public async Task Send(IRealtimeTransport transport,MessageId id,IMessage message,CancellationToken? cancellation = null)
        {byte[] bytes=new byte[1200];Check(RealtimeProtocolCodec.TryEncode(id,message,bytes,out var channel,out int length),"encode");Check((await transport.SendAsync(channel,bytes.AsMemory(0,length),cancellation ?? ct)).Status==SendStatus.Accepted,"send");}
        public async Task<T> Receive<T>(IRealtimeTransport transport,MessageId id,int seconds,CancellationToken? cancellation = null) where T:IMessage
        {
            CancellationToken token = cancellation ?? ct;
            var end=DateTime.UtcNow.AddSeconds(seconds);byte[] bytes=new byte[1200];
            while(DateTime.UtcNow<end){while(transport.TryReceive(bytes,out var packet))if(packet.IsComplete && RealtimeProtocolCodec.TryDecode(bytes.AsSpan(0,packet.WrittenBytes),out var decoded)==ProtocolDecodeStatus.Accepted && decoded.MessageId==id)return(T)decoded.Message;await Task.Delay(10,token);}
            throw new TimeoutException("receive-"+id);
        }
        public Task<Snapshot> Snapshot(IRealtimeTransport transport)=>Receive<Snapshot>(transport,MessageId.Snapshot,15);
        public Task<Snapshot> FreshSnapshot(IRealtimeTransport transport,CancellationToken? cancellation = null)
        {
            byte[] bytes = new byte[1200]; while (transport.TryReceive(bytes, out _)) { }
            return Receive<Snapshot>(transport,MessageId.Snapshot,15,cancellation);
        }
        public async Task<Snapshot> Finished(IRealtimeTransport transport)
        {Snapshot value;do{value=await Snapshot(transport);}while(value.MatchPhase!=ArenaMatchPhase.ArenaMatchFinished);return value;}
        public async Task ProfileOnce(Pair pair)
        {
            await using var gate=await runtime.ConnectGateProbeAsync("127.0.0.1:23001",ct);
            PlayerProfile profile;var end=DateTime.UtcNow.AddSeconds(30);
            do{if(DateTime.UtcNow>end)throw new TimeoutException("profile-retry");await Task.Delay(300,ct);var reply=await gate.CallAsync(ServiceMethods.Profile,new ProfileRequest{PlayerId=pair.LoginA.PlayerId}.ToByteArray(),pair.LoginA.SessionToken,ct);if(!reply.Success)continue;profile=reply.Read(PlayerProfile.Parser);if(profile.Played==1)break;}while(true);
            await Task.Delay(1200,ct);profile=(await gate.CallAsync(ServiceMethods.Profile,new ProfileRequest{PlayerId=pair.LoginA.PlayerId}.ToByteArray(),pair.LoginA.SessionToken,ct)).Read(PlayerProfile.Parser);Check(profile.Played==1,"profile-count-once");
            evidence.Add(new{scenario="completed-profile",passed=true,profile.Played,profile.Won,profile.Kills});
        }
        public async Task FinishAndProfile(Pair pair){await Finished(pair.A.Transport);await ProfileOnce(pair);}

        public async Task PartyNotifications()
        {
            await using var leader = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
            await using var member = await runtime.ConnectGateProbeAsync("127.0.0.1:23001", ct);
            async Task<LoginResult> Register(FantasyBackendProbe gate)
            {
                var account=new AccountRequest{Username="party_"+Guid.NewGuid().ToString("N")[..16],Password=Guid.NewGuid().ToString("N")};
                (await gate.CallAsync(ServiceMethods.RegisterAccount,account.ToByteArray(),ct:ct)).Read(LoginResult.Parser);
                await using var loginConnection=await runtime.ConnectGateProbeAsync("127.0.0.1:23001",ct);
                return(await loginConnection.CallAsync(ServiceMethods.Login,account.ToByteArray(),ct:ct)).Read(LoginResult.Parser);
            }
            var leaderLogin=await Register(leader);var memberLogin=await Register(member);
            var party=(await leader.CallAsync(ServiceMethods.PartyCreate,new Empty().ToByteArray(),leaderLogin.SessionToken,ct)).Read(PartyState.Parser);
            party=(await leader.CallAsync(ServiceMethods.PartyInvite,new PartyCommand{PartyId=party.PartyId,TargetPlayerId=memberLogin.PlayerId,ExpectedVersion=party.Version}.ToByteArray(),leaderLogin.SessionToken,ct)).Read(PartyState.Parser);
            var invitations=(await member.CallAsync(ServiceMethods.PartyInvites,new Empty().ToByteArray(),memberLogin.SessionToken,ct)).Read(PartyInvitations.Parser);
            Check(invitations.Parties.Any(x=>x.PartyId==party.PartyId),"member-discovers-invitation");
            party=(await member.CallAsync(ServiceMethods.PartyAccept,new PartyCommand{PartyId=party.PartyId,ExpectedVersion=party.Version}.ToByteArray(),memberLogin.SessionToken,ct)).Read(PartyState.Parser);
            party=(await leader.CallAsync(ServiceMethods.PartyReady,new PartyCommand{PartyId=party.PartyId,ExpectedVersion=party.Version,Ready=true}.ToByteArray(),leaderLogin.SessionToken,ct)).Read(PartyState.Parser);
            party=(await member.CallAsync(ServiceMethods.PartyReady,new PartyCommand{PartyId=party.PartyId,ExpectedVersion=party.Version,Ready=true}.ToByteArray(),memberLogin.SessionToken,ct)).Read(PartyState.Parser);
            string request=Guid.NewGuid().ToString("N");
            (await leader.CallAsync(ServiceMethods.PartyQueue,new PartyQueueRequest{PartyId=party.PartyId,ExpectedVersion=party.Version,RequestId=request}.ToByteArray(),leaderLogin.SessionToken,ct)).Read(MatchStatus.Parser);
            async Task<MatchReady> Notification(FantasyBackendProbe gate){var end=DateTime.UtcNow.AddSeconds(20);while(DateTime.UtcNow<end){while(gate.TryReadNotification(out var notification))if(notification?.Method=="match.ready")return MatchReady.Parser.ParseFrom(notification.Body);await Task.Delay(50,ct);}throw new TimeoutException("member-match-ready-notification");}
            var readyLeader=await Notification(leader);var readyMember=await Notification(member);
            Check(readyLeader.Status.Allocation.RoomId==readyMember.Status.Allocation.RoomId && readyLeader.EntryTicket!=readyMember.EntryTicket,"distinct-member-tickets-one-party-room");
            await using var wireLeader=await runtime.ConnectBattleProbeAsync(readyLeader.Status.Allocation.Address,ct);
            await using var wireMember=await runtime.ConnectBattleProbeAsync(readyMember.Status.Allocation.Address,ct);
            var joinedLeader=await Join(wireLeader.Transport,readyLeader);var joinedMember=await Join(wireMember.Transport,readyMember);
            Check(joinedLeader.EntityId!=joinedMember.EntityId,"member-ticket-distinct-entity");
            evidence.Add(new{scenario="two-members-one-party-notifications",passed=true,partyMembers=2,readyLeader.Status.Allocation.RoomId,leaderEntity=joinedLeader.EntityId,memberEntity=joinedMember.EntityId});
        }

        public async Task CapacityAndBots(bool bots)
        {
            var cancelled=await Queue();
            var cancellation=(await cancelled.gate.CallAsync(ServiceMethods.QueueCancel,new MatchQuery{RequestId=cancelled.request}.ToByteArray(),cancelled.login.SessionToken,ct)).Read(MatchStatus.Parser);Check(cancellation.State=="cancelled","cancel-before-match");await cancelled.gate.DisposeAsync();
            evidence.Add(new{scenario="cancel-before-partner",passed=true,cancellation.State});
            List<Pair> pairs=[];
            try {
                for(int i=0;i<8;i++){var pair=await CreatePair();pairs.Add(pair);BeginKeepAlive(pair);await Task.Delay(1100,ct);}
                var nodes=pairs.GroupBy(p=>p.ReadyA.Status.Allocation.NodeId).ToDictionary(g=>g.Key,g=>g.Count());Check(nodes.Count==2 && nodes.Values.All(n=>n==4),"eight-slots-two-nodes");
                await Task.Delay(5500,ct);
                var extraA=await Queue();var extraB=await Queue();var rejected=await Ready(extraA.gate,extraA.login,extraA.request,true);
                Check(rejected.EntryTicket.Length==0 && rejected.Status.State!="ready" && rejected.Status.Failure=="capacity-unavailable" && string.IsNullOrEmpty(rejected.Status.Allocation?.RoomId),"ninth-match-bounded-wait-without-allocation");
                var cancel=(await extraA.gate.CallAsync(ServiceMethods.QueueCancel,new MatchQuery{RequestId=extraA.request}.ToByteArray(),extraA.login.SessionToken,ct)).Read(MatchStatus.Parser);Check(cancel.State=="cancelled","cancel-definite-no-capacity-job");
                await extraA.gate.DisposeAsync();await extraB.gate.DisposeAsync();
                evidence.Add(new{scenario="actual-eight-room-capacity-bound",passed=true,rooms=8,nodes,rejection=rejected.Status.Failure,waitingState=rejected.Status.State,cancellation=cancel.State});
                if(bots){foreach(var pair in pairs)await pair.StopKeepAlive();await MeasureBots(pairs);foreach(var pair in pairs)BeginKeepAlive(pair);await FinishAndProfile(pairs[0]);}
            }finally{foreach(var pair in pairs)await pair.DisposeAsync();}
        }
        async Task MeasureBots(List<Pair> pairs)
        {
            const int seed=20261002,warmupSeconds=3,durationSeconds=10;
            var random=new Random(seed);List<Bot> bots=[];
            foreach(var pair in pairs){bots.Add(new(pair.A.Transport,pair.EntityA));bots.Add(new(pair.B.Transport,pair.EntityB));}
            var timer=Stopwatch.StartNew();long nextSend=0;List<double> samples=[];int discarded=0,snapshotCount=0;byte[] buffer=new byte[1200];
            foreach(var bot in bots){bot.Last=await FreshSnapshot(bot.Transport);bot.Sequence=bot.Last.LastProcessedInputSequence;bot.StartX=bot.Last.Players.Single(p=>p.EntityId==bot.Entity).PositionXMilli;}
            timer.Restart();
            using CancellationTokenSource diagnosticStop = new();
            List<DiagnosticPoint> diagnostics = [];
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
            async Task CollectDiagnostics()
            {
                foreach (var node in new[] { (Id:"battle-1",Port:24106),(Id:"battle-2",Port:24107) })
                {
                    using var json = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{node.Port}/health/diagnostics", ct));
                    diagnostics.Add(new(node.Id,DateTimeOffset.UtcNow,timer.Elapsed.TotalSeconds,json.RootElement.Clone()));
                }
            }
            Task diagnosticTask = Task.Run(async () =>
            {
                try { await Task.Delay(warmupSeconds*1000,diagnosticStop.Token); while(!diagnosticStop.IsCancellationRequested){await CollectDiagnostics();await Task.Delay(2000,diagnosticStop.Token);} }
                catch(OperationCanceledException) when(diagnosticStop.IsCancellationRequested) { }
            },ct);
            while(timer.Elapsed.TotalSeconds<warmupSeconds+durationSeconds){
                foreach(var bot in bots){while(bot.Transport.TryReceive(buffer,out var packet)){if(!packet.IsComplete||RealtimeProtocolCodec.TryDecode(buffer.AsSpan(0,packet.WrittenBytes),out var decoded)!=ProtocolDecodeStatus.Accepted||decoded.Message is not Snapshot snap)continue;bot.Last=snap;snapshotCount++;
                    if(bot.Pending.TryGetValue(snap.LastProcessedInputSequence,out long sent)){if(timer.Elapsed.TotalSeconds>=warmupSeconds)samples.Add((Stopwatch.GetTimestamp()-sent)*1000d/Stopwatch.Frequency);foreach(uint old in bot.Pending.Keys.Where(x=>x<=snap.LastProcessedInputSequence).ToArray()){if(old!=snap.LastProcessedInputSequence)discarded++;bot.Pending.Remove(old);}}
                }}
                if(timer.ElapsedMilliseconds>=nextSend){nextSend=timer.ElapsedMilliseconds+50;foreach(var bot in bots){uint seq=++bot.Sequence;bot.Pending[seq]=Stopwatch.GetTimestamp();await Send(bot.Transport,MessageId.InputCommand,new InputCommand{RoomTick=bot.Last.RoomTick+2,Sequence=seq,MoveXMilli=random.Next(2)==0?1000:-1000,MoveYMilli=250,YawMillidegrees=random.Next(360000),Buttons=1,WeaponId=1});}}
                await Task.Delay(2,ct);
            }
            diagnosticStop.Cancel();await diagnosticTask;await CollectDiagnostics();
            File.WriteAllText(Path.Combine(HostSettings.Required("AINATIVE_ACCEPTANCE_RUN_DIRECTORY"),"bot-worker-diagnostics.json"),JsonSerializer.Serialize(diagnostics));
            evidence.Add(new{scenario="actual-worker-gc-memory-diagnostic",passed=true,workers=SummarizeWorkers(diagnostics),nodes=diagnostics.GroupBy(x=>x.Node).Select(g=>new{node=g.Key,startUtc=g.First().Utc,endUtc=g.Last().Utc,start=g.First().Data.GetProperty("gc"),end=g.Last().Data.GetProperty("gc"),processStart=g.First().Data.GetProperty("process"),processEnd=g.Last().Data.GetProperty("process"),replay=g.Last().Data.GetProperty("replay")}).ToArray(),note="Two-second polls merge validated tick-ID windows; incomplete windows are marked. Process GC differs from worker-thread allocation. Local diagnostics, not production capacity."});
            Check(samples.Count>100,"enough-actual-ack-samples");Check(bots.Any(bot=>bot.Last.Players.Single(p=>p.EntityId==bot.Entity).PositionXMilli!=bot.StartX),"actual-bots-moved");
            var sorted=samples.Order().ToArray();double Percent(double p)=>sorted[Math.Clamp((int)Math.Ceiling(p*sorted.Length)-1,0,sorted.Length-1)];
            File.WriteAllText(Path.Combine(HostSettings.Required("AINATIVE_ACCEPTANCE_RUN_DIRECTORY"),"bot-latency-samples.json"),JsonSerializer.Serialize(samples));
            evidence.Add(new{scenario="actual-arena-kcp-bot-diagnostic",passed=true,seed,warmupSeconds,durationSeconds,rooms=pairs.Count,players=bots.Count,inputHz=20,snapshotCount,ackSamples=samples.Count,discardedOrCoalesced=discarded,p50Milliseconds=Percent(.5),p95Milliseconds=Percent(.95),p99Milliseconds=Percent(.99),maximumMilliseconds=sorted[^1],percentileMethod="nearest-rank",configuration="2 Battle nodes x2 workers x2 rooms; no production capacity claim",matchTicks=Environment.GetEnvironmentVariable("AINATIVE_MATCH_LENGTH_TICKS"),cpu=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),logicalProcessors=Environment.ProcessorCount,processAvailableMemoryBytes=GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,physicalRam="unavailable: WMI permission denied",concurrentLoad="Other repository builds/tests may run; no exclusive benchmark host",os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,battleBuildSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"AiNative.Server.Battle.dll")))),acceptanceBuildSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(FailureScenarios).Assembly.Location)))});
        }
        static object[] SummarizeWorkers(List<DiagnosticPoint> points)
        {
            Dictionary<string,(long Start,long End,long AllocStart,long AllocEnd,long BudgetStart,long BudgetEnd,Dictionary<long,long> Samples)> windows=[];
            foreach(var point in points)foreach(var worker in point.Data.GetProperty("workers").EnumerateArray())
            {
                string key=point.Node+"/worker-"+worker.GetProperty("workerId").GetInt32();
                long ticks=worker.GetProperty("tickCount").GetInt64(),allocation=worker.GetProperty("allocatedBytes").GetInt64(),over=worker.GetProperty("overBudgetTicks").GetInt64(),first=worker.GetProperty("firstSampleTick").GetInt64();
                if(!windows.TryGetValue(key,out var window))window=(ticks,ticks,allocation,allocation,over,over,[]);
                window.End=ticks;window.AllocEnd=allocation;window.BudgetEnd=over;
                var recent=worker.GetProperty("recentTickMicros").EnumerateArray().Select(x=>x.GetInt64()).ToArray();
                if(worker.TryGetProperty("recentTickIds",out var idsElement)){
                    var ids=idsElement.EnumerateArray().Select(x=>x.GetInt64()).ToArray();
                    Check(ids.Length==recent.Length,"worker-sample-identities");
                    for(int i=0;i<ids.Length;i++)if(ids[i]>window.Start)window.Samples[ids[i]]=recent[i];
                }
                else if(recent.LongLength==ticks-first+1)for(int i=0;i<recent.Length;i++){long id=first+i;if(id>window.Start)window.Samples[id]=recent[i];}
                windows[key]=window;
            }
            return windows.Select(entry=>{var value=entry.Value;var sorted=value.Samples.Values.Order().ToArray();long P(double p)=>sorted.Length==0?0:sorted[Math.Clamp((int)Math.Ceiling(p*sorted.Length)-1,0,sorted.Length-1)];return(object)new{worker=entry.Key,firstTick=value.Start+1,lastTick=value.End,observedTicks=value.End-value.Start,sampleCount=sorted.Length,complete=sorted.LongLength==value.End-value.Start,allocatedBytes=value.AllocEnd-value.AllocStart,overBudgetTicks=value.BudgetEnd-value.BudgetStart,p50Micros=P(.5),p95Micros=P(.95),p99Micros=P(.99),maximumMicros=sorted.Length==0?0:sorted[^1],percentileMethod="nearest-rank over deduplicated captured worker ticks"};}).ToArray();
        }
        sealed record DiagnosticPoint(string Node,DateTimeOffset Utc,double ElapsedSeconds,JsonElement Data);
        sealed class Bot(IRealtimeTransport transport,uint entity){public IRealtimeTransport Transport=transport;public uint Entity=entity,Sequence;public Snapshot Last=new();public int StartX;public Dictionary<uint,long> Pending=[];}
    }
    internal sealed record Pair(FantasyBackendProbe GateA,FantasyBackendProbe GateB,LoginResult LoginA,LoginResult LoginB,string RequestA,string RequestB,MatchReady ReadyA,MatchReady ReadyB,FantasyKcpProbe A,FantasyKcpProbe B,uint EntityA,uint EntityB):IAsyncDisposable
    {
        public CancellationTokenSource KeepAliveStop { get; private set; } = new();
        public Task? KeepAliveTask;
        public async Task StopKeepAlive(){KeepAliveStop.Cancel();if(KeepAliveTask is not null)await KeepAliveTask;KeepAliveStop.Dispose();KeepAliveStop=new();KeepAliveTask=null;}
        public async ValueTask DisposeAsync(){KeepAliveStop.Cancel();if(KeepAliveTask is not null)await KeepAliveTask;KeepAliveStop.Dispose();await A.DisposeAsync();await B.DisposeAsync();await GateA.DisposeAsync();await GateB.DisposeAsync();}
    }
}
