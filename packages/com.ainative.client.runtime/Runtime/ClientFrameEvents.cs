using System;
using UnityEngine;

namespace AiNative.Client.Runtime
{
    /// <summary>
    /// 应用实例拥有的 Unity 帧和前后台通知入口，所有回调在 Unity 主线程同步触发。
    /// 不驱动网络、预测或业务 Tick；应用应只选择一个现有驱动入口，避免重复推进。
    /// 订阅由应用的 BindingScope 等拥有者解绑，销毁时也清空监听；回调异常直接传播。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientFrameEvents : MonoBehaviour
    {
        /// <summary>Update 阶段，参数为不受 timeScale 影响的帧间隔，单位秒。</summary>
        public event Action<float> Updating;

        /// <summary>FixedUpdate 阶段，参数为 Unity 固定步长，单位秒；不是服务端固定 Tick。</summary>
        public event Action<float> FixedUpdating;

        /// <summary>LateUpdate 阶段，参数为不受 timeScale 影响的帧间隔，单位秒。</summary>
        public event Action<float> LateUpdating;

        /// <summary>应用获得或失去焦点时通知；true 表示获得焦点，不代表网络已恢复。</summary>
        public event Action<bool> FocusChanged;

        /// <summary>Unity 应用暂停状态通知；true 表示暂停，不承诺后台仍会执行帧回调。</summary>
        public event Action<bool> PauseChanged;

        private void Update()
        {
            Updating?.Invoke(Time.unscaledDeltaTime);
        }

        private void FixedUpdate()
        {
            FixedUpdating?.Invoke(Time.fixedDeltaTime);
        }

        private void LateUpdate()
        {
            LateUpdating?.Invoke(Time.unscaledDeltaTime);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            FocusChanged?.Invoke(hasFocus);
        }

        private void OnApplicationPause(bool isPaused)
        {
            PauseChanged?.Invoke(isPaused);
        }

        private void OnDestroy()
        {
            // 销毁入口只断开本组件持有的监听；异步业务必须由应用先显式停止。
            Updating = null;
            FixedUpdating = null;
            LateUpdating = null;
            FocusChanged = null;
            PauseChanged = null;
        }
    }
}
