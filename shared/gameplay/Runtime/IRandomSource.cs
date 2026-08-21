namespace AiNative.Gameplay
{
    public interface IRandomSource
    {
        uint NextUInt32();

        RandomState CaptureState();

        void RestoreState(in RandomState state);
    }
}
