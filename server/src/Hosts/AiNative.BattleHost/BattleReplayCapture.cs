using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AiNative.Protocol.V1;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

internal sealed class BattleReplayCapture : IAsyncDisposable
{
    private const uint FileMagic = 0x50524E41; // ANRP in little endian.
    private const ushort FormatVersion = 1;
    private const byte InputRecord = 1;
    private const byte FooterRecord = 255;
    private readonly Channel<CaptureRecord>? _records;
    private readonly Task? _writer;
    private readonly BattleMetrics _metrics;
    private int _completed;
    private long _droppedRecords;

    public BattleReplayCapture(IConfiguration configuration, BattleMetrics metrics)
    {
        _metrics = metrics;
        string? path = configuration["AINATIVE_REPLAY_CAPTURE_PATH"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int capacity = configuration.GetValue("AINATIVE_REPLAY_CAPTURE_CAPACITY", 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ReplayIdentity identity = ReplayIdentity.FromConfiguration(configuration);
        _records = Channel.CreateBounded<CaptureRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _writer = WriteAsync(Path.GetFullPath(path), identity, _records.Reader);
    }

    public bool IsEnabled => _records is not null;

    public bool TryRecordInput(ulong roomTick, int entityIndex, ReadOnlySpan<byte> frame)
    {
        if (_records is null)
        {
            return true;
        }

        byte[] ownedFrame = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(ownedFrame);
        if (_records.Writer.TryWrite(CaptureRecord.Input(roomTick, entityIndex, ownedFrame, frame.Length)))
        {
            return true;
        }

        ArrayPool<byte>.Shared.Return(ownedFrame);
        Interlocked.Increment(ref _droppedRecords);
        _metrics.RecordReplayDropped();
        return false;
    }

    public async ValueTask CompleteAsync(ulong finalTick, ulong finalStateHash)
    {
        if (_records is null || Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        await _records.Writer.WriteAsync(CaptureRecord.Footer(
            finalTick,
            finalStateHash,
            Interlocked.Read(ref _droppedRecords)));
        _records.Writer.Complete();
        await _writer!;
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync(0, 0);
    }

    private async Task WriteAsync(string path, ReplayIdentity identity, ChannelReader<CaptureRecord> reader)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(FileMagic);
        writer.Write(FormatVersion);
        writer.Write(identity.BotCount);
        writer.Write(identity.InitialRandomState);
        WriteIdentity(writer, identity.SourceCommit);
        WriteIdentity(writer, identity.FantasyCommit);
        WriteIdentity(writer, identity.ProtocolIdentity);
        WriteIdentity(writer, identity.ConfigurationIdentity);

        await foreach (CaptureRecord record in reader.ReadAllAsync())
        {
            writer.Write(record.Kind);
            writer.Write(record.RoomTick);
            if (record.Kind == InputRecord)
            {
                try
                {
                    writer.Write(record.EntityIndex);
                    writer.Write(checked((ushort)record.FrameLength));
                    writer.Write(record.Frame.AsSpan(0, record.FrameLength));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(record.Frame);
                }
            }
            else
            {
                writer.Write(record.FinalStateHash);
                writer.Write(record.DroppedRecords);
            }
        }

        writer.Flush();
        await stream.FlushAsync();
    }

    private static void WriteIdentity(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private readonly record struct CaptureRecord(
        byte Kind,
        ulong RoomTick,
        int EntityIndex,
        byte[] Frame,
        int FrameLength,
        ulong FinalStateHash,
        long DroppedRecords)
    {
        public static CaptureRecord Input(ulong roomTick, int entityIndex, byte[] frame, int frameLength) =>
            new(InputRecord, roomTick, entityIndex, frame, frameLength, 0, 0);

        public static CaptureRecord Footer(ulong finalTick, ulong finalStateHash, long droppedRecords) =>
            new(FooterRecord, finalTick, 0, Array.Empty<byte>(), 0, finalStateHash, droppedRecords);
    }

    internal readonly record struct ReplayIdentity(
        int BotCount,
        ulong InitialRandomState,
        string SourceCommit,
        string FantasyCommit,
        string ProtocolIdentity,
        string ConfigurationIdentity)
    {
        public static ReplayIdentity FromConfiguration(IConfiguration configuration) => new(
            BotCount: 64,
            InitialRandomState: SyntheticRoom.InitialRandomState,
            RequiredIdentity(configuration, "AINATIVE_SOURCE_COMMIT"),
            RequiredIdentity(configuration, "AINATIVE_FANTASY_COMMIT"),
            RequiredIdentity(configuration, "AINATIVE_PROTOCOL_IDENTITY"),
            ResolveConfigurationIdentity(configuration));

        private static string RequiredIdentity(IConfiguration configuration, string key) =>
            configuration[key] is { Length: > 0 } value ? value : "unrecorded";

        private static string ResolveConfigurationIdentity(IConfiguration configuration)
        {
            if (configuration["AINATIVE_CONFIGURATION_IDENTITY"] is { Length: > 0 } configured)
            {
                return configured;
            }

            string path = Path.Combine(AppContext.BaseDirectory, "Fantasy.config");
            return File.Exists(path)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
                : "missing";
        }
    }
}

internal static class BattleReplayVerifier
{
    private const uint FileMagic = 0x50524E41;
    private const ushort FormatVersion = 1;
    private const byte InputRecord = 1;
    private const byte FooterRecord = 255;

    public static ReplayVerificationResult Verify(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadUInt32() != FileMagic || reader.ReadUInt16() != FormatVersion)
        {
            throw new InvalidDataException("The replay header is not a supported AI Native replay.");
        }

        int botCount = reader.ReadInt32();
        ulong initialRandomState = reader.ReadUInt64();
        string sourceCommit = ReadIdentity(reader);
        string fantasyCommit = ReadIdentity(reader);
        string protocolIdentity = ReadIdentity(reader);
        string configurationIdentity = ReadIdentity(reader);
        EnsureRecordedIdentity(nameof(sourceCommit), sourceCommit);
        EnsureRecordedIdentity(nameof(fantasyCommit), fantasyCommit);
        EnsureRecordedIdentity(nameof(protocolIdentity), protocolIdentity);
        EnsureRecordedIdentity(nameof(configurationIdentity), configurationIdentity);
        if (initialRandomState != SyntheticRoom.InitialRandomState)
        {
            throw new InvalidDataException("The replay RNG state is not supported by this simulation build.");
        }

        SyntheticRoom room = new(botCount);
        ulong simulatedTick = 0;
        long inputCount = 0;
        uint[] lastInputSequences = new uint[botCount];
        bool sawFooter = false;
        ulong finalTick = 0;
        ulong expectedHash = 0;

        while (stream.Position < stream.Length)
        {
            byte kind = reader.ReadByte();
            ulong recordTick = reader.ReadUInt64();
            if (kind == FooterRecord)
            {
                expectedHash = reader.ReadUInt64();
                long droppedRecords = reader.ReadInt64();
                if (droppedRecords != 0)
                {
                    throw new InvalidDataException($"The replay is incomplete: {droppedRecords} input records were dropped.");
                }

                finalTick = recordTick;
                sawFooter = true;
                break;
            }

            if (kind != InputRecord || recordTick < simulatedTick)
            {
                throw new InvalidDataException("The replay contains an unknown or out-of-order record.");
            }

            while (simulatedTick < recordTick)
            {
                room.Tick();
                simulatedTick++;
            }

            int entityIndex = reader.ReadInt32();
            int frameLength = reader.ReadUInt16();
            byte[] frame = reader.ReadBytes(frameLength);
            if (frame.Length != frameLength ||
                RealtimeProtocolCodec.TryDecode(frame, out DecodedProtocolMessage decoded) != ProtocolDecodeStatus.Accepted)
            {
                throw new InvalidDataException("The replay contains an invalid production Input frame.");
            }

            switch (decoded.MessageId, decoded.Message)
            {
                case (MessageId.InputCommand, InputCommand command)
                    when (uint)entityIndex < (uint)botCount &&
                         command.Sequence > lastInputSequences[entityIndex]:
                    lastInputSequences[entityIndex] = command.Sequence;
                    room.ApplyInput(entityIndex, command.MoveXMilli, command.MoveYMilli);
                    inputCount++;
                    break;
                case (MessageId.InputBatch, InputBatch batch)
                    when (uint)entityIndex < (uint)botCount && batch.Commands.Count is >= 1 and <= 2:
                    uint previousSequence = lastInputSequences[entityIndex];
                    foreach (InputCommand batchedCommand in batch.Commands)
                    {
                        if (batchedCommand.Sequence <= previousSequence)
                        {
                            throw new InvalidDataException(
                                "The replay contains an out-of-order production Input batch.");
                        }

                        previousSequence = batchedCommand.Sequence;
                        room.ApplyInput(entityIndex, batchedCommand.MoveXMilli, batchedCommand.MoveYMilli);
                        inputCount++;
                    }
                    lastInputSequences[entityIndex] = previousSequence;
                    break;
                default:
                    throw new InvalidDataException("The replay contains an invalid production Input frame.");
            }
        }

        if (!sawFooter || stream.Position != stream.Length || finalTick < simulatedTick)
        {
            throw new InvalidDataException("The replay is truncated or has trailing data.");
        }

        while (simulatedTick < finalTick)
        {
            room.Tick();
            simulatedTick++;
        }

        ulong actualHash = room.ComputeStateHash();
        if (actualHash != expectedHash)
        {
            throw new InvalidDataException(
                $"Replay state hash mismatch: expected {expectedHash:x16}, actual {actualHash:x16}.");
        }

        return new ReplayVerificationResult(
            sourceCommit,
            fantasyCommit,
            protocolIdentity,
            configurationIdentity,
            botCount,
            finalTick,
            inputCount,
            actualHash);
    }

    private static string ReadIdentity(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("The replay identity is truncated.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void EnsureRecordedIdentity(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "unrecorded" or "missing")
        {
            throw new InvalidDataException($"The replay {name} was not recorded.");
        }
    }
}

internal readonly record struct ReplayVerificationResult(
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity,
    string ConfigurationIdentity,
    int BotCount,
    ulong FinalTick,
    long InputCount,
    ulong StateHash);
