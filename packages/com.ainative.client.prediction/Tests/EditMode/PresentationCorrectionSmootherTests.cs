using System;
using AiNative.Gameplay;
using NUnit.Framework;

namespace AiNative.Client.Prediction.Tests
{
    public sealed class PresentationCorrectionSmootherTests
    {
        [Test]
        public void CorrectionPreservesVisualContinuityAndConverges()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            KinematicState before = State(1, 100);
            KinematicState after = State(1, 80);
            smoother.Initialize(before);

            PresentationCorrectionAction action = smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.Corrected, before, after));
            PresentationPosition initial = smoother.Advance(after, 0);
            PresentationPosition halfway = smoother.Advance(after, 0.05f);
            PresentationPosition settled = smoother.Advance(after, 0.05f);

            Assert.That(action, Is.EqualTo(PresentationCorrectionAction.Smoothed));
            Assert.That(initial.XMillimetres, Is.EqualTo(100d));
            Assert.That(halfway.XMillimetres, Is.EqualTo(90d).Within(0.0001d));
            Assert.That(settled.XMillimetres, Is.EqualTo(80d));
            Assert.That(smoother.Diagnostics.IsSmoothing, Is.False);
        }

        [Test]
        public void SuccessiveCorrectionPreservesCurrentPresentationPosition()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            KinematicState firstBefore = State(1, 100);
            KinematicState firstAfter = State(1, 80);
            smoother.Initialize(firstBefore);
            smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.Corrected, firstBefore, firstAfter));
            PresentationPosition prior = smoother.Advance(firstAfter, 0.025f);
            KinematicState secondBefore = State(2, 130);
            KinematicState secondAfter = State(2, 120);

            smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.Corrected, secondBefore, secondAfter));
            PresentationPosition current = smoother.Advance(secondAfter, 0);

            Assert.That(prior.XMillimetres, Is.EqualTo(95d).Within(0.0001d));
            Assert.That(current.XMillimetres, Is.EqualTo(145d).Within(0.0001d));
            Assert.That(current.XMillimetres - prior.XMillimetres, Is.EqualTo(50d).Within(0.0001d));
        }

        [Test]
        public void LargeOrUntrustedCorrectionSnapsImmediately()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            KinematicState before = State(1, 0);
            KinematicState largeAfter = State(1, 251);
            smoother.Initialize(before);

            PresentationCorrectionAction large = smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.Corrected, before, largeAfter));
            PresentationPosition snapped = smoother.Advance(largeAfter, 0);
            PresentationCorrectionAction historyMiss = smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.HistoryMiss, largeAfter, State(2, 400)));

            Assert.That(large, Is.EqualTo(PresentationCorrectionAction.Snapped));
            Assert.That(snapped.XMillimetres, Is.EqualTo(251d));
            Assert.That(historyMiss, Is.EqualTo(PresentationCorrectionAction.Snapped));
            Assert.That(smoother.Diagnostics.SnappedCorrections, Is.EqualTo(2));
        }

        [Test]
        public void ResetStatePreventsResidualFromCrossingReconnectBoundary()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            KinematicState before = State(1, 100);
            KinematicState after = State(1, 80);
            smoother.Initialize(before);
            smoother.ApplyReconciliation(
                Reconciliation(ReconciliationStatus.Corrected, before, after));

            smoother.ResetState();
            PresentationPosition rebound = smoother.Advance(State(2, 500), 0);

            Assert.That(rebound.XMillimetres, Is.EqualTo(500d));
            Assert.That(smoother.Diagnostics.IsSmoothing, Is.False);
            Assert.That(smoother.Diagnostics.ResidualMillimetres, Is.Zero);
        }

        [Test]
        public void SteadyStateAdvanceAllocatesNothing()
        {
            PresentationCorrectionSmoother smoother = CreateSmoother();
            KinematicState state = State(1, 0);
            smoother.Initialize(state);
            for (int index = 0; index < 1000; index++)
            {
                smoother.Advance(state, 1f / 60f);
            }

            _ = GC.GetAllocatedBytesForCurrentThread();
            long before = GC.GetAllocatedBytesForCurrentThread();
            PresentationPosition last = default;
            for (int index = 0; index < 1000; index++)
            {
                last = smoother.Advance(state, 1f / 60f);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(last.XMillimetres, Is.Zero);
            Assert.That(allocated, Is.Zero);
        }

        private static PresentationCorrectionSmoother CreateSmoother() =>
            new PresentationCorrectionSmoother(
                new PresentationCorrectionOptions(0.1f, 250));

        private static KinematicState State(long tick, int x) =>
            new KinematicState(tick, (uint)tick, x, 0);

        private static ReconciliationResult Reconciliation(
            ReconciliationStatus status,
            in KinematicState before,
            in KinematicState after) =>
            new ReconciliationResult(
                status,
                before,
                after,
                after.PositionXMillimetres - before.PositionXMillimetres,
                after.PositionZMillimetres - before.PositionZMillimetres,
                0,
                0);
    }
}
