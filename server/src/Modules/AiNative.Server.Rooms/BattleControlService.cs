using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;

namespace AiNative.Server.Rooms;

public sealed class BattleControlService : IServiceHandler
{
    private readonly BattleWorkerPool pool;
    private readonly DurableResultOutbox outbox;
    private readonly string address;
    public BattleControlService(BattleWorkerPool pool, DurableResultOutbox outbox, string address)
    {
        this.pool = pool; this.outbox = outbox; this.address = address;
        pool.ConfigureResultOutbox(outbox);
    }
    public async ValueTask<ServiceReply> HandleAsync(ServiceCallContext context, string method, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (context.Caller != ServiceRole.Coordinator) return ServiceReply.Reject("forbidden");
        try
        {
            string error;
            switch (method)
            {
                case ServiceMethods.Fence:
                    var fence = CoordinatorFence.Parser.ParseFrom(payload.Span);
                    if (fence.BootEpoch != pool.Inventory().BootEpoch || fence.CoordinatorEpoch.Length == 0) return ServiceReply.Reject("stale-epoch");
                    return ServiceReply.From(pool.Fence(fence.CoordinatorEpoch, address, outbox.CanAdmit));
                case ServiceMethods.Reserve:
                    var reserve = RoomReservation.Parser.ParseFrom(payload.Span);
                    if (reserve.Allocation is null || reserve.Allocation.CoordinatorEpoch.Length == 0) return ServiceReply.Reject("invalid-allocation");
                    error = pool.Reserve(reserve.Allocation);
                    return error.Length == 0 ? ServiceReply.From(reserve.Allocation) : ServiceReply.Reject(error);
                case ServiceMethods.Create:
                    var create = RoomCreation.Parser.ParseFrom(payload.Span);
                    if (create.CoordinatorEpoch.Length == 0) return ServiceReply.Reject("stale-coordinator");
                    error = await pool.CreateAsync(create.RoomId, create.AllocationId, create.BootEpoch, cancellationToken, create.CoordinatorEpoch);
                    return error.Length == 0 ? ServiceReply.From(new Empty()) : ServiceReply.Reject(error);
                case ServiceMethods.Release:
                    var release = RoomRelease.Parser.ParseFrom(payload.Span);
                    if (release.CoordinatorEpoch.Length == 0) return ServiceReply.Reject("stale-coordinator");
                    error = await pool.ReleaseAsync(release.RoomId, release.AllocationId, release.BootEpoch, cancellationToken, release.CoordinatorEpoch);
                    return error.Length == 0 ? ServiceReply.From(new Empty()) : ServiceReply.Reject(error);
                case ServiceMethods.Drain:
                    var drain = DrainRequest.Parser.ParseFrom(payload.Span);
                    if (drain.BootEpoch != pool.Inventory().BootEpoch) return ServiceReply.Reject("stale-epoch");
                    error = pool.BeginDrain(drain.CoordinatorEpoch);
                    return error.Length == 0 ? ServiceReply.From(new Empty()) : ServiceReply.Reject(error);
                default: return ServiceReply.Reject("unknown-method");
            }
        }
        catch (InvalidProtocolBufferException) { return ServiceReply.Reject("invalid-payload"); }
        catch (InvalidOperationException ex) when (ex.Message is "replay-storage-quota" or "replay-persistence-unavailable") { return ServiceReply.Reject(ex.Message); }
        catch (InvalidOperationException ex) when (ex.Message is "stale-coordinator" or "fence-history-capacity-exceeded") { return ServiceReply.Reject(ex.Message); }
    }
}
