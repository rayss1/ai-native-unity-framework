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
        Assert.That((await session.ProfileAsync()).Played, Is.EqualTo(1));
        var settlement = await session.SettlementAsync("match-self");
        Assert.That((settlement.MatchId, settlement.Confirmed), Is.EqualTo(("match-self", true)));
        await server;
    }
}
