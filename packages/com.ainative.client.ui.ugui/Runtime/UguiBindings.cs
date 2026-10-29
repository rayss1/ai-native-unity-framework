using System;
using System.ComponentModel;
using AiNative.Client.Core.Mvvm;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AiNative.Client.UI
{
    /// <summary>主线程强类型绑定；调用方在界面关闭时释放 BindingScope。</summary>
    public static class UguiBindings
    {
        /// <summary>初始同步及后续属性通知；不使用字符串属性路径或反射。</summary>
        public static void Bind<T>(
            this BindingScope scope,
            BindableProperty<T> property,
            Action<T> render
        )
        {
            render(property.Value);
            PropertyChangedEventHandler changed = (_, __) => render(property.Value);
            property.PropertyChanged += changed;
            scope.Add(() => property.PropertyChanged -= changed);
        }

        /// <summary>输入双向绑定；程序回写不再次触发输入事件。</summary>
        public static void Bind(
            this BindingScope scope,
            InputField input,
            BindableProperty<string> property
        )
        {
            scope.Bind(property, value => input.SetTextWithoutNotify(value ?? string.Empty));
            UnityAction<string> changed = value => property.Value = value;
            input.onValueChanged.AddListener(changed);
            scope.Add(() => input.onValueChanged.RemoveListener(changed));
        }

        /// <summary>开关双向绑定，解绑时只移除本绑定拥有的监听。</summary>
        public static void Bind(
            this BindingScope scope,
            Toggle toggle,
            BindableProperty<bool> property
        )
        {
            scope.Bind(property, toggle.SetIsOnWithoutNotify);
            UnityAction<bool> changed = value => property.Value = value;
            toggle.onValueChanged.AddListener(changed);
            scope.Add(() => toggle.onValueChanged.RemoveListener(changed));
        }

        /// <summary>命令执行状态驱动按钮；错误由命令公开状态承载，入口观察意外异常。</summary>
        public static void Bind(this BindingScope scope, Button button, AsyncCommand command)
        {
            PropertyChangedEventHandler changed = (_, __) =>
                button.interactable = command.CanExecute;
            UnityAction clicked = () => ExecuteObservedAsync(command).Forget();
            changed(null, null);
            command.PropertyChanged += changed;
            button.onClick.AddListener(clicked);
            scope.Add(() =>
            {
                command.PropertyChanged -= changed;
                button.onClick.RemoveListener(clicked);
            });
        }

        private static async UniTask ExecuteObservedAsync(AsyncCommand command)
        {
            try
            {
                await command.ExecuteAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }
    }
}
