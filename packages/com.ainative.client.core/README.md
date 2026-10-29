# 客户端 Core

不依赖 Unity 的生命周期与强类型 MVVM，目标为 .NET Standard 2.1 / C# 9。
Unity 与 .NET 使用同一组测试源码。Core 不引用容器、任务库、网络或 UI 实现。

## 使用

```csharp
var application = new ClientApplication(new IClientModule[] { assetsModule, uiModule });
await application.StartAsync(lifetimeToken);
// 正常退出必须等待释放完成，而不是仅依赖 MonoBehaviour.OnDestroy。
await application.StopAsync();

var name = new BindableProperty<string>("");
using var bindings = new BindingScope();
using var login = new AsyncCommand(token => account.LoginAsync(name.Value, token));
PropertyChangedEventHandler handler = (_, args) => Render(name.Value);
name.PropertyChanged += handler;
bindings.Add(() => name.PropertyChanged -= handler);
await login.ExecuteAsync();
```

示例中的业务、模块和显示由应用提供；Core 不自行寻找全局对象。

## 生命周期与线程

- 所有入口在同一个所有者上下文调用，Unity 通常为主线程；不承诺跨线程并发访问。
- 等待期间允许停止启动：启动收到取消，部分启动模块仍被逆序清理。
- 重复启动共享未完成的启动；重复停止共享停止结果，清理错误也保留。
- StopAsync 的取消令牌只控制受理前取消；受理后清理不可中断，模块收到 None。
- 启动失败时先完成清理，再向启动调用方报告原始错误和清理错误。
- 模块必须配合取消；不使用强制超时抛弃仍拥有资源的后台任务。
- AsyncCommand 拒绝重入，Error 保留业务异常。取消不当作错误，Dispose 后不再通知。
- BindingScope 逆序释放全部订阅；单个失败不会跳过其他订阅，最后聚合报告错误。

应用配置使用应用拥有的强类型不可变快照，不通过 Core 增加弱类型全局字典。

## 验证

```sh
./artifacts/dotnet/dotnet test packages/com.ainative.client.core/Tests/AiNative.Client.Core.Tests.csproj -c Release
```

Unity 消费方需在项目 testables 中添加本包，并执行 `AiNative.Client.Core.Tests`。
首轮本地 .NET 测试已执行，Unity/IL2CPP 由集成阶段单独验证，不能以 .NET 通过替代。
