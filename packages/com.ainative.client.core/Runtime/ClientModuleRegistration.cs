#nullable enable
using System;
using System.Collections.Generic;

namespace AiNative.Client.Core
{
    /// <summary>模块及其显式启动依赖的不可变注册项；依赖 ID 使用区分大小写的序号比较。</summary>
    public sealed class ClientModuleRegistration
    {
        /// <summary>
        /// 复制依赖 ID，不接管模块生命周期；由 ClientApplication 负责启动和停止。
        /// ID 和依赖 ID 不得为空白；依赖是否存在及是否成环由应用构造时验证。
        /// </summary>
        public ClientModuleRegistration(
            string id,
            IClientModule module,
            params string[] dependencies
        )
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Module ID cannot be blank.", nameof(id));
            }
            Module = module ?? throw new ArgumentNullException(nameof(module));
            if (dependencies == null)
            {
                throw new ArgumentNullException(nameof(dependencies));
            }
            var copied = new string[dependencies.Length];
            for (int index = 0; index < dependencies.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(dependencies[index]))
                {
                    throw new ArgumentException(
                        "Dependency ID cannot be blank.",
                        nameof(dependencies)
                    );
                }
                copied[index] = dependencies[index];
            }
            Id = id;
            Dependencies = Array.AsReadOnly(copied);
        }

        /// <summary>区分大小写的稳定模块标识；不隐式裁剪或规范化。</summary>
        public string Id { get; }

        /// <summary>应用生命周期管理的模块实例；注册对象本身不启动或释放它。</summary>
        public IClientModule Module { get; }

        /// <summary>启动前必须完成的模块 ID 只读快照；重复依赖按同一条边处理。</summary>
        public IReadOnlyList<string> Dependencies { get; }
    }
}
