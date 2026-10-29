# ADR-0022：客户端模块依赖编排与异步实现边界

Status: Accepted
Date: 2026-10-29
Decision source: 项目负责人批准本轮 Core 依赖编排及客户端异步实现边界调整
Supersedes in part: ADR-0021 的仅传入顺序启动及 UniTask 仅限适配包的约束

## 背景与决定

ADR-0021 的模块列表顺序可表达简单启动，但模块增多后无法验证缺失依赖或循环。
新增 `ClientModuleRegistration(string id, IClientModule module, params string[] dependencies)`，
其 Id、Module 和 Dependencies 只读；依赖数组防御复制并以只读集合暴露。
ID 按 Ordinal 区分大小写，不自动裁剪。空白 ID、空模块和空白依赖 ID 被拒绝。

`ClientApplication(IEnumerable<ClientModuleRegistration>)` 在构造期间复制注册列表，
拒绝重复 ID、重复实例、缺失依赖、自依赖及一般依赖环。重复依赖 ID 视为同一条边。
稳定 Kahn 排序每次选择原注册序最早的 ready 节点，而非先把一批 ready 节点排队。
验证不调用模块方法；全部验证成功后才允许启动。空列表允许启动为空应用。
旧 `IEnumerable<IClientModule>` 构造保持顺序兼容；直接传 null 字面量需显式类型以选择重载。

模块仍实现原 `IClientModule`，公共异步接口仍为 `ValueTask`/`CancellationToken`。
失败包括当前部分启动模块在内按实际启动顺序逆序清理；未开始模块不清理。
停止取消正在进行的启动并等待清理，清理不可因调用方取消而放弃；原幂等和重启语义不变。
不加入依赖注入服务定位、反射扫描、并行启动、Tick 调度或强制超时。

UniTask 2.5.11 除原适配包外，允许在 `client.runtime`、`client.ui.ugui`、
`client.assets.assetbundle` 的实现内部使用，也允许应用实现选择使用。
复用包的公共及受保护 API 不暴露第三方任务类型；Core、Assets 纯契约、网络、协议和预测包
继续禁止引用 UniTask。VContainer 仍限定原装配适配层；不修改 Shared 或 Server。
ARC010 对实现包放行内部使用，同时检查公共签名、接口隐式公开成员、泛型和 using 别名。
这是源码语法边界检查，不替代编译后的 API 审计或生命周期评审。

## 备选与权衡

仅维护有序列表无需新 API，但无法表达和审查模块依赖；自动扫描与容器排序会隐藏依赖并增加第三方耦合。
选择项目自有显式注册项。稳定排序采用简单扫描，构造复杂度至多平方级，运行时 Tick 无新增开销。
不允许任意包直接使用 UniTask，避免实现便利扩散到可移植契约；不强迫所有 Unity 实现绕过适配器，
允许需要 PlayerLoop 的实现直接使用固定版本，但仍要求可取消、异常可观察且所有权明确。

## 迁移、验证与回滚

应用可逐步把有序模块转换为依赖注册，旧入口可继续使用；排序与停止不变的模块无需修改。
覆盖稳定排序、新就绪节点优先级、依赖快照、无效图、部分失败清理、取消和重启。
Core 同源测试须在 .NET 和 Unity 分别执行；ARC010 用允许和拒绝夹具验证。
本 ADR 不宣称新平台测试已完成。回滚可恢复旧构造调用及原异步适配路径，
不得改变固定 Tick、协议或服务端权威语义。
