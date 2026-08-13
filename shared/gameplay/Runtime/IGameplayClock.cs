namespace AiNative.Gameplay
{
    /// <summary>
    /// Exposes fixed-step simulation time for one simulation epoch.
    /// </summary>
    public interface IGameplayClock
    {
        /// <summary>
        /// Gets the monotonically increasing committed simulation tick.
        /// </summary>
        long Tick { get; }

        /// <summary>
        /// Gets the constant duration of one simulation tick in seconds.
        /// </summary>
        float FixedDeltaSeconds { get; }
    }
}
