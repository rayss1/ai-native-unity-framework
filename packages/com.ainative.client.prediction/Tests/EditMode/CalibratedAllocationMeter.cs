using System;
using System.Runtime.CompilerServices;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using Unity.Profiling;
#endif
namespace AiNative.Client.Prediction.Tests
{
    /// <summary>先用已知分配和空区间校准，再信任零分配结果。
    /// Unity 使用原生 Profiler，因为部分 Mono 版本的 GC 计数始终返回零。</summary>
    internal sealed class CalibratedAllocationMeter : IDisposable
    {
#if UNITY_5_3_OR_NEWER
        private ProfilerRecorder recorder;
#else
        private long _baseline;
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
            recorder = new ProfilerRecorder(
                ProfilerCategory.Memory,
                "GC.Alloc",
                1,
                ProfilerRecorderOptions.WrapAroundWhenCapacityReached
                    | ProfilerRecorderOptions.SumAllSamplesInFrame
                    | ProfilerRecorderOptions.CollectOnlyOnCurrentThread
            );
            if (!recorder.Valid)
                return;
#endif
            // 检查空区间前，先预热测量与分配方法。
            Begin();
            AllocateKnownArray();
            EndRaw();
            Begin();
            long empty = EndRaw();
            Begin();
            AllocateKnownArray();
            long known = EndRaw();
            IsSupported = empty == 0 && known >= (MeasuresBytes ? 1024 : 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AllocateKnownArray()
        {
            GC.KeepAlive(new byte[1024]);
        }

        public void RequireSupported()
        {
            if (!IsSupported)
            {
                Assert.Ignore(
                    "Allocation result UNVERIFIED: calibrated meter failed known-allocation or empty-interval checks: "
                        + Source
                );
            }
        }

        public void Begin()
        {
#if UNITY_5_3_OR_NEWER
            if (!recorder.Valid)
                return;
            recorder.Reset();
            recorder.Start();
#else
            _baseline = GC.GetAllocatedBytesForCurrentThread();
#endif
        }

        private long EndRaw()
        {
#if UNITY_5_3_OR_NEWER
            if (!recorder.Valid)
                return -1;
            recorder.Stop();
            // 同步测试区间不跨帧；SumAllSamplesInFrame 将该帧全部分配事件
            // 聚合为一个样本，避免事件数量超出环形缓冲区容量。
            if (recorder.Count > 1)
                return -1;
            return recorder.Count == 0 ? 0 : recorder.GetSample(0).Count;
#else
            return GC.GetAllocatedBytesForCurrentThread() - _baseline;
#endif
        }

        public long End()
        {
            long value = EndRaw();
            return IsSupported ? value : -1;
        }

        public void Dispose()
        {
#if UNITY_5_3_OR_NEWER
            recorder.Dispose();
#endif
        }
    }
}
