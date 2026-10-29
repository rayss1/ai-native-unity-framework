#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiNative.Client.Core
{
    /// <summary>
    /// 按显式依赖排序启动、逆序停止模块；旧构造保持传入顺序。生命周期入口应由同一所有者上下文调用；
    /// 异步等待期间允许 StopAsync 中断启动，不启动线程池工作。
    /// </summary>
    public sealed class ClientApplication
    {
        private readonly IClientModule[] _modules;
        private CancellationTokenSource? _lifetime;
        private Task? _startTask;
        private Task? _stopTask;
        private Exception? _startupCleanupFailure;
        private int _startedCount;
        private bool _isStarting;

        /// <summary>复制显式模块顺序，不扫描程序集；同一个模块不能注册两次。</summary>
        public ClientApplication(IEnumerable<IClientModule> modules)
        {
            if (modules == null)
            {
                throw new ArgumentNullException(nameof(modules));
            }
            var ordered = new List<IClientModule>();
            foreach (IClientModule module in modules)
            {
                if (module == null)
                {
                    throw new ArgumentException("Module cannot be null.", nameof(modules));
                }
                foreach (IClientModule prior in ordered)
                {
                    if (ReferenceEquals(prior, module))
                    {
                        throw new ArgumentException("Module registered twice.", nameof(modules));
                    }
                }
                ordered.Add(module);
            }
            _modules = ordered.ToArray();
        }

        /// <summary>
        /// 复制注册项并在构造时验证全部依赖；无副作用地拒绝重复 ID/实例、缺失依赖和环。
        /// 每次选择注册顺序最早的可启动模块；失败清理及停止按实际启动顺序逆序执行。
        /// </summary>
        public ClientApplication(IEnumerable<ClientModuleRegistration> registrations)
        {
            if (registrations == null)
            {
                throw new ArgumentNullException(nameof(registrations));
            }
            var entries = new List<ClientModuleRegistration>();
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ClientModuleRegistration registration in registrations)
            {
                if (registration == null)
                {
                    throw new ArgumentException(
                        "Registration cannot be null.",
                        nameof(registrations)
                    );
                }
                if (indices.ContainsKey(registration.Id))
                {
                    throw new ArgumentException(
                        "Duplicate module ID: " + registration.Id,
                        nameof(registrations)
                    );
                }
                foreach (ClientModuleRegistration prior in entries)
                {
                    if (ReferenceEquals(prior.Module, registration.Module))
                    {
                        throw new ArgumentException(
                            "Module instance registered twice.",
                            nameof(registrations)
                        );
                    }
                }
                indices.Add(registration.Id, entries.Count);
                entries.Add(registration);
            }
            var remainingDependencies = new int[entries.Count];
            var dependents = new List<int>[entries.Count];
            for (int index = 0; index < entries.Count; index++)
            {
                dependents[index] = new List<int>();
            }
            for (int index = 0; index < entries.Count; index++)
            {
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dependency in entries[index].Dependencies)
                {
                    if (!indices.TryGetValue(dependency, out int dependencyIndex))
                    {
                        throw new ArgumentException(
                            "Missing dependency: " + dependency,
                            nameof(registrations)
                        );
                    }
                    if (distinct.Add(dependency))
                    {
                        dependents[dependencyIndex].Add(index);
                        remainingDependencies[index]++;
                    }
                }
            }
            _modules = new IClientModule[entries.Count];
            var selected = new bool[entries.Count];
            for (int position = 0; position < entries.Count; position++)
            {
                int ready = -1;
                // 每选出一个节点就重新比较注册顺序，避免整批 ready 入队改变稳定次序。
                for (int index = 0; index < entries.Count; index++)
                {
                    if (!selected[index] && remainingDependencies[index] == 0)
                    {
                        ready = index;
                        break;
                    }
                }
                if (ready < 0)
                {
                    throw new ArgumentException(
                        "Module dependency cycle detected.",
                        nameof(registrations)
                    );
                }
                selected[ready] = true;
                _modules[position] = entries[ready].Module;
                foreach (int dependent in dependents[ready])
                {
                    remainingDependencies[dependent]--;
                }
            }
        }

        /// <summary>全部模块完成启动后为真，开始停止时立即为假。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// 启动新生命周期；并发入口共享正在执行的启动，运行状态下不重复启动。
        /// 停止尚未完成时拒绝启动。首次调用的取消令牌控制整个启动操作。
        /// 启动失败先完成清理；清理也失败时以 AggregateException 同时报告，模块仍由应用拥有。
        /// </summary>
        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopTask != null && !_stopTask.IsCompleted)
            {
                throw new InvalidOperationException("Application is stopping.");
            }
            if (_isStarting)
            {
                return new ValueTask(_startTask!);
            }
            if (IsRunning)
            {
                return default;
            }
            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _startTask = completion.Task;
            _stopTask = null;
            _startupCleanupFailure = null;
            _isStarting = true;
            _ = StartCoreAsync(_lifetime.Token, completion);
            return new ValueTask(completion.Task);
        }

        /// <summary>
        /// 取消正在启动的模块并等待逆序清理；重复调用共享同一次停止。
        /// 令牌只控制停止受理前的取消，受理后的清理不可中途放弃，模块收到 None。
        /// 取消回调或模块释放失败仍继续清理其他模块，错误保留在本次停止结果中。
        /// </summary>
        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopTask != null)
            {
                return new ValueTask(_stopTask);
            }
            if (!_isStarting && !IsRunning && _startedCount == 0)
            {
                return default;
            }
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _stopTask = completion.Task;
            IsRunning = false;
            _ = StopCoreAsync(completion);
            return new ValueTask(completion.Task);
        }

        private async Task StartCoreAsync(
            CancellationToken cancellationToken,
            TaskCompletionSource<bool> completion
        )
        {
            try
            {
                for (int index = 0; index < _modules.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // 在 await 前登记，启动只做了一半的模块也需要回收。
                    _startedCount = index + 1;
                    await _modules[index].StartAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                IsRunning = true;
                _isStarting = false;
                completion.TrySetResult(true);
            }
            catch (Exception failure)
            {
                Exception? cleanupFailure = await StopModulesAsync();
                _startupCleanupFailure = cleanupFailure;
                _isStarting = false;
                IsRunning = false;
                _lifetime?.Dispose();
                _lifetime = null;
                if (cleanupFailure != null)
                {
                    completion.TrySetException(new AggregateException(failure, cleanupFailure));
                }
                else if (failure is OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
        }

        private async Task StopCoreAsync(TaskCompletionSource<bool> completion)
        {
            Exception? cancellationFailure = null;
            try
            {
                _lifetime?.Cancel();
            }
            catch (Exception failure)
            {
                // 用户注册的取消回调抛错也不能阻止剩余模块释放。
                cancellationFailure = failure;
            }
            if (_startTask != null)
            {
                try
                {
                    await _startTask;
                }
                catch (Exception)
                {
                    // 启动调用方观察原始错误，启动路径已经完成部分模块清理。
                }
            }
            Exception? cleanupFailure = await StopModulesAsync();
            if (_startupCleanupFailure != null)
            {
                cleanupFailure =
                    cleanupFailure == null
                        ? _startupCleanupFailure
                        : new AggregateException(_startupCleanupFailure, cleanupFailure);
            }
            IsRunning = false;
            _lifetime?.Dispose();
            _lifetime = null;
            if (cancellationFailure != null && cleanupFailure != null)
            {
                completion.TrySetException(
                    new AggregateException(cancellationFailure, cleanupFailure)
                );
            }
            else if (cancellationFailure != null || cleanupFailure != null)
            {
                completion.TrySetException(cancellationFailure ?? cleanupFailure!);
            }
            else
            {
                completion.TrySetResult(true);
            }
        }

        private async Task<Exception?> StopModulesAsync()
        {
            List<Exception>? failures = null;
            while (_startedCount > 0)
            {
                IClientModule module = _modules[--_startedCount];
                try
                {
                    await module.StopAsync(CancellationToken.None);
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
            return failures == null ? null : new AggregateException(failures);
        }
    }
}
