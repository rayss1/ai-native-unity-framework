# 强类型 uGUI

依照 [ADR-0021](../../Docs/ADR/0021-client-framework-foundation.md)，在 Unity 主线程绑定 Core MVVM 并管理界面所有权。ViewModel 留在应用，不引用控件。

```csharp
var bindings = new BindingScope();
bindings.Bind(nameInput, viewModel.Name);
bindings.Bind(submitButton, viewModel.Submit);
// 关闭时先解绑，再取消/释放 ViewModel 命令，最后销毁视图及释放资源。
bindings.Dispose();
viewModel.Submit.Dispose();
```

泛型 `Bind` 支持单向显示，InputField/Toggle 双向绑定用无通知回写避免循环。Button 绑定只观察命令，不拥有命令；ViewModel 必须自己取消命令并观察进行中的 ExecuteAsync。`KeyedViewList` 通过应用提供的稳定键、创建、渲染与释放回调复用列表项；重复键在更新前拒绝，渲染回调负责排序，Dispose 尝试释放所有项并聚合错误。

```csharp
var ui = new UiService(applicationEventSystem);
ui.Register("lobby", UiLayer.Page, CreateLobbyAsync);
await ui.OpenAsync("lobby", lifetimeToken);
await ui.BackAsync();
await ui.DisposeAsync();
```

工厂返回 `UiView(root, release)`，应用的 release 回调负责解绑、取消命令、销毁实例以及释放源资源。每个 ID 默认单实例；Page 保留下层实例但隐藏显示，Modal 阻挡下层交互，Overlay 不参与返回栈。关闭尝试恢复仍有效的焦点。取消等待中的工厂后，迟到返回的 UiView 仍会释放。UiView 重复释放共享结果，UiService 停止会等待工厂并关闭全部视图。

应用每实例管理一个 EventSystem，显式传给 UiService。无参构造只为旧调用保留当前场景系统回退；显式注入的系统销毁后不借用其他应用的系统。应用将 `BlocksGameplay`、文本输入焦点和应用失焦共同接入游戏输入隔离；服务不自行创建 EventSystem。测试覆盖绑定、列表和界面生命周期，真实 Canvas 焦点与平台输入需另行验收。


多应用文本输入使用 `ScopedInputField`，View 在首次交互前调用 `ConfigureEventSystem(ownedEventSystem)`。它借用系统并隔离 uGUI 延迟激活的全局上下文；所属系统失效后停止处理输入。优先通过所属系统发送选择/取消选择，并先关闭 View 再销毁系统。旧 InputField 字段可引用该派生类型；未配置的旧资产继续保留原生行为，不保证多应用焦点隔离。基类非虚拟直接输入处理 API 不在适配范围内。
