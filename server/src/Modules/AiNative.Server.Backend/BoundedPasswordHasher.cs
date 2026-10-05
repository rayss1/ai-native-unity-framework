using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AiNative.Server.Backend.Tests")]

namespace AiNative.Server.Backend;

internal sealed class BoundedPasswordHasher(Func<string, byte[], string, byte[]> compute)
{
    private readonly SemaphoreSlim slots = new(2, 2);

    internal async ValueTask<byte[]> HashAsync(string password, byte[] salt, string operation, CancellationToken cancellationToken = default)
    {
        // Player's existing handler budget bounds callers. Only slot owners enqueue CPU work.
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Once computation starts, cancellation must not abandon it or return its slot early.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return compute(password, salt, operation);
            }).ConfigureAwait(false);
        }
        finally { slots.Release(); }
    }
}
