using System;
using System.Buffers.Binary;
using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class AcceptanceSimulationVectorTests
    {
        private const int BotCount = 64;
        private const int TickCount = 3600;

        [Test]
        public void SixtyFourBotMovementAndFireVectorIsReplayable()
        {
            AcceptanceSimulation first = new AcceptanceSimulation(BotCount, 0x5eedUL);
            AcceptanceSimulation replay = new AcceptanceSimulation(BotCount, 0x5eedUL);

            for (int tick = 0; tick < TickCount; tick++)
            {
                first.Tick(tick);
                replay.Tick(tick);
            }

            byte[] firstState = first.WriteCanonicalState(TickCount);
            byte[] replayState = replay.WriteCanonicalState(TickCount);
            XxHash64StateHasher hasher = new XxHash64StateHasher();

            Assert.That(replayState, Is.EqualTo(firstState));
            Assert.That(hasher.ComputeHash(firstState), Is.EqualTo(0xbcf2dd42887f4f4eUL));
        }

        private sealed class AcceptanceSimulation
        {
            private const int StateHeaderSize = 28;
            private const int BotStateSize = 20;
            private readonly BotState[] _bots;
            private readonly Pcg32Random _random;

            public AcceptanceSimulation(int botCount, ulong seed)
            {
                _bots = new BotState[botCount];
                _random = new Pcg32Random(seed, 17);

                for (int index = 0; index < _bots.Length; index++)
                {
                    _bots[index] = new BotState
                    {
                        EntityId = (uint)(index + 1),
                        PositionXMillimetres = (int)(_random.NextUInt32() % 20001) - 10000,
                        PositionZMillimetres = (int)(_random.NextUInt32() % 20001) - 10000,
                        Health = 100,
                    };
                }
            }

            public void Tick(int tick)
            {
                for (int index = 0; index < _bots.Length; index++)
                {
                    ref BotState bot = ref _bots[index];
                    bot.PositionXMillimetres += (int)(_random.NextUInt32() % 7) - 3;
                    bot.PositionZMillimetres += (int)(_random.NextUInt32() % 7) - 3;

                    if ((tick + index) % 15 == 0)
                    {
                        int targetIndex = (index + 1 + (int)(_random.NextUInt32() % 63)) % _bots.Length;
                        ref BotState target = ref _bots[targetIndex];
                        target.Health = target.Health > 0 ? target.Health - 1 : 100;
                        bot.ShotsFired++;
                    }
                }
            }

            public byte[] WriteCanonicalState(long tick)
            {
                byte[] bytes = new byte[StateHeaderSize + (_bots.Length * BotStateSize)];
                Span<byte> destination = bytes;
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), 1);
                BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(4, 8), tick);
                RandomState randomState = _random.CaptureState();
                BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(12, 8), randomState.State);
                BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(20, 8), randomState.Stream);

                int offset = StateHeaderSize;
                for (int index = 0; index < _bots.Length; index++)
                {
                    BotState bot = _bots[index];
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), bot.EntityId);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 4, 4), bot.PositionXMillimetres);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 8, 4), bot.PositionZMillimetres);
                    BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset + 12, 4), bot.Health);
                    BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset + 16, 4), bot.ShotsFired);
                    offset += BotStateSize;
                }

                return bytes;
            }

            private struct BotState
            {
                public uint EntityId;
                public int PositionXMillimetres;
                public int PositionZMillimetres;
                public int Health;
                public uint ShotsFired;
            }
        }
    }
}
