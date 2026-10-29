using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using AiNative.Client.Protocol.Protobuf;
using AiNative.Realtime;

namespace AiNative.Client.Fantasy
{
    /// <summary>有界 Fantasy 外层 RPC 客户端，支持可选 TLS 流传输；不执行玩法逻辑。</summary>
    public sealed class FantasyGateClient : IDisposable
    {
        private readonly TcpClient _socket;
        private readonly Stream _stream;

        // 毫秒；包含 RPC 排队及交换的总超时，构造参数 timeout 使用同一单位。
        private readonly int _timeout;
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private int _pending;
        private int _closed;
        private uint _rpc;
        private readonly byte[] _header = new byte[20];

        /// <summary>本地连接尚未关闭；此属性不执行远端存活探测。</summary>
        public bool IsConnected => Volatile.Read(ref _closed) == 0;

        private FantasyGateClient(TcpClient socket, Stream stream, int timeout)
        {
            _socket = socket;
            _stream = stream;
            _timeout = timeout;
        }

        /// <summary>建立 TCP 及可选 TLS 连接；成功后调用方负责释放返回的客户端。</summary>
        /// <remarks>不依赖 Unity 主线程，不捕获同步上下文。超时单位由 options.TimeoutMilliseconds 指定；超时或取消抛出取消异常，其他连接或 TLS 错误原样传播，失败关闭已创建的套接字。</remarks>
        public static async Task<FantasyGateClient> ConnectAsync(
            GateConnectionOptions options,
            CancellationToken cancellationToken = default
        )
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var socket = new TcpClient { NoDelay = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.TimeoutMilliseconds);
            using var cancellation = deadline.Token.Register(socket.Dispose);
            try
            {
                await socket.ConnectAsync(options.Host, options.Port).ConfigureAwait(false);
                Stream stream = socket.GetStream();
                if (options.UseTls)
                {
                    var tls = new SslStream(
                        stream,
                        false,
                        (_, certificate, chain, errors) =>
                            certificate != null
                            && GateTlsTrust.Accept(
                                certificate,
                                errors,
                                options.CertificateSha256,
                                DateTime.UtcNow
                            )
                    );
                    stream = tls;
                    await tls.AuthenticateAsClientAsync(
                            options.Host,
                            null,
                            SslProtocols.Tls12,
                            checkCertificateRevocation: true
                        )
                        .ConfigureAwait(false);
                }

                deadline.Token.ThrowIfCancellationRequested();
                return new FantasyGateClient(socket, stream, options.TimeoutMilliseconds);
            }
            catch
            {
                socket.Dispose();
                deadline.Token.ThrowIfCancellationRequested();
                throw;
            }
        }

        /// <summary>发起有界 RPC；返回响应字节由调用方持有。</summary>
        /// <remarks>可并发请求，最多允许 8 个待处理调用，网络交换串行进行。总超时包含排队；排队取消不关闭连接，交换开始后的取消或传输/协议失败关闭连接。服务器业务失败以 GateCallException 传播；取消不保证服务端未执行。不捕获同步上下文，调用期间调用方不得修改 payload。</remarks>
        public async Task<byte[]> CallAsync(
            string method,
            byte[] payload,
            string credential = "",
            CancellationToken cancellationToken = default
        )
        {
            if (!IsConnected)
            {
                throw new GateCallException("unavailable");
            }

            if (
                payload == null
                || payload.Length > GateWire.MaxBody
                || string.IsNullOrWhiteSpace(method)
            )
            {
                throw new ArgumentException("Invalid Gate request.");
            }

            if (Interlocked.Increment(ref _pending) > 8)
            {
                Interlocked.Decrement(ref _pending);
                throw new GateCallException("unavailable");
            }

            bool entered = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                await _serial.WaitAsync(deadline.Token).ConfigureAwait(false);
                entered = true;
                if (!IsConnected)
                {
                    throw new GateCallException("unavailable");
                }

                // 部分交换取消后流失效；迟到响应不得被另一次调用误用。
                using var cancellation = deadline.Token.Register(Dispose);
                uint rpcId = ++_rpc;
                if (rpcId == 0)
                {
                    rpcId = ++_rpc;
                }

                string correlationId = Guid.NewGuid().ToString("N");
                // 业务信封包含操作、参数、凭证和请求身份。
                byte[] request = GateWire.Request(method, payload, credential, correlationId);
                // Fantasy 消息体封装业务信封，不修改其内容。
                byte[] envelope = GateProtocolCodec.Envelope(request);
                // TCP 帧头包含负载长度、消息类型和传输 RPC 标识。
                byte[] frame = GateFrame.Encode(GateTransportOpCodes.Request, rpcId, envelope);
                await _stream
                    .WriteAsync(frame, 0, frame.Length, deadline.Token)
                    .ConfigureAwait(false);
                byte[] header = _header;
                for (int skipped = 0; skipped <= 64; ++skipped)
                {
                    await ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length < -1 || length > GateWire.MaxBody)
                    {
                        throw new InvalidDataException("gate-frame-length");
                    }

                    uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),
                        receivedRpc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                    byte[] body = new byte[Math.Max(length, 0)];
                    await ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
                    if (opcode == GateTransportOpCodes.Push)
                    {
                        continue; // 通知仅作提示，应用仍须查询权威状态。
                    }

                    if (opcode != GateTransportOpCodes.Response || receivedRpc != rpcId)
                    {
                        throw new InvalidDataException("gate-response-identity");
                    }

                    var responseEnvelope = GateProtocolCodec.ResponseEnvelope(body);
                    if (responseEnvelope.Error != 0)
                    {
                        throw new GateCallException("unavailable");
                    }

                    return GateWire.Response(responseEnvelope.Payload, correlationId);
                }

                throw new InvalidDataException("gate-notification-limit");
            }
            catch (GateCallException)
            {
                throw;
            }
            catch
            {
                if (entered)
                {
                    Dispose();
                }

                deadline.Token.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                if (entered)
                {
                    _serial.Release();
                }

                Interlocked.Decrement(ref _pending);
            }
        }

        private async Task ReadExactlyAsync(byte[] bytes, CancellationToken ct)
        {
            for (int offset = 0; offset < bytes.Length; )
            {
                int read = await _stream
                    .ReadAsync(bytes, offset, bytes.Length - offset, ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("gate-disconnected");
                }

                offset += read;
            }
        }

        /// <summary>幂等关闭拥有的套接字和流；不等待在途调用，调用方须观察这些调用的失败或取消。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            _socket.Dispose();
            _stream.Dispose();
        }
    }
}
