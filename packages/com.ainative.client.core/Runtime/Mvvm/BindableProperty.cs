#nullable enable
using System.Collections.Generic;
using System.ComponentModel;

namespace AiNative.Client.Core.Mvvm
{
    /// <summary>强类型可观察值。调用者负责在界面所属线程读写；相等值不重复通知。</summary>
    public sealed class BindableProperty<T> : INotifyPropertyChanged
    {
        private static readonly PropertyChangedEventArgs ValueChanged =
            new PropertyChangedEventArgs(nameof(Value));
        private readonly IEqualityComparer<T> _comparer;
        private T _value;

        /// <summary>创建属性；可传入领域专用的相等比较，默认使用 EqualityComparer。</summary>
        public BindableProperty(T value = default!, IEqualityComparer<T>? comparer = null)
        {
            _value = value;
            _comparer = comparer ?? EqualityComparer<T>.Default;
        }

        /// <summary>当前值。先更新再同步通知，监听者由绑定作用域负责解绑。
        /// 监听回调异常直接向写入方传播；值已更新，不自动回滚。</summary>
        public T Value
        {
            get => _value;
            set
            {
                if (_comparer.Equals(_value, value))
                {
                    return;
                }
                _value = value;
                PropertyChanged?.Invoke(this, ValueChanged);
            }
        }

        /// <summary>值实际变化时触发；只产生 Value 属性通知。</summary>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
