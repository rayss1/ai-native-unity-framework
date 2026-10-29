#nullable enable
using System;
using System.Collections.Generic;

namespace AiNative.Client.Core.Mvvm
{
    /// <summary>拥有界面订阅和清理操作；在所属线程逆序释放，单个释放失败不阻断其余清理。</summary>
    public sealed class BindingScope : IDisposable
    {
        private readonly List<IDisposable> _bindings = new List<IDisposable>();
        private bool _disposed;

        /// <summary>将订阅所有权移入作用域；已关闭时立即释放传入对象。</summary>
        public T Add<T>(T binding)
            where T : IDisposable
        {
            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }
            if (_disposed)
            {
                binding.Dispose();
            }
            else
            {
                _bindings.Add(binding);
            }
            return binding;
        }

        /// <summary>登记一次性清理回调，返回令牌支持提前释放；作用域后续释放不会重复调用。</summary>
        public IDisposable Add(Action release)
        {
            if (release == null)
            {
                throw new ArgumentNullException(nameof(release));
            }
            return Add(new ReleaseAction(release));
        }

        /// <summary>幂等逆序释放；所有回调都执行后，聚合报告清理错误。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            List<Exception>? failures = null;
            for (int index = _bindings.Count - 1; index >= 0; index--)
            {
                try
                {
                    _bindings[index].Dispose();
                }
                catch (Exception failure)
                {
                    if (failures == null)
                    {
                        failures = new List<Exception>();
                    }
                    failures.Add(failure);
                }
            }
            _bindings.Clear();
            if (failures != null)
            {
                throw new AggregateException(failures);
            }
        }

        private sealed class ReleaseAction : IDisposable
        {
            private Action? _release;

            internal ReleaseAction(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                Action? release = _release;
                _release = null;
                release?.Invoke();
            }
        }
    }
}
