using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AiNative.Client.Fantasy;
using AiNative.Protocol.Backend.V1;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Gate.Tests;

public sealed class GateConnectionTests
{
    [Test]
    public async Task Fragmented_response_and_notification_preserve_the_pending_call()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = peer.GetStream();
            byte[] header = new byte[20]; await stream.ReadExactlyAsync(header);
            byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)]; await stream.ReadExactlyAsync(body);
            var envelope = GateWire.Read(body); var request = ControlRequest.Parser.ParseFrom(GateWire.Data(envelope, 1));
            byte[] notification = GateWire.Frame(134217905u, 0, GateWire.Bytes(1, new ControlNotification { Method = "lobby.changed" }.ToByteArray()));
            await stream.WriteAsync(notification);
            var response = new ControlResponse { CorrelationId = request.CorrelationId, Body = ByteString.CopyFrom(new PlayerProfile { PlayerId = "p1", Played = 2 }.ToByteArray()) };
            byte[] frame = GateWire.Frame(402653361u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)), GateWire.Bytes(2, response.ToByteArray()));
            for (int offset = 0; offset < frame.Length; offset += 3) await stream.WriteAsync(frame.AsMemory(offset, Math.Min(3, frame.Length - offset)));
        });
        using FantasyGateClient client = await FantasyGateClient.ConnectAsync(new("127.0.0.1", port, useTls: false));
        byte[] result = await client.CallAsync("player.profile", new ProfileRequest { PlayerId = "p1" }.ToByteArray(), "test-session");
        Assert.That(PlayerProfile.Parser.ParseFrom(result).Played, Is.EqualTo(2));
        await server;
    }

    [TestCase(-2)]
    [TestCase(60001)]
    public async Task Malicious_length_closes_connection_before_body_allocation(int length)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        Task server = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(); using NetworkStream stream = peer.GetStream();
            byte[] request = new byte[20]; await stream.ReadExactlyAsync(request);
            byte[] header = new byte[20]; BinaryPrimitives.WriteInt32LittleEndian(header, length); await stream.WriteAsync(header);
        });
        using FantasyGateClient client = await FantasyGateClient.ConnectAsync(new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, useTls: false));
        Assert.ThrowsAsync<InvalidDataException>(() => client.CallAsync("player.profile", Array.Empty<byte>()));
        Assert.That(client.IsConnected, Is.False);
        await server;
    }

    [Test]
    public async Task Cancellation_of_partial_response_closes_stream_and_does_not_reuse_it()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
        using FantasyGateClient client = await FantasyGateClient.ConnectAsync(new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, useTls: false));
        using TcpClient peer = await listener.AcceptTcpClientAsync();
        using CancellationTokenSource cancel = new(100);
        Assert.ThrowsAsync<OperationCanceledException>(() => client.CallAsync("player.profile", Array.Empty<byte>(), cancellationToken: cancel.Token));
        Assert.That(client.IsConnected, Is.False);
        GateCallException failure = Assert.ThrowsAsync<GateCallException>(() => client.CallAsync("player.profile", Array.Empty<byte>()))!;
        Assert.That(failure.Code, Is.EqualTo("unavailable"));
    }
}
