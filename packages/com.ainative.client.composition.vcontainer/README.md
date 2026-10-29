# VContainer 装配适配

固定 VContainer 1.19.0；依照 [ADR-0021](../../Docs/ADR/0021-client-framework-foundation.md)，容器类型只出现在适配层和应用组合根。业务对象使用构造注入，不接收容器。

```csharp
using var application = new ClientServiceScope(builder =>
    builder.Register<AccountService>(Lifetime.Scoped));
using var flow = application.CreateChild(builder =>
    builder.Register<MatchController>(Lifetime.Scoped));
var controller = flow.Resolve<MatchController>();
await controller.StopAsync();
```

示例业务类型由应用定义，注册使用 VContainer 扩展方法。注册在构建后冻结；Resolve 仅用于组合根。作用域由调用方拥有，先等待异步服务停止，再释放子容器，最后父容器；`Dispose` 仅同步释放当前容器拥有的服务，不会代替 StopAsync，也不承诺自动等待异步释放。重复 Dispose 无副作用，释放后解析/创建子作用域会失败。

每个应用建立独立根；不要用静态容器共享账号、对局或 UI 实例。EditMode 测试覆盖构造注入、子作用域及多实例隔离；平台/裁剪结果需独立验证。
