using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AiNative.Client.UI
{
    /// <summary>界面层次由页面、模态窗口到覆盖层排序，同层保留打开顺序。</summary>
    public enum UiLayer
    {
        /// <summary>页面栈；仅顶层页面显示。</summary>
        Page,

        /// <summary>模态窗口；阻止下层控件和游戏输入。</summary>
        Modal,

        /// <summary>始终位于模态窗口之上的提示层。</summary>
        Overlay,
    }

    /// <summary>界面实例的独立所有权；关闭先取消绑定，再释放实例和资源。</summary>
    public sealed class UiView : IAsyncDisposable
    {
        /// <summary>此视图拥有的根对象；调用方不得提前销毁或另行释放。</summary>
        public GameObject Root { get; }
        private readonly Func<ValueTask> _release;
        private Task _releaseTask;

        /// <summary>在主线程接收根对象和完整释放回调；回调必须清理绑定、实例及源资源。</summary>
        public UiView(GameObject root, Func<ValueTask> release)
        {
            Root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>主线程幂等释放；并发调用共享完成或失败，释放异常交给调用方观察。</summary>
        public ValueTask DisposeAsync()
        {
            if (_releaseTask == null)
            {
                var completion = new TaskCompletionSource<bool>();
                _releaseTask = completion.Task;
                _ = ReleaseCoreAsync(completion);
            }
            return new ValueTask(_releaseTask);
        }

        private async Task ReleaseCoreAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await _release();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>由组合根登记界面工厂；所有调用限定 Unity 主线程。</summary>
    public interface IUIService
    {
        /// <summary>串行打开单实例界面；取消后等待迟到工厂结果释放，失败不加入栈。</summary>
        ValueTask OpenAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>关闭指定界面并等待其释放；取消只作用于排队等待。</summary>
        ValueTask CloseAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>关闭最上层非覆盖界面并恢复有效焦点；取消只作用于排队等待。</summary>
        ValueTask BackAsync(CancellationToken cancellationToken = default);

        /// <summary>存在模态窗口时为真；应用必须将该状态接入游戏输入门禁。</summary>
        bool BlocksGameplay { get; }
    }

    /// <summary>界面串行切换和单实例管理。下层页面保留实例，返回时恢复显示及焦点。</summary>
    public sealed class UiService : IUIService, IAsyncDisposable
    {
        private sealed class Definition
        {
            public UiLayer Layer;
            public Func<CancellationToken, ValueTask<UiView>> Factory;
        }

        private sealed class Entry
        {
            public string Id;
            public UiLayer Layer;
            public UiView View;
            public GameObject PreviousFocus;
        }

        private readonly EventSystem _eventSystem;
        private readonly bool _usesLegacyEventSystem;

        /// <summary>注入应用拥有的输入系统；无参兼容入口使用当前场景系统，不取得其释放所有权。</summary>
        public UiService(EventSystem eventSystem = null)
        {
            _eventSystem = eventSystem;
            _usesLegacyEventSystem = eventSystem == null;
        }

        /// <summary>本应用的焦点系统；显式注入后不回退到其他应用的全局当前系统。</summary>
        public EventSystem EventSystem =>
            _usesLegacyEventSystem ? UnityEngine.EventSystems.EventSystem.current : _eventSystem;

        private readonly Dictionary<string, Definition> _definitions =
            new Dictionary<string, Definition>();
        private readonly List<Entry> _open = new List<Entry>();
        private readonly SemaphoreSlim _serial = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _disposeTask;
        private bool _disposed;

        /// <inheritdoc/>
        public bool BlocksGameplay
        {
            get
            {
                foreach (Entry entry in _open)
                {
                    if (entry.Layer == UiLayer.Modal)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>当前持有的页面、模态窗口与覆盖层总数，含暂时隐藏的页面。</summary>
        public int OpenCount => _open.Count;

        /// <summary>主线程登记工厂；重复 ID 或非法参数失败，不接管外部已创建视图。</summary>
        public void Register(
            string id,
            UiLayer layer,
            Func<CancellationToken, ValueTask<UiView>> factory
        )
        {
            if (_disposed || string.IsNullOrWhiteSpace(id) || factory == null)
            {
                throw new ArgumentException("invalid-ui-registration");
            }
            _definitions.Add(id, new Definition { Layer = layer, Factory = factory });
        }

        /// <inheritdoc/>
        public async ValueTask OpenAsync(string id, CancellationToken cancellationToken = default)
        {
            using (
                var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token
                )
            )
            {
                await _serial.WaitAsync(linked.Token);
                try
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(UiService));
                    }
                    foreach (Entry entry in _open)
                    {
                        if (entry.Id == id)
                        {
                            return;
                        }
                    }
                    if (!_definitions.TryGetValue(id, out Definition definition))
                    {
                        throw new ArgumentException("ui-not-registered", nameof(id));
                    }
                    // 工厂可能在初始化期间选择新控件，必须在调用前保存原界面焦点。
                    GameObject previousFocus =
                        EventSystem != null ? EventSystem.currentSelectedGameObject : null;
                    UiView view;
                    try
                    {
                        view = await definition.Factory(linked.Token);
                        if (linked.IsCancellationRequested)
                        {
                            await view.DisposeAsync();
                            linked.Token.ThrowIfCancellationRequested();
                        }
                    }
                    catch
                    {
                        // 尚未登记到打开列表，失败或取消不能依赖 CloseAtAsync 恢复焦点。
                        if (
                            EventSystem != null
                            && previousFocus != null
                            && previousFocus.activeInHierarchy
                        )
                        {
                            EventSystem.SetSelectedGameObject(previousFocus);
                        }
                        throw;
                    }
                    _open.Add(
                        new Entry
                        {
                            Id = id,
                            Layer = definition.Layer,
                            View = view,
                            PreviousFocus = previousFocus,
                        }
                    );
                    RefreshLayers();
                }
                finally
                {
                    _serial.Release();
                }
            }
        }

        /// <inheritdoc/>
        public async ValueTask CloseAsync(string id, CancellationToken cancellationToken = default)
        {
            await _serial.WaitAsync(cancellationToken);
            try
            {
                for (int index = _open.Count - 1; index >= 0; index--)
                {
                    if (_open[index].Id == id)
                    {
                        await CloseAtAsync(index);
                        break;
                    }
                }
            }
            finally
            {
                _serial.Release();
            }
        }

        /// <inheritdoc/>
        public async ValueTask BackAsync(CancellationToken cancellationToken = default)
        {
            await _serial.WaitAsync(cancellationToken);
            try
            {
                for (int index = _open.Count - 1; index >= 0; index--)
                {
                    if (_open[index].Layer != UiLayer.Overlay)
                    {
                        await CloseAtAsync(index);
                        return;
                    }
                }
            }
            finally
            {
                _serial.Release();
            }
        }

        private async ValueTask CloseAtAsync(int index)
        {
            Entry entry = _open[index];
            _open.RemoveAt(index);
            try
            {
                await entry.View.DisposeAsync();
            }
            finally
            {
                RefreshLayers();
                if (
                    EventSystem != null
                    && entry.PreviousFocus != null
                    && entry.PreviousFocus.activeInHierarchy
                )
                {
                    EventSystem.SetSelectedGameObject(entry.PreviousFocus);
                }
            }
        }

        private void RefreshLayers()
        {
            // 稳定按层排列，同层仍保持打开顺序。
            for (int index = 1; index < _open.Count; index++)
            {
                Entry moving = _open[index];
                int destination = index;
                while (destination > 0 && _open[destination - 1].Layer > moving.Layer)
                {
                    _open[destination] = _open[destination - 1];
                    destination--;
                }
                _open[destination] = moving;
            }
            bool pageVisible = false;
            bool modalAbove = false;
            for (int index = _open.Count - 1; index >= 0; index--)
            {
                Entry entry = _open[index];
                Canvas canvas = entry.View.Root.GetComponent<Canvas>();
                if (canvas != null)
                {
                    canvas.overrideSorting = true;
                    canvas.sortingOrder = index * 10;
                }
                bool visible = entry.Layer != UiLayer.Page || !pageVisible;
                entry.View.Root.SetActive(visible);
                if (entry.Layer == UiLayer.Page)
                {
                    pageVisible = true;
                }
                CanvasGroup group = entry.View.Root.GetComponent<CanvasGroup>();
                if (group == null)
                {
                    group = entry.View.Root.AddComponent<CanvasGroup>();
                }
                group.interactable = !modalAbove;
                group.blocksRaycasts = !modalAbove;
                if (entry.Layer == UiLayer.Modal)
                {
                    modalAbove = true;
                }
            }
        }

        /// <summary>主线程幂等释放；并发调用共享完成或失败，释放异常交给调用方观察。</summary>
        public ValueTask DisposeAsync()
        {
            if (_disposeTask == null)
            {
                _disposed = true;
                var completion = new TaskCompletionSource<bool>();
                _disposeTask = completion.Task;
                _ = DisposeObservedAsync(completion);
            }
            return new ValueTask(_disposeTask);
        }

        private async Task DisposeObservedAsync(TaskCompletionSource<bool> completion)
        {
            Exception cancellationError = null;
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception)
            {
                cancellationError = exception;
            }
            try
            {
                await DisposeCoreAsync();
                if (cancellationError != null)
                {
                    throw cancellationError;
                }
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeCoreAsync()
        {
            await _serial.WaitAsync();
            var errors = new List<Exception>();
            try
            {
                while (_open.Count > 0)
                {
                    try
                    {
                        await CloseAtAsync(_open.Count - 1);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(exception);
                    }
                }
            }
            finally
            {
                _serial.Release();
            }
            if (errors.Count > 0)
            {
                throw new AggregateException(errors);
            }
        }
    }
}
