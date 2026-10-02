using System.Security.Cryptography;
using AiNative.Protocol.Backend.V1;
using AiNative.Server.Control;
using Google.Protobuf;

namespace AiNative.Server.Rooms;

/// <summary>Off-Tick immutable result files, retained until the receiver confirms the same payload hash.</summary>
public sealed class DurableResultOutbox
{
    private readonly string _directory;
    private readonly int _maxCount;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<string, MatchResult> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Reservation> _reserved = new(StringComparer.Ordinal);
    private long _reservedBytes, _writingBytes;
    private int _writingCount;
    private string? _storingMatch;
    private bool _releaseAfterStore;
    private long _bytes;
    private volatile bool _healthy;
    public DurableResultOutbox(string directory, int maxCount, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _directory = Path.GetFullPath(directory); _maxCount = maxCount; _maxBytes = maxBytes;
    }
    public bool CanAdmit { get { lock (_pending) return _healthy && _pending.Count + _reserved.Count + _writingCount < _maxCount && _bytes + _reservedBytes + _writingBytes < _maxBytes; } }
    public int ReservedCount { get { lock (_pending) return _reserved.Count; } }
    public long ReservedBytes { get { lock (_pending) return _reservedBytes; } }
    public bool CanAdmitRoom(string matchId) { lock (_pending) return _healthy && (_reserved.ContainsKey(matchId) || CanAdmit); }
    public bool TryReserve(RoomAllocation allocation)
    {
        if (allocation.MatchId.Length == 0 || allocation.PlayerIds.Count is < 1 or > 8 || allocation.PlayerIds.Any(string.IsNullOrWhiteSpace) || allocation.PlayerIds.Distinct(StringComparer.Ordinal).Count() != allocation.PlayerIds.Count) return false;
        // Negative int32 takes ten wire bytes, covering every possible supported kill value.
        MatchResult upper = new() { MatchId = allocation.MatchId, RoomId = allocation.RoomId, NodeId = allocation.NodeId, BootEpoch = allocation.BootEpoch, Completion = new string('x', 64) };
        upper.Players.Add(allocation.PlayerIds.Select(p => new PlayerResult { PlayerId = p, Won = true, Kills = -1 }));
        int bytes = upper.CalculateSize();
        lock (_pending)
        {
            if (_reserved.TryGetValue(allocation.MatchId, out Reservation? prior)) return SameAllocation(prior.Allocation, allocation);
            if (!_healthy || _pending.ContainsKey(allocation.MatchId) || _pending.Count + _reserved.Count + _writingCount >= _maxCount || bytes > _maxBytes - _bytes - _reservedBytes - _writingBytes) return false;
            _reserved.Add(allocation.MatchId, new(allocation.Clone(), bytes)); _reservedBytes += bytes; return true;
        }
    }
    public void ReleaseReservation(string matchId)
    {
        lock (_pending)
        {
            if (_storingMatch == matchId) { _releaseAfterStore = true; return; }
            if (_reserved.Remove(matchId, out Reservation? reserved)) _reservedBytes -= reserved.Bytes;
        }
    }
    private static bool SameAllocation(RoomAllocation a, RoomAllocation b) => a.AllocationId == b.AllocationId && a.MatchId == b.MatchId && a.RoomId == b.RoomId && a.NodeId == b.NodeId && a.BootEpoch == b.BootEpoch && a.PlayerIds.SequenceEqual(b.PlayerIds);
    private sealed record Reservation(RoomAllocation Allocation, int Bytes);
    public IReadOnlyList<MatchResult> Pending { get { lock (_pending) return _pending.Values.Select(x => x.Clone()).ToArray(); } }
    public static string Hash(MatchResult result) => ResultPayload.Hash(result);
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_directory);
            lock (_pending) { _pending.Clear(); _reserved.Clear(); _bytes = _reservedBytes = _writingBytes = 0; _writingCount = 0; }
            foreach (string path in Directory.EnumerateFiles(_directory, "*.result"))
            {
                await using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                long fileBytes = source.Length;
                lock (_pending)
                    if (_pending.Count >= _maxCount || fileBytes > _maxBytes - _bytes || fileBytes > int.MaxValue) throw new InvalidDataException("outbox-recovery-capacity-exceeded");
                byte[] bytes = new byte[(int)fileBytes];
                await source.ReadExactlyAsync(bytes, ct);
                MatchResult result = MatchResult.Parser.ParseFrom(bytes);
                if (string.IsNullOrEmpty(result.MatchId) || Path.GetFileName(path) != FileName(result.MatchId)) throw new InvalidDataException("outbox-corrupt");
                lock (_pending) { _pending.Add(result.MatchId, result); _bytes += bytes.Length; }
            }
            _healthy = true;
        }
        catch { _healthy = false; throw; }
        finally { _serial.Release(); }
    }
    public async ValueTask<bool> StoreAsync(MatchResult result, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(result.MatchId)) throw new InvalidDataException("match-identity-required");
        await _serial.WaitAsync(ct);
        try
        {
            result = ResultPayload.Canonicalize(result);
            byte[] bytes = result.ToByteArray();
            lock (_pending)
            {
                if (_pending.TryGetValue(result.MatchId, out MatchResult? previous))
                {
                    if (!previous.ToByteArray().AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("result-conflict");
                    return true;
                }
                if (_reserved.TryGetValue(result.MatchId, out Reservation? reservation))
                {
                    RoomAllocation a = reservation.Allocation;
                    if (result.RoomId != a.RoomId || result.NodeId != a.NodeId || result.BootEpoch != a.BootEpoch || !result.Players.Select(p => p.PlayerId).Order(StringComparer.Ordinal).SequenceEqual(a.PlayerIds.Order(StringComparer.Ordinal)) || bytes.Length > reservation.Bytes) throw new InvalidDataException("result-reservation-conflict");
                    // An admitted room already owns this capacity. Retry its write after an I/O outage;
                    // admission remains closed until a real durable write succeeds.
                }
                else
                {
                    if (!_healthy || _pending.Count + _reserved.Count >= _maxCount || bytes.Length > _maxBytes - _bytes - _reservedBytes) return false;
                    // Hold capacity while disk I/O runs, so concurrent advance reservations cannot steal it.
                    _writingCount = 1; _writingBytes = bytes.Length;
                }
                _storingMatch = result.MatchId;
            }
            string target = Path.Combine(_directory, FileName(result.MatchId));
            string temp = target + ".pending";
            await using (FileStream stream = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, ct); stream.Flush(flushToDisk: true); }
            File.Move(temp, target, overwrite: false);
            lock (_pending) { _pending.Add(result.MatchId, result.Clone()); _bytes += bytes.Length; if (_reserved.Remove(result.MatchId, out Reservation? reserved)) _reservedBytes -= reserved.Bytes; }
            _healthy = true;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _healthy = false; throw; }
        finally { lock (_pending) { if (_releaseAfterStore && _storingMatch is not null && _reserved.Remove(_storingMatch, out Reservation? reserved)) _reservedBytes -= reserved.Bytes; _storingMatch = null; _releaseAfterStore = false; _writingCount = 0; _writingBytes = 0; } _serial.Release(); }
    }
    public async ValueTask ConfirmAsync(string matchId, string payloadHash, CancellationToken ct = default)
    {
        await _serial.WaitAsync(ct);
        try
        {
            MatchResult? result; lock (_pending) _pending.TryGetValue(matchId, out result);
            if (result is null) return;
            if (!string.Equals(Hash(result), payloadHash, StringComparison.Ordinal)) throw new InvalidDataException("receipt-conflict");
            File.Delete(Path.Combine(_directory, FileName(matchId)));
            lock (_pending) { _pending.Remove(matchId); _bytes -= result.CalculateSize(); }
            // Deleting an older receipt does not prove that new result writes work.
            // Preserve an I/O failure latch until a durable Store or initialization succeeds.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _healthy = false; throw; }
        finally { _serial.Release(); }
    }
    public async ValueTask RetryAsync(IServiceRpc rpc, CancellationToken ct = default)
    {
        foreach (MatchResult result in Pending)
        {
            try
            {
                ServiceReply response = await rpc.CallAsync(new(ServiceRole.Player), ServiceMethods.Settle, result, ct);
                if (!response.Success) continue;
                SettlementReceipt receipt = response.Read(SettlementReceipt.Parser);
                if (receipt.Confirmed && receipt.MatchId == result.MatchId) await ConfirmAsync(result.MatchId, receipt.PayloadHash, ct);
            }
            catch (TimeoutException) { }
            catch (ServiceException ex) when (ex.Code is "unavailable" or "timeout") { }
        }
    }
    private static string FileName(string matchId) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(matchId))) + ".result";
}
