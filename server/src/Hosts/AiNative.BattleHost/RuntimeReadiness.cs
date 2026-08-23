namespace AiNative.BattleHost;

public sealed class RuntimeReadiness
{
    private readonly bool _networkRequired;
    private int _accepting = 1;
    private int _networkReady;
    private int _roomReady;
    private long _tick;

    public RuntimeReadiness(bool networkRequired)
    {
        _networkRequired = networkRequired;
        _networkReady = networkRequired ? 0 : 1;
    }

    public bool IsReady =>
        Volatile.Read(ref _accepting) == 1 &&
        Volatile.Read(ref _roomReady) == 1 &&
        (!_networkRequired || Volatile.Read(ref _networkReady) == 1);

    public long Tick => Interlocked.Read(ref _tick);

    public void MarkRoomReady() => Volatile.Write(ref _roomReady, 1);

    public void MarkNetworkReady() => Volatile.Write(ref _networkReady, 1);

    public void BeginDrain() => Volatile.Write(ref _accepting, 0);

    public void AdvanceTick() => Interlocked.Increment(ref _tick);
}
