using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Client.Fantasy
{
    /// <summary>Bounded Fantasy outer RPC client, with optional TLS stream transport. No gameplay work runs here.</summary>
    public sealed class FantasyGateClient : IDisposable
    {
        private readonly TcpClient _socket;
        private readonly Stream _stream;
        private readonly int _timeout;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private int _pending;
        private int _closed;
        private uint _rpc;
        public bool IsConnected => Volatile.Read(ref _closed) == 0;
        private FantasyGateClient(TcpClient socket, Stream stream, int timeout) { _socket = socket; _stream = stream; _timeout = timeout; }

        public static async Task<FantasyGateClient> ConnectAsync(GateConnectionOptions options, CancellationToken cancellationToken = default)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var socket = new TcpClient { NoDelay = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(options.TimeoutMilliseconds);
            using var cancellation = deadline.Token.Register(socket.Dispose);
            try
            {
                await socket.ConnectAsync(options.Host, options.Port).ConfigureAwait(false);
                Stream stream = socket.GetStream();
                if (options.UseTls)
                {
                    var tls = new SslStream(stream, false, (_, certificate, chain, errors) => certificate != null && GateTlsTrust.Accept(certificate, errors, options.CertificateSha256, DateTime.UtcNow));
                    stream = tls;
                    await tls.AuthenticateAsClientAsync(options.Host, null, SslProtocols.Tls12, checkCertificateRevocation: true).ConfigureAwait(false);
                }
                deadline.Token.ThrowIfCancellationRequested();
                return new FantasyGateClient(socket, stream, options.TimeoutMilliseconds);
            }
            catch
            {
                socket.Dispose(); deadline.Token.ThrowIfCancellationRequested(); throw;
            }
        }

        public async Task<byte[]> CallAsync(string method, byte[] payload, string credential = "", CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new GateCallException("unavailable");
            if (payload == null || payload.Length > GateWire.MaxBody || string.IsNullOrWhiteSpace(method)) throw new ArgumentException("Invalid Gate request.");
            if (Interlocked.Increment(ref _pending) > 8) { Interlocked.Decrement(ref _pending); throw new GateCallException("unavailable"); }
            bool entered = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(_timeout);
            try
            {
                await _serial.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
                if (!IsConnected) throw new GateCallException("unavailable");
                // A canceled partial exchange invalidates the stream: a future response must not satisfy another call.
                using var cancellation = deadline.Token.Register(Dispose);
                uint rpc = ++_rpc; if (rpc == 0) rpc = ++_rpc;
                string correlation = Guid.NewGuid().ToString("N");
                byte[] request = GateWire.Frame(268435633u, rpc, GateWire.Bytes(1, GateWire.Request(method, payload, credential, correlation)));
                await _stream.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
                byte[] header = new byte[20];
                for (int skipped = 0; skipped <= 64; ++skipped)
                {
                    await ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header); if (length < -1 || length > GateWire.MaxBody) throw new InvalidDataException("gate-frame-length");
                    uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), receivedRpc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    byte[] body = new byte[Math.Max(length, 0)]; await ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                    if (opcode == 134217905u) continue; // Notifications are advisory; application queries authoritative state.
                    if (opcode != 402653361u || receivedRpc != rpc) throw new InvalidDataException("gate-response-identity");
                    var envelope = GateWire.Read(body); if (GateWire.Value(envelope, 1) != 0) throw new GateCallException("unavailable");
                    return GateWire.Response(GateWire.Data(envelope, 2), correlation);
                }
                throw new InvalidDataException("gate-notification-limit");
            }
            catch (GateCallException) { throw; }
            catch
            {
                if (entered) Dispose(); deadline.Token.ThrowIfCancellationRequested(); throw;
            }
            finally { if (entered) _serial.Release(); Interlocked.Decrement(ref _pending); }
        }

        private async Task ReadExactlyAsync(byte[] bytes, CancellationToken ct)
        {
            for (int offset = 0; offset < bytes.Length;)
            {
                int read = await _stream.ReadAsync(bytes, offset, bytes.Length - offset, ct).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("gate-disconnected"); offset += read;
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _socket.Dispose(); _stream.Dispose();
        }
    }
}
