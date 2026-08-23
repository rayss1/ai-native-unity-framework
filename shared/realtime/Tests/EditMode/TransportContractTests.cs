using NUnit.Framework;

namespace AiNative.Realtime.Tests
{
    public sealed class TransportContractTests
    {
        [Test]
        public void ReceivedPacketReportsTruncationWithoutHidingRequiredSize()
        {
            TransportChannel channel = new TransportChannel(
                2,
                TransportDelivery.Unreliable,
                TransportOrdering.Sequenced);

            ReceivedPacket packet = new ReceivedPacket(channel, 64, 128, 42, 3);

            Assert.That(packet.IsComplete, Is.False);
            Assert.That(packet.WrittenBytes, Is.EqualTo(64));
            Assert.That(packet.RequiredBytes, Is.EqualTo(128));
            Assert.That(packet.Sequence, Is.EqualTo(42));
            Assert.That(packet.ConnectionEpoch, Is.EqualTo(3));
        }

        [Test]
        public void AcceptedSendRecordsCopiedByteCount()
        {
            SendResult result = new SendResult(SendStatus.Accepted, 1200);

            Assert.That(result.Status, Is.EqualTo(SendStatus.Accepted));
            Assert.That(result.AcceptedBytes, Is.EqualTo(1200));
        }
    }
}
