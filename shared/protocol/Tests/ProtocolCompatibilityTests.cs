using AiNative.Protocol.V1;
using Google.Protobuf;
using NUnit.Framework;

namespace AiNative.Protocol.Tests;

public sealed class ProtocolCompatibilityTests
{
    [Test]
    public void InputCommandMatchesGoldenBytes()
    {
        InputCommand command = CreateInputCommand();

        Assert.That(Convert.ToHexString(command.ToByteArray()), Is.EqualTo("092A00000000000000100718CF0F20D00F28D0BB1B3003"));
    }

    [Test]
    public void InputBatchMatchesGoldenBytes()
    {
        InputBatch batch = new();
        batch.Commands.Add(CreateInputCommand());
        batch.Commands.Add(new InputCommand
        {
            RoomTick = 43,
            Sequence = 8,
            MoveXMilli = 1000,
            MoveYMilli = -1000,
        });

        Assert.That(
            Convert.ToHexString(batch.ToByteArray()),
            Is.EqualTo("0A17092A00000000000000100718CF0F20D00F28D0BB1B30030A11092B00000000000000100818D00F20CF0F"));
    }

    [Test]
    public void UnknownFieldsSurviveParseAndReserialize()
    {
        byte[] current = CreateInputCommand().ToByteArray();
        byte[] future = current.Concat(new byte[] { 0xA0, 0x06, 0x01 }).ToArray();

        InputCommand parsed = InputCommand.Parser.ParseFrom(future);

        Assert.That(parsed.ToByteArray(), Is.EqualTo(future));
    }

    [Test]
    public void SnapshotInputAcknowledgementIsAdditiveAndStable()
    {
        Snapshot current = new()
        {
            ProtocolMajor = 1,
            LastProcessedInputSequence = 42,
        };
        Snapshot legacy = Snapshot.Parser.ParseFrom(new byte[] { 0x08, 0x01 });

        Assert.That(
            Convert.ToHexString(current.ToByteArray()),
            Is.EqualTo("0801302A"));
        Assert.That(legacy.ProtocolMajor, Is.EqualTo(1));
        Assert.That(legacy.LastProcessedInputSequence, Is.Zero);
    }

    [Test]
    public void MalformedInputIsRejected()
    {
        Assert.That(
            () => InputCommand.Parser.ParseFrom(new byte[] { 0x0A, 0x05, 0x01 }),
            Throws.TypeOf<InvalidProtocolBufferException>());
    }

    [Test]
    public void MessageIdsRemainUniqueAndNonZero()
    {
        int[] ids = Enum.GetValues<MessageId>().Select(value => (int)value).Where(value => value != 0).ToArray();

        Assert.That(ids, Is.Unique);
        Assert.That(ids, Has.All.Positive);
    }

    private static InputCommand CreateInputCommand() => new()
    {
        RoomTick = 42,
        Sequence = 7,
        MoveXMilli = -1000,
        MoveYMilli = 1000,
        YawMillidegrees = 225000,
        Buttons = 3,
    };
}
