using System;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AiNative.Client.UI
{
    /// <summary>
    /// 显式所属 EventSystem 的 uGUI 输入框。修正原生 InputField 延迟激活读取全局 current 的边界。
    /// 限 Unity 主线程；多应用必须在交互前配置所属系统。未配置时保留旧输入框行为。
    /// 系统为借用引用，不负责释放；所属系统销毁或停用后停止处理输入。
    /// 应通过事件系统发送选择/取消选择；基类非虚拟的直接输入处理 API 不提供额外上下文隔离。
    /// </summary>
    public sealed class ScopedInputField : InputField
    {
        private EventSystem _owner;
        private bool _configured;

        /// <summary>由 View 在首次交互前注入应用输入系统；不允许借用另一应用的系统。</summary>
        public void ConfigureEventSystem(EventSystem owner)
        {
            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }
            _owner = owner;
            _configured = true;
        }

        /// <summary>延迟激活和输入状态更新只在所属系统上下文执行，结束后恢复原全局上下文。</summary>
        protected override void LateUpdate()
        {
            if (_configured && (_owner == null || !_owner.isActiveAndEnabled))
            {
                return;
            }
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured)
                {
                    EventSystem.current = _owner;
                }
                base.LateUpdate();
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        /// <summary>指针入口同样使用所属系统；不依赖调用方提前设置全局 current。</summary>
        public override void OnPointerDown(PointerEventData eventData)
        {
            if (_configured && (_owner == null || !_owner.isActiveAndEnabled))
            {
                return;
            }
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured)
                {
                    EventSystem.current = _owner;
                }
                base.OnPointerDown(eventData);
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        /// <summary>在所属系统中处理输入事件；同步嵌套调用结束后恢复调用者上下文。</summary>
        public override void OnSelect(BaseEventData eventData)
        {
            if (_configured && (_owner == null || !_owner.isActiveAndEnabled))
            {
                return;
            }
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured)
                {
                    EventSystem.current = _owner;
                }
                base.OnSelect(eventData);
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        /// <summary>在所属系统中处理输入事件；同步嵌套调用结束后恢复调用者上下文。</summary>
        public override void OnDeselect(BaseEventData eventData)
        {
            if (_configured && (_owner == null || !_owner.isActiveAndEnabled))
            {
                return;
            }
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured)
                {
                    EventSystem.current = _owner;
                }
                base.OnDeselect(eventData);
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        /// <summary>在所属系统中处理输入事件；同步嵌套调用结束后恢复调用者上下文。</summary>
        public override void OnUpdateSelected(BaseEventData eventData)
        {
            if (_configured && (_owner == null || !_owner.isActiveAndEnabled))
            {
                return;
            }
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured)
                {
                    EventSystem.current = _owner;
                }
                base.OnUpdateSelected(eventData);
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        /// <summary>停用时仍完成基类释放；拥有者有效时，输入法收尾也使用所属系统。</summary>
        protected override void OnDisable()
        {
            EventSystem previous = EventSystem.current;
            try
            {
                if (_configured && _owner != null && _owner.isActiveAndEnabled)
                {
                    EventSystem.current = _owner;
                }
                base.OnDisable();
            }
            finally
            {
                RestoreCurrent(previous);
            }
        }

        private static void RestoreCurrent(EventSystem previous)
        {
            // EventSystem.current 不接受已注销或 null 实例；销毁期间不复活旧系统。
            if (previous != null && previous.isActiveAndEnabled)
            {
                EventSystem.current = previous;
            }
        }
    }
}
