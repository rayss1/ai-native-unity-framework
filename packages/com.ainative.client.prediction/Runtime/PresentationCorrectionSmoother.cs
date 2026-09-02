using System;
using AiNative.Gameplay;

namespace AiNative.Client.Prediction
{
    public enum PresentationCorrectionAction : byte
    {
        None = 0,
        Smoothed = 1,
        Snapped = 2,
    }

    public readonly struct PresentationCorrectionOptions
    {
        public PresentationCorrectionOptions(
            float smoothingSeconds,
            int snapThresholdMillimetres)
        {
            if (float.IsNaN(smoothingSeconds) ||
                float.IsInfinity(smoothingSeconds) ||
                smoothingSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(smoothingSeconds));
            }

            if (snapThresholdMillimetres <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(snapThresholdMillimetres));
            }

            SmoothingSeconds = smoothingSeconds;
            SnapThresholdMillimetres = snapThresholdMillimetres;
        }

        public float SmoothingSeconds { get; }

        public int SnapThresholdMillimetres { get; }

        public static PresentationCorrectionOptions Default =>
            new PresentationCorrectionOptions(0.1f, 250);
    }

    public readonly struct PresentationPosition
    {
        public PresentationPosition(double xMillimetres, double zMillimetres)
        {
            XMillimetres = xMillimetres;
            ZMillimetres = zMillimetres;
        }

        public double XMillimetres { get; }

        public double ZMillimetres { get; }
    }

    public readonly struct PresentationCorrectionDiagnostics
    {
        public PresentationCorrectionDiagnostics(
            long smoothedCorrections,
            long snappedCorrections,
            bool isSmoothing,
            int residualMillimetres)
        {
            SmoothedCorrections = smoothedCorrections;
            SnappedCorrections = snappedCorrections;
            IsSmoothing = isSmoothing;
            ResidualMillimetres = residualMillimetres;
        }

        public long SmoothedCorrections { get; }

        public long SnappedCorrections { get; }

        public bool IsSmoothing { get; }

        public int ResidualMillimetres { get; }
    }

    /// <summary>
    /// Keeps simulation state authoritative while decaying only its visual offset.
    /// The caller supplies render delta time and applies the returned position.
    /// </summary>
    public sealed class PresentationCorrectionSmoother
    {
        private readonly PresentationCorrectionOptions _options;
        private double _residualXMillimetres;
        private double _residualZMillimetres;
        private float _remainingSeconds;
        private long _smoothedCorrections;
        private long _snappedCorrections;
        private bool _initialized;

        public PresentationCorrectionSmoother()
            : this(PresentationCorrectionOptions.Default)
        {
        }

        public PresentationCorrectionSmoother(in PresentationCorrectionOptions options)
        {
            if (float.IsNaN(options.SmoothingSeconds) ||
                float.IsInfinity(options.SmoothingSeconds) ||
                options.SmoothingSeconds <= 0 ||
                options.SnapThresholdMillimetres <= 0)
            {
                throw new ArgumentException("Presentation correction options are invalid.", nameof(options));
            }

            _options = options;
        }

        public bool IsInitialized => _initialized;

        public PresentationCorrectionDiagnostics Diagnostics =>
            new PresentationCorrectionDiagnostics(
                _smoothedCorrections,
                _snappedCorrections,
                _remainingSeconds > 0,
                CalculateMagnitude(_residualXMillimetres, _residualZMillimetres));

        public void Initialize(in KinematicState state)
        {
            _residualXMillimetres = 0;
            _residualZMillimetres = 0;
            _remainingSeconds = 0;
            _initialized = true;
        }

        public void ResetState()
        {
            _residualXMillimetres = 0;
            _residualZMillimetres = 0;
            _remainingSeconds = 0;
            _initialized = false;
        }

        public void ResetDiagnostics()
        {
            _smoothedCorrections = 0;
            _snappedCorrections = 0;
        }

        public PresentationCorrectionAction ApplyReconciliation(
            in ReconciliationResult reconciliation)
        {
            if (!_initialized)
            {
                Initialize(reconciliation.After);
                return PresentationCorrectionAction.Snapped;
            }

            if (reconciliation.Status == ReconciliationStatus.StaleSnapshotIgnored ||
                reconciliation.Status == ReconciliationStatus.Matched)
            {
                return PresentationCorrectionAction.None;
            }

            if (reconciliation.Status == ReconciliationStatus.AuthoritativeAhead ||
                reconciliation.Status == ReconciliationStatus.HistoryMiss)
            {
                Snap();
                return PresentationCorrectionAction.Snapped;
            }

            double correctionX = (double)reconciliation.After.PositionXMillimetres -
                                 reconciliation.Before.PositionXMillimetres;
            double correctionZ = (double)reconciliation.After.PositionZMillimetres -
                                 reconciliation.Before.PositionZMillimetres;
            double candidateResidualX = _residualXMillimetres - correctionX;
            double candidateResidualZ = _residualZMillimetres - correctionZ;
            int correctionMagnitude = CalculateMagnitude(correctionX, correctionZ);
            int residualMagnitude = CalculateMagnitude(
                candidateResidualX,
                candidateResidualZ);
            if (correctionMagnitude > _options.SnapThresholdMillimetres ||
                residualMagnitude > _options.SnapThresholdMillimetres)
            {
                Snap();
                return PresentationCorrectionAction.Snapped;
            }

            _residualXMillimetres = candidateResidualX;
            _residualZMillimetres = candidateResidualZ;
            _remainingSeconds = _options.SmoothingSeconds;
            _smoothedCorrections++;
            return PresentationCorrectionAction.Smoothed;
        }

        public PresentationPosition Advance(
            in KinematicState simulationState,
            float deltaSeconds)
        {
            if (float.IsNaN(deltaSeconds) ||
                float.IsInfinity(deltaSeconds) ||
                deltaSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            }

            if (!_initialized)
            {
                Initialize(simulationState);
            }

            if (_remainingSeconds > 0 && deltaSeconds > 0)
            {
                if (deltaSeconds >= _remainingSeconds)
                {
                    _residualXMillimetres = 0;
                    _residualZMillimetres = 0;
                    _remainingSeconds = 0;
                }
                else
                {
                    double retained = 1d - deltaSeconds / _remainingSeconds;
                    _residualXMillimetres *= retained;
                    _residualZMillimetres *= retained;
                    _remainingSeconds -= deltaSeconds;
                }
            }

            return new PresentationPosition(
                simulationState.PositionXMillimetres + _residualXMillimetres,
                simulationState.PositionZMillimetres + _residualZMillimetres);
        }

        private void Snap()
        {
            _residualXMillimetres = 0;
            _residualZMillimetres = 0;
            _remainingSeconds = 0;
            _snappedCorrections++;
        }

        private static int CalculateMagnitude(double x, double z)
        {
            double magnitude = Math.Sqrt(x * x + z * z);
            if (magnitude >= int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)Math.Ceiling(magnitude);
        }
    }
}
