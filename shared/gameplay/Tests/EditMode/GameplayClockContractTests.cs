using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class GameplayClockContractTests
    {
        [Test]
        public void ClockExposesCommittedTickAndFixedDelta()
        {
            IGameplayClock clock = new FakeGameplayClock(42, 1f / 60f);

            Assert.That(clock.Tick, Is.EqualTo(42));
            Assert.That(clock.FixedDeltaSeconds, Is.EqualTo(1f / 60f));
        }

        private sealed class FakeGameplayClock : IGameplayClock
        {
            public FakeGameplayClock(long tick, float fixedDeltaSeconds)
            {
                Tick = tick;
                FixedDeltaSeconds = fixedDeltaSeconds;
            }

            public long Tick { get; }

            public float FixedDeltaSeconds { get; }
        }
    }
}
