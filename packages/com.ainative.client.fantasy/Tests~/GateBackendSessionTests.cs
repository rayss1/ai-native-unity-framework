using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AiNative.Client.Fantasy;
using AiNative.Protocol.Backend.V1;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Gate.Tests;

public sealed class GateBackendSessionTests
{
    [Test]
    public async Task Server_rejected_credential_is_invalidated_without_another_authenticated_request()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        int profileRequests = 0;
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(); using NetworkStream stream = peer.GetStream();
            try
            {
                for (int i = 0; ; ++i)
                {
                    byte[] header = new byte[20]; await stream.ReadExactlyAsync(header);
                    byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await stream.ReadExactlyAsync(body);
                    ControlRequest request = ControlRequest.Parser.ParseFrom(GateWire.Data(GateWire.Read(body), 1));
                    ControlResponse reply = new() { CorrelationId = request.CorrelationId };
                    if (i == 0) reply.Body = ByteString.CopyFrom(new LoginResult { PlayerId = "self", SessionToken = "rejected-session", ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() }.ToByteArray());
                    else { Assert.That(request.Method, Is.EqualTo("player.profile")); Interlocked.Increment(ref profileRequests); reply.Error = "invalid_session"; }
                    await stream.WriteAsync(GateWire.Frame(402653361u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), GateWire.Bytes(2, reply.ToByteArray())));
                }
            }
            catch (EndOfStreamException) { }
        });
        using FantasyGateClient client = await FantasyGateClient.ConnectAsync(new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, useTls: false));
        using GateBackendSession session = new(client);
        await session.LoginAsync("fixture", "fixture-password");
        var rejected = Assert.ThrowsAsync<GateCallException>(async () => await session.ProfileAsync());
        Assert.That(rejected!.Code, Is.EqualTo("invalid_session"));
        Assert.That(session.IsConnected, Is.True);
        Assert.That(session.IsAuthenticated, Is.False);
        Assert.ThrowsAsync<GateCallException>(async () => await session.ProfileAsync());
        client.Dispose(); await server;
        Assert.That(profileRequests, Is.EqualTo(1), "A rejected credential must not be reused on the still-connected socket.");
        Assert.That(session.PlayerId, Is.EqualTo("self"), "Original player identity survives credential invalidation.");
    }

    [Test]
    public async Task Login_binds_session_credential_and_profile_to_the_authenticated_player()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(); using NetworkStream stream = peer.GetStream();
            for (int i = 0; i < 3; ++i)
            {
                byte[] header = new byte[20]; await stream.ReadExactlyAsync(header);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await stream.ReadExactlyAsync(body);
                ControlRequest request = ControlRequest.Parser.ParseFrom(GateWire.Data(GateWire.Read(body), 1));
                byte[] reply;
                if (i == 0)
                {
                    Assert.That(request.Method, Is.EqualTo("player.login"));
                    Assert.That(AccountRequest.Parser.ParseFrom(request.Body).Username, Is.EqualTo("fixture"));
                    reply = new LoginResult { PlayerId = "self", SessionToken = "fixture-session", ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() }.ToByteArray();
                }
                else if (i == 1)
                {
                    Assert.That(request.Method, Is.EqualTo("player.profile"));
                    Assert.That(request.Credential, Is.EqualTo("fixture-session"));
                    Assert.That(ProfileRequest.Parser.ParseFrom(request.Body).PlayerId, Is.EqualTo("self"));
                    reply = new PlayerProfile { PlayerId = "self", Played = 1 }.ToByteArray();
                }
                else
                {
                    Assert.That(request.Method, Is.EqualTo("player.settlement-status"));
                    Assert.That(request.Credential, Is.EqualTo("fixture-session"));
                    Assert.That(SettlementQuery.Parser.ParseFrom(request.Body).MatchId, Is.EqualTo("match-self"));
                    reply = new SettlementReceipt { MatchId = "match-self", Confirmed = true, PayloadHash = "authority-hash" }.ToByteArray();
                }
                byte[] frame = GateWire.Frame(402653361u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), GateWire.Bytes(2, new ControlResponse { CorrelationId = request.CorrelationId, Body = ByteString.CopyFrom(reply) }.ToByteArray()));
                await stream.WriteAsync(frame);
            }
        });
        using FantasyGateClient client = await FantasyGateClient.ConnectAsync(new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, useTls: false));
        using GateBackendSession session = new(client);
        await session.LoginAsync("fixture", "fixture-password");
        Assert.That(session.PlayerId, Is.EqualTo("self"));
        Assert.That(session.IsAuthenticated, Is.True);
        Assert.That((await session.ProfileAsync()).Played, Is.EqualTo(1));
        var settlement = await session.SettlementAsync("match-self");
        Assert.That((settlement.MatchId, settlement.Confirmed), Is.EqualTo(("match-self", true)));
        await server;
    }
}
