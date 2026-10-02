using System;
using System.Net;

namespace AiNative.Client.Fantasy
{
    public sealed class GateCallException : Exception
    {
        public string Code { get; }
        public GateCallException(string code) : base(code) => Code = code;
    }

    public sealed class GateConnectionOptions
    {
        public string Host { get; }
        public int Port { get; }
        public bool UseTls { get; }
        public string CertificateSha256 { get; }
        public int TimeoutMilliseconds { get; }
        public GateConnectionOptions(string host, int port, bool useTls = true, string certificateSha256 = "", int timeoutMilliseconds = 10000)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Gate host required.");
            if (port < 1 || port > 65535 || timeoutMilliseconds < 100 || timeoutMilliseconds > 60000) throw new ArgumentOutOfRangeException(nameof(port));
            if (!useTls && !(IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)))
                throw new ArgumentException("Plaintext Gate connections are restricted to explicit loopback IPs.");
            certificateSha256 = (certificateSha256 ?? "").Replace(":", "").ToUpperInvariant();
            if (certificateSha256.Length != 0 && (certificateSha256.Length != 64 || !IsHex(certificateSha256))) throw new ArgumentException("Certificate pin must be a SHA256 hex digest.");
            Host = host; Port = port; UseTls = useTls; CertificateSha256 = certificateSha256; TimeoutMilliseconds = timeoutMilliseconds;
        }
        private static bool IsHex(string value) { foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')) return false; return true; }
    }

    public sealed class GateProfile
    {
        public string PlayerId { get; internal set; } = "";
        public string DisplayName { get; internal set; } = "";
        public long Played { get; internal set; }
        public long Won { get; internal set; }
        public long Kills { get; internal set; }
    }
    public sealed class GatePartyMember
    {
        public string PlayerId { get; internal set; } = "";
        public bool Ready { get; internal set; }
    }
    public sealed class GateParty
    {
        public string PartyId { get; internal set; } = "";
        public string LeaderId { get; internal set; } = "";
        public ulong Version { get; internal set; }
        public GatePartyMember[] Members { get; internal set; } = Array.Empty<GatePartyMember>();
        public string QueueRequestId { get; internal set; } = "";
    }
    public sealed class GateMatch
    {
        public string RequestId { get; internal set; } = "";
        public string State { get; internal set; } = "";
        public string MatchId { get; internal set; } = "";
        public string RoomId { get; internal set; } = "";
        public string BootEpoch { get; internal set; } = "";
        public string NodeId { get; internal set; } = "";
        public string Address { get; internal set; } = "";
        public string Failure { get; internal set; } = "";
        public string EntryTicket { get; internal set; } = "";
    }
    public sealed class GateSettlement
    {
        public string MatchId { get; internal set; } = "";
        public bool Confirmed { get; internal set; }
    }
}
