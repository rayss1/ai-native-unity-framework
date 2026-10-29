using System;
using System.Collections.Generic;

namespace AiNative.Client.UI
{
    /// <summary>按稳定键复用列表项；创建、更新和释放由 View 显式提供。</summary>
    public sealed class KeyedViewList<TKey, TValue, TView> : IDisposable
    {
        private readonly Dictionary<TKey, TView> _views = new Dictionary<TKey, TView>();
        private readonly HashSet<TKey> _keys = new HashSet<TKey>();
        private readonly List<TKey> _removed = new List<TKey>();
        private readonly Func<TValue, TKey> _key;
        private readonly Func<TValue, TView> _create;
        private readonly Action<TView, TValue, int> _render;
        private readonly Action<TView> _release;
        private bool _disposed;

        /// <summary>视图拥有列表项；所有回调在调用线程执行，Unity 控件使用者必须从主线程调用。</summary>
        public KeyedViewList(
            Func<TValue, TKey> key,
            Func<TValue, TView> create,
            Action<TView, TValue, int> render,
            Action<TView> release
        )
        {
            _key = key ?? throw new ArgumentNullException(nameof(key));
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _render = render ?? throw new ArgumentNullException(nameof(render));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>先验证唯一键再更新，避免错误输入部分改变界面。</summary>
        public void Apply(IReadOnlyList<TValue> values)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(KeyedViewList<TKey, TValue, TView>));
            }
            _keys.Clear();
            for (int index = 0; index < values.Count; index++)
            {
                if (!_keys.Add(_key(values[index])))
                {
                    throw new ArgumentException("duplicate-view-key", nameof(values));
                }
            }
            for (int index = 0; index < values.Count; index++)
            {
                TValue value = values[index];
                TKey key = _key(value);
                if (!_views.TryGetValue(key, out TView view))
                {
                    view = _create(value);
                    _views.Add(key, view);
                }
                _render(view, value, index);
            }
            _removed.Clear();
            foreach (KeyValuePair<TKey, TView> pair in _views)
            {
                if (!_keys.Contains(pair.Key))
                {
                    _removed.Add(pair.Key);
                }
            }
            foreach (TKey key in _removed)
            {
                TView view = _views[key];
                _views.Remove(key);
                _release(view);
            }
        }

        /// <summary>幂等释放所有列表项；单项释放失败仍继续其他项，最终聚合异常。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            var errors = new List<Exception>();
            foreach (TView view in _views.Values)
            {
                try
                {
                    _release(view);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }
            _views.Clear();
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }
    }
}
