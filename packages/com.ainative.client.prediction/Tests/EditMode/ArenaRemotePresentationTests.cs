using AiNative.Client.Protocol.Protobuf;
using AiNative.Client.Protocol;
using System;
using System.Collections.Generic;
using NUnit.Framework;
using AiNative.Gameplay;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ArenaRemotePresentationTests
    {
        private static bool Apply(ArenaRemotePresentation view, byte[] frame, uint entity)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var decoded = new BattlePacketBuffer();
            return codec.TryDecode(frame, decoded) == BattleDecodeStatus.Accepted &&
                view.ApplySnapshot(decoded.PlayerSpan, (long)decoded.Tick, entity);
        }

        [Test]
        public void LegacyHealthFieldDoesNotSelectArenaDecoding()
        {
            byte[] frame = Frame(100, new byte[] { 0x08, 1, 0x30, 100 });
            Assert.That(ArenaClientProtocolV1.TryDecodeSnapshot(frame, 1, out var decoded), Is.True);
            Assert.That(decoded.HasArenaData, Is.False, "Health is legacy field 6, not an Arena extension.");
        }

        [Test]
        public void UnknownAdditivePlayerFieldDoesNotSelectArenaDecoding()
        {
            byte[] frame = Frame(100, new byte[] { 0x08, 1, 0x30, 100, 0xa0, 1, 7 });
            Assert.That(ArenaClientProtocolV1.TryDecodeSnapshot(frame, 1, out var decoded), Is.True);
            Assert.That(decoded.HasArenaData, Is.False, "Unknown additive field 20 must be skipped without changing the protocol path.");
        }

        [Test]
        public void InterpolatesDelayedPositionAndShortestYawAndHoldsOnStarvation()
        {
            var view = new ArenaRemotePresentation();
            Assert.That(Apply(view, Frame(100, Player(1, 999), Player(2, 0, 359000)), 1), Is.True);
            Assert.That(Apply(view, Frame(112, Player(1, 999), Player(2, 1200, 1000)), 1), Is.True);
            var output = new ArenaRemotePose[8];
            Assert.That(view.Advance(0, output), Is.EqualTo(1));
            Assert.That(output[0].EntityId, Is.EqualTo(2));
            Assert.That(output[0].XMillimetres, Is.EqualTo(600).Within(0.01));
            Assert.That(output[0].YawMillidegrees % 360000, Is.EqualTo(0).Within(0.01));
            view.Advance(1, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(1200));
            view.Advance(100, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(1200));
        }

        [Test]
        public void AnimationSampleInterpolatesMotionWithoutEarlyDiscreteTransitions()
        {
            var view = new ArenaRemotePresentation();
            var output = new ArenaRemotePose[8];
            Apply(view, Frame(100, AnimatedPlayer(2, 0, 0, 1000, 2000, 100, 1)), 1);
            Apply(view, Frame(112, AnimatedPlayer(2, 1200, 600, 3000, 10000, 40, 2)), 1);
            view.Advance(0, output);
            ArenaPlayerState state = output[0].State;
            Assert.That(state.PositionXMillimetres, Is.EqualTo(600));
            Assert.That(state.PositionYMillimetres, Is.EqualTo(300));
            Assert.That(state.VelocityXMillimetresPerSecond, Is.EqualTo(2000));
            Assert.That(state.PitchMillidegrees, Is.EqualTo(6000));
            Assert.That(state.Grounded, Is.True);
            Assert.That(state.Health, Is.EqualTo(100));
            Assert.That(state.Weapon, Is.EqualTo(ArenaWeaponId.Machinegun));
            view.Advance(1, output);
            state = output[0].State;
            Assert.That(state.Grounded, Is.False);
            Assert.That(state.Health, Is.EqualTo(40));
            Assert.That(state.Weapon, Is.EqualTo(ArenaWeaponId.Shotgun));
            Assert.That(state.VelocityXMillimetresPerSecond, Is.EqualTo(3000));
            view.Advance(1, output);
            Assert.That(output[0].State, Is.EqualTo(state), "Starvation holds the full animation sample.");
        }

        [Test]
        public void LegacyPoseConstructorProvidesConsistentPresentationState()
        {
            var pose = new ArenaRemotePose(2, 100, 200, 300, 90000, false);
            Assert.That(pose.Alive, Is.False);
            Assert.That(pose.State.Alive, Is.False);
            Assert.That(pose.State.PositionXMillimetres, Is.EqualTo(100));
            Assert.That(pose.State.YawMillidegrees, Is.EqualTo(90000));
        }

        private static byte[] AnimatedPlayer(uint id, int x, int y, int vx, int pitch, uint health, uint weapon)
        {
            var data = new List<byte> { 0x08 }; Varint(data, id);
            data.Add(0x10); Varint(data, (uint)((x << 1) ^ (x >> 31)));
            data.Add(0x18); Varint(data, (uint)((y << 1) ^ (y >> 31)));
            data.Add(0x30); Varint(data, health);
            data.Add(0x38); Varint(data, (uint)((vx << 1) ^ (vx >> 31)));
            data.Add(0x50); Varint(data, weapon);
            data.Add(0x60); Varint(data, 1);
            data.Add(0x70); Varint(data, (uint)((pitch << 1) ^ (pitch >> 31)));
            return data.ToArray();
        }

        [Test]
        public void RejectsDuplicateStaleMalformedAndOverCapacityWithoutPartialMutation()
        {
            var view = new ArenaRemotePresentation();
            Apply(view, Frame(10, Player(2, 100)), 1);
            Assert.That(Apply(view, Frame(10, Player(2, 999)), 1), Is.False);
            Assert.That(Apply(view, Frame(9, Player(2, 999)), 1), Is.False);
            Assert.That(Apply(view, Frame(11, Player(2, 999), Player(2, 1000)), 1), Is.False);
            var broken = Frame(11, Player(2, 999));
            Array.Resize(ref broken, broken.Length - 1);
            Assert.That(Apply(view, broken, 1), Is.False);
            var tooMany = new byte[9][];
            for (int i = 0; i < tooMany.Length; i++) tooMany[i] = Player((uint)i + 1, 0);
            Assert.That(Apply(view, Frame(12, tooMany), 1), Is.False);
            var output = new ArenaRemotePose[8];
            Assert.That(view.Advance(0, output), Is.EqualTo(1));
            Assert.That(output[0].XMillimetres, Is.EqualTo(100));
        }

        [Test]
        public void RemovesAbsentPlayersSnapsTeleportAndRespawnAndResetsRoom()
        {
            var view = new ArenaRemotePresentation();
            var output = new ArenaRemotePose[8];
            Apply(view, Frame(100, Player(2, 0), Player(3, 300)), 1);
            Apply(view, Frame(112, Player(2, 20000)), 1);
            Assert.That(view.Advance(0, output), Is.EqualTo(1));
            Assert.That(output[0].XMillimetres, Is.EqualTo(20000));
            Apply(view, Frame(124, Player(2, 20000, alive: false)), 1);
            view.Advance(0, output);
            Assert.That(output[0].Alive, Is.False);
            Assert.That(output[0].State.Alive, Is.False);
            Assert.That(output[0].State.Tick, Is.EqualTo(124));
            Apply(view, Frame(136, Player(2, 21000)), 1);
            view.Advance(0, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(21000));
            Assert.That(output[0].State.Alive, Is.True);
            Assert.That(output[0].State.PositionXMillimetres, Is.EqualTo(21000));
            view.Reset();
            Assert.That(view.Advance(0, output), Is.Zero);
            Assert.That(Apply(view, Frame(1, Player(4, 800)), 1), Is.True);
        }

        [Test]
        public void DecoderDoesNotWriteCallerBufferOnFailure()
        {
            var players = new ArenaSnapshotPlayer[8];
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(Frame(10, Player(2, 100)), players, out int count, out long tick), Is.True);
            Assert.That(count, Is.EqualTo(1)); Assert.That(tick, Is.EqualTo(10));
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(Frame(11, Player(3, 200), Player(3, 300)), players, out count, out tick), Is.False);
            Assert.That(players[0].EntityId, Is.EqualTo(2));
            Assert.That(count, Is.Zero);
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(Frame(11, Player(2, 0), Player(3, 0)), players.AsSpan(0, 1), out count, out tick), Is.False);
        }

        [Test]
        public void HistoryWrapAndInvalidRenderTimeStayBounded()
        {
            var view = new ArenaRemotePresentation();
            for (int i = 1; i <= 100; i++) Apply(view, Frame((ulong)i * 3, Player(2, i * 30)), 1);
            var output = new ArenaRemotePose[8];
            view.Advance(float.NaN, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(2940).Within(0.01));
            view.Advance(float.PositiveInfinity, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(2940).Within(0.01));
        }

        [Test]
        public void StarvationRebuffersThenRestoresDelayedContinuousMotion()
        {
            var view = new ArenaRemotePresentation();
            var output = new ArenaRemotePose[8];
            Apply(view, Frame(100, Player(2, 1000)), 1);
            view.Advance(1, output);
            Apply(view, Frame(103, Player(2, 1030)), 1);
            view.Advance(1f / 60, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(1000), "Hold while restoring the six-tick buffer.");
            Apply(view, Frame(106, Player(2, 1060)), 1);
            view.Advance(1f / 60, output);
            Assert.That(output[0].XMillimetres, Is.EqualTo(1010).Within(0.01));
            double previous = output[0].XMillimetres;
            for (int i = 1; i <= 60; i++)
            {
                if (i % 3 == 0) Apply(view, Frame((ulong)(106 + i), Player(2, 1060 + i * 10)), 1);
                view.Advance(1f / 60, output);
                Assert.That(output[0].XMillimetres, Is.GreaterThan(previous));
                Assert.That(output[0].XMillimetres, Is.LessThan(1060 + i * 10 - 20));
                previous = output[0].XMillimetres;
            }
        }

        [Test]
        public void SteadyStateDecodedHistoryWrapAllocateNothing()
        {
            var view = new ArenaRemotePresentation();
            var output = new ArenaRemotePose[8];
            byte[] frame = Frame(1, Player(1, 0), Player(2, 100));
            var codec = new ProtobufBattleProtocolCodec();
            var decoded = new BattlePacketBuffer();
            Assert.That(codec.TryDecode(frame, decoded), Is.EqualTo(BattleDecodeStatus.Accepted));
            for (ulong tick = 1; tick <= 100; tick++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5, 8), tick);
                view.ApplySnapshot(decoded.PlayerSpan, (long)tick, 1); view.Advance(1f / 60, output);
            }

            using var allocationMeter = new CalibratedAllocationMeter(); allocationMeter.RequireSupported(); allocationMeter.Begin();
            bool accepted = true;
            for (ulong tick = 101; tick <= 10100; tick++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5, 8), tick);
                accepted &= view.ApplySnapshot(decoded.PlayerSpan, (long)tick, 1); view.Advance(1f / 60, output);
            }
            long allocated = allocationMeter.End();
            Assert.That(accepted, Is.True); Assert.That(allocated, Is.Zero);
            Assert.That(output[0].EntityId, Is.EqualTo(2));
        }

        [Test]
        public void WrongWireDuplicateScalarAndZeroIdentityFailClosed()
        {
            var players = new ArenaSnapshotPlayer[8];
            byte[] badWire = Frame(10, Player(2, 100)); badWire[2] = 0x0a;
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(badWire, players, out _, out _), Is.False);
            var duplicated = new List<byte>(Frame(10, Player(2, 100))); duplicated.AddRange(new byte[] { 8, 1 });
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(duplicated.ToArray(), players, out _, out _), Is.False);
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(Frame(10, Player(0, 100)), players, out _, out _), Is.False);
            var duplicateId = new List<byte>(Player(2, 100)); duplicateId.AddRange(new byte[] { 8, 3 });
            Assert.That(ArenaClientProtocolV1.TryDecodePlayers(Frame(10, duplicateId.ToArray()), players, out _, out _), Is.False);
            var view = new ArenaRemotePresentation();
            Assert.Throws<ArgumentException>(() => view.Advance(0, new ArenaRemotePose[7]));
        }
        internal static byte[] Frame(ulong tick, params byte[][] players)
        {
            var data = new List<byte> { 0x4d, 0x04, 0x08, 1, 0x11 };
            data.AddRange(BitConverter.GetBytes(tick));
            foreach (var player in players) { data.Add(0x22); Varint(data, (uint)player.Length); data.AddRange(player); }
            return data.ToArray();
        }
        internal static byte[] Player(uint id, int x, int yaw = 0, bool alive = true)
        {
            var data = new List<byte> { 0x08 }; Varint(data, id);
            data.Add(0x10); Varint(data, (uint)((x << 1) ^ (x >> 31)));
            data.Add(0x28); Varint(data, (uint)((yaw << 1) ^ (yaw >> 31)));
            data.Add(0x30); Varint(data, 100);
            data.Add(0x60); Varint(data, alive ? 1u : 0u);
            return data.ToArray();
        }
        private static void Varint(List<byte> data, uint value)
        {
            do { byte next = (byte)(value & 127); value >>= 7; data.Add((byte)(next | (value == 0 ? 0 : 128))); } while (value != 0);
        }
    }
}
