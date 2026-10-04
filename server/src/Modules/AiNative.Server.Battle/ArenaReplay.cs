using System.Text;
using AiNative.BattleHost;
using AiNative.Gameplay;
namespace AiNative.Server.Battle;

public sealed record ArenaReplayIdentity(string Source, string Fantasy, string Protocol, string Configuration);
public sealed record ArenaReplayVerification(string RoomId, string MatchId, ulong FinalTick, ulong FinalHash, long Records);
internal enum ArenaReplayStatus : byte { Capturing, Complete, Overflow, PersistenceFailure, Aborted }
internal enum ArenaReplayKind : byte { Join = 1, Input = 2, Clear = 3, Tick = 4 }
internal readonly record struct ArenaReplayRecord(ArenaReplayKind Kind, ulong Tick, uint Entity, ArenaInput Input, ulong Hash);

// One worker produces, one off-Tick pump consumes. No owned frames or dynamic buffers cross Tick.
internal sealed class ArenaReplayCapture
{
    private readonly TopologyBattleEngine.SpscRing<ArenaReplayRecord> _records;
    private int _status;
    public readonly ArenaReplayIdentity Identity;
    public readonly string Room, Allocation, Match, Node, Boot;
    public readonly int MatchLength;
    public ulong FinalTick, FinalHash;
    public long RecordCount;
    public ArenaReplayStatus Status => (ArenaReplayStatus)Volatile.Read(ref _status);
    public ArenaReplayCapture(ArenaReplayIdentity identity, string room, string allocation, string match, string node, string boot, int length, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 1_000_000);
        ArenaReplayEncoding.ValidateIdentity(identity);
        Identity = identity; Room = room; Allocation = allocation; Match = match; Node = node; Boot = boot; MatchLength = length;
        if (length < 1) throw new ArgumentException("invalid-replay-header");
        foreach (string text in new[] { room, allocation, match, node, boot }) ArenaReplayEncoding.TextLength(text);
        _records = new(capacity);
    }
    private void Record(ArenaReplayRecord record)
    {
        if (Status != ArenaReplayStatus.Capturing) return;
        if (!_records.TryWrite(record)) { Interlocked.CompareExchange(ref _status, (int)ArenaReplayStatus.Overflow, 0); return; }
        RecordCount++;
    }
    public void RecordJoin(ulong tick, uint entity) => Record(new(ArenaReplayKind.Join, tick, entity, default, 0));
    public void RecordInput(ulong tick, uint entity, in ArenaInput input) => Record(new(ArenaReplayKind.Input, tick, entity, input, 0));
    public void RecordClear(ulong tick, uint entity) => Record(new(ArenaReplayKind.Clear, tick, entity, default, 0));
    public void RecordTick(ulong tick, ulong hash) { FinalTick = tick; FinalHash = hash; Record(new(ArenaReplayKind.Tick, tick, 0, default, hash)); }
    public bool TryRead(out ArenaReplayRecord record) => _records.TryRead(out record);
    public void Complete(ulong tick, ulong hash) { FinalTick = tick; FinalHash = hash; Interlocked.CompareExchange(ref _status, (int)ArenaReplayStatus.Complete, 0); }
    public void Abort() => Interlocked.CompareExchange(ref _status, (int)ArenaReplayStatus.Aborted, 0);
    public void PersistenceFailed() => Interlocked.Exchange(ref _status, (int)ArenaReplayStatus.PersistenceFailure);
}

internal static class ArenaReplayEncoding
{
    public const uint Magic = 0x52414E41; // ANAR
    public const ushort Version = 1;
    public const string Ordering = "mutations-at-current-tick;advance-one;hash-every-tick";
    public const int MaximumStringBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static int TextLength(string value)
    {
        int length = StrictUtf8.GetByteCount(value);
        if (length is 0 or > MaximumStringBytes) throw new InvalidDataException("invalid-replay-string");
        return length;
    }
    public static void ValidateIdentity(ArenaReplayIdentity identity)
    {
        foreach (string value in new[] { identity.Source, identity.Fantasy, identity.Protocol, identity.Configuration })
            if (string.IsNullOrWhiteSpace(value) || value is "unrecorded" or "missing") throw new InvalidDataException("unrecorded-replay-identity");
        foreach (string value in new[] { identity.Source, identity.Fantasy, identity.Protocol, identity.Configuration }) TextLength(value);
    }
    public static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length is 0 or > MaximumStringBytes) throw new InvalidDataException("invalid-replay-string");
        writer.Write((ushort)bytes.Length); writer.Write(bytes);
    }
    public static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadUInt16(); if (length is 0 or > MaximumStringBytes) throw new InvalidDataException("invalid-replay-string");
        byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}

internal sealed class ArenaReplayWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly Stream _stream;
    private readonly ArenaReplayCapture _capture;
    private long _written;
    private readonly long _maximumBytes;
    private long _writtenBytes;
    private void EnsureSpace(long bytes)
    { if (bytes < 0 || _writtenBytes > _maximumBytes - bytes) throw new InvalidDataException("replay-reserved-byte-budget-exceeded"); }
    public bool IsFinalized { get; private set; }
    public ArenaReplayWriter(Stream stream, ArenaReplayCapture capture, long maximumBytes = long.MaxValue)
    {
        _maximumBytes = maximumBytes;
        string[] header = { capture.Room, capture.Allocation, capture.Match, capture.Node, capture.Boot, capture.Identity.Source, capture.Identity.Fantasy, capture.Identity.Protocol, capture.Identity.Configuration, ArenaReplayVerifier.GameplayFingerprint, ArenaReplayEncoding.Ordering };
        long headerBytes = 10; foreach (string text in header) headerBytes += 2 + ArenaReplayEncoding.TextLength(text);
        EnsureSpace(headerBytes + 26);
        _stream = stream; _capture = capture; _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        _writer.Write(ArenaReplayEncoding.Magic); _writer.Write(ArenaReplayEncoding.Version);
        foreach (string value in header) ArenaReplayEncoding.WriteString(_writer, value);
        _writer.Write(capture.MatchLength); _writtenBytes += headerBytes;
    }
    // The caller runs this outside the worker Tick. Bounded drain protects the host pump.
    public void Drain(int budget = 8192)
    {
        if (IsFinalized) return;
        for (int i = 0; i < budget && _capture.TryRead(out var record); i++)
        {
            long recordBytes = record.Kind == ArenaReplayKind.Input ? 46 : record.Kind == ArenaReplayKind.Tick ? 17 : 13;
            EnsureSpace(recordBytes + 26); _writtenBytes += recordBytes;
            _writer.Write((byte)record.Kind); _writer.Write(record.Tick); _written++;
            if (record.Kind is ArenaReplayKind.Join or ArenaReplayKind.Clear) _writer.Write(record.Entity);
            else if (record.Kind == ArenaReplayKind.Tick) _writer.Write(record.Hash);
            else
            {
                var input = record.Input; _writer.Write(record.Entity); _writer.Write(input.Sequence); _writer.Write(input.ClientTick);
                _writer.Write(input.MoveXMilli); _writer.Write(input.MoveZMilli); _writer.Write(input.LookYawMilli); _writer.Write(input.LookPitchMilli); _writer.Write((uint)input.Buttons); _writer.Write((byte)input.Weapon);
            }
        }
        var status = _capture.Status;
        if (status == ArenaReplayStatus.Capturing || _written != Volatile.Read(ref _capture.RecordCount)) return;
        EnsureSpace(26); _writtenBytes += 26;
        _writer.Write((byte)255); _writer.Write(_capture.FinalTick); _writer.Write(_capture.FinalHash); _writer.Write(_written); _writer.Write((byte)status);
        _writer.Flush();
        if (_stream is FileStream file) file.Flush(flushToDisk: true); else _stream.Flush();
        IsFinalized = true;
    }
    public void Dispose() => _writer.Dispose();
}

public static class ArenaReplayVerifier
{
    // UTF-8 without BOM, LF-normalized ArenaRoom + Shared ArenaGameplay source; independent of checkout EOL.
    public const string GameplayFingerprint = "7f368b5cddc3254c660b74e2c3c57654e16828baa0a8ca995dd1f0e310b24a2d";
    public static ArenaReplayVerification Verify(string path, ArenaReplayIdentity expected)
    { if (!path.EndsWith(".anar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("unpublished-arena-replay"); using var stream = File.OpenRead(path); return Verify(stream, expected); }
    public static ArenaReplayVerification Verify(Stream stream, ArenaReplayIdentity expected)
    {
        try { return VerifyCore(stream, expected); }
        catch (Exception e) when (e is EndOfStreamException or OverflowException or ArgumentException) { throw new InvalidDataException("Malformed or truncated Arena replay.", e); }
    }
    private static ArenaReplayVerification VerifyCore(Stream stream, ArenaReplayIdentity expected)
    {
        ArenaReplayEncoding.ValidateIdentity(expected);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != ArenaReplayEncoding.Magic || reader.ReadUInt16() != ArenaReplayEncoding.Version) throw new InvalidDataException("unsupported-arena-replay");
        string room = ArenaReplayEncoding.ReadString(reader);
        _ = ArenaReplayEncoding.ReadString(reader); // allocation
        string match = ArenaReplayEncoding.ReadString(reader); _ = ArenaReplayEncoding.ReadString(reader); _ = ArenaReplayEncoding.ReadString(reader); // node + epoch
        var actual = new ArenaReplayIdentity(ArenaReplayEncoding.ReadString(reader), ArenaReplayEncoding.ReadString(reader), ArenaReplayEncoding.ReadString(reader), ArenaReplayEncoding.ReadString(reader));
        if (actual != expected || ArenaReplayEncoding.ReadString(reader) != GameplayFingerprint || ArenaReplayEncoding.ReadString(reader) != ArenaReplayEncoding.Ordering) throw new InvalidDataException("replay-identity-or-gameplay-drift");
        int length = reader.ReadInt32(); if (length is < 1 or > 100_000_000) throw new InvalidDataException("invalid-match-length");
        var game = new ArenaRoom(length); long count = 0; ulong lastHash = game.ComputeStateHash();
        while (true)
        {
            byte kind = reader.ReadByte(); ulong tick = reader.ReadUInt64();
            if (kind == 255)
            {
                ulong hash = reader.ReadUInt64(); long records = reader.ReadInt64(); byte status = reader.ReadByte();
                if (status != (byte)ArenaReplayStatus.Complete || tick != game.Tick || hash != lastHash || hash != game.ComputeStateHash() || records != count || stream.ReadByte() != -1) throw new InvalidDataException("incomplete-or-invalid-replay-footer");
                return new(room, match, tick, hash, count);
            }
            if (++count > 100_000_000) throw new InvalidDataException("replay-record-limit");
            if (kind == (byte)ArenaReplayKind.Tick)
            {
                if (tick != game.Tick + 1) throw new InvalidDataException("missing-or-reordered-tick");
                game.TickOnce(); lastHash = reader.ReadUInt64(); if (lastHash != game.ComputeStateHash()) throw new InvalidDataException("arena-state-hash-drift");
                continue;
            }
            if (tick != game.Tick) throw new InvalidDataException("reordered-mutation");
            uint entity = reader.ReadUInt32(); if (entity is < 1 or > ArenaRoom.MaxPlayers) throw new InvalidDataException("invalid-replay-entity");
            switch ((ArenaReplayKind)kind)
            {
                case ArenaReplayKind.Join:
                    if (!game.TryJoin(out uint assigned) || assigned != entity) throw new InvalidDataException("invalid-replay-join"); break;
                case ArenaReplayKind.Clear:
                    if (!game.TryGetPlayer(entity, out _)) throw new InvalidDataException("invalid-replay-clear"); game.ClearPendingInputs(entity); break;
                case ArenaReplayKind.Input:
                    var input = new ArenaInput(reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), (ArenaButtons)reader.ReadUInt32(), (ArenaWeaponId)reader.ReadByte());
                    if (!game.SubmitInput(entity, input)) throw new InvalidDataException("invalid-replay-input"); break;
                default: throw new InvalidDataException("unknown-replay-record");
            }
        }
    }
}
