using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiNative.RuntimeAcceptance;

internal static class PcapCommand
{
    public static int Run(string[] args)
    {
        Dictionary<string, string> values = ParsePairs(args);
        string pcapPath = Require(values, "pcap");
        string outputPath = Require(values, "output");
        int serverPort = int.Parse(values.GetValueOrDefault("server-port", "22000"), System.Globalization.CultureInfo.InvariantCulture);
        long startUnixMilliseconds = long.Parse(Require(values, "start-unix-ms"), System.Globalization.CultureInfo.InvariantCulture);
        int durationSeconds = int.Parse(Require(values, "duration-seconds"), System.Globalization.CultureInfo.InvariantCulture);
        int expectedClientCount = int.Parse(
            values.GetValueOrDefault("expected-clients", "64"),
            System.Globalization.CultureInfo.InvariantCulture);
        int headroomPercent = int.Parse(
            values.GetValueOrDefault("headroom-percent", "0"),
            System.Globalization.CultureInfo.InvariantCulture);
        PcapReport report = PcapAnalyzer.Analyze(
            pcapPath,
            serverPort,
            startUnixMilliseconds,
            durationSeconds,
            expectedClientCount,
            headroomPercent);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(fullOutputPath, json + Environment.NewLine);
        Console.WriteLine(json);
        return report.GatesPassed ? 0 : 1;
    }

    private static Dictionary<string, string> ParsePairs(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            }

            values[args[index][2..]] = args[index + 1];
        }

        return values;
    }

    private static string Require(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value)
            ? value
            : throw new ArgumentException($"Missing required argument --{key}.");
}

internal static class PcapAnalyzer
{
    private const uint MicrosecondMagic = 0xa1b2c3d4;
    private const uint NanosecondMagic = 0xa1b23c4d;

    public static PcapReport Analyze(
        string path,
        int serverPort,
        long startUnixMilliseconds,
        int durationSeconds,
        int expectedClientCount = 64,
        int headroomPercent = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(serverPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(serverPort, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(durationSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedClientCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedClientCount, 128);
        ArgumentOutOfRangeException.ThrowIfNegative(headroomPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(headroomPercent, 50);
        byte[] fileHash;
        using (FileStream hashStream = File.OpenRead(path))
        {
            fileHash = SHA256.HashData(hashStream);
        }
        using FileStream stream = File.OpenRead(path);
        Span<byte> global = stackalloc byte[24];
        stream.ReadExactly(global);
        uint littleMagic = BinaryPrimitives.ReadUInt32LittleEndian(global);
        bool littleEndian;
        bool nanoseconds;
        if (littleMagic is MicrosecondMagic or NanosecondMagic)
        {
            littleEndian = true;
            nanoseconds = littleMagic == NanosecondMagic;
        }
        else
        {
            uint bigMagic = BinaryPrimitives.ReadUInt32BigEndian(global);
            if (bigMagic is not (MicrosecondMagic or NanosecondMagic))
            {
                throw new InvalidDataException("The capture is not a supported classic PCAP file.");
            }

            littleEndian = false;
            nanoseconds = bigMagic == NanosecondMagic;
        }

        uint linkType = ReadUInt32(global[20..], littleEndian);
        long endUnixMilliseconds = checked(startUnixMilliseconds + (durationSeconds * 1000L));
        Dictionary<ushort, ClientWireSamples> clients = new();
        List<int> datagramPayloadBytes = new();
        long packetCount = 0;
        long capturedBytes = 0;
        Span<byte> packetHeader = stackalloc byte[16];

        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(packetHeader);
            uint seconds = ReadUInt32(packetHeader, littleEndian);
            uint fraction = ReadUInt32(packetHeader[4..], littleEndian);
            int includedLength = checked((int)ReadUInt32(packetHeader[8..], littleEndian));
            if (includedLength < 0 || includedLength > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("The PCAP packet length is invalid.");
            }

            byte[] packet = new byte[includedLength];
            stream.ReadExactly(packet);
            long packetUnixMilliseconds = checked(
                (seconds * 1000L) + (nanoseconds ? fraction / 1_000_000L : fraction / 1_000L));
            if (packetUnixMilliseconds < startUnixMilliseconds || packetUnixMilliseconds >= endUnixMilliseconds)
            {
                continue;
            }

            if (!TryReadUdp(packet, linkType, out UdpPacket udp) ||
                (udp.SourcePort != serverPort && udp.DestinationPort != serverPort))
            {
                continue;
            }

            ushort clientPort = udp.SourcePort == serverPort ? udp.DestinationPort : udp.SourcePort;
            if (!clients.TryGetValue(clientPort, out ClientWireSamples? samples))
            {
                samples = new ClientWireSamples(durationSeconds);
                clients.Add(clientPort, samples);
            }

            int bucket = Math.Clamp(
                checked((int)((packetUnixMilliseconds - startUnixMilliseconds) / 1000)),
                0,
                durationSeconds - 1);
            if (udp.SourcePort == serverPort)
            {
                samples.DownstreamBytes[bucket] += udp.IpTotalLength;
            }
            else
            {
                samples.UpstreamBytes[bucket] += udp.IpTotalLength;
            }

            packetCount++;
            capturedBytes += udp.IpTotalLength;
            datagramPayloadBytes.Add(udp.UdpPayloadLength);
        }

        double[] downstreamKbps = FlattenKbps(clients.Values.Select(client => client.DownstreamBytes));
        double[] upstreamKbps = FlattenKbps(clients.Values.Select(client => client.UpstreamBytes));
        datagramPayloadBytes.Sort();
        double downstreamP95 = Percentile(downstreamKbps, 0.95);
        double upstreamP95 = Percentile(upstreamKbps, 0.95);
        int datagramP95 = Percentile(datagramPayloadBytes, 0.95);
        double retainedFraction = 1d - (headroomPercent / 100d);
        double downstreamLimit = 256d * retainedFraction;
        double upstreamLimit = 64d * retainedFraction;
        double datagramLimit = 1200d * retainedFraction;
        bool gatesPassed =
            clients.Count == expectedClientCount &&
            packetCount > 0 &&
            downstreamP95 <= downstreamLimit &&
            upstreamP95 <= upstreamLimit &&
            datagramP95 <= datagramLimit;

        return new PcapReport(
            EvidenceClass: "linux-loopback-netem-pcap",
            QualifiedSocketImpairment: true,
            ByteAccounting: "IPv4 total length including UDP and KCP/Fantasy wire overhead",
            PcapSha256: Convert.ToHexString(fileHash).ToLowerInvariant(),
            LinkType: linkType,
            ServerPort: serverPort,
            StartUnixMilliseconds: startUnixMilliseconds,
            DurationSeconds: durationSeconds,
            ExpectedClientCount: expectedClientCount,
            ClientCount: clients.Count,
            HeadroomPercent: headroomPercent,
            DownstreamP95LimitKbps: downstreamLimit,
            UpstreamP95LimitKbps: upstreamLimit,
            DatagramPayloadP95LimitBytes: datagramLimit,
            PacketCount: packetCount,
            CapturedIpBytes: capturedBytes,
            DownstreamP50Kbps: Percentile(downstreamKbps, 0.50),
            DownstreamP95Kbps: downstreamP95,
            DownstreamP99Kbps: Percentile(downstreamKbps, 0.99),
            UpstreamP50Kbps: Percentile(upstreamKbps, 0.50),
            UpstreamP95Kbps: upstreamP95,
            UpstreamP99Kbps: Percentile(upstreamKbps, 0.99),
            DatagramPayloadP50Bytes: Percentile(datagramPayloadBytes, 0.50),
            DatagramPayloadP95Bytes: datagramP95,
            DatagramPayloadP99Bytes: Percentile(datagramPayloadBytes, 0.99),
            MaxDatagramPayloadBytes: datagramPayloadBytes.Count == 0 ? 0 : datagramPayloadBytes[^1],
            SourceCommit: Environment.GetEnvironmentVariable("AINATIVE_SOURCE_COMMIT") ?? "unrecorded",
            FantasyCommit: Environment.GetEnvironmentVariable("AINATIVE_FANTASY_COMMIT") ?? "unrecorded",
            ProtocolIdentity: Environment.GetEnvironmentVariable("AINATIVE_PROTOCOL_IDENTITY") ?? "unrecorded",
            GatesPassed: gatesPassed);
    }

    private static bool TryReadUdp(ReadOnlySpan<byte> packet, uint linkType, out UdpPacket udp)
    {
        udp = default;
        int ipOffset;
        ushort protocol;
        switch (linkType)
        {
            case 1 when packet.Length >= 14:
                protocol = BinaryPrimitives.ReadUInt16BigEndian(packet[12..]);
                ipOffset = 14;
                if (protocol == 0x8100 && packet.Length >= 18)
                {
                    protocol = BinaryPrimitives.ReadUInt16BigEndian(packet[16..]);
                    ipOffset = 18;
                }
                break;
            case 113 when packet.Length >= 16:
                protocol = BinaryPrimitives.ReadUInt16BigEndian(packet[14..]);
                ipOffset = 16;
                break;
            case 276 when packet.Length >= 20:
                protocol = BinaryPrimitives.ReadUInt16BigEndian(packet);
                ipOffset = 20;
                break;
            case 0 when packet.Length >= 4:
                protocol = 0x0800;
                ipOffset = 4;
                break;
            default:
                return false;
        }

        if (protocol != 0x0800 || packet.Length < ipOffset + 28)
        {
            return false;
        }

        ReadOnlySpan<byte> ip = packet[ipOffset..];
        int ipHeaderLength = (ip[0] & 0x0f) * 4;
        if ((ip[0] >> 4) != 4 || ipHeaderLength < 20 || ip.Length < ipHeaderLength + 8 || ip[9] != 17)
        {
            return false;
        }

        int ipTotalLength = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (ipTotalLength < ipHeaderLength + 8 || ipTotalLength > ip.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> udpHeader = ip[ipHeaderLength..];
        int udpLength = BinaryPrimitives.ReadUInt16BigEndian(udpHeader[4..]);
        if (udpLength < 8 || ipHeaderLength + udpLength > ipTotalLength)
        {
            return false;
        }

        udp = new UdpPacket(
            BinaryPrimitives.ReadUInt16BigEndian(udpHeader),
            BinaryPrimitives.ReadUInt16BigEndian(udpHeader[2..]),
            ipTotalLength,
            udpLength - 8);
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static double[] FlattenKbps(IEnumerable<long[]> windows)
    {
        double[] values = windows.SelectMany(window => window).Select(bytes => bytes * 8d / 1000d).ToArray();
        Array.Sort(values);
        return values;
    }

    private static double Percentile(double[] values, double percentile) =>
        values.Length == 0 ? 0 : values[PercentileIndex(values.Length, percentile)];

    private static int Percentile(List<int> values, double percentile) =>
        values.Count == 0 ? 0 : values[PercentileIndex(values.Count, percentile)];

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private sealed class ClientWireSamples(int durationSeconds)
    {
        public long[] DownstreamBytes { get; } = new long[durationSeconds];

        public long[] UpstreamBytes { get; } = new long[durationSeconds];
    }

    private readonly record struct UdpPacket(
        ushort SourcePort,
        ushort DestinationPort,
        int IpTotalLength,
        int UdpPayloadLength);
}

internal sealed record PcapReport(
    string EvidenceClass,
    bool QualifiedSocketImpairment,
    string ByteAccounting,
    string PcapSha256,
    uint LinkType,
    int ServerPort,
    long StartUnixMilliseconds,
    int DurationSeconds,
    int ExpectedClientCount,
    int ClientCount,
    int HeadroomPercent,
    double DownstreamP95LimitKbps,
    double UpstreamP95LimitKbps,
    double DatagramPayloadP95LimitBytes,
    long PacketCount,
    long CapturedIpBytes,
    double DownstreamP50Kbps,
    double DownstreamP95Kbps,
    double DownstreamP99Kbps,
    double UpstreamP50Kbps,
    double UpstreamP95Kbps,
    double UpstreamP99Kbps,
    int DatagramPayloadP50Bytes,
    int DatagramPayloadP95Bytes,
    int DatagramPayloadP99Bytes,
    int MaxDatagramPayloadBytes,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity,
    bool GatesPassed);
