using System;
using UnityEngine;

namespace AiNative.Client.Application
{
    /// <summary>应用拥有的启动配置，不保存账号、口令或运行中会话。</summary>
    [CreateAssetMenu(menuName = "AI Native/Client Settings")]
    public sealed class FrameworkClientSettings : ScriptableObject
    {
        [SerializeField]
        private int _targetFrameRate = 60;

        [SerializeField]
        private bool _runInBackground = true;

        /// <summary>配置在启动时校验并冻结，业务层不持有可变 ScriptableObject。</summary>
        public ClientSettingsSnapshot Capture()
        {
            return new ClientSettingsSnapshot(_targetFrameRate, _runInBackground);
        }
    }

    /// <summary>客户端质量和生命周期的不可变启动快照。</summary>
    public readonly struct ClientSettingsSnapshot
    {
        public int TargetFrameRate { get; }
        public bool RunInBackground { get; }

        public ClientSettingsSnapshot(int targetFrameRate, bool runInBackground)
        {
            if (targetFrameRate < 15 || targetFrameRate > 240)
            {
                throw new ArgumentOutOfRangeException(nameof(targetFrameRate));
            }
            TargetFrameRate = targetFrameRate;
            RunInBackground = runInBackground;
        }
    }
}
