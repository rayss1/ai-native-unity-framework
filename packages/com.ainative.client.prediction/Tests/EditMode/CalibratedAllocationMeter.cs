using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using Unity.Profiling;
#endif

namespace AiNative.Client.Prediction.Tests
{
    /// <summary>Calibrates a known allocation and an empty interval before trusting zero.
    /// Unity uses its native profiler because some Mono versions return a constant zero from GC.</summary>
    internal sealed class CalibratedAllocationMeter : IDisposable
    {
#if UNITY_5_3_OR_NEWER
        private ProfilerRecorder recorder;
#else
        private long baseline;
#endif
        public bool IsSupported { get; }
        public string Source =>
#if UNITY_5_3_OR_NEWER
            "Unity ProfilerRecorder GC.Alloc current-thread allocation events";
#else
            "GC.GetAllocatedBytesForCurrentThread";
#endif
        public bool MeasuresBytes =>
#if UNITY_5_3_OR_NEWER
            false;
#else
            true;
#endif
        public CalibratedAllocationMeter()
        {
#if UNITY_5_3_OR_NEWER
            recorder = new ProfilerRecorder(ProfilerCategory.Memory, "GC.Alloc", 1,
                ProfilerRecorderOptions.WrapAroundWhenCapacityReached | ProfilerRecorderOptions.SumAllSamplesInFrame |
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            if (!recorder.Valid) return;
#endif
            // Warm the measurement/allocator methods before checking an empty interval.
            Begin(); AllocateKnownArray(); EndRaw();
            Begin(); long empty = EndRaw();
            Begin(); AllocateKnownArray(); long known = EndRaw();
            IsSupported = empty == 0 && known >= (MeasuresBytes ? 1024 : 1);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AllocateKnownArray() { GC.KeepAlive(new byte[1024]); }
        public void RequireSupported()
        {
            if (!IsSupported) Assert.Ignore("Allocation result UNVERIFIED: calibrated meter failed known-allocation or empty-interval checks: " + Source);
        }
        public void Begin()
        {
#if UNITY_5_3_OR_NEWER
            if (!recorder.Valid) return;
            recorder.Reset(); recorder.Start();
#else
            baseline = GC.GetAllocatedBytesForCurrentThread();
#endif
        }
        private long EndRaw()
        {
#if UNITY_5_3_OR_NEWER
            if (!recorder.Valid) return -1;
            recorder.Stop();
            // Synchronous test intervals never cross a frame. SumAllSamplesInFrame aggregates
            // all allocation events into one sample, so event count cannot saturate the ring.
            if (recorder.Count > 1) return -1;
            return recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
#else
            return GC.GetAllocatedBytesForCurrentThread() - baseline;
#endif
        }
        public long End() { long value = EndRaw(); return IsSupported ? value : -1; }
        public void Dispose()
        {
#if UNITY_5_3_OR_NEWER
            recorder.Dispose();
#endif
        }
    }
}
