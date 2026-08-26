using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AiNative.Protocol.V1;
using AiNative.Server.Protocol;

namespace AiNative.BattleHost;

internal sealed class BattleReplayCapture : IAsyncDisposable
{
    private const uint FileMagic = 0x50524E41; // ANRP in little endian.
    private const ushort FormatVersion = 2;
    private const byte InputRecord = 1;
    private const byte FooterRecord = 255;
    private readonly Channel<CaptureRecord>? _records;
    private readonly Task? _writer;
    private readonly BattleMetrics _metrics;
    private readonly int _roomCount;
    private int _completed;
    private long _droppedRecords;

    public BattleReplayCapture(
        IConfiguration configuration,
        BattleHostCapacitySettings capacitySettings,
        BattleMetrics metrics)
    {
        _metrics = metrics;
        _roomCount = capacitySettings.RoomCount;
        string? path = configuration["AINATIVE_REPLAY_CAPTURE_PATH"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int capacity = configuration.GetValue("AINATIVE_REPLAY_CAPTURE_CAPACITY", 65_536);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ReplayIdentity identity = ReplayIdentity.FromConfiguration(configuration, capacitySettings);
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

    public bool TryRecordInput(
        int roomIndex,
        ulong roomTick,
        int entityIndex,
        ReadOnlySpan<byte> frame)
    {
        if (_records is null)
        {
            return true;
        }

        if ((uint)roomIndex >= (uint)_roomCount)
        {
            throw new ArgumentOutOfRangeException(nameof(roomIndex));
        }

        byte[] ownedFrame = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(ownedFrame);
        if (_records.Writer.TryWrite(CaptureRecord.Input(
                roomIndex,
                roomTick,
                entityIndex,
                ownedFrame,
                frame.Length)))
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
        writer.Write(identity.RoomCount);
        writer.Write(identity.BotsPerRoom);
        writer.Write(identity.InitialRandomState);
        WriteIdentity(writer, identity.SourceCommit);
        WriteIdentity(writer, identity.FantasyCommit);
        WriteIdentity(writer, identity.ProtocolIdentity);
        WriteIdentity(writer, identity.ConfigurationIdentity);

        await foreach (CaptureRecord record in reader.ReadAllAsync())
        {
            writer.Write(record.Kind);
            if (record.Kind == InputRecord)
            {
                writer.Write(record.RoomIndex);
            }

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
        int RoomIndex,
        ulong RoomTick,
        int EntityIndex,
        byte[] Frame,
        int FrameLength,
        ulong FinalStateHash,
        long DroppedRecords)
    {
        public static CaptureRecord Input(
            int roomIndex,
            ulong roomTick,
            int entityIndex,
            byte[] frame,
            int frameLength) =>
            new(InputRecord, roomIndex, roomTick, entityIndex, frame, frameLength, 0, 0);

        public static CaptureRecord Footer(ulong finalTick, ulong finalStateHash, long droppedRecords) =>
            new(FooterRecord, 0, finalTick, 0, Array.Empty<byte>(), 0, finalStateHash, droppedRecords);
    }

    internal readonly record struct ReplayIdentity(
        int RoomCount,
        int BotsPerRoom,
        ulong InitialRandomState,
        string SourceCommit,
        string FantasyCommit,
        string ProtocolIdentity,
        string ConfigurationIdentity)
    {
        public static ReplayIdentity FromConfiguration(
            IConfiguration configuration,
            BattleHostCapacitySettings capacitySettings) => new(
            capacitySettings.RoomCount,
            BattleHostCapacitySettings.BotsPerRoom,
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
    private const ushort LegacyFormatVersion = 1;
    private const ushort RoomAwareFormatVersion = 2;
    private const byte InputRecord = 1;
    private const byte FooterRecord = 255;

    public static ReplayVerificationResult Verify(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        uint magic = reader.ReadUInt32();
        ushort formatVersion = reader.ReadUInt16();
        if (magic != FileMagic || formatVersion is not (LegacyFormatVersion or RoomAwareFormatVersion))
        {
            throw new InvalidDataException("The replay header is not a supported AI Native replay.");
        }

        int roomCount;
        int botsPerRoom;
        if (formatVersion == LegacyFormatVersion)
        {
            roomCount = 1;
            botsPerRoom = reader.ReadInt32();
        }
        else
        {
            roomCount = reader.ReadInt32();
            botsPerRoom = reader.ReadInt32();
        }

        if (roomCount is < 1 or > BattleHostCapacitySettings.MaximumEvaluationRooms ||
            botsPerRoom != BattleHostCapacitySettings.BotsPerRoom)
        {
            throw new InvalidDataException("The replay room topology is not supported by this simulation build.");
        }

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

        SyntheticRoom[] rooms = Enumerable.Range(0, roomCount)
            .Select(_ => new SyntheticRoom(botsPerRoom))
            .ToArray();
        ulong simulatedTick = 0;
        long inputCount = 0;
        uint[][] lastInputSequences = Enumerable.Range(0, roomCount)
            .Select(_ => new uint[botsPerRoom])
            .ToArray();
        bool sawFooter = false;
        ulong finalTick = 0;
        ulong expectedHash = 0;

        while (stream.Position < stream.Length)
        {
            byte kind = reader.ReadByte();
            if (kind is not (InputRecord or FooterRecord))
            {
                throw new InvalidDataException("The replay contains an unknown record.");
            }

            int roomIndex = kind == InputRecord && formatVersion == RoomAwareFormatVersion
                ? reader.ReadInt32()
                : 0;
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

            if ((uint)roomIndex >= (uint)roomCount || recordTick < simulatedTick)
            {
                throw new InvalidDataException("The replay contains an invalid room or out-of-order record.");
            }

            while (simulatedTick < recordTick)
            {
                foreach (SyntheticRoom room in rooms)
                {
                    room.Tick();
                }

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
                    when (uint)entityIndex < (uint)botsPerRoom &&
                         command.Sequence > lastInputSequences[roomIndex][entityIndex]:
                    lastInputSequences[roomIndex][entityIndex] = command.Sequence;
                    rooms[roomIndex].ApplyInput(entityIndex, command.MoveXMilli, command.MoveYMilli);
                    inputCount++;
                    break;
                case (MessageId.InputBatch, InputBatch batch)
                    when (uint)entityIndex < (uint)botsPerRoom && batch.Commands.Count is >= 1 and <= 2:
                    uint previousSequence = lastInputSequences[roomIndex][entityIndex];
                    foreach (InputCommand batchedCommand in batch.Commands)
                    {
                        if (batchedCommand.Sequence <= previousSequence)
                        {
                            throw new InvalidDataException(
                                "The replay contains an out-of-order production Input batch.");
                        }

                        previousSequence = batchedCommand.Sequence;
                        rooms[roomIndex].ApplyInput(
                            entityIndex,
                            batchedCommand.MoveXMilli,
                            batchedCommand.MoveYMilli);
                        inputCount++;
                    }
                    lastInputSequences[roomIndex][entityIndex] = previousSequence;
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
            foreach (SyntheticRoom room in rooms)
            {
                room.Tick();
            }

            simulatedTick++;
        }

        ulong actualHash = BattleRoomSet.ComputeCombinedStateHash(rooms);
        if (actualHash != expectedHash)
        {
            throw new InvalidDataException(
                $"Replay state hash mismatch: expected {expectedHash:x16}, actual {actualHash:x16}.");
        }

        return new ReplayVerificationResult(
            formatVersion,
            sourceCommit,
            fantasyCommit,
            protocolIdentity,
            configurationIdentity,
            roomCount,
            botsPerRoom,
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
    ushort FormatVersion,
    string SourceCommit,
    string FantasyCommit,
    string ProtocolIdentity,
    string ConfigurationIdentity,
    int RoomCount,
    int BotsPerRoom,
    ulong FinalTick,
    long InputCount,
    ulong StateHash)
{
    public int BotCount => BotsPerRoom;

    public string StateHashHex => StateHash.ToString("x16", CultureInfo.InvariantCulture);
}
