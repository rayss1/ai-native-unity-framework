# 客户端框架验证记录：2026-10-29

本记录区分规范整理证据、框架接入代码和实际运行验证。未完成项目不可用历史成绩替代。源代码入口见 [开发指南](client-framework.md)。

## 阶段 0：已有客户端代码整理

范围为 98 个既有项目自有客户端 C# 文件，覆盖客户端应用与客户端 UPM 的 Runtime、Editor、Tests 和自有示例。排除第三方、协议生成物、Input System 生成代码及 Unity 自动生成工程文件；Shared、Server 未纳入此整理。

先用仓库固定 CSharpier 1.3.0 完成排版，再单独执行 Roslyn 语法树控制流整理与语义符号改名。没有通过正则重写控制流。保留公共 API、序列化字段、Unity 回调和事件绑定名称；没有为了命名统一改动资源或 GUID。

| 整理项目 | 数量 |
| --- | ---: |
| 审计源文件 | 98 |
| 为条件/循环添加大括号 | 756 |
| 拆分多字段声明 | 57 |
| 改为 `_camelCase` 的私有非序列化字段 | 106 |
| 基于符号解析的标识符修改 | 707 |
| 翻译为中文的说明性注释行 | 233 |
| 四种预处理配置的可执行 token 比较 | 392 |

预处理配置为默认、`UNITY_EDITOR`、`UNITY_ANDROID`、`UNITY_IOS`。比较以原始源文件为基准，施加明确允许的括号/字段拆分变换，并对预期私有名称进行规范化，再比较可执行 token 的种类和文本。全部 392 次比较通过。这能发现未声明的语法修改，但不是 Unity 序列化、运行时顺序、分配预算或所有平台组合的证明。

本次会话本地证据保存在忽略目录 `artifacts/client-readability/`：

- `sources.json`：完整范围；`before.json`：整理前源文件。
- `Readability.csproj`、`Program.cs`：Roslyn 辅助程序；`renames.txt`：私有字段改名清单。
- `translate.py`：说明性注释替换记录；`summary.json`：统计和未验证项。
- `tests/prediction.trx`、`tests/gate.trx`：定向 .NET 回归报告。

这些是本地运行产物，不是已经提交到版本库的测试证据。后续 Flow/View 的功能迁移单独验收，不能再用阶段 0 的等价比较结果宣称最终所有文件语义未变。

| 实际执行检查 | 结果 |
| --- | --- |
| CSharpier 1.3.0 检查全部 98 个整理文件 | 通过 |
| `git diff --check`，规范整理完成时 | 通过 |
| Prediction .NET 回归 | 102 通过，0 失败，0 跳过 |
| Gate .NET 回归 | 25 通过，0 失败，0 跳过 |
| 四配置 token 等价比较 | 392/392 通过 |

可读性仍需人工按规范检查表审查。语义比较不能证明命名均足够表达业务意义，也不能取代长方法职责审查。已有 Flow 承担多项业务流程的情况留在框架迁移中处理，未利用 partial、region 或单纯行数门槛掩盖。

### 后续可读性审查与局部展开

阶段 0 的机械整理不等于全部代码规范已经满足。复查发现以下既有嵌套三元，并已在独立批次展开成明确分支，保留原条件的求值顺序和短路行为：

- `TopologyClientFlow`：战斗状态选择、武器选择、自动化输入计算。
- `AiNative.CharacterDemo/Runtime/TrainingTarget`：命中闪烁颜色选择。
- `com.ainative.client.character/Runtime/CharacterAnimationDriver`：动画模式选择。
- `com.ainative.client.character/Runtime/CharacterMotor`：移动速度和方向选择。
- `com.ainative.client.character/Runtime/CharacterOrbitCamera`：表现目标与回退目标选择。
- `com.ainative.client.prediction/Tests/Support/ArenaClientProtocolV1`、`ArenaCombatEventProtocolV1`：测试协议线型选择。
- `com.ainative.client.protocol.protobuf/Runtime/ProtobufBattleProtocolCodec`：协议字段线型选择。
- `ArenaGreyboxController`、`TopologyClientFlow`：键盘为空时的输入向量构造。
- `TopologyClientFlowRecoveryTests`：模拟匹配服务的回复构造；固定字节内容不变。
- `ProtobufBattleProtocolCodecTests`：不同帧类型构造和分配诊断输出。

其中控制流自动展开工具仅处理显式类型的单变量初始化或标识符赋值，递归生成 if/else，不改变条件分支内部的调用顺序；其他位置按局部逻辑人工展开。此批次不能纳入最初 392 次机械等价证明；应以代码审查及重跑回归为证据。`BattleClientCompositionRoot` 的对应分支由集成负责人单独处理。

展开后重新运行 Prediction/Gate 定向测试，结果文件为 `tests/prediction-final.trx` 和 `tests/gate-final.trx`。Prediction 102/102、Gate 25/25 通过，均无失败或跳过。曾并行构建时因共享 `AiNative.Realtime.deps.json` 被另一个构建占用而失败，随后改为串行重试；该构建失败不属于测试用例通过或失败。

英文协议术语、错误码、测试固定内容和机器标记按要求保留。既有公共和序列化字段不按私有字段规则强制改名，以免破坏兼容。未声称全量公共 API XML 注释已经人工审查完毕。长方法职责、复杂表达式和命名表达力仍须人工审查，不能由 392 次 token 比较替代。

### 最终人工审查与注释补齐

人工复查后，为 8 个文件补齐 Gate 公共 API 所有权/取消/失败、角色配置单位、诊断继承说明与恢复/预测 ADR 引用。默认、Editor、Android IL2CPP、Windows 四配置共 32 次可执行 token 比较完全一致，包含字面量；本批次只改注释，不改变当前运行的逻辑。审查记录见本地 `readability-final-review.md`。

较长的固定输入构造、单步角色运动、Bundle 构建事务和原队列释放对账仍有单一操作目标，保留显式顺序。Flow 的恢复/命令/帧方法作为应用协调入口保留，未用大量小方法隐藏流程，也不宣称它已成为纯装配类。框架装配入口是 FrameworkClientRoot。兼容名称与序列化字段保持原样，单位和所有权通过中文契约说明。

## 框架与产品接入

已实现 Core 生命周期与 MVVM、UniTask/VContainer 适配、本地 AssetBundle 构建和加载、uGUI 绑定与界面管理、串行 Additive 场景服务，以及应用配置快照、诊断和启动组合根。UniTask 2.5.11、VContainer 1.19.0 使用固定 Git 提交，锁文件与许可证随仓库维护。框架公共异步契约使用 ValueTask，第三方类型不进入 Shared、协议或预测契约。

产品界面通过 UiService 加载真实 `ui.topology` Bundle。View 使用强类型 ViewModel 和 BindingScope；每应用独立拥有 MultiplayerEventSystem，向 UiService 和 View 显式注入。TopologyAccountLobbyService 拥有账号会话、大厅和队伍；TopologyMatchController 拥有匹配身份与 Battle。Flow 保留兼容 API、恢复与结算协调、原网络 Pump/Update/FixedUpdate 顺序，仍有较长的状态协调方法，不能称为纯装配入口。

Player 使用持久 FrameworkBootstrap 启动场景，Additive 加载原 BattleClient。退出显式等待根停止及内容场景卸载；直接打开旧场景和原命令行入口仍可用。既有场景及 meta 保持；自有 UI Prefab 由 Unity Editor API 迁移 ScopedInputField，保留 GUID 与 View 字段引用，新启动场景同样由 Unity Editor API 生成。美术源资源保留原 GUID/路径，生产加载使用 Bundle，不静默回退 Resources；源文件仍在 Resources 的部分会增加 Player 包体，是后续资源物理迁移的已知成本。

集成中发现并单独修复 CF-001 独立 Fantasy Scene 会话路由冲突、CF-002 结算后匹配终态传播等待、CF-004 双应用输入焦点串扰，详见 [问题登记](client-framework-known-issues.md)。这些是功能修复，不纳入阶段 0 语义等价声明。Shared 与 Server 源码没有修改。

## 当前版本实际验证

环境：Apple M4 Pro、48 GB RAM、macOS 26.6.2、Unity 6000.3.23f1 arm64，Unity CLI 1.0.0-beta.13；.NET SDK 10.0.202。机器证据为 `artifacts/client-framework-20261029/environment.json`。以下均为本地工作区运行结果，没有自动提交或上传证据。

| 检查 | 结果与证据 |
| --- | --- |
| 最终 CSharpier 检查 | 144 个自有客户端 C# 文件通过，包含未暂存、非忽略新文件 |
| 格式文件发现规则 | Python 4/4 通过，覆盖 tracked/untracked、忽略规则与生成物排除 |
| 依赖边界 | ArchitectureCheck 0 条诊断；对应 .NET 测试 41/41 通过 |
| .NET 解决方案 | 14 份 TRX，706 Passed、10 NotExecuted、0 Failed；不是全量无跳过通过，见下文 |
| Unity EditMode | `editmode-final.xml`：361/361，0 失败、0 跳过，包含客户端编译与 39 个 fixture |
| Unity PlayMode | `playmode-final.xml`：52/52，0 失败、0 跳过 |
| 真实 Bundle | 最新 EditMode 中 LocalAssetBundleTests 14/14，含并发所有权、取消、依赖、损坏内容和错误平台 |
| 场景生命周期 | 最新 PlayMode 中 5/5，覆盖失败保留旧场景、迟到取消清理、预留路径多实例隔离及拒绝短名称 |
| 界面绑定与输入 | 最新 PlayMode 包含双向绑定 5/5、焦点/独立 EventSystem 5/5、现有输入隔离 5/5 |
| 场景/Prefab 完整性 | ClientAssetIntegrityTests 1/1、启动场景构建 2/2；扫描自有场景/Prefab 丢失脚本及持久事件目标/方法。场景/meta 保持、UI Prefab 的输入组件迁移另验字段引用，未据此宣称所有平台视觉表现通过 |
| 启动与 MVVM | EditMode 中生命周期 10/10、MVVM 9/9；覆盖失败回滚、取消、停止重入和命令迟到结果 |
| 真实交互联机 | 最新 PlayMode 中 1/1：双客户端真实 Bundle UI 注册、重新登录、匹配、战斗、重连、持久化结算、返回大厅及停止释放 |
| 战斗/击杀/复活专项 | `p1-fixed.xml`：1/1，在 7200 ticks 拓扑验证；时间早于最终 UI/场景隔离改动，与最终 52 项分开记录 |
| Domain Reload 关闭与多实例 | `domain-reload-disabled-fixed.xml`：2/2，0 失败/跳过；包含重复 20 次 UI/根启停与双应用输入系统隔离 |
| 20 次真实对局稳定性 | `soak-after-fixes.xml`：1/1，预热 1 局 + 20 局、629.82 秒，各轮拥有资源回到基线；内存趋势见补录，不等于零泄漏证明 |
| macOS 最终构建与运行 | `macos-release-candidate/AiNative.app` 构建成功；两个独立无图形 Player 同场匹配、重连、结算并正常退出（0/0），结果见 `result-1.json`、`result-2.json` |
| Android 最终构建 | `android-release-candidate/AiNative.apk`：ARM64、IL2CPP、非 Development，132,772,789 字节；构建成功，未在真机运行 |
| Windows / 移动真机 | 未完成：本机无 Windows 构建模块和执行环境，ADB 无可用 Android 设备 |

.NET 未执行的 10 项为：3 项原生 Windows 计时器、1 项 Windows 优先级、5 项 PostgreSQL 专项和 1 项只读 Pending 文件条件测试。此次 dotnet test 未配置 PostgreSQL 专项测试环境；真实联机使用本地 PostgreSQL 不等于执行了这些独立测试。

Development 验证清单已与 `editmode-final.xml` 的 361 项、39 个 fixture 对齐，结果见 `validation-profile-static.json`。后续发现已有本地 PowerShell 7.5.4，可运行的 `test-source-profiles.ps1` 已实际通过；另实际运行 `test-nunit-report.ps1`：1 个有效报告及 6 个拒绝案例通过；其余 PowerShell 验证入口未据此自动宣称通过。

### 最终生命周期、稳定性与构建补录

`soak-after-fixes.xml` 实际运行 629.8226701 秒，1 个长测试通过、0 失败、0 跳过。先预热 1 局，再完成 20 局真实双客户端流程；全部 21 个停止采样的句柄、Bundle、页面、绑定、Battle 会话、View、EventSystem 与测试持有客户端列表均为 0。服务为本机独占拓扑：2 玩家、1200 ticks、60 Hz、Gate 明文 23001、本地 PostgreSQL；服务端固定种子由服务定义，账号后缀使用随机 GUID。

停止边界的 GC 后托管内存估计从 929,185,792 增至 1,420,365,824 字节（+468.43 MiB，+52.86%）；UnityAllocated 从 185,606,094 增至 200,299,078 字节（+14.01 MiB）。后 5 局托管估计仍增加 9.21 MiB，不能以增幅趋缓或拥有权计数归零宣称内存稳定。每次采样显式调用完整 GC；`managedLiveAfterGc` 是运行时估计字段名，不是精确 live 对象快照，也不代表产品 GC 策略。

20 局流程耗时 nearest-rank（ceil(p×n)）p50=29.93634 秒、p95=30.18083 秒。样本量 20，不是帧时间、Tick 吞吐或零分配性能成绩。硬件与版本见上文，全部原始样本与计算见 `soak-analysis-final.json`。尚需结合独立短诊断判断底层会话和缓存保留，内存稳定性验收仍保留风险。

关闭 Domain Reload 的独立批次 `domain-reload-disabled-fixed.xml` 为 2/2；运行后 EditorSettings 已还原。ScopedInputField 修复双应用文本焦点串扰，最终 UI Prefab 迁移仅改变 6 个输入组件的脚本引用，组件 fileID、Prefab GUID 和序列化引用保持。

`memory-isolation.xml` 三个对照测试均执行通过，每组两轮：角色资源加载与目录准备均能在没有网络会话时复现托管内存增长，仅 UI 组增长较小。同一 Editor 进程按角色、目录准备、UI 顺序运行，存在缓存与堆状态影响，不能直接用组间差值归因。详见 CF-003；测试通过仅表示完成诊断流程，不表示内存验收通过。

最终 Android 构建使用 NDK 27.2.12479018、JDK 17、min API 25、target API 35，APK SHA-256 为 `a2da62a53177332cc491164f60686639bcb9efbd3ca595fb228b7fd6cc09e310`。检查 APK 包含 `lib/arm64-v8a/libil2cpp.so` 和 `assets/AiNativeContent/catalog.json`；这不能证明真机运行或 jar 内读取已经通过。两平台构建目录均包含 UniTask/VContainer 依赖声明与许可证。构建后 ProjectSettings、EditorSettings、EditorBuildSettings 与构建前快照逐字节一致，活动目标恢复为 OSXUniversal。

### 验证中曾出现的失败

保留原失败日志，修复后使用独立报告：初次 EditMode 将依赖运行时 EventSystem 的 InputField/焦点测试放错执行模式，已移至 PlayMode 并通过；初次启动场景生成不适用批处理未保存场景，改为安全的 Editor 场景创建/恢复流程；P1 与真实 UI 流程分别暴露 CF-001、CF-002，已新增边界回归并复跑。第一次并行 .NET 构建遇到共享 deps.json 文件占用，串行重跑通过。连续对局首次被 NUnit 默认单测试 180 秒超时中断，未出现业务断言失败；已给长测单独设置 1200 秒上限，完整重跑 634.03 秒通过。不存在把零测试发现或失败批次写成通过的处理。

以下资源专项保留早期批次的详细说明；最终通过状态以上述 final 报告为准。

### 资源专项实际证据

已逐项复查 `artifacts/client-framework-20261029/editmode.xml` 中
`AiNative.Client.Assets.AssetBundle.Tests.LocalAssetBundleTests` 的 13 条结果：
13 通过、0 失败、0 跳过。fixture 运行时间为 2026-10-29 11:32:14–11:32:19
（Asia/Shanghai），耗时 5.501001 秒。该 XML 的 SHA-256 为
`20180751670267b502c398b5cf1fd22d73c622e2091002c552e33296ac7bb08b`。
这是本地工作区证据；整个报告仍有其他 fixture 的 2 项失败，不把资源通过表述为全套通过。

本组使用真实 BuildPipeline 构建的 Bundle，验证资源共享与独立句柄、跨 Bundle 共享依赖、
同进程双服务原生加载共享与独立关闭、不同依赖身份拒绝复用、单调用取消、预加载释放、
完整性失败、错误平台、依赖环、未知 ID 与类型错误、随包目录提取与已安装内容复核、
未知构建目录保护及连续 20 次实例创建/释放。20 次循环只证明本资源 fixture 的句柄和
Bundle 计数回到零，不等于完整对局的 20 次进出、订阅/会话基线或内存趋势验收。

资源包随后仅补充中文 XML 说明和 README 使用说明，未改变执行代码；这些说明编辑
不新增运行时测试成绩。Android jar 内随包读取与设备运行仍需真机证据；IL2CPP 构建状态见当前版本验证表。


## 交付边界与后续验收

规范、框架实现、现有游戏接入和开发指南已落地，但第一期的全部平台与内存验收未完成。所有修改保留在当前工作区，未自动暂存、提交或推送。自动生成工程文件与已有工作区修改未清理。

| 后续任务 | 负责人角色 / 前置条件 | 验收条件 |
| --- | --- | --- |
| CF-003 内存保留根定位 | 资源与验证负责人；使用已有两轮最小对照 | 对比托管对象快照与引用根，证明修复针对实际保留对象；同配置重复 20 局，并补独立 Player 趋势，不能仅凭计数归零关闭 |
| Windows Player | 平台验证负责人；可用 Windows 构建与运行环境 | 当前源版本构建、双客户端登录至结算与返回大厅，保存构建及运行证据 |
| Android ARM64 真机 | 平台验证负责人；可用 Android 设备 | 安装本次 APK，验证 jar 内随包提取、裁剪后绑定/资源/异步、输入、暂停恢复及内存趋势 |
| 图形交互验收 | 客户端负责人；有图形输出的桌面和设备 | 复查新增启动/UI 流程的视觉、键鼠/触控、焦点和模态输入；无图形自动化不替代手动视觉结果 |

源文件 SHA-256 清单保存在本地 `final-source-manifest.json`，用于识别本次工作区内容，不代表干净提交或签名发布产物。完整套件之后新增的三组诊断测试另由 `memory-isolation.xml` 验证；生产运行时代码与最终平台构建一致。
