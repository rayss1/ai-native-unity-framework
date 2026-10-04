using System.Reflection;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Topology.ToolTests;

public class PreparedPartyCleanupTests
{
    [Test]
    public void FailureDiagnosticsRetainsQualificationCategoryAndNestedStatusRejection()
    {
        var error = new AggregateException(new InvalidOperationException("qualification-party-cleanup-unconfirmed",
            new InvalidOperationException("match-status-rejected:invalid_allocation")));
        var diagnostics = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationFailureDiagnostics");
        Assert.That(diagnostics, Is.Not.Null);
        var find = diagnostics!.GetMethod("Find")!;
        Assert.That(find.Invoke(null, [error, "qualification-"]), Is.EqualTo("qualification-party-cleanup-unconfirmed"));
        Assert.That(find.Invoke(null, [error, "match-status-rejected:"]), Is.EqualTo("match-status-rejected:invalid_allocation"));
        Assert.That(find.Invoke(null, [new Exception("unrelated"), "qualification-"]), Is.Null);
    }

    [Test]
    public async Task ReleasedRoomTicketRaceIsRecheckedUntilOriginalPartyAbsenceIsProven()
    {
        int statuses = 0, reads = 0; bool removed = false;
        await Leave((method, payload, _) =>
        {
            if (method == ServiceMethods.PartyGet)
            {
                reads++;
                return Task.FromResult(removed ? ServiceReply.Reject("party_not_found") :
                    ServiceReply.From(Party(statuses < 2 ? "request" : "", statuses < 2 ? 3UL : 4UL)));
            }
            if (method == ServiceMethods.MatchStatus)
                return Task.FromResult(++statuses == 1 ? ServiceReply.Reject("invalid_allocation") :
                    ServiceReply.From(new MatchReady { Status = new MatchStatus { RequestId = "request", State = "failed", Failure = "room_released" } }));
            Assert.That(method, Is.EqualTo(ServiceMethods.PartyLeave), "A rejected ticket must never authorize queue cancellation.");
            Assert.That(((PartyCommand)payload).ExpectedVersion, Is.EqualTo(4UL));
            removed = true;
            return Task.FromResult(ServiceReply.From(Party("", 5)));
        });
        Assert.That(statuses, Is.EqualTo(2)); Assert.That(reads, Is.EqualTo(4));
    }

    [TestCase("invalid_allocation")]
    [TestCase("queue_not_found")]
    [TestCase("forbidden")]
    public void RejectedStatusNeverCountsAsProofOfCleanup(string error)
    {
        var clock = new AdvancingClock();
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await Leave((method, _, _) =>
        {
            clock.Advance();
            if (method == ServiceMethods.PartyGet) return Task.FromResult(ServiceReply.From(Party("request")));
            Assert.That(method, Is.EqualTo(ServiceMethods.MatchStatus));
            return Task.FromResult(ServiceReply.Reject(error));
        }, clock));
        Assert.That(failure!.Message, Is.EqualTo("qualification-party-cleanup-unconfirmed"));
    }

    [Test]
    public async Task UnknownLeaveReplyRequiresProofThatTheOriginalSingletonPartyNoLongerExists()
    {
        bool removed = false; int leaves = 0, reads = 0;
        await Leave(async (method, _, _) =>
        {
            await Task.Yield();
            if (method == ServiceMethods.PartyGet)
            { reads++; return removed ? ServiceReply.Reject("party_not_found") : ServiceReply.From(Party()); }
            Assert.That(method, Is.EqualTo(ServiceMethods.PartyLeave));
            leaves++; removed = true; throw new TimeoutException("reply-lost-after-server-applied-leave");
        });
        Assert.That(leaves, Is.EqualTo(1)); Assert.That(reads, Is.EqualTo(2));
    }

    [Test]
    public void UnknownLeaveAndUnknownVerificationRejectCleanupInsteadOfClaimingSuccess()
    {
        bool attempted = false;
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await Leave((method, _, _) =>
        {
            if (method == ServiceMethods.PartyGet && !attempted) return Task.FromResult(ServiceReply.From(Party()));
            attempted = true; throw new TimeoutException("unknown-outcome");
        }));
        Assert.That(failure!.Message, Is.EqualTo("qualification-party-cleanup-unconfirmed"));
    }

    [Test]
    public async Task CancelsOnlyUnallocatedQueueThenUsesFreshPartyVersionToLeave()
    {
        bool queued = true, removed = false; int cancelled = 0;
        await Leave((method, payload, _) =>
        {
            if (method == ServiceMethods.PartyGet)
                return Task.FromResult(removed ? ServiceReply.Reject("party_not_found") : ServiceReply.From(Party(queued ? "request" : "", queued ? 3UL : 4UL)));
            if (method == ServiceMethods.MatchStatus)
                return Task.FromResult(ServiceReply.From(new MatchReady { Status = new MatchStatus { RequestId = "request", State = "queued" } }));
            if (method == ServiceMethods.QueueCancel)
            { cancelled++; queued = false; return Task.FromResult(ServiceReply.From(new MatchStatus { RequestId = "request", State = "cancelled" })); }
            Assert.That(method, Is.EqualTo(ServiceMethods.PartyLeave));
            Assert.That(((PartyCommand)payload).ExpectedVersion, Is.EqualTo(4UL));
            removed = true; return Task.FromResult(ServiceReply.From(Party("", 5)));
        });
        Assert.That(cancelled, Is.EqualTo(1));
    }

    [Test]
    public void ClaimedAllocationIsNeverCancelledAndCleanupHasABoundedDeadline()
    {
        var clock = new AdvancingClock(); int cancels = 0;
        var failure = Assert.ThrowsAsync<InvalidOperationException>(async () => await Leave((method, _, _) =>
        {
            clock.Advance();
            if (method == ServiceMethods.PartyGet) return Task.FromResult(ServiceReply.From(Party("request")));
            if (method == ServiceMethods.QueueCancel) cancels++;
            Assert.That(method, Is.EqualTo(ServiceMethods.MatchStatus));
            return Task.FromResult(ServiceReply.From(new MatchReady { Status = new MatchStatus { RequestId = "request", State = "ready" } }));
        }, clock));
        Assert.That(failure!.Message, Is.EqualTo("qualification-party-cleanup-unconfirmed"));
        Assert.That(cancels, Is.Zero);
    }

    static PartyState Party(string request = "", ulong version = 3)
    {
        var party = new PartyState { PartyId = "original-party", Version = version, QueueRequestId = request };
        party.Members.Add(new PartyMember { PlayerId = "original-player", Ready = true }); return party;
    }
    static Task Leave(Func<string, IMessage, CancellationToken, Task<ServiceReply>> call, TimeProvider? clock = null)
    {
        var feature = Assembly.Load("AiNative.TopologyAcceptance").GetType("QualificationPartyCleanup");
        Assert.That(feature, Is.Not.Null, "Missing confirmed original-party cleanup behavior");
        return (Task)feature!.GetMethod("LeaveAsync")!.Invoke(null,
            ["original-player", "original-party", "request", call, clock ?? TimeProvider.System])!;
    }
    sealed class AdvancingClock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public void Advance() => now = now.AddSeconds(20);
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.Ticks;
    }
}
