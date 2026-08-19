namespace AiNative.BattleHost;

public sealed class RuntimeReadiness
{
    private int _ready;
    private long _tick;

    public bool IsReady => Volatile.Read(ref _ready) == 1;

    public long Tick => Interlocked.Read(ref _tick);

    public void MarkReady() => Volatile.Write(ref _ready, 1);

    public void BeginDrain() => Volatile.Write(ref _ready, 0);

    public void AdvanceTick() => Interlocked.Increment(ref _tick);
}
