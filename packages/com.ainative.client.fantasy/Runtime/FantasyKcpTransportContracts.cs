using System;

namespace AiNative.Client.Fantasy
{
    public sealed class FantasyKcpTransportOptions
    {
        public FantasyKcpTransportOptions(
            string host,
            int port,
            int connectTimeoutMilliseconds = 5000)
        {
            Host = host;
            Port = port;
            ConnectTimeoutMilliseconds = connectTimeoutMilliseconds;
        }

        public string Host { get; }

        public int Port { get; }

        public int ConnectTimeoutMilliseconds { get; }

        internal bool IsValid =>
            !string.IsNullOrWhiteSpace(Host) &&
            Port > 0 &&
            Port <= 65535 &&
            ConnectTimeoutMilliseconds > 0;
    }

    public enum FantasyKcpConnectStatus : byte
    {
        Connected = 0,
        InvalidConfiguration = 1,
        TimedOut = 2,
        Cancelled = 3,
        Faulted = 4,
    }

    public readonly struct FantasyKcpConnectResult
    {
        internal FantasyKcpConnectResult(
            FantasyKcpConnectStatus status,
            FantasyKcpRealtimeTransport transport,
            Exception error)
        {
            Status = status;
            Transport = transport;
            Error = error;
        }

        public FantasyKcpConnectStatus Status { get; }

        public FantasyKcpRealtimeTransport Transport { get; }

        public Exception Error { get; }

        public bool IsConnected =>
            Status == FantasyKcpConnectStatus.Connected && Transport != null;
    }

    public readonly struct FantasyKcpTransportDiagnostics
    {
        internal FantasyKcpTransportDiagnostics(
            long sendsAccepted,
            long sendBackpressure,
            long receivesAccepted,
            long oversizedFrames,
            long invalidChannels,
            long staleSequences,
            long inboundDropped,
            long connectionFaults)
        {
            SendsAccepted = sendsAccepted;
            SendBackpressure = sendBackpressure;
            ReceivesAccepted = receivesAccepted;
            OversizedFrames = oversizedFrames;
            InvalidChannels = invalidChannels;
            StaleSequences = staleSequences;
            InboundDropped = inboundDropped;
            ConnectionFaults = connectionFaults;
        }

        public long SendsAccepted { get; }

        public long SendBackpressure { get; }

        public long ReceivesAccepted { get; }

        public long OversizedFrames { get; }

        public long InvalidChannels { get; }

        public long StaleSequences { get; }

        public long InboundDropped { get; }

        public long ConnectionFaults { get; }
    }
}
