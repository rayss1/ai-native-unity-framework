using System;
using VContainer;

namespace AiNative.Client.Composition
{
    /// <summary>显式创建的容器作用域。仅组合根解析服务，业务对象使用构造注入。</summary>
    public sealed class ClientServiceScope : IDisposable
    {
        private readonly IObjectResolver _resolver;
        private bool _disposed;

        /// <summary>建立独立应用容器；注册在构建完成后冻结。</summary>
        public ClientServiceScope(Action<IContainerBuilder> configure)
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }
            var builder = new ContainerBuilder();
            configure(builder);
            _resolver = builder.Build();
        }

        private ClientServiceScope(IObjectResolver resolver)
        {
            _resolver = resolver;
        }

        /// <summary>仅供装配入口获得显式注册的对象，不向业务层传递作用域。</summary>
        public T Resolve<T>()
        {
            ThrowIfDisposed();
            return _resolver.Resolve<T>();
        }

        /// <summary>创建流程或界面子作用域；调用方先释放子作用域，再释放父作用域。</summary>
        public ClientServiceScope CreateChild(Action<IContainerBuilder> configure)
        {
            ThrowIfDisposed();
            return new ClientServiceScope(_resolver.CreateScope(configure));
        }

        /// <summary>幂等释放当前容器拥有的同步服务；异步服务必须先显式停止。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _resolver.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ClientServiceScope));
            }
        }
    }
}
