using System;
using AiNative.Protocol.V1;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ArenaCombatEventParityTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void GeneratedReliableEventMatchesRuntimeDecoder(int kind)
        {
            var message = new S2C_ReliableEventPush
            {
                RoomTick = 100,
                Sequence = 42,
                EventType = (uint)kind,
                CombatEvent = new ArenaCombatEvent
                {
                    EventType = (ArenaCombatEventType)kind,
                    EventTick = 100,
                    SourceEntityId = 1,
                    TargetEntityId = kind == 1 || kind == 2 || kind == 4 ? 2u : 0u,
                    WeaponId = (ArenaWeaponId)(kind == 4 ? 0 : 1),
                    Damage = 0,
                    PositionXMilli = int.MinValue,
                    PositionYMilli = int.MaxValue,
                    PositionZMilli = -42,
                },
            };
            byte[] payload = message.ToByteArray();
            var frame = new byte[payload.Length + 2];
            frame[0] = 0x4e;
            frame[1] = 4;
            payload.CopyTo(frame, 2);
            Assert.That(ArenaCombatEventProtocolV1.TryDecode(frame, out var result), Is.True);
            Assert.That((int)result.Kind, Is.EqualTo(kind));
            Assert.That(result.PositionXMillimetres, Is.EqualTo(int.MinValue));
            Assert.That(result.PositionYMillimetres, Is.EqualTo(int.MaxValue));
            Assert.That(result.PositionZMillimetres, Is.EqualTo(-42));
            Assert.That(result.Damage, Is.Zero);
        }
    }
}
