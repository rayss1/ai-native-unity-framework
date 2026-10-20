using System;
using System.Buffers.Binary;
using System.Diagnostics;
using AiNative.Client.Protocol;
using AiNative.Client.Protocol.Protobuf;
using AiNative.Gameplay;
using AiNative.Realtime;
using Google.Protobuf;
using NUnit.Framework;
using Wire = AiNative.Protocol.V1;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class ProtobufBattleProtocolCodecTests
    {
        private static byte[] Frame(ushort id, IMessage message)
        {
            byte[] bytes = new byte[message.CalculateSize() + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, id);
            message.WriteTo(bytes.AsSpan(2));
            return bytes;
        }

        private static Wire.Snapshot Snapshot(int count = 1, bool arena = true)
        {
            var snapshot = new Wire.Snapshot
            {
                ProtocolMajor = 1,
                RoomTick = 100,
                LastProcessedInputSequence = 42,
            };
            for (int i = 1; i <= count; i++)
                snapshot.Players.Add(
                    new Wire.PlayerState
                    {
                        EntityId = (uint)i,
                        Health = 100,
                        PositionXMilli = -i,
                        WeaponId = arena ? 1u : 0u,
                        Alive = arena,
                    }
                );
            return snapshot;
        }

        [Test]
        public void InputEncodingAllocatesZeroAfterWarmupWithCalibratedMeter()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var input = new ArenaInput(
                42,
                100,
                -1000,
                1000,
                -400,
                500,
                ArenaButtons.Fire,
                ArenaWeaponId.Shotgun
            );
            byte[] frame = new byte[64];
            for (int i = 0; i < 10000; i++)
                codec.TryEncodeInput(input, frame, out _);
            using var allocationMeter = new CalibratedAllocationMeter();
            allocationMeter.RequireSupported();
            allocationMeter.Begin();
            int written = 0;
            bool success = true;
            for (int i = 0; i < 10000; i++)
                success &= codec.TryEncodeInput(input, frame, out written);
            long allocated = allocationMeter.End();
            Assert.That(success, Is.True);
            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void InputEncodingIsCompatibleAndShortDestinationIsUnchanged()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var input = new ArenaInput(
                42,
                100,
                -1000,
                1000,
                -400,
                500,
                ArenaButtons.Fire,
                ArenaWeaponId.Shotgun
            );
            byte[] frame = new byte[64];
            Assert.That(codec.TryEncodeInput(input, frame, out int written), Is.True);
            var message = Wire.InputCommand.Parser.ParseFrom(frame.AsSpan(2, written - 2));
            Assert.That(message.Sequence, Is.EqualTo(42));
            Assert.That(message.MoveXMilli, Is.EqualTo(-1000));
            Assert.That(message.LookPitchMilli, Is.EqualTo(500));
            Assert.That(message.WeaponId, Is.EqualTo(2));
            byte[] tiny = { 22, 33 };
            Assert.That(codec.TryEncodeInput(input, tiny, out written), Is.False);
            Assert.That(written, Is.Zero);
            Assert.That(tiny, Is.EqualTo(new byte[] { 22, 33 }));
        }

        [Test]
        public void TenThousandDomainCopyAndPredictionRemoteDispatchCyclesAllocateZero()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var decoded = new BattlePacketBuffer();
            Assert.That(
                codec.TryDecode(Frame(1101, Snapshot(8)), decoded),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            var dispatch = new BattlePacketBuffer
            {
                Kind = BattleMessageKind.Snapshot,
                HasArenaData = true,
                PlayerCount = decoded.PlayerCount,
                Acknowledgement = decoded.Acknowledgement,
            };
            var prediction = new ArenaClientPredictionAdapter(
                new FakeRealtimeTransport(),
                codec,
                1
            );
            var remote = new ArenaRemotePresentation();
            var poses = new ArenaRemotePose[8];
            var packet = new ReceivedPacket(codec.SnapshotChannel, 1, 1, 1, 1);
            for (int i = 0; i < 1000; i++)
                CopyAndDispatch(decoded, dispatch, prediction, remote, poses, packet, i);

            using var allocationMeter = new CalibratedAllocationMeter();
            allocationMeter.RequireSupported();
            allocationMeter.Begin();
            int presentations = 0;
            for (int i = 1000; i < 11000; i++)
                presentations += CopyAndDispatch(
                    decoded,
                    dispatch,
                    prediction,
                    remote,
                    poses,
                    packet,
                    i
                );
            long allocated = allocationMeter.End();
            Assert.That(
                allocated,
                Is.Zero,
                "Generated parsing was completed before measurement; domain copy and both consumers allocate nothing."
            );
            Assert.That(presentations, Is.EqualTo(70000));
            Assert.That(prediction.Current.Tick, Is.EqualTo(11099));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedInputNearTickLimitFailsBeforeEncodingOrPrediction(bool arena)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var decoded = new BattlePacketBuffer();
            var source = Snapshot(1, arena);
            source.RoomTick = (ulong)long.MaxValue - 2;
            codec.TryDecode(Frame(1101, source), decoded);
            var packet = new ReceivedPacket(codec.SnapshotChannel, 1, 1, 1, 1);
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 1);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 1);
            byte[] input = new byte[64];
            if (arena)
            {
                fps.ApplyDecoded(decoded, packet);
                Assert.That(
                    fps.PrepareInput(
                        1,
                        0,
                        0,
                        0,
                        0,
                        ArenaButtons.None,
                        ArenaWeaponId.Machinegun,
                        input
                    ).Status,
                    Is.EqualTo(ArenaPredictionPrepareStatus.Prepared)
                );
            }
            else
            {
                legacy.ApplyDecoded(decoded, packet);
                Assert.That(
                    legacy.PrepareInput(1, 0, 0, input).Status,
                    Is.EqualTo(PredictionPrepareStatus.Prepared)
                );
            }
            legacy.TryGetPredictedState(out var beforeLegacy);
            var beforeArena = arena ? fps.Current : default;
            for (int i = 0; i < input.Length; i++)
                input[i] = 0xaa;
            if (arena)
            {
                Assert.That(
                    fps.PrepareInput(
                        1,
                        0,
                        0,
                        0,
                        0,
                        ArenaButtons.None,
                        ArenaWeaponId.Machinegun,
                        input
                    ).Status,
                    Is.EqualTo(ArenaPredictionPrepareStatus.ArithmeticOverflow)
                );
                Assert.That(fps.Current, Is.EqualTo(beforeArena));
            }
            else
            {
                Assert.That(
                    legacy.PrepareInput(1, 0, 0, input).Status,
                    Is.EqualTo(PredictionPrepareStatus.ArithmeticOverflow)
                );
                legacy.TryGetPredictedState(out var after);
                Assert.That(after, Is.EqualTo(beforeLegacy));
            }
            Assert.That(input, Is.All.EqualTo((byte)0xaa));
        }

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        public void OverflowSnapshotPreservesHistoryEpochAndNextInput(bool arena, int invalidField)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var decoded = new BattlePacketBuffer();
            var source = Snapshot(1, arena);
            source.LastProcessedInputSequence = 0;
            Assert.That(
                codec.TryDecode(Frame(1101, source), decoded),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            var packet = new ReceivedPacket(codec.SnapshotChannel, 1, 1, 1, 1);
            var nextEpoch = new ReceivedPacket(codec.SnapshotChannel, 1, 1, 2, 2);
            var legacy = new ClientPredictionAdapter(new FakeRealtimeTransport(), codec, 1);
            var fps = new ArenaClientPredictionAdapter(new FakeRealtimeTransport(), codec, 1);
            byte[] input = new byte[64];
            if (arena)
                fps.ApplyDecoded(decoded, packet);
            else
                legacy.ApplyDecoded(decoded, packet);
            if (arena)
                fps.PrepareInput(
                    101,
                    1000,
                    0,
                    0,
                    0,
                    ArenaButtons.None,
                    ArenaWeaponId.Machinegun,
                    input
                );
            else
                legacy.PrepareInput(101, 1000, 0, input);
            legacy.TryGetPredictedState(out var beforeLegacy);
            var beforeArena = arena ? fps.Current : default;
            long accepted = arena ? fps.AcceptedSnapshots : legacy.Diagnostics.AcceptedSnapshots;
            source.RoomTick = invalidField == 0 ? (ulong)long.MaxValue : 102;
            if (invalidField == 1)
                source.Players[0].PositionXMilli = int.MaxValue;
            if (invalidField == 2)
                source.Players[0].PositionYMilli = int.MaxValue;
            if (invalidField == 3)
                source.Players[0].VelocityYMilliPerSecond = int.MinValue;
            Assert.That(
                codec.TryDecode(Frame(1101, source), decoded),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            if (arena)
            {
                Assert.That(
                    fps.ApplyDecoded(decoded, nextEpoch).Status,
                    Is.EqualTo(ArenaSnapshotApplyStatus.Malformed)
                );
                Assert.That(fps.Current, Is.EqualTo(beforeArena));
                Assert.That(fps.AcceptedSnapshots, Is.EqualTo(accepted));
            }
            else
            {
                Assert.That(
                    legacy.ApplyDecoded(decoded, nextEpoch).Status,
                    Is.EqualTo(SnapshotApplyStatus.ArithmeticOverflow)
                );
                legacy.TryGetPredictedState(out var after);
                Assert.That(after, Is.EqualTo(beforeLegacy));
                Assert.That(legacy.ConnectionEpoch, Is.EqualTo(1));
                Assert.That(legacy.Diagnostics.AcceptedSnapshots, Is.EqualTo(accepted));
            }
            // A valid followup on the original epoch must still replay the pending input.
            source = Snapshot(1, arena);
            source.RoomTick = 102;
            source.LastProcessedInputSequence = 0;
            codec.TryDecode(Frame(1101, source), decoded);
            int replayed = arena
                ? fps.ApplyDecoded(decoded, packet).Reconciliation.ReplayedInputCount
                : legacy.ApplyDecoded(decoded, packet).Reconciliation.ReplayedInputCount;
            Assert.That(replayed, Is.EqualTo(1));
            int written = arena
                ? fps.PrepareInput(
                    103,
                    0,
                    0,
                    0,
                    0,
                    ArenaButtons.None,
                    ArenaWeaponId.Machinegun,
                    input
                ).WrittenBytes
                : legacy.PrepareInput(103, 0, 0, input).WrittenBytes;
            Assert.That(
                Wire.InputCommand.Parser.ParseFrom(input.AsSpan(2, written - 2)).Sequence,
                Is.EqualTo(2)
            );
        }

        private static int CopyAndDispatch(
            BattlePacketBuffer decoded,
            BattlePacketBuffer dispatch,
            ArenaClientPredictionAdapter prediction,
            ArenaRemotePresentation remote,
            ArenaRemotePose[] poses,
            in ReceivedPacket packet,
            int index
        )
        {
            dispatch.Tick = (ulong)(100 + index);
            for (int i = 0; i < decoded.PlayerCount; i++)
            {
                var state = decoded.Players[i].State;
                state.Tick = (long)dispatch.Tick;
                dispatch.Players[i] = new ArenaSnapshotPlayer(decoded.Players[i].EntityId, state);
            }
            prediction.ApplyDecoded(dispatch, packet);
            remote.ApplySnapshot(dispatch.PlayerSpan, (long)dispatch.Tick, 1);
            return remote.Advance(1f / 60f, poses);
        }

        [TestCase(64, false)]
        [TestCase(8, true)]
        public void SnapshotMapsAllPlayersWithoutTreatingLegacyHealthAsArena(int count, bool arena)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            Assert.That(
                codec.TryDecode(Frame(1101, Snapshot(count, arena)), output),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            Assert.That(output.PlayerCount, Is.EqualTo(count));
            Assert.That(output.HasArenaData, Is.EqualTo(arena));
            Assert.That(output.TryGetPlayer((uint)count, out var player), Is.True);
            Assert.That(player.State.PositionXMillimetres, Is.EqualTo(-count));
            Assert.That(player.Acknowledgement, Is.EqualTo(42));
        }

        [Test]
        public void FailedDecodeNeverChangesDestinationAndUnknownFieldsAreAccepted()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            byte[] valid = Frame(1101, Snapshot());
            Assert.That(codec.TryDecode(valid, output), Is.EqualTo(BattleDecodeStatus.Accepted));
            var duplicate = Snapshot();
            duplicate.Players.Add(duplicate.Players[0].Clone());
            Assert.That(
                codec.TryDecode(Frame(1101, duplicate), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            Assert.That(output.PlayerCount, Is.EqualTo(1));
            Assert.That(output.Tick, Is.EqualTo(100));
            byte[] unknown = new byte[valid.Length + 3];
            valid.CopyTo(unknown, 0);
            unknown[valid.Length] = 0xa0;
            unknown[valid.Length + 1] = 6;
            unknown[valid.Length + 2] = 1;
            Assert.That(codec.TryDecode(unknown, output), Is.EqualTo(BattleDecodeStatus.Accepted));
            byte[] duplicateField = new byte[valid.Length + 2];
            valid.CopyTo(duplicateField, 0);
            duplicateField[valid.Length] = 8;
            duplicateField[valid.Length + 1] = 1;
            Assert.That(
                codec.TryDecode(duplicateField, output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            Assert.That(
                codec.TryDecode(valid.AsSpan(0, valid.Length - 1), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            Assert.That(output.Players[0].State.Health, Is.EqualTo(100));
        }

        [Test]
        public void ReconnectUsesSameSnapshotValidationAndResetDisposeAreSafe()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            var response = new Wire.ReconnectResponse
            {
                ConnectionEpoch = 2,
                ResumeTick = 100,
                Snapshot = Snapshot(),
            };
            Assert.That(
                codec.TryDecode(Frame(1201, response), output),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            Assert.That(output.Kind, Is.EqualTo(BattleMessageKind.ReconnectResponse));
            Assert.That(output.ConnectionEpoch, Is.EqualTo(2));
            response.ResumeTick = 101;
            Assert.That(
                codec.TryDecode(Frame(1201, response), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            codec.Reset();
            Assert.That(
                codec.TryDecode(Frame(1101, Snapshot()), output),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            codec.Dispose();
            codec.Dispose();
            codec.Reset();
            Assert.That(
                codec.TryDecode(Frame(1101, Snapshot()), output),
                Is.EqualTo(BattleDecodeStatus.Disposed)
            );
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        [TestCase(5)]
        public void AllCombatEventsUseGeneratedMessage(int kind)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            var e = new Wire.ReliableEvent
            {
                RoomTick = 100,
                Sequence = 10,
                EventType = (uint)kind,
                CombatEvent = new Wire.ArenaCombatEvent
                {
                    EventTick = 100,
                    EventType = (Wire.ArenaCombatEventType)kind,
                    SourceEntityId = 1,
                    TargetEntityId = kind == 1 || kind == 2 || kind == 4 ? 2u : 0u,
                    WeaponId = (Wire.ArenaWeaponId)(kind == 4 ? 0 : 1),
                },
            };
            Assert.That(
                codec.TryDecode(Frame(1102, e), output),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            Assert.That((int)output.CombatEvent.Kind, Is.EqualTo(kind));
        }

        [Test]
        public void ExplicitZeroArenaFieldAndWrongWireAreHandled()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            // Explicit velocity=0 remains an Arena discriminator, unlike legacy health.
            byte[] frame = { 0x4d, 4, 8, 1, 0x22, 6, 8, 1, 0x30, 100, 0x38, 0 };
            Assert.That(codec.TryDecode(frame, output), Is.EqualTo(BattleDecodeStatus.Accepted));
            Assert.That(output.HasArenaData, Is.True);
            frame[10] = 0x3a;
            Assert.That(
                codec.TryDecode(frame, output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            Assert.That(output.HasArenaData, Is.True);
        }

        [Test]
        public void ControlRequestsAndResponsesRemainCompatible()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            byte[] bytes = new byte[1200];
            var output = new BattlePacketBuffer();
            Assert.That(
                codec.TryEncodeLogin(
                    "build",
                    new BattleAdmission("room", "ticket"),
                    bytes,
                    out int n
                ),
                Is.True
            );
            var login = Wire.LoginRequest.Parser.ParseFrom(bytes.AsSpan(2, n - 2));
            Assert.That(login.ProtocolMajor, Is.EqualTo(1));
            Assert.That(login.EntryTicket, Is.EqualTo("ticket"));
            Assert.That(codec.TryEncodeJoin(7, 2, bytes, out n), Is.True);
            Assert.That(
                Wire.JoinRoomRequest.Parser.ParseFrom(bytes.AsSpan(2, n - 2)).SessionId,
                Is.EqualTo(7)
            );
            Assert.That(codec.TryEncodeReconnect(7, 2, 100, bytes, out n), Is.True);
            Assert.That(
                Wire.ReconnectRequest.Parser.ParseFrom(bytes.AsSpan(2, n - 2)).LastReceivedTick,
                Is.EqualTo(100)
            );
            Assert.That(
                codec.TryDecode(
                    Frame(
                        1001,
                        new Wire.LoginResponse
                        {
                            SessionId = 7,
                            ConnectionEpoch = 2,
                            RoomTick = 100,
                        }
                    ),
                    output
                ),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            Assert.That(output.SessionId, Is.EqualTo(7));
            Assert.That(
                codec.TryDecode(
                    Frame(
                        1011,
                        new Wire.JoinRoomResponse
                        {
                            RoomId = 2,
                            EntityId = 1,
                            TickRate = 60,
                        }
                    ),
                    output
                ),
                Is.EqualTo(BattleDecodeStatus.Accepted)
            );
            Assert.That(output.TickRate, Is.EqualTo(60));
        }

        [Test]
        public void InvalidArenaAndPickupDataDoNotMutateState()
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer { Tick = 999 };
            Assert.That(
                codec.TryDecode(Frame(1101, Snapshot(9)), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            var s = Snapshot();
            s.Players[0].Health = uint.MaxValue;
            Assert.That(
                codec.TryDecode(Frame(1101, s), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            s = Snapshot();
            s.Pickups.Add(
                new Wire.PickupState { PickupId = 1, PickupType = (Wire.ArenaPickupType)1 }
            );
            s.Pickups.Add(s.Pickups[0].Clone());
            Assert.That(
                codec.TryDecode(Frame(1101, s), output),
                Is.EqualTo(BattleDecodeStatus.MalformedPayload)
            );
            Assert.That(output.Tick, Is.EqualTo(999));
            Assert.That(output.PlayerCount, Is.Zero);
        }

        [TestCase(64, false, "snapshot")]
        [TestCase(8, true, "snapshot")]
        [TestCase(8, true, "reconnect")]
        [TestCase(0, true, "event")]
        public void DecodeAllocationAndLatencyDiagnostic(int count, bool arena, string kind)
        {
            using var codec = new ProtobufBattleProtocolCodec();
            var output = new BattlePacketBuffer();
            var snapshot = Snapshot(count, arena);
            if (arena)
                for (int i = 1; i <= 4; i++)
                    snapshot.Pickups.Add(
                        new Wire.PickupState
                        {
                            PickupId = (uint)i,
                            PickupType = (Wire.ArenaPickupType)(i % 2 + 1),
                            Active = true,
                        }
                    );
            byte[] frame =
                kind == "reconnect"
                    ? Frame(
                        1201,
                        new Wire.ReconnectResponse
                        {
                            ConnectionEpoch = 2,
                            ResumeTick = 100,
                            Snapshot = snapshot,
                        }
                    )
                : kind == "event"
                    ? Frame(
                        1102,
                        new Wire.ReliableEvent
                        {
                            RoomTick = 100,
                            Sequence = 10,
                            CombatEvent = new Wire.ArenaCombatEvent
                            {
                                EventTick = 100,
                                SourceEntityId = 1,
                                WeaponId = (Wire.ArenaWeaponId)1,
                            },
                        }
                    )
                : Frame(1101, snapshot);
            const int samples = 10000;
            for (int i = 0; i < 1000; i++)
                codec.TryDecode(frame, output);
            var times = new long[samples];
            using var allocationMeter = new CalibratedAllocationMeter();
            allocationMeter.Begin();
            bool accepted = true;
            long begin = Stopwatch.GetTimestamp();
            for (int i = 0; i < samples; i++)
            {
                long start = Stopwatch.GetTimestamp();
                accepted &= codec.TryDecode(frame, output) == BattleDecodeStatus.Accepted;
                times[i] = Stopwatch.GetTimestamp() - start;
            }
            long elapsed = Stopwatch.GetTimestamp() - begin,
                allocated = allocationMeter.End();
            Array.Sort(times);
            Assert.That(accepted, Is.True);
            string allocationReport =
                allocated >= 0
                    ? allocationMeter.MeasuresBytes
                        ? $"bytes/message={allocated / (double)samples}; bytes/sec@20Hz={allocated / (double)samples * 20}"
                        : $"allocation-events/message={allocated / (double)samples}; allocation-events/sec@20Hz={allocated / (double)samples * 20}; bytes/message=UNVERIFIED; bytes/sec@20Hz=UNVERIFIED"
                    : "bytes/message=UNVERIFIED; bytes/sec@20Hz=UNVERIFIED; allocation calibration failed";
            TestContext.Out.WriteLine(
                $"Diagnostic only: meter={allocationMeter.Source}; runtime={Environment.Version}; OS={Environment.OSVersion}; CPUs={Environment.ProcessorCount}; vector=kind:{kind},players:{count},arena:{arena},pickups:{snapshot.Pickups.Count},tick:100,ack:42; warmup=1000; samples={samples}; frameBytes={frame.Length}; {allocationReport}; durationMs={elapsed * 1000.0 / Stopwatch.Frequency}; percentile=nearest-rank; p50us={times[4999] * 1e6 / Stopwatch.Frequency}; p95us={times[9499] * 1e6 / Stopwatch.Frequency}; p99us={times[9899] * 1e6 / Stopwatch.Frequency}; generated parse trees/message=1"
            );
        }
    }
}
