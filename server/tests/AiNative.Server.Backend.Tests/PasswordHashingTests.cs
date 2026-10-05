using System.Security.Cryptography;
using System.Collections.Concurrent;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Backend;
using AiNative.Server.Control;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Server.Backend.Tests;

public sealed class PasswordHashingTests
{
    [Test]
    public async Task Player_registration_resumes_store_and_caller_on_the_owning_message_pump()
    {
        using RSA key = RSA.Create(2048);
        using var hashStarted = new ManualResetEventSlim();
        using var releaseHash = new ManualResetEventSlim();
        using var heartbeat = new ManualResetEventSlim();
        using var pump = new OwnerMessagePump();
        var completed = new TaskCompletionSource<ServiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ownerThread = 0, hashThread = 0, continuationThread = 0;
        SynchronizationContext? hashContext = null, continuationContext = null;
        var store = new ContextRecordingStore();
        var hasher = new BoundedPasswordHasher((_, _, _) =>
        {
            hashThread = Environment.CurrentManagedThreadId;
            hashContext = SynchronizationContext.Current;
            hashStarted.Set(); releaseHash.Wait(); return new byte[32];
        });
        var player = new PlayerService(store, key, TimeProvider.System, hasher);
        var request = new AccountRequest { Username = "pump_user", Password = "correct-password" }.ToByteArray();
        var owner = new Thread(() =>
        {
            ownerThread = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(pump);
            pump.Post(async _ =>
            {
                try
                {
                    var reply = await player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.RegisterAccount, request);
                    continuationThread = Environment.CurrentManagedThreadId;
                    continuationContext = SynchronizationContext.Current;
                    completed.SetResult(reply);
                }
                catch (Exception error) { completed.SetException(error); }
                finally { pump.Complete(); }
            }, null);
            pump.Run();
            SynchronizationContext.SetSynchronizationContext(null);
        }) { IsBackground = true };
        owner.Start();
        try
        {
            Assert.That(hashStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
            pump.Post(_ => heartbeat.Set(), null);
            Assert.That(heartbeat.Wait(TimeSpan.FromSeconds(5)), Is.True, "Owner must process another callback while KDF is blocked");
            Assert.That(completed.Task.IsCompleted, Is.False);
            releaseHash.Set();
            Assert.That((await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success, Is.True);
            Assert.That(store.CreateThread, Is.EqualTo(ownerThread));
            Assert.That(store.CreateContext, Is.SameAs(pump));
            Assert.That(continuationThread, Is.EqualTo(ownerThread));
            Assert.That(continuationContext, Is.SameAs(pump));
            Assert.That(hashThread, Is.Not.EqualTo(ownerThread));
            Assert.That(hashContext, Is.Null);
        }
        finally { releaseHash.Set(); Assert.That(owner.Join(TimeSpan.FromSeconds(5)), Is.True); }
    }

    [Test]
    public async Task Cancelling_one_of_two_running_hashes_keeps_its_slot_until_compute_returns()
    {
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        using var firstTwo = new CountdownEvent(2);
        using var thirdStarted = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        int entered = 0, active = 0, maximum = 0;
        object sync = new();
        var hasher = new BoundedPasswordHasher((password, _, _) =>
        {
            Interlocked.Increment(ref entered);
            lock (sync) { active++; maximum = Math.Max(maximum, active); }
            try
            {
                if (password == "first") { firstTwo.Signal(); firstRelease.Wait(); }
                else if (password == "second") { firstTwo.Signal(); secondRelease.Wait(); }
                else { thirdStarted.Set(); }
                return [1];
            }
            finally { lock (sync) active--; }
        });
        var first = hasher.HashAsync("first", [], "login", cancelled.Token).AsTask();
        var second = hasher.HashAsync("second", [], "login").AsTask();
        Task<byte[]>? third = null;
        try
        {
            Assert.That(firstTwo.Wait(TimeSpan.FromSeconds(5)), Is.True);
            cancelled.Cancel();
            Assert.That(first.IsCompleted, Is.False);
            third = hasher.HashAsync("third", [], "login").AsTask();
            Assert.That(third.IsCompleted, Is.False);
            Assert.That(Volatile.Read(ref entered), Is.EqualTo(2));
            Assert.That(thirdStarted.IsSet, Is.False);
            firstRelease.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(thirdStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(second.IsCompleted, Is.False, "Third used the first slot while the second remained occupied");
            Assert.That(maximum, Is.EqualTo(2));
        }
        finally
        {
            firstRelease.Set(); secondRelease.Set();
            await Task.WhenAll(third is null ? [first, second] : new[] { first, second, third }).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    sealed class OwnerMessagePump : SynchronizationContext, IDisposable
    {
        readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
        public void Run() { foreach (var work in queue.GetConsumingEnumerable()) work.Callback(work.State); }
        public void Complete() { if (!queue.IsAddingCompleted) queue.CompleteAdding(); }
        public void Dispose() => queue.Dispose();
    }

    sealed class ContextRecordingStore : IPlayerStore
    {
        readonly MemoryStore inner = new();
        public int CreateThread;
        public SynchronizationContext? CreateContext;
        public ValueTask<bool> CreateAsync(PlayerAccount account, CancellationToken cancellationToken = default)
        {
            CreateThread = Environment.CurrentManagedThreadId;
            CreateContext = SynchronizationContext.Current;
            return inner.CreateAsync(account, cancellationToken);
        }
        public ValueTask<PlayerAccount?> FindAsync(string username, CancellationToken cancellationToken = default) => inner.FindAsync(username, cancellationToken);
        public ValueTask<PlayerProfile?> ProfileAsync(string playerId, CancellationToken cancellationToken = default) => inner.ProfileAsync(playerId, cancellationToken);
        public ValueTask<SettlementReceipt> SettleAsync(MatchResult result, string payloadHash, CancellationToken cancellationToken = default) => inner.SettleAsync(result, payloadHash, cancellationToken);
        public ValueTask<SettlementReceipt?> SettlementAsync(string matchId, CancellationToken cancellationToken = default) => inner.SettlementAsync(matchId, cancellationToken);
    }

    [Test]
    public async Task Hashing_yields_the_calling_message_context_and_runs_without_it()
    {
        using var release = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        int workerThread = 0;
        SynchronizationContext? workerContext = null;
        var hasher = new BoundedPasswordHasher((_, _, _) =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            workerContext = SynchronizationContext.Current;
            release.Wait();
            return [1];
        });
        Task<byte[]>? hashing = null;
        var owner = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            hashing = hasher.HashAsync("password", [], "login").AsTask();
            returned.Set(); // The message loop can process its next callback before KDF finishes.
        });
        owner.Start();
        try { Assert.That(returned.Wait(TimeSpan.FromSeconds(2)), Is.True, "KDF blocked the message context"); }
        finally { release.Set(); Assert.That(owner.Join(TimeSpan.FromSeconds(5)), Is.True); }
        Assert.That(await hashing!.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 1 }));
        Assert.That(workerThread, Is.Not.EqualTo(owner.ManagedThreadId));
        Assert.That(workerContext, Is.Null);
    }

    [Test]
    public async Task At_most_two_hashes_run_while_waiters_do_not_start_computation()
    {
        using var release = new ManualResetEventSlim();
        using var firstTwo = new CountdownEvent(2);
        int entered = 0, active = 0, maximum = 0;
        object sync = new();
        var hasher = new BoundedPasswordHasher((_, _, _) =>
        {
            int ordinal = Interlocked.Increment(ref entered);
            lock (sync) { active++; maximum = Math.Max(maximum, active); }
            if (ordinal <= 2) firstTwo.Signal();
            try { release.Wait(); return [1]; }
            finally { lock (sync) active--; }
        });
        Task<byte[]>[] requests = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await hasher.HashAsync("password", [], "login"))).ToArray();
        try
        {
            Assert.That(firstTwo.Wait(TimeSpan.FromSeconds(5)), Is.True);
            await Task.Delay(200);
            Assert.That(Volatile.Read(ref entered), Is.EqualTo(2), "Queued requests executed before a slot was free");
        }
        finally { release.Set(); await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.That(maximum, Is.EqualTo(2));
        Assert.That(entered, Is.EqualTo(8));
    }

    [Test]
    public void Pre_cancelled_hash_never_starts()
    {
        int entered = 0;
        var hasher = new BoundedPasswordHasher((_, _, _) => { entered++; return [1]; });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await hasher.HashAsync("password", [], "login", cancelled.Token));
        Assert.That(entered, Is.Zero);
    }

    [Test]
    public async Task Queued_cancellation_does_not_compute_or_release_an_unowned_slot()
    {
        using var release = new ManualResetEventSlim();
        using var firstTwo = new CountdownEvent(2);
        int entered = 0;
        var hasher = new BoundedPasswordHasher((_, _, _) =>
        {
            if (Interlocked.Increment(ref entered) <= 2) firstTwo.Signal();
            release.Wait(); return [1];
        });
        var first = Task.Run(async () => await hasher.HashAsync("password", [], "login"));
        var second = Task.Run(async () => await hasher.HashAsync("password", [], "login"));
        Task<byte[]>? queued = null;
        using var cancelled = new CancellationTokenSource();
        try
        {
            Assert.That(firstTwo.Wait(TimeSpan.FromSeconds(5)), Is.True);
            queued = Task.Run(async () => await hasher.HashAsync("password", [], "login", cancelled.Token));
            await Task.Delay(100); cancelled.Cancel();
            Assert.That(await Task.WhenAny(queued, Task.Delay(1000)), Is.SameAs(queued), "Waiting cancellation did not complete");
            Assert.That(queued.IsCanceled, Is.True);
            Assert.That(Volatile.Read(ref entered), Is.EqualTo(2));
        }
        finally
        {
            release.Set(); await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            if (queued is not null) { try { await queued; } catch (OperationCanceledException) { } }
        }
        Assert.That(await hasher.HashAsync("password", [], "login"), Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public async Task Cancellation_after_computation_starts_waits_for_real_completion()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var cancelled = new CancellationTokenSource();
        var hasher = new BoundedPasswordHasher((_, _, _) => { started.Set(); release.Wait(); return [7]; });
        Task<byte[]> hashing = Task.Run(async () => await hasher.HashAsync("password", [], "login", cancelled.Token));
        try
        {
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            cancelled.Cancel(); await Task.Delay(100);
            Assert.That(hashing.IsCompleted, Is.False, "Cancellation abandoned a running KDF");
        }
        finally { release.Set(); }
        Assert.That(await hashing.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task Computation_exceptions_release_slots_for_later_requests()
    {
        int calls = 0;
        var hasher = new BoundedPasswordHasher((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new CryptographicException("test");
            return [9];
        });
        for (int i = 0; i < 2; i++)
            Assert.ThrowsAsync<CryptographicException>(async () => await hasher.HashAsync("password", [], "login"));
        Assert.That(await hasher.HashAsync("password", [], "login").AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 9 }));
    }

    [TestCase(ServiceMethods.RegisterAccount)]
    [TestCase(ServiceMethods.Login)]
    public async Task Pre_cancelled_authentication_does_not_hash_or_mutate_accounts(string method)
    {
        using RSA key = RSA.Create(2048);
        var store = new MemoryStore();
        var player = new PlayerService(store, key, TimeProvider.System);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await player.HandleAsync(
            new(ServiceRole.Gate, "gate"), method,
            new AccountRequest { Username = "cancelled_user", Password = "correct-password" }.ToByteArray(), cancelled.Token));
        Assert.That(await store.FindAsync("cancelled_user"), Is.Null);
    }

    [Test]
    public async Task Unknown_account_still_waits_for_login_hash_with_dummy_salt()
    {
        using RSA key = RSA.Create(2048);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string? operation = null;
        byte[]? suppliedSalt = null;
        var hasher = new BoundedPasswordHasher((_, salt, kind) =>
        {
            operation = kind; suppliedSalt = salt;
            started.Set(); release.Wait(); return new byte[32];
        });
        var player = new PlayerService(new MemoryStore(), key, TimeProvider.System, hasher);
        var request = new AccountRequest { Username = "unknown_user", Password = "correct-password" };
        Task<ServiceReply> login = player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.Login, request.ToByteArray()).AsTask();
        try
        {
            Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(login.IsCompleted, Is.False);
        }
        finally { release.Set(); }
        Assert.That((await login.WaitAsync(TimeSpan.FromSeconds(5))).Error, Is.EqualTo("invalid_credentials"));
        Assert.That(operation, Is.EqualTo("login"));
        Assert.That(suppliedSalt, Is.EqualTo(new byte[32]));
    }

    [Test]
    public async Task Production_hash_preserves_parameters_and_password_validation()
    {
        using RSA key = RSA.Create(2048);
        var store = new MemoryStore();
        var player = new PlayerService(store, key, TimeProvider.System);
        var request = new AccountRequest { Username = "hash_user", Password = "correct-password" };
        Assert.That((await player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.RegisterAccount, request.ToByteArray())).Success, Is.True);
        var account = await store.FindAsync(request.Username);
        Assert.That(account!.PasswordHash, Is.EqualTo(Rfc2898DeriveBytes.Pbkdf2(request.Password, account.Salt, 210000, HashAlgorithmName.SHA256, 32)));
        Assert.That((await player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.Login, request.ToByteArray())).Success, Is.True);
        request.Password = "incorrect-password";
        Assert.That((await player.HandleAsync(new(ServiceRole.Gate, "gate"), ServiceMethods.Login, request.ToByteArray())).Error, Is.EqualTo("invalid_credentials"));
    }
}
