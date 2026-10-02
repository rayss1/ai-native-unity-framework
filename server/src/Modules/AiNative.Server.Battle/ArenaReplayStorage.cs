using System.Text.RegularExpressions;
namespace AiNative.Server.Battle;

// Initialization, admission and completion only; never called from a room Tick.
internal sealed class ArenaReplayStorage
{
    public const int DefaultMaximumFiles = 1024;
    public const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _reservations = new(StringComparer.Ordinal);
    private readonly int _maximumFiles;
    private readonly long _maximumBytes;
    private int _files;
    private long _bytes;
    public bool Healthy { get; private set; } = true;
    public ArenaReplayStorage(string directory, int maximumFiles, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFiles, 1); ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        _maximumFiles = maximumFiles; _maximumBytes = maximumBytes;
        try
        {
            if (!Directory.Exists(directory)) { if (File.Exists(directory)) Healthy = false; return; }
            var ownName = new Regex("^[0-9a-f]{64}\\.anar(?:\\.pending|\\.incomplete)?$", RegexOptions.CultureInvariant);
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (!ownName.IsMatch(Path.GetFileName(path))) continue;
                _files = checked(_files + 1); _bytes = checked(_bytes + new FileInfo(path).Length);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException) { Healthy = false; }
    }
    public static long ReservationBytes(int matchLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(matchLength, 1);
        // 128 commands: each emits at most one record, plus at most eight first joins.
        // Include the per-Tick hash and reserve the largest (46-byte) encoding for every record.
        return checked(45088L + 26L + ((long)matchLength + 2701) * (128 + 8 + 1) * 46);
    }
    public bool CanReserve(long bytes) { lock (_gate) return Healthy && _files < _maximumFiles && bytes <= _maximumBytes - _bytes; }
    public int Files { get { lock (_gate) return _files; } }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int ActiveReservations { get { lock (_gate) return _reservations.Count; } }
    public bool TryReserve(string key, long bytes)
    {
        lock (_gate)
        {
            if (!Healthy || bytes < 0 || _reservations.ContainsKey(key) || _files >= _maximumFiles || bytes > _maximumBytes - _bytes) return false;
            _reservations.Add(key, bytes); _files++; _bytes += bytes; return true;
        }
    }
    public void ReleaseUncreated(string key)
    {
        lock (_gate) if (_reservations.Remove(key, out long bytes)) { _files--; _bytes -= bytes; }
    }
    public void Complete(string key, long actualBytes)
    {
        lock (_gate)
        {
            if (!_reservations.Remove(key, out long budget)) return;
            if (actualBytes < 0 || actualBytes > budget) { Healthy = false; return; }
            _bytes -= budget - actualBytes;
        }
    }
}
