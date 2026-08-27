using System;
using NUnit.Framework;

namespace AiNative.Gameplay.Tests
{
    public sealed class ClientPredictionTests
    {
        [Test]
        public void IntegerMovementClampsInputAndAdvancesOneFixedTick()
        {
            KinematicState initial = new KinematicState(10, 4, 100, -100);
            KinematicInput input = new KinematicInput(5, 2000, -500);

            KinematicState result = KinematicMovement.Step(initial, input);

            Assert.That(result.Tick, Is.EqualTo(11));
            Assert.That(result.LastProcessedInputSequence, Is.EqualTo(5));
            Assert.That(result.PositionXMillimetres, Is.EqualTo(150));
            Assert.That(result.PositionZMillimetres, Is.EqualTo(-125));
        }

        [Test]
        public void ReconciliationReplaysOnlyUnacknowledgedInputs()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(16);
            history.Initialize(new KinematicState(100, 0, 0, 0));
            history.Predict(new KinematicInput(1, 1000, 0), out _);
            history.Predict(new KinematicInput(2, 0, 1000), out _);
            history.Predict(new KinematicInput(3, 1000, 0), out _);

            ReconciliationResult result = history.Reconcile(
                new KinematicState(101, 1, 40, 0));

            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.Corrected));
            Assert.That(result.ErrorXMillimetres, Is.EqualTo(-10));
            Assert.That(result.ErrorZMillimetres, Is.Zero);
            Assert.That(result.DiscardedInputCount, Is.EqualTo(1));
            Assert.That(result.ReplayedInputCount, Is.EqualTo(2));
            Assert.That(result.After, Is.EqualTo(new KinematicState(103, 3, 90, 50)));
            Assert.That(history.Count, Is.EqualTo(2));
        }

        [Test]
        public void MatchingSnapshotRetainsPredictedStateWithoutCorrection()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(16);
            history.Initialize(new KinematicState(20, 10, 0, 0));
            history.Predict(new KinematicInput(11, 1000, 0), out _);
            KinematicState predicted = history.Predict(
                new KinematicInput(12, 0, 1000),
                out _);

            ReconciliationResult result = history.Reconcile(predicted);

            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.Matched));
            Assert.That(result.After, Is.EqualTo(predicted));
            Assert.That(result.DiscardedInputCount, Is.EqualTo(2));
            Assert.That(result.ReplayedInputCount, Is.Zero);
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void FullHistoryDropsOldestAndIgnoresOlderSnapshot()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(2);
            KinematicState initial = new KinematicState(0, 0, 0, 0);
            history.Initialize(initial);
            history.Predict(new KinematicInput(1, 1000, 0), out bool firstDrop);
            history.Predict(new KinematicInput(2, 1000, 0), out bool secondDrop);
            KinematicState current = history.Predict(
                new KinematicInput(3, 1000, 0),
                out bool thirdDrop);

            ReconciliationResult result = history.Reconcile(initial);

            Assert.That(firstDrop, Is.False);
            Assert.That(secondDrop, Is.False);
            Assert.That(thirdDrop, Is.True);
            Assert.That(history.DroppedInputCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.StaleSnapshotIgnored));
            Assert.That(result.After, Is.EqualTo(current));
        }

        [Test]
        public void AuthoritativeSequenceAheadResetsPredictionEpoch()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(8);
            history.Initialize(new KinematicState(40, 4, 0, 0));
            history.Predict(new KinematicInput(5, 1000, 0), out _);
            KinematicState authoritative = new KinematicState(50, 8, 500, -250);

            ReconciliationResult result = history.Reconcile(authoritative);

            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.AuthoritativeAhead));
            Assert.That(result.After, Is.EqualTo(authoritative));
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void MissingAcknowledgementFailsClosedToAuthoritativeState()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(8);
            history.Initialize(new KinematicState(10, 10, 0, 0));
            history.Predict(new KinematicInput(12, 1000, 0), out _);
            history.Predict(new KinematicInput(13, 1000, 0), out _);
            KinematicState authoritative = new KinematicState(11, 11, 25, -25);

            ReconciliationResult result = history.Reconcile(authoritative);

            Assert.That(result.Status, Is.EqualTo(ReconciliationStatus.HistoryMiss));
            Assert.That(result.After, Is.EqualTo(authoritative));
            Assert.That(history.Count, Is.Zero);
        }

        [Test]
        public void PredictionAndMatchingReconciliationAllocateNothingAfterWarmup()
        {
            ClientPredictionHistory history = new ClientPredictionHistory(16);
            history.Initialize(new KinematicState(0, 0, 0, 0));
            uint sequence = 0;

            for (int index = 0; index < 100; index++)
            {
                KinematicState predicted = history.Predict(
                    new KinematicInput(++sequence, 1000, -500),
                    out _);
                history.Reconcile(predicted);
            }

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 1000; index++)
            {
                KinematicState predicted = history.Predict(
                    new KinematicInput(++sequence, 1000, -500),
                    out _);
                history.Reconcile(predicted);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
        }
    }
}
