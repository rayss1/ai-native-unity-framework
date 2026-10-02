using System.Security.Cryptography;
using AiNative.Server.Battle;
using NUnit.Framework;
namespace AiNative.Server.Battle.Tests;
public sealed class ArenaReplayStorageTests
{
    [Test]
    public void ExistingOwnedFilesAndActiveReservationsConsumeCountAndByteQuota()
    {
        string directory = Path.Combine(Path.GetTempPath(), "replay-quota-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, new string('a', 64) + ".anar"), new byte[100]);
            File.WriteAllBytes(Path.Combine(directory, new string('b', 64) + ".anar.pending"), new byte[50]);
            File.WriteAllBytes(Path.Combine(directory, "unknown.anar"), new byte[1000]);
            var storage = new ArenaReplayStorage(directory, 3, 400);
            Assert.That(storage.Files, Is.EqualTo(2)); Assert.That(storage.Bytes, Is.EqualTo(150));
            Assert.That(storage.TryReserve("one", 200), Is.True); Assert.That(storage.TryReserve("two", 1), Is.False);
            Assert.That(storage.Healthy, Is.True); storage.Complete("one", 20);
            Assert.That(storage.Files, Is.EqualTo(3)); Assert.That(storage.Bytes, Is.EqualTo(170));
            Assert.That(File.ReadAllBytes(Path.Combine(directory, "unknown.anar")).Length, Is.EqualTo(1000));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test]
    public void ByteQuotaReservesFullLifetimeAndShrinksToPublishedActualBytes()
    {
        long budget = ArenaReplayStorage.ReservationBytes(600);
        var storage = new ArenaReplayStorage(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), 10, budget);
        Assert.That(storage.TryReserve("one", budget), Is.True); Assert.That(storage.CanReserve(budget), Is.False);
        Assert.That(storage.TryReserve("two", budget), Is.False); Assert.That(storage.Healthy, Is.True);
        storage.Complete("one", 100); Assert.That(storage.Bytes, Is.EqualTo(100));
        Assert.That(storage.TryReserve("two", budget - 100), Is.True);
    }
    [Test]
    public void GameplayFingerprintEqualsPinnedSourceBytes()
    {
        string? root = TestContext.CurrentContext.TestDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "AGENTS.md"))) root = Directory.GetParent(root)?.FullName;
        Assert.That(root, Is.Not.Null);
        byte[] arena = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root!, "server/src/Modules/AiNative.Server.Battle/ArenaRoom.cs")).ReplaceLineEndings("\n"));
        byte[] shared = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root!, "shared/gameplay/Runtime/ArenaGameplay.cs")).ReplaceLineEndings("\n"));
        string fingerprint = Convert.ToHexString(SHA256.HashData(arena.Concat(shared).ToArray())).ToLowerInvariant();
        Assert.That(ArenaReplayVerifier.GameplayFingerprint, Is.EqualTo(fingerprint), "Update the build gameplay/map fingerprint when rule sources change.");
    }
    [Test]
    public void StartupIoFailureIsUnhealthyWhileExistingQuotaExhaustionIsHealthy()
    {
        string path = Path.Combine(Path.GetTempPath(), "replay-file-" + Guid.NewGuid().ToString("N")); File.WriteAllText(path, "not a directory");
        try { Assert.That(new ArenaReplayStorage(path, 1, 100).Healthy, Is.False); }
        finally { File.Delete(path); }
    }
}
