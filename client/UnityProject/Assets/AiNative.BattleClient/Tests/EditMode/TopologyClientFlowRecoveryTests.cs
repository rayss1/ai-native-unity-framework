using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Fantasy;
using NUnit.Framework;
using UnityEngine;

namespace AiNative.Client.Application.Tests
{
    public sealed class TopologyClientFlowRecoveryTests
    {
        [Test]
        public async Task ExpiredInteractiveSession_OffersAuthenticationAndKeepsOriginalBattle()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(await FantasyGateClient.ConnectAsync(gate.Options));
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ExpiredInteractiveFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, false);
                BattleClientSession battle = flow.Battle;
                SetProperty(backend, "ExpiresUnixSeconds", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await Poll(flow);

                Assert.That(flow.BackendStatus, Is.Not.Empty);
                Assert.That(flow.State, Is.EqualTo("Battle"));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That((flow.PlayerId, flow.MatchId, flow.RoomId), Is.EqualTo(("original-player", "original-match", "original-room")));
                Assert.That(flow.AuthenticationRequired, Is.True, "A connected socket with an expired credential must expose login controls.");
                Assert.That(backend.IsConnected, Is.True, "Credential expiry does not mean the socket disconnected.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test]
        public async Task ExpiredAutomatedSession_ReauthenticatesSameAccountAndCompletesOriginalSettlement()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(await FantasyGateClient.ConnectAsync(gate.Options));
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("ExpiredAutomatedFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                BattleClientSession battle = flow.Battle;
                SetProperty(flow, "State", "Settling");
                SetProperty(flow, "Reconnected", true);
                SetProperty(backend, "ExpiresUnixSeconds", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await Poll(flow);

                Assert.That(flow.Completed && flow.SettlementConfirmed, Is.True);
                Assert.That(flow.BackendReconnectCount, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That((flow.PlayerId, flow.MatchId, flow.RoomId), Is.EqualTo(("original-player", "original-match", "original-room")));
                Assert.That(gate.LoginUsers.ToArray(), Is.EqualTo(new[] { "original-user", "original-user" }));
                Assert.That(gate.LoginPasswords.ToArray(), Is.EqualTo(new[] { "original-password", "original-password" }));
                Assert.That(gate.SettlementMatches.ToArray(), Is.EqualTo(new[] { "original-match" }));
                Assert.That(flow.Error, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        private static TopologyClientFlow CreateFlow(GameObject owner, GateConnectionOptions options, GateBackendSession backend, bool automated)
        {
            var flow = owner.AddComponent<TopologyClientFlow>();
            flow.Initialize(options, automated);
            SetField(flow, "_backend", backend);
            SetField(flow, "_username", "original-user"); SetField(flow, "_password", "original-password");
            var match = new GateMatch(); SetProperty(match, "MatchId", "original-match"); SetProperty(match, "RoomId", "original-room");
            SetProperty(match, "BootEpoch", "original-boot"); SetProperty(match, "NodeId", "original-node");
            SetField(flow, "_match", match); SetField(flow, "_requestId", "original-request");
            SetProperty(flow, "Battle", new BattleClientSession("127.0.0.1", 22000, new BattleAdmissionInfo("original-room", "original-boot", "ticket", "original-node", "original-player")));
            SetProperty(flow, "State", "Battle");
            return flow;
        }

        [Test]
        public async Task ServerRejectedSession_RetriesAuthenticationWithoutFailingAutomatedBattle()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(await FantasyGateClient.ConnectAsync(gate.Options));
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("RejectedAutomatedFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                gate.RejectNextProfile = true;
                typeof(TopologyClientFlow).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, new object[] { new Func<Task>(() => Poll(flow)) });
                while ((bool)typeof(TopologyClientFlow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(flow)) await Task.Yield();

                Assert.That(flow.Completed, Is.False, "An expired server credential must not end the automated battle.");
                Assert.That(flow.Error, Is.Empty);
                Assert.That(flow.State, Is.EqualTo("Battle"));
                SetProperty(flow, "State", "Settling"); SetProperty(flow, "Reconnected", true);
                await Poll(flow);
                Assert.That(flow.SettlementConfirmed && flow.Completed, Is.True);
                Assert.That(gate.LoginUsers.ToArray(), Is.EqualTo(new[] { "original-user", "original-user" }));
                Assert.That(flow.MatchId, Is.EqualTo("original-match"));
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test]
        public async Task RecoveryRejectsDifferentPlayerWithoutReplacingOriginalBattleOrAllocation()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(await FantasyGateClient.ConnectAsync(gate.Options));
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("MismatchedRecoveryFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true); BattleClientSession battle = flow.Battle;
                SetProperty(backend, "ExpiresUnixSeconds", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); gate.LoginPlayer = "different-player";
                GateCallException rejection = null;
                try { await Poll(flow); } catch (GateCallException exception) { rejection = exception; }
                Assert.That(rejection, Is.Not.Null);
                Assert.That(rejection.Code, Is.EqualTo("account-mismatch-active-battle"));
                Assert.That(flow.Battle, Is.SameAs(battle));
                Assert.That((flow.PlayerId, flow.MatchId, flow.RoomId), Is.EqualTo(("original-player", "original-match", "original-room")));
                Assert.That(flow.BackendReconnectCount, Is.Zero);
                Assert.That(gate.SettlementMatches, Is.Empty);
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [Test]
        public async Task WaitingForBackendAuthentication_LeavesFixedTickPredictionRunning()
        {
            using var gate = new GatePeer();
            using var backend = new GateBackendSession(await FantasyGateClient.ConnectAsync(gate.Options));
            await backend.LoginAsync("original-user", "original-password");
            var owner = new GameObject("RecoveringBattleFlow");
            try
            {
                var flow = CreateFlow(owner, gate.Options, backend, true);
                var transport = new FakeTransport();
                var battle = new BattleClientSession("127.0.0.1", 22000, "fixture", 4, new FakeConnector(transport), new BattleAdmissionInfo("original-room", "original-boot", "ticket", "original-node", "original-player"));
                SetProperty(flow, "Battle", battle); SetProperty(flow, "Reconnected", true);
                battle.Start(); battle.Pump(0);
                transport.Enqueue(TestFrames.Login(42, 1, "original-room", "original-boot"), BattleClientProtocolV1.ControlChannel, 1);
                transport.Enqueue(TestFrames.Join(19, 7, 60), BattleClientProtocolV1.ControlChannel, 1);
                transport.Enqueue(TestFrames.Snapshot(7, 110, 0), BattleClientProtocolV1.SnapshotChannel, 1);
                battle.Pump(0);
                Assert.That(battle.IsPredictionInitialized, Is.True);
                SetProperty(backend, "ExpiresUnixSeconds", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); gate.PauseLogin = true;
                Task recovering = Poll(flow);
                await Task.WhenAny(gate.LoginBlocked.Task, Task.Delay(2500));
                Assert.That(gate.LoginBlocked.Task.IsCompleted, Is.True, "The replacement login did not arrive.");
                Assert.That(recovering.IsCompleted, Is.False);

                typeof(TopologyClientFlow).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, null);

                Assert.That(flow.PreparedInputs, Is.EqualTo(1), "Backend authentication must not stop battle prediction.");
                Assert.That(battle.QueuedInputFrames, Is.EqualTo(1));
                Assert.That(flow.Battle, Is.SameAs(battle));
                gate.ReleaseLogin.TrySetResult(true); await recovering;
                Assert.That(flow.SettlementConfirmed, Is.True);
            }
            finally { gate.ReleaseLogin.TrySetResult(true); UnityEngine.Object.DestroyImmediate(owner); }
        }

        private static Task Poll(TopologyClientFlow flow) => (Task)typeof(TopologyClientFlow).GetMethod("PollAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, null);
        private static void SetField(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void SetProperty(object target, string property, object value) => target.GetType().GetProperty(property).GetSetMethod(true).Invoke(target, new[] { value });

        // Actual Fantasy TCP framing with a small backend.proto fixture peer; no Unity work runs on the server threads.
        private sealed class GatePeer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly ConcurrentBag<TcpClient> _peers = new ConcurrentBag<TcpClient>();
            internal readonly ConcurrentQueue<string> LoginUsers = new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> LoginPasswords = new ConcurrentQueue<string>();
            internal readonly ConcurrentQueue<string> SettlementMatches = new ConcurrentQueue<string>();
            internal bool RejectNextProfile;
            internal bool PauseLogin;
            internal string LoginPlayer = "original-player";
            internal readonly TaskCompletionSource<bool> LoginBlocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseLogin = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal GateConnectionOptions Options { get; }
            internal GatePeer()
            {
                _listener.Start(); Options = new GateConnectionOptions("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, false, timeoutMilliseconds: 2000);
                _ = Accept();
            }
            private async Task Accept()
            {
                try { while (!_stop.IsCancellationRequested) { TcpClient peer = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); _peers.Add(peer); _ = Serve(peer); } }
                catch (ObjectDisposedException) { } catch (SocketException) { }
            }
            private async Task Serve(TcpClient peer)
            {
                try
                {
                    using NetworkStream stream = peer.GetStream();
                    while (!_stop.IsCancellationRequested)
                    {
                        byte[] header = new byte[20]; await Read(stream, header, _stop.Token).ConfigureAwait(false);
                        byte[] outer = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await Read(stream, outer, _stop.Token).ConfigureAwait(false);
                        byte[] request = Data(outer, 1); string method = Text(request, 2), correlation = Text(request, 1), error = ""; byte[] body = Data(request, 3), reply;
                        if (method == "player.login")
                        {
                            LoginUsers.Enqueue(Text(body, 1));
                            LoginPasswords.Enqueue(Text(body, 2));
                            if (PauseLogin) { LoginBlocked.TrySetResult(true); await ReleaseLogin.Task.ConfigureAwait(false); }
                            reply = Join(Field(1, LoginPlayer), Field(2, "renewed-credential"), Number(3, (ulong)DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()));
                        }
                        else if (method == "player.profile") { reply = Join(Field(1, "original-player"), Number(3, 1)); if (RejectNextProfile) { RejectNextProfile = false; error = "invalid_session"; reply = Array.Empty<byte>(); } }
                        else if (method == "player.settlement-status") { SettlementMatches.Enqueue(Text(body, 1)); reply = Join(Field(1, "original-match"), Number(3, 1)); }
                        else throw new InvalidDataException("Unexpected method: " + method);
                        byte[] response = Bytes(2, Join(Field(1, correlation), Bytes(2, reply), Field(3, error)));
                        byte[] frame = new byte[20 + response.Length]; BinaryPrimitives.WriteInt32LittleEndian(frame, response.Length); BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 402653361u);
                        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8))); response.CopyTo(frame, 20);
                        await stream.WriteAsync(frame, 0, frame.Length, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (IOException) { } catch (ObjectDisposedException) { } catch (OperationCanceledException) { }
            }
            public void Dispose() { ReleaseLogin.TrySetResult(true); _stop.Cancel(); _listener.Stop(); foreach (TcpClient peer in _peers) peer.Dispose(); }
            private static async Task Read(Stream stream, byte[] buffer, CancellationToken token)
            {
                int offset = 0; while (offset < buffer.Length) { int count = await stream.ReadAsync(buffer, offset, buffer.Length - offset, token).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException(); offset += count; }
            }
            private static byte[] Data(byte[] message, int field)
            {
                int offset = 0;
                while (offset < message.Length)
                {
                    ulong key = Varint(message, ref offset); int wire = (int)(key & 7);
                    if (wire == 0) { Varint(message, ref offset); continue; }
                    if (wire != 2) throw new InvalidDataException();
                    int length = checked((int)Varint(message, ref offset));
                    if ((int)(key >> 3) == field) { var value = new byte[length]; Array.Copy(message, offset, value, 0, length); return value; }
                    offset += length;
                }
                return Array.Empty<byte>();
            }
            private static string Text(byte[] message, int field) => Encoding.UTF8.GetString(Data(message, field));
            private static byte[] Field(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
            private static byte[] Bytes(int field, byte[] value) => Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)value.Length), value);
            private static byte[] Number(int field, ulong value) => Join(Varint((ulong)(field << 3)), Varint(value));
            private static byte[] Join(params byte[][] values) { using var output = new MemoryStream(); foreach (byte[] value in values) output.Write(value, 0, value.Length); return output.ToArray(); }
            private static byte[] Varint(ulong value) { using var output = new MemoryStream(); while (value >= 128) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; } output.WriteByte((byte)value); return output.ToArray(); }
            private static ulong Varint(byte[] buffer, ref int offset) { ulong value = 0; for (int shift = 0; shift < 64; shift += 7) { byte next = buffer[offset++]; value |= (ulong)(next & 127) << shift; if ((next & 128) == 0) return value; } throw new InvalidDataException(); }
        }
    }
}
