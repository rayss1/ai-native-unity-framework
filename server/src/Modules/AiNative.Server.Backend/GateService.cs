using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;

namespace AiNative.Server.Backend;

public sealed class GateService(
    IServiceRpc rpc,
    IClientNotifier notifier,
    TimeProvider clock,
    int maxConnections = 1024
) : IServiceHandler, IServiceConnectionObserver
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    public async ValueTask<ServiceReply> HandleAsync(
        ServiceCallContext context,
        string method,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default
    )
    {
        if (
            context.Caller != ServiceRole.Client
            || context.PeerId.Length is < 1 or > 128
            || payload.Length > 65536
        )
            return ServiceReply.Reject("forbidden");
        C2S_GateRequest request;
        try
        {
            request = C2S_GateRequest.Parser.ParseFrom(payload.Span);
        }
        catch (InvalidProtocolBufferException)
        {
            return ServiceReply.Reject("invalid-payload");
        }
        if (request.Method != method)
            return ServiceReply.Reject("invalid-method");
        ServiceReply? rejection = GetConnectionAndCheckLimits(
            context.PeerId,
            method,
            out Connection connection
        );
        if (rejection is not null)
            return rejection;

        return method switch
        {
            "gate.ping" => ServiceReply.From(new Empty()),
            ServiceMethods.RegisterAccount or ServiceMethods.Login => await HandleAccountAsync(
                connection,
                method,
                request,
                cancellationToken
            ),
            _ => await HandleAuthenticatedRequestAsync(
                connection,
                method,
                request,
                cancellationToken
            ),
        };
    }

    private ServiceReply? GetConnectionAndCheckLimits(
        string peerId,
        string method,
        out Connection connection
    )
    {
        lock (_sync)
        {
            if (!_connections.TryGetValue(peerId, out connection!))
            {
                if (_connections.Count >= maxConnections)
                    return ServiceReply.Reject("capacity-unavailable");
                connection = new(peerId);
                _connections.Add(peerId, connection);
            }
            long now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (connection.Window != now)
            {
                connection.Window = now;
                connection.Requests = 0;
            }
            // 如果是普通请求，限制每秒最多 40 次请求；如果是登录或注册请求，限制每分钟最多 5 次请求。
            if (++connection.Requests > 40)
                return ServiceReply.Reject("rate-limited");
            connection.Seen = clock.GetUtcNow();
            if (method is ServiceMethods.RegisterAccount or ServiceMethods.Login)
            {
                if (connection.LoginWindow != now / 60)
                {
                    connection.LoginWindow = now / 60;
                    connection.LoginRequests = 0;
                }
                if (++connection.LoginRequests > 5)
                    return ServiceReply.Reject("rate-limited");
            }
        }
        return null;
    }

    private async ValueTask<ServiceReply> HandleAccountAsync(
        Connection connection,
        string method,
        C2S_GateRequest request,
        CancellationToken cancellationToken
    )
    {
        lock (_sync)
            if (connection.Player.Length != 0)
                return ServiceReply.Reject("connection-already-authenticated");
        ServiceReply login = await rpc.CallAsync(
            new(ServiceRole.Player),
            method,
            request.Body.Memory,
            cancellationToken
        );
        if (login.Success)
        {
            LoginResult identity = login.Read(LoginResult.Parser);
            lock (_sync)
            {
                if (connection.Player.Length != 0 && connection.Player != identity.PlayerId)
                    return ServiceReply.Reject("connection-identity-conflict");
                connection.Player = identity.PlayerId;
                connection.Token = identity.SessionToken;
                connection.Expires = identity.ExpiresUnixSeconds;
            }
            await PresenceAsync(connection, true, cancellationToken, identity.PlayerId);
        }
        return login;
    }

    private async ValueTask<(string Player, ServiceReply? Rejection)> ValidateSessionAsync(
        Connection connection,
        C2S_GateRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Credential.Length == 0 || request.Credential.Length > 8192)
            return ("", ServiceReply.Reject("invalid-session"));
        string player;
        bool cached;
        lock (_sync)
        {
            cached =
                connection.Token == request.Credential
                && connection.Expires > clock.GetUtcNow().ToUnixTimeSeconds();
            player = connection.Player;
        }
        if (!cached)
        {
            ServiceReply validation = await rpc.CallAsync(
                new(ServiceRole.Player),
                ServiceMethods.ValidateSession,
                new SessionCredentials { SessionToken = request.Credential },
                cancellationToken
            );
            if (!validation.Success)
                return ("", validation);
            PlayerIdentity identity = validation.Read(PlayerIdentity.Parser);
            player = identity.PlayerId;
            lock (_sync)
            {
                if (connection.Player.Length != 0 && connection.Player != player)
                    return ("", ServiceReply.Reject("connection-identity-conflict"));
                connection.Player = player;
                connection.Token = request.Credential;
                connection.Expires = identity.ExpiresUnixSeconds;
            }
            await PresenceAsync(connection, true, cancellationToken, player);
        }
        if (player.Length == 0)
            return ("", ServiceReply.Reject("invalid-session"));
        return (player, null);
    }

    private async ValueTask<ServiceReply> HandleAuthenticatedRequestAsync(
        Connection connection,
        string method,
        C2S_GateRequest request,
        CancellationToken cancellationToken
    )
    {
        var (player, rejection) = await ValidateSessionAsync(
            connection,
            request,
            cancellationToken
        );
        if (rejection is not null)
            return rejection;

        // Keep authentication before the allowlist check, including its presence update.
        ServiceTarget? target = GetAllowedTarget(method);
        if (target is null)
            return ServiceReply.Reject("forbidden");

        ServiceReply response = await rpc.CallAsync(
            target.Value,
            method,
            request.Body.Memory,
            cancellationToken,
            player
        );
        return await ProcessResponseAsync(connection, method, player, response, cancellationToken);
    }

    // This switch is the client-facing business allowlist.
    private static ServiceTarget? GetAllowedTarget(string method) =>
        method switch
        {
            ServiceMethods.Profile or ServiceMethods.SettlementStatus => new(ServiceRole.Player),
            ServiceMethods.PartyCreate
            or ServiceMethods.PartyInvite
            or ServiceMethods.PartyAccept
            or ServiceMethods.PartyLeave
            or ServiceMethods.PartyReady
            or ServiceMethods.PartyGet
            or ServiceMethods.PartyQueue
            or ServiceMethods.QueueCancel
            or ServiceMethods.MatchStatus
            or ServiceMethods.PartyInvites => new(ServiceRole.Lobby),
            _ => null,
        };

    private async ValueTask<ServiceReply> ProcessResponseAsync(
        Connection connection,
        string method,
        string player,
        ServiceReply response,
        CancellationToken cancellationToken
    )
    {
        if (!response.Success)
            return response;

        switch (method)
        {
            case ServiceMethods.PartyCreate:
            case ServiceMethods.PartyAccept:
            case ServiceMethods.PartyGet:
            case ServiceMethods.PartyReady:
            {
                var party = response.Read(PartyState.Parser);
                lock (_sync)
                {
                    connection.PartyId = party.PartyId;
                    if (
                        party.QueueRequestId.Length > 0
                        && connection.RequestId != party.QueueRequestId
                    )
                    {
                        connection.RequestId = party.QueueRequestId;
                        connection.LastNotified = "";
                    }
                }
                break;
            }
            case ServiceMethods.PartyLeave:
                lock (_sync)
                {
                    connection.PartyId = "";
                    connection.RequestId = "";
                    connection.LastNotified = "";
                }
                break;
            case ServiceMethods.PartyQueue:
            {
                MatchStatus status = response.Read(MatchStatus.Parser);
                lock (_sync)
                {
                    connection.RequestId = status.RequestId;
                    connection.LastNotified = "";
                }
                break;
            }
            case ServiceMethods.MatchStatus:
                return await WithTicketAsync(
                    player,
                    response.Read(MatchStatus.Parser),
                    cancellationToken
                );
        }
        return response;
    }

    public async ValueTask PumpAsync(CancellationToken ct = default)
    {
        Connection[] connections;
        lock (_sync)
            connections = _connections.Values.Take(1024).ToArray();
        foreach (Connection connection in connections)
        {
            if (clock.GetUtcNow() - connection.Seen > TimeSpan.FromMinutes(2))
            {
                await DisconnectedAsync(connection.Id, ct);
                continue;
            }
            if (
                connection.Player.Length == 0
                || connection.Expires <= clock.GetUtcNow().ToUnixTimeSeconds()
            )
                continue;
            try
            {
                if (clock.GetUtcNow() - connection.PresenceSent > TimeSpan.FromSeconds(10))
                    await PresenceAsync(connection, true, ct);
                if (connection.RequestId.Length == 0 && connection.PartyId.Length > 0)
                {
                    var partyReply = await rpc.CallAsync(
                        new(ServiceRole.Lobby),
                        ServiceMethods.PartyGet,
                        new PartyOperation { PartyId = connection.PartyId },
                        ct,
                        connection.Player
                    );
                    if (partyReply.Success)
                    {
                        var party = partyReply.Read(PartyState.Parser);
                        lock (_sync)
                            if (party.QueueRequestId.Length > 0)
                            {
                                connection.RequestId = party.QueueRequestId;
                                connection.LastNotified = "";
                            }
                    }
                }
                if (connection.RequestId.Length == 0)
                    continue;
                ServiceReply status = await rpc.CallAsync(
                    new(ServiceRole.Lobby),
                    ServiceMethods.MatchStatus,
                    new MatchQuery { RequestId = connection.RequestId },
                    ct,
                    connection.Player
                );
                if (!status.Success)
                    continue;
                MatchStatus match = status.Read(MatchStatus.Parser);
                if (
                    match.State is not ("ready" or "failed" or "cancelled")
                    || match.State == connection.LastNotified
                )
                    continue;
                ServiceReply ready = await WithTicketAsync(connection.Player, match, ct);
                if (!ready.Success)
                    continue;
                await notifier.NotifyAsync(
                    connection.Id,
                    "match." + match.State,
                    ready.Payload,
                    ct
                );
                lock (_sync)
                {
                    connection.LastNotified = match.State;
                    if (match.State is "failed" or "cancelled")
                        connection.RequestId = "";
                }
            }
            catch (ServiceException) { }
            catch (TimeoutException) { }
        }
    }

    public async ValueTask DisconnectedAsync(
        string connectionId,
        CancellationToken cancellationToken = default
    )
    {
        Connection? connection;
        lock (_sync)
            _connections.Remove(connectionId, out connection);
        if (connection?.Player.Length > 0)
        {
            try
            {
                await PresenceAsync(connection, false, cancellationToken);
            }
            catch (ServiceException) { }
        }
    }

    private async ValueTask<ServiceReply> WithTicketAsync(
        string player,
        MatchStatus status,
        CancellationToken ct
    )
    {
        MatchReady result = new() { Status = status };
        if (status.State == "ready")
        {
            if (
                status.Allocation is null
                || status.Allocation.State != "Ready"
                || !status.Allocation.PlayerIds.Contains(player)
            )
                return ServiceReply.Reject("allocation-conflict");
            ServiceReply ticket = await rpc.CallAsync(
                new(ServiceRole.Player),
                ServiceMethods.IssueTicket,
                new TicketSpec { PlayerId = player, Allocation = status.Allocation },
                ct,
                player
            );
            if (!ticket.Success)
                return ticket;
            result.EntryTicket = ticket.Read(EntryTicket.Parser).Ticket;
        }
        return ServiceReply.From(result);
    }

    private async ValueTask PresenceAsync(
        Connection connection,
        bool online,
        CancellationToken ct,
        string? player = null
    )
    {
        await rpc.CallAsync(
            new(ServiceRole.Lobby),
            ServiceMethods.Presence,
            new PresenceUpdate { Online = online },
            ct,
            player ?? connection.Player
        );
        lock (_sync)
            connection.PresenceSent = clock.GetUtcNow();
    }

    private sealed class Connection(string id)
    {
        public string Id { get; } = id;
        public string Player = "",
            Token = "",
            PartyId = "",
            RequestId = "",
            LastNotified = "";
        public long Expires,
            Window,
            LoginWindow;
        public int Requests,
            LoginRequests;
        public DateTimeOffset Seen,
            PresenceSent;
    }
}
