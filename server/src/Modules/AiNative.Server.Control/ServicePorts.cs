using Google.Protobuf;

namespace AiNative.Server.Control;

public enum ServiceRole { Client, Gate, Player, Lobby, Match, Coordinator, Battle }
public readonly record struct ServiceTarget(ServiceRole Role, string NodeId = "");
// Supplied by authenticated adapter ingress, never by the request body.
public readonly record struct ServiceCallContext(ServiceRole Caller, string PeerId, string PlayerId = "");
public sealed record ServiceReply(byte[] Payload, string Error = "")
{
    public bool Success => Error.Length == 0;
    public static ServiceReply Reject(string error) => new([], error);
    public static ServiceReply From(IMessage message) => new(message.ToByteArray());
    public T Read<T>(MessageParser<T> parser) where T : IMessage<T> => Success
        ? parser.ParseFrom(Payload) : throw new ServiceException(Error);
}
public sealed class ServiceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public interface IServiceRpc
{
    ValueTask<ServiceReply> CallAsync(ServiceTarget target, string method, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default, string playerId = "");
}
public interface IServiceHandler
{
    ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}
public interface IClientNotifier
{
    ValueTask NotifyAsync(string connectionId, string method, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
public interface IServiceConnectionObserver
{
    ValueTask DisconnectedAsync(string connectionId, CancellationToken cancellationToken = default);
}
public static class ServiceRpcExtensions
{
    public static ValueTask<ServiceReply> CallAsync(this IServiceRpc rpc, ServiceTarget target, string method,
        IMessage request, CancellationToken cancellationToken = default, string playerId = "") =>
        rpc.CallAsync(target, method, request.ToByteArray(), cancellationToken, playerId);
}
public static class ServiceMethods
{
    public const string RegisterAccount = "player.register", Login = "player.login", ValidateSession = "player.session",
        Profile = "player.profile", IssueTicket = "player.ticket", Settle = "player.settle", SettlementStatus = "player.settlement-status",
        Presence = "lobby.presence", PartyCreate = "lobby.create", PartyInvite = "lobby.invite", PartyAccept = "lobby.accept", PartyLeave = "lobby.leave",
        PartyReady = "lobby.ready", PartyGet = "lobby.get", PartyQueue = "lobby.queue", PartyInvites = "lobby.invites",
        QueueJoin = "match.join", QueueCancel = "match.cancel", QueueAbort = "match.abort", MatchStatus = "match.status", MatchEpoch = "match.epoch",
        NodeReport = "room.report", Allocate = "room.allocate", RoomGet = "room.get", RoomList = "room.list",
        Reserve = "battle.reserve", Create = "battle.create", Release = "battle.release", Drain = "battle.drain", Fence = "battle.fence";
}
