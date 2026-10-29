# 客户端框架开发指南

本指南对应第一期代码，架构选择见 [ADR-0021](../ADR/0021-client-framework-foundation.md)，编码要求见 [客户端代码规范](client-coding-standards.md)。完成状态以 [当次验证记录](client-framework-validation-2026-10-29.md) 为准；接口存在不等于平台验收通过。

## 模块与装配

| 位置 | 职责 |
| --- | --- |
| `packages/com.ainative.client.core` | 不依赖 Unity 的模块生命周期、诊断与 MVVM 基础类型 |
| `packages/com.ainative.client.runtime` | Unity 帧/前后台通知、日志诊断、Additive 内容场景切换 |
| `packages/com.ainative.client.composition.vcontainer` | VContainer 显式作用域装配 |
| `packages/com.ainative.client.async.unitask` | Unity 异步操作与下一帧适配，向上提供 ValueTask |
| `packages/com.ainative.client.assets` | 项目自有资源 ID、句柄和服务契约 |
| `packages/com.ainative.client.assets.assetbundle` | 本地目录、Bundle 构建、加载、引用和实例所有权 |
| `packages/com.ainative.client.ui.ugui` | 属性绑定、界面管理、稳定键列表 |
| `client/UnityProject/Assets/AiNative.BattleClient` | 产品配置、具体 ViewModel、场景和组合根 |

新增可复用能力时，在所属包的 Runtime 中实现；Editor 工具单独放 Editor 程序集。业务只引用需要的项目契约。不要让 Shared、协议或预测契约引用 Unity、UniTask 或 VContainer 类型。

有启动和停止工作的对象实现 `IClientModule.StartAsync(CancellationToken)` 和 `StopAsync(CancellationToken)`。将实例以依赖先后的顺序传给 `ClientApplication`。它按注册顺序启动，逆序停止，启动部分失败也会清理。模块自己的停止必须允许部分启动与重复调用。调用方应显式等待 `ClientApplication.StopAsync()`，再销毁容器或 GameObject。

`ClientServiceScope` 只供组合根使用。`CreateChild(configure)` 创建流程或界面作用域；先停止其中的异步服务，再 Dispose 子作用域，最后 Dispose 父作用域。不要把容器传给业务对象充当服务查找器。

应用入口为 `FrameworkClientRoot`。现有 UI 通过 `GetOrCreate(gameObject).AssetsAsync(token)` 和 `UiAsync(token)` 获取初始化后的服务。`FrameworkClientSettings` 保留应用拥有的配置，框架接收快照。新增模块必须明确拥有者、线程、取消、失败回滚与释放顺序；不能仅依赖异步 `OnDestroy`。

## 添加 ViewModel 和页面

ViewModel 使用 `BindableProperty<T>` 表达状态，使用 `AsyncCommand` 表达交互，不引用 Unity 控件。`AsyncCommand` 默认禁止重入，提供 `IsExecuting`、`CanExecute`、`Error`；业务条件变化后调用 `RefreshCanExecute()`。关闭模型时 Dispose 命令，异步操作仍需检查自己的取消令牌，避免迟到结果写回已关闭状态。

View 创建一个 `BindingScope`，用 `AiNative.Client.UI.UguiBindings` 绑定控件：

```csharp
var bindings = new BindingScope();
bindings.Bind(nameInput, viewModel.Name);
bindings.Bind(submitButton, viewModel.Submit);
bindings.Bind(viewModel.Status, value => statusText.text = value);
```

`InputField` 与 `Toggle` 使用双向绑定；属性到 Action 用于单向显示。View 关闭时 Dispose 此 BindingScope，随后释放 ViewModel 和界面拥有的列表对象。不要使用 `RemoveAllListeners()` 清掉其他拥有者注册的事件。

动态列表使用 `KeyedViewList<TKey, TValue, TView>`，指定稳定业务键、创建、渲染和释放回调，再调用 `Apply(items)`。不要用列表索引作为玩家或邀请的身份。行拥有的命令和绑定由行释放回调清理。

界面向 `UiService.Register(id, UiLayer.Page/Modal/Overlay, factory)` 注册异步工厂，通过 `OpenAsync`、`CloseAsync`、`BackAsync` 管理。工厂返回 `UiView(instance, release)`；release 必须解除绑定、销毁实例、等待实际销毁后释放资源句柄。工厂取消或失败时也必须清理已取得的对象。界面 ID 默认单实例。

现有产品实现可参考 `TopologyViewModel`、`TopologyClientView` 和 `TopologyClientFlow.CreateUiAsync`。页面 ID 为 `topology`，其内部登录、大厅、匹配、战斗和结算状态由 ViewModel.Page 控制。应用业务按实际拥有权拆出 `TopologyAccountLobbyService` 和 `TopologyMatchController`：前者拥有 Gate 会话、账号资料、队伍与邀请，负责候选登录验证后的会话替换和大厅操作；后者拥有原始分配、排队请求 ID 和 Battle 会话，负责身份校验、启动与释放。Flow 的公共属性继续兼容转发，网络 Pump、Update 和 FixedUpdate 仍在原位置执行。恢复、结算和界面请求排队状态机仍由 Flow 协调，尚未把它简化成纯装配入口。

调用方仍通过 Flow 的应用 API 操作业务，不直接替换服务内部状态。退出先等待 Flow 停止界面和账号请求，再由对局控制器释放 Battle；返回大厅同样经控制器释放对局。新增业务应归入具有实际状态与资源所有权的服务，不增加只转发一层的接口包装。

输入由应用统一隔离。每个 FrameworkClientRoot 独立拥有 MultiplayerEventSystem 和 InputSystemUIInputModule，通过 UiService 显式注入 View；playerRoot 指向本应用的 UI 根，选择和焦点恢复只使用该实例。正常应用路径不借用 EventSystem.current；无参 UiService 与旧 Bind 入口仅保留兼容回退。根在 UI 关闭后才禁用、销毁所属 EventSystem，并等待销毁完成后重试，不能影响其他应用实例。文本输入、失焦和模态状态必须阻止游戏输入，不要改变 Input System 的既有采样顺序。 多应用页面的输入框使用 `ScopedInputField`，在首次交互前调用 `ConfigureEventSystem` 注入同一所属系统；其延迟激活和事件入口临时使用该上下文并恢复。普通 InputField 仍可用于旧的单系统页面，但不能据此承诺多系统焦点隔离。导航通过所属 EventSystem 发送选择，避免直接调用基类中依赖全局上下文的非虚拟输入处理 API。

现有产品 UI 的输入框迁移入口为 `TopologyUiPrefabBuilder.MigrateExistingInputFields`。它通过 Unity 序列化 API 仅替换脚本，保存后核对原 fileID、View 字段引用与组件数量；不重建整个 Prefab。`TopologyUiPrefabBuilder.Build` 用于显式完整重建界面，执行前应另存已有界面编辑。迁移后重新构建本地内容，不能继续使用旧 Bundle。

## 添加本地资源

应用构建入口为 `FrameworkContentBuild`。Unity 菜单 `AI Native/Framework/Build Local Content` 调用 `LocalAssetBundleBuilder.BuildManaged`，输出到 `StreamingAssets/AiNativeContent`。进入 Play 和构建 Player 也会触发此入口，构建失败不得静默继续使用 Resources。

在该入口的资源定义中添加 `BundleAssetDefinition(logicalId, assetPath, bundleName, group)`。逻辑 ID 应稳定且表达用途；不要把物理路径直接暴露给业务。目前包括 `ui.topology`、`character.network` 和 `material.player`。源 Prefab 位置暂时保留以保护 GUID 和已有引用，生产加载路径已经通过资源服务接入。

构建目录包含平台、依赖、大小及哈希。重复 ID、无效引用与依赖环属于构建错误，应修正定义而不是绕过验证。切换平台后重新构建匹配目标的内容，不能把桌面 Bundle 交给 Android Player。

```csharp
AssetHandle<GameObject> handle = await assets.LoadAsync<GameObject>(
    new AssetId("ui.topology"), cancellationToken);
try
{
    // 在主线程使用 handle.Value；实例的生命周期不得超过此句柄。
}
finally
{
    await assets.ReleaseAsync(handle);
}
```

每次获取返回独立所有权句柄。调用方取消不应影响其他持有者；不再使用时调用创建服务的 `ReleaseAsync`，不可把句柄交给另一个服务释放。重复释放幂等。对 GameObject 实例必须先 Destroy、等待销毁，再释放源句柄；本地实现也提供 `InstantiateAsync` 返回 `AssetInstance`，由实例拥有者显式等待 `DisposeAsync`。预加载使用 `PreloadAsync(new AssetGroupId(group), token)`，由服务作用域管理预加载持有。

当前只支持随包内容。远端下载、在线目录切换和 HybridCLR 不在此实现范围；后续扩展仍须遵守签名校验与原子切换约束。

## 添加内容场景

`ClientSceneService` 构造时接收 `SceneId` 到构建场景路径的映射。把内容场景加入 Player 的构建场景列表，保留启动场景，再通过 `SwitchAsync(id, token)` 串行切换。服务 Additive 加载新场景成功后才卸载旧场景；无法中断的 Unity 加载完成后，若取消已发生则卸载迟到结果。

停止场景所属流程时等待 `UnloadAsync()`。不要让两个场景服务分别拥有同一个已加载场景，也不要使用新旧入口同时驱动网络或预测。Player 构建入口使用 `FrameworkBootstrapBuild.GetPlayerScenes()`：首场景为 `FrameworkBootstrap`，由其 Additive 加载既有 `BattleClient`。启动失败保留错误与重试入口；正常退出经 `FrameworkClientRoot.RequestQuit(exitCode)` 转交启动场景，先等应用停止，再卸载内容场景，最后保留退出码退出。直接打开旧场景的开发入口仍有效。

## 开发检查

新增代码遵循中文说明性注释、英文标识符、明确单位和所有权。格式、依赖检查入口以 [代码规范](client-coding-standards.md) 为准。针对实际风险补充测试：生命周期失败/取消、界面解绑、资源迟到释放和平台内容匹配。Unity EditMode、PlayMode、Player、IL2CPP 与真实联机分别记录，零测试发现或跳过不能写成通过。

## 帧通知与诊断

需要 Unity 阶段通知时，在应用拥有的对象添加 `ClientFrameEvents`；`Updating`、`FixedUpdating`、`LateUpdating` 的参数单位为秒，前后台分别订阅 `FocusChanged` 和 `PauseChanged`。使用所属 `BindingScope` 解绑。该组件不会驱动网络，不能再注册第二个预测 Tick 入口。

`UnityClientDiagnostics` 输出启动与异常日志；开发构建中的应用根面板显示资源句柄、已加载 Bundle、界面数和启动状态。既有网络诊断继续由对局界面提供。资源计数归零不等于进程内存已回收；稳定性报告需同时记录环境、预热和采样方式。

## 验证入口

```sh
python3 tools/formatting/format_csharp.py --scope client --check
python3 -m unittest discover -s tools/formatting/tests -v
dotnet run --project tools/ArchitectureCheck -- --root .
dotnet test AiNative.sln -c Release
unity test client/UnityProject --mode EditMode --output artifacts/client-editmode.xml
```

关闭域重载的重复生命周期用例应独立运行 `--mode PlayMode --filter FrameworkLifetimeTests`。真实交互流程筛选 `TopologyFrameworkEndToEndTests`，需先启动 2 人、1200 ticks 的独占本地拓扑。P1 战斗/复活用例需要独立的 7200 ticks 拓扑，不能复用短局配置。`Development` 配置已更新为当前 361 项 EditMode、39 个 fixture 的精确清单；Baseline、Candidate、TerminalDelivery、Current 历史配置保留原数量。本次 `editmode-final.xml` 为 361/361，`playmode-final.xml` 为 52/52，均无失败或跳过。已有本地 PowerShell 7.5.4，实际执行的 source-profile 和 NUnit 报告校验测试均通过。独立平台、Domain Reload 关闭、20 局联机及未关闭内存风险见 [验证记录](client-framework-validation-2026-10-29.md)。

### 复现真实流程与稳定性检查

本地服务准备见 [手工验证环境](unity-manual-validation.md) 与 [拓扑运维](server-topology-operations.md)。在独占本地端口环境启动 2 人、1200 ticks 拓扑；本机使用 SDK 10.0.202 与 PostgreSQL 17.11。不要将此配置用于需要 7200 ticks 的 P1 用例。

```sh
python3 tools/topology/manual.py start --players-per-room 2 --match-ticks 1200
unity test client/UnityProject --mode PlayMode --filter TopologyFrameworkEndToEndTests.TwoInteractiveClientsUseBundlesUiReconnectSettlementAndReturnToLobby --output artifacts/framework-e2e.xml --timeout 600
unity test client/UnityProject --mode PlayMode --filter TopologyFrameworkEndToEndTests.TwentyInteractiveMatchesReturnOwnedResourcesToBaseline --output artifacts/framework-soak.xml --timeout 1500
python3 tools/topology/manual.py stop
```

可以通过 `--sdk-path`、`--postgres-bin-directory`、`--postgres-port` 和 `--run-directory` 显式指定本机环境；停止时传入同一 run-directory。测试每轮使用独立账号，真实注册后关闭再登录，以 UI 按钮推进流程并核对原对局身份；不是只调用内存假服务。稳定性测试先预热 1 局，再完整运行 20 局，采样输出写入 NUnit XML。它会在停止边界执行完整 GC，不能作为产品帧内 GC 或 Tick 零分配的性能数据。

关闭 Domain Reload 的测试应另起 Unity 批次：

```sh
unity test client/UnityProject --mode PlayMode --filter FrameworkLifetimeTests --output artifacts/framework-lifetime.xml --timeout 600
```

测试临时调整并恢复 Editor 设置；若上次测试崩溃留下恢复快照，应先检查并恢复，不覆盖未知状态。所有 Unity 构建和测试串行使用同一工程，避免互相修改活动平台或占用 Library。
