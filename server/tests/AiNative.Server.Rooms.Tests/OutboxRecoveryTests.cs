using System.Security.Cryptography;
using System.Text;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Rooms;
using NUnit.Framework;

namespace AiNative.Server.Rooms.Tests;

public sealed class OutboxRecoveryTests
{
    [Test]
    public async Task AdmittedResultRetriesAfterDirectoryReturnsWithoutRestartOrLosingItsClaim()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ainative_outbox_recovery_" + Guid.NewGuid().ToString("N"));
        string backup = directory + "_backup";
        try
        {
            DurableResultOutbox outbox = new(directory, 2, 4096); await outbox.InitializeAsync();
            var allocation = Allocation(); Assert.That(outbox.TryReserve(allocation), Is.True);
            Directory.Move(directory, backup); File.WriteAllText(directory, "owned test obstruction");
            Assert.That(async () => await outbox.StoreAsync(Result()), Throws.InstanceOf<IOException>());
            Assert.That(outbox.CanAdmit, Is.False); Assert.That(outbox.ReservedCount, Is.EqualTo(1));
            File.Delete(directory); Directory.Move(backup, directory);
            Assert.That(await outbox.StoreAsync(Result()), Is.True);
            Assert.That(outbox.ReservedCount, Is.Zero); Assert.That(outbox.CanAdmit, Is.True);
            DurableResultOutbox recovered = new(directory, 2, 4096); await recovered.InitializeAsync();
            Assert.That(recovered.Pending.Single(), Is.EqualTo(Result()));
        }
        finally
        {
            if (File.Exists(directory)) File.Delete(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
        }
    }

    [Test]
    public async Task ReadOnlyPendingFileClosesAdmissionAndPermissionRepairAllowsRetry()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows read-only-file permission behavior; Linux permissions require its deployment qualification.");
        string directory = Path.Combine(Path.GetTempPath(), "ainative_outbox_permission_" + Guid.NewGuid().ToString("N"));
        string pending = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("m1"))) + ".result.pending");
        try
        {
            DurableResultOutbox outbox = new(directory, 2, 4096); await outbox.InitializeAsync();
            Assert.That(outbox.TryReserve(Allocation()), Is.True);
            File.WriteAllText(pending, "owned test obstruction"); File.SetAttributes(pending, FileAttributes.ReadOnly);
            Assert.That(async () => await outbox.StoreAsync(Result()), Throws.TypeOf<UnauthorizedAccessException>());
            Assert.That(outbox.CanAdmit, Is.False);
            File.SetAttributes(pending, FileAttributes.Normal);
            Assert.That(await outbox.StoreAsync(Result()), Is.True); Assert.That(outbox.CanAdmit, Is.True);
        }
        finally
        {
            if (File.Exists(pending)) File.SetAttributes(pending, FileAttributes.Normal);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static RoomAllocation Allocation() => new() { MatchId = "m1", RoomId = "r1", NodeId = "b1", BootEpoch = "e1", AllocationId = "a1", PlayerIds = { "p1" } };
    private static MatchResult Result() => new() { MatchId = "m1", RoomId = "r1", NodeId = "b1", BootEpoch = "e1", Completion = "Finished", Players = { new PlayerResult { PlayerId = "p1", Won = true } } };
}
