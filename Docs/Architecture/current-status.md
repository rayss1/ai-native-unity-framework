# 当前进度

更新时间：2026-10-29。本文是当前状态入口；ADR 决定架构约束，日期报告保留当时的结果，不能直接替代新版本验证。

## 10 月 29 日：显式生命周期与异步实现迁移

当前代码继续收敛启动与停止职责：场景入口/Bootstrap 显式调用应用根，根以依赖注册表启动 `foundation → resources → ui → account → match → presentation → flow`，停止逆序执行。Flow 借用注入的业务和 UI 服务；角色表现、账号大厅、对局会话各自有明确释放者。每场 Battle 建立独立子生命周期，返回大厅后释放，原网络 Pump 与固定 Tick 驱动顺序保留。

本轮范围内 Unity 侧内部异步已统一为 UniTask，公共契约仍为 ValueTask；共享完成源用于重复启动/停止等待。Core、按原帧轮询的网络任务、后台文件读取及公共转换缓存保留有理由的 Task 例外，详见 [代码规范](client-coding-standards.md)。直接打开产品场景由 `ClientSceneEntry` 管理，Bootstrap 只管理其明确预留的内容场景，不引入全局应用根单例。

本轮 EditMode 380 项通过；PlayMode 75 项通过、1 项失败、0 跳过，20 局拥有权循环通过。CF-003 内存增长仍存在，CF-005 终局传播窗口已复现；用户要求保留的根解决方案引用使架构检查仍有 3 项失败。不能宣称整体验收通过。平台验证及最新实际结果与未完成项由 [生命周期迁移验证](client-lifecycle-validation-2026-10-29.md) 单独记录；下方第一期报告保留历史，不能替代新入口和新释放链路的回归。

## 10 月 29 日：客户端框架第一期

本轮优先建设客户端底座：已有自有客户端代码统一规范，新增纯 C# Core、UniTask/VContainer 适配、uGUI MVVM、本地 AssetBundle、场景与帧生命周期。产品通过持久启动场景装配，账号大厅与对局状态分离，既有网络/预测 Tick 保持原驱动顺序。服务端权威和协议未改变。

开发入口见 [客户端框架指南](client-framework.md)，第一期测试数量、平台构建、未完成项保留在 [第一期验证记录](client-framework-validation-2026-10-29.md)。独立会话 ID 路由、结算终态等待和双 EventSystem 文本焦点问题均作为[集成问题](client-framework-known-issues.md)单独修复，不归入可读性整理。重复运行的托管内存增长仍在诊断，未宣称内存稳定或第一期全部发布验收完成。Windows 与移动真机不能用本机 macOS 结果替代。

以下保留历史版本背景，其中“当前”仅指相应日期，不作为新增框架的完成声明。

## 10 月 20 日：客户端协议迁移

当前实现基于 main `aa06c05`，位于 `codex/client-protocol-codecs`。Gate 改用生成消息；战斗新增独立协议接口和 Protobuf 实现，预测、远端插值和比赛状态共用已解码数据。会话显式注入 codec，旧构造调用已迁移。服务器数据格式和玩法没有调整。

已增加旧 Tick、旧确认序号、旧连接和序号耗尽的保护。测试使用另一种编码格式验证预测不依赖 Protobuf，并检查预热后 10,000 次输入、预测和重放的分配。接收解析仍有分配，不宣称整个网络栈零 GC。

架构、迁移方法见 [协议适配层](client-protocol-codecs.md)，最新结果和未验收项以 [本次验证报告](client-protocol-validation-2026-10-20.md) 为准。旧报告中的测试数量和性能成绩仅属于当时版本。

下一步是完成 PR 审查，并补齐数据库、完整双客户端拓扑和 Android 真机验收。

## 现在做到哪里

工程已经具备多人射击原型的完整链路：登录、组队、匹配、分配房间、战斗、重连和结算。当前重点是完善试玩体验，并验证当前版本在真实设备和网络上的稳定性。

- P0 已通过 PR #42 合并；P1 联网角色已通过 PR #43 合并；训练场与战斗修复已通过 PR #44 合并。10 月 20 日同步远端后，`origin/main` 为 `aa06c05`，包含训练场实现提交 `b63109b`。
- 当前 Arena 每房最多 **8 人**。历史 128 客户端成绩是多个房间的总人数；单房 64 人目标尚未完成。
- 联网角色已有模型、动画、相机和战斗反馈。单机演示的 CharacterController、翻滚、蹲伏、近战、冲刺和本地步枪伤害未接入联网规则。
- PR #44 已加入 40×40m 训练场、静态盒体碰撞、沿墙滑动、低箱落地、射击遮挡与换弹；不支持斜坡、动态刚体、玩家间碰撞或寻路。Jolt、Recast、完整资源更新和 HybridCLR 仍属于待实现、待验证能力。
- 合并代码不代表发布验收完成；当时尚未重跑 Windows Player；后续协议迁移的 Windows/Android 构建结果见上文，Android 真机、Linux/TLS 部署、弱网、公网和回滚仍需分别验证。

## 架构和代码入口

Unity 负责输入、UI、画面和预测；服务器决定战斗结果；双方共用 Shared 中的移动规则、状态和协议。服务器固定每秒计算 60 次，保存数据和文件等慢操作放在战斗 Tick 之外。Fantasy 通信细节限制在适配层内。

| 部分 | 负责什么 | 主要代码 |
| --- | --- | --- |
| Unity 应用 | 登录到结算的流程，场景和资源组装 | [TopologyClientFlow](../../client/UnityProject/Assets/AiNative.BattleClient/Runtime/TopologyClientFlow.cs)、[BattleClientCompositionRoot](../../client/UnityProject/Assets/AiNative.BattleClient/Runtime/BattleClientCompositionRoot.cs) |
| 客户端预测 | 本地先响应输入，再按服务器状态纠正；平滑显示远端角色 | [ArenaClientPredictionAdapter](../../packages/com.ainative.client.prediction/Runtime/ArenaClientPredictionAdapter.cs)、[ArenaRemotePresentation](../../packages/com.ainative.client.prediction/Runtime/ArenaRemotePresentation.cs) |
| 角色表现 | 将联网状态转为动画和开火表现；单机控制另有独立实现 | [NetworkCharacterPresenter](../../client/UnityProject/Assets/AiNative.BattleClient/Runtime/NetworkCharacterPresenter.cs)、[角色包](../../packages/com.ainative.client.character/README.md) |
| Shared | 共用移动、角色状态、武器参数和预测基础；不依赖 Unity 或数据库 | [ArenaGameplay](../../shared/gameplay/Runtime/ArenaGameplay.cs)、[协议 Schema](../../shared/schemas) |
| 训练场与命中 | 客户端和服务器共用静态地图与角色命中几何；Unity 按地图生成场景 | [ArenaTrainingMap](../../shared/gameplay/Runtime/ArenaTrainingMap.cs)、[ArenaHitGeometry](../../shared/gameplay/Runtime/ArenaHitGeometry.cs)、[ArenaTrainingGroundPresentation](../../client/UnityProject/Assets/AiNative.BattleClient/Runtime/ArenaTrainingGroundPresentation.cs) |
| 业务服务 | Gate 接入；Player 账号与结算；Lobby 队伍；Match 匹配 | [Backend 模块](../../server/src/Modules/AiNative.Server.Backend) |
| 房间调度 | Coordinator 分配房间；固定 Worker 执行房间 Tick | [RoomCoordinator](../../server/src/Modules/AiNative.Server.Rooms/RoomCoordinator.cs)、[BattleWorkerPool](../../server/src/Modules/AiNative.Server.Rooms/BattleWorkerPool.cs) |
| 战斗 | 权威移动、射击、伤害、死亡重生、补给、比分与比赛结束 | [ArenaRoom](../../server/src/Modules/AiNative.Server.Battle/ArenaRoom.cs)、[TopologyBattleEngine](../../server/src/Modules/AiNative.Server.Battle/TopologyBattleEngine.cs) |
| 保存与复查 | PostgreSQL 玩家数据；结算落盘重试；战斗录像重模拟 | [PostgresPlayerStore](../../server/src/Modules/AiNative.Server.Backend/PostgresPlayerStore.cs)、[DurableResultOutbox](../../server/src/Modules/AiNative.Server.Rooms/DurableResultOutbox.cs)、[ArenaReplay](../../server/src/Modules/AiNative.Server.Battle/ArenaReplay.cs) |
| 工程工具 | 启动调试、架构检查、协议生成、测试和部署 | [本机调试](../../tools/topology/README-local-debug.md)、[验证入口](../../tools/validation/README.md)、[架构检查](../../tools/ArchitectureCheck)、[部署配置](../../infrastructure/battle-host) |

Gate、Player、Lobby、Match、Coordinator、Battle 是六类独立服务；常用本地配置开两个 Battle，因此合计七个服务进程。详细边界以 [依赖矩阵](dependency-matrix.md) 和 [公共接口契约](public-api-contracts.md) 为准。接口契约包含提前设计的能力，不能仅凭文档中存在接口就判断实现已完成。

## PR #44：训练场与战斗修复（已合并）

最新实现和原始验证范围见 [P1 后续记录](p1-network-character.md)。此次文档整合保留 PR #44 的规则与证据，没有重跑其功能测试。

- 训练场使用 Shared 的同一份毫米整数盒体数据，客户端预测和服务器共用碰撞规则；Unity 只据此生成场景。支持静态墙体、滑动、低箱落地和离边下落。
- 机枪改用角色胶囊命中，霰弹保留辅助锥并检查遮挡；火箭检测路径碰撞与爆炸视线。客户端按屏幕中心选择瞄准点，最终伤害仍由服务器决定。
- 支持 R 键换弹及空仓自动换弹，耗时 120 ticks（2 秒）；备用弹暂不限，弹药数量和换弹进度尚未同步到 HUD。
- PR #44 当时的 `Development` 清单为 Unity EditMode **241 项**、全量 .NET **607 项**；协议迁移及方向命名已更新为 294/660 项。PR #44 的验证说明记录本地 .NET 602 项通过、5 项 PostgreSQL 用例因未配置数据库跳过；当时完整验收要求 607 项全部通过。
- PR #44 的训练场记录包含共享玩法 Unity/.NET 各 33 项、Battle 75 项、预测 56 项与场景 PlayMode 1 项通过；旧 Host 77/79，两个 Python 启动用例因 PATH 问题失败。这些是上游工作区的历史记录，不是本次合并后复测。
- 新地图真实双客户端联机、移动设备和弱网验收仍待执行；本次 Windows Player 通信和 Android 构建结果见上文。旧地图录像需对应旧构建；客户端和服务端必须同时更新。

## 10 月 20 日代码审查与定向验证（PR #44 之前）

以下检查发生在同步 PR #44 之前，不能作为训练场或战斗修复的验证。环境为 Windows、.NET SDK 10.0.202；基于 `2d92bcb` 所在工作区执行。工作区已有 Unity 自动生成工程文件、角色 Prefab 和项目设置等修改，未清理或覆盖。这是本地开发检查，没有冻结完整源码清单，也没有形成干净提交发布验收包。

| 实际命令 | 结果 |
| --- | --- |
| `dotnet build AiNative.sln -c Release --nologo` | 通过，0 警告、0 错误 |
| `dotnet run --project tools/ArchitectureCheck -c Release -- --root . --format text` | 通过 |
| `dotnet test packages/com.ainative.client.prediction/Tests/AiNative.Client.Prediction.Tests.csproj -c Release --nologo` | 56/56 通过 |
| `dotnet test shared/gameplay/Tests/Gameplay.Shared.Tests.csproj -c Release --no-build --no-restore --nologo` | 20/20 通过 |
| `dotnet test server/tests/AiNative.Server.Battle.Tests/AiNative.Server.Battle.Tests.csproj -c Release --no-build --no-restore --nologo` | 56/56 通过 |

共执行 132 项测试，0 失败、0 跳过。结果来自本次终端输出，未单独保存 TRX 或发布报告。本次未运行全量 .NET 测试、Unity 编译/测试、Player 构建、真实 PostgreSQL 验收、多进程真实对战、容量或移动设备测试。以下日期记录是历史证据，不代表 10 月 20 日重新执行。

## 10 月 16 日 P1：3C 联网接入

本节保留 10 月 16 日 P1 工作区的实现与验证记录；代码现已通过 PR #43 合并，合并状态见上文。LocalMultiplayer 的组合根已使用真实 VBOT / Invector / 步枪资源，输入接入 60 Hz Arena 预测；远端角色按快照插值，开火动画、枪口闪光与命中/死亡/重生提示消费服务端可靠事件。本地 Motor、CharacterController 和 HP 未接入联网权威。操作、接口和限制见 [P1 说明](p1-network-character.md)。

实际验证：Unity 6000.3.23f1 EditMode **221/221**；Character PlayMode **26/26**；角色 Prefab **2/2**；Input System 回归 **5/5**；原远端胶囊池兼容回归 **1/1**；真实双客户端战斗 **1/1**（Fire/Hit/Kill/Respawn、HP/击杀快照、重连会话与实体身份）；.NET 预测/协议 **56/56**；架构检查与 source-profile 工具回归通过。角色实际渲染已检查，模型、步枪和握枪姿态正常。没有重启已运行的本地七服务拓扑，也没有改动服务端代码。

一次完整 EditMode 运行曾出现会话恢复用例 `session-expired`，完整重跑 221 项全部通过；原因尚未确认，不将重跑通过描述为修复。失败与成功报告均保留在 `artifacts/p1-validation-20261016/`。最初显式集成测试过滤得到 0 项，改用外部拓扑分类后实际运行 1 项通过；0 项运行不算验证。

`Development` 已更新为 221 项 EditMode 清单；.NET 全量预计为原 546 + 新增 17 = 563，本次只重跑受影响的 56 项，不宣称完整 Windows/.NET 发布验收通过。Windows Player、Android 真机、弱网和公网仍待验证。三种武器暂共用步枪模型，近距离第三人称瞄准有视差，死亡/重生遵循现有下一 Tick 重生规则。

## 10 月 15 日增量与 P0

本次 P0 验证基于 `0ed2e01` 及其后的未提交修改；该记录描述验证时的源码状态。10 月 14–15 日新增 UGUI 联网流程、本机双／四客户端和 IDE 调试、macOS 原生拓扑启动、Input System 迁移，以及独立的本地角色 3C／Invector 动画／步枪伤害原型。当时角色原型尚未接入服务端权威模拟；P1 最新状态见上文，原型详见 [角色说明](character-3c-demo.md)。

P0 已修复端口测试对已有服务的依赖、macOS 路径断言、Unity CI 子模块初始化，以及 Android 构建器硬编码的 Windows 工具链路径。Fantasy 已恢复固定 gitlink；本机使用已有 SDK 10.0.202，移除已备份的局部 SDK 覆盖。已有 Unity 自动生成文件和 PlayerSettings 改动保留。

本次 macOS 工作区 Release 编译和架构检查通过；.NET 541 项通过、5 项 Windows 专属用例跳过；独立 KCP 初始化 1 项和真实 PostgreSQL 必需 5 项通过；Unity EditMode 198 项通过；拓扑 Python 109 项和 PostgreSQL 报告工具 8 项通过；7 个 PowerShell 验证回归脚本通过。详情、首轮失败及平台限制见 [P0 验证记录](p0-validation-2026-10-15.md)。这些结果不替代干净提交发布验收。

当时新增的 `Development` 配置覆盖 198 项 EditMode；P1 后已扩展为 221 项。旧 `Current` 的 166 项清单保持不变。当时 Windows 全量 .NET 预期发现数为 546；P1 后预期为 563，仍不能视为已完成全量重跑。以下 10 月 8 日记录继续作为历史证据。

## 10 月 8 日基线：已实现、已验证、未验证

| 项目 | 已实现 | 已有验证 | 还缺什么 |
| --- | --- | --- | --- |
| 联网完整流程 | 登录、组队、匹配、房间分配、战斗、重连、结算 | Windows 双 Unity Player 和服务故障测试有历史证据 | 当前版本部署后的公网、TLS 和故障恢复验收 |
| 战斗与同步 | 服务端 60 Hz；移动、射击、伤害、死亡重生；预测、校正、远端插值、录像重放 | Unity/.NET 共用测试；独立录像重模拟 | 当前 Arena 的弱网表现、代表性玩法和视觉验收 |
| 服务端部署 | Gate、Player、Lobby、Match、Coordinator、Battle 六类独立服务；固定 Worker | 本地跨进程通信、重启、结算重试有证据 | 当前版本 Linux、真实环境回滚和生产密度验收 |
| 数据保存 | Npgsql 10.0.3 直接访问 PostgreSQL；Player/Coordinator 各自拥有 Schema；有界结算文件 | CI 强制执行五项真实 PostgreSQL 用例；本地历史测试通过 | 文件系统断电恢复和长期数据归档 |
| 本机容量 | 当前 Arena 每房最多 8 人 | [2026-10-06 报告](battle-slice-qualification-2026-10-06.md)：Windows 共 128 客户端、一小时持续运行通过 | 单房 64 人、当前 Arena 弱网容量、其他平台和线上容量 |
| Android | ARM64 IL2CPP 打包、启动配置、后台/前台恢复逻辑 | 较早候选 APK 构建和生命周期单元测试通过 | 当前源码 APK、真实设备上的双客户端对战与恢复 |
| 开发验证 | 架构检查、协议兼容测试、统一验证入口、Windows Player 验证 | 本次 532 项 .NET、166 项 EditMode、3 项 legacy PlayMode；Windows Player 构建、登录和重连通过 | 干净提交后的正式验证，以及其他平台验收 |
| 资源与扩展 | 内容更新、Jolt、Recast、HybridCLR 已有设计边界 | 尚无完成这些产品能力的证据 | 按实际玩法逐项实现并验证 |

128 客户端是多个 8 人房间的总人数。它不是单房 128 人，也没有完成最初的单房 64 人目标。旧版 64 人房间报告与当前 Arena 是不同的代码和负载，不能互相代替。

## 10 月 8 日修改与验证

本次修开发验证工具、同步文档，并修复旧协议快照被误判为 Arena 的兼容性问题；不改协议结构、容量门槛、依赖版本或部署。两个 Windows 验证入口共用配置：`Baseline` 95 项、`Candidate` 147 项、`TerminalDelivery` 164 项、`Current` 166 项。新源码使用最后一项；旧配置继续检查各自的测试清单。

默认 `CleanCommit` 仍要求干净提交。显式 `Worktree` 模式绑定提交、固定 Fantasy 和所有源码文件的 SHA256，逐阶段复查源码；它是开发验证，不是正式发布验收。运行方式见 [验证说明](../../tools/validation/README.md)。

本次完整验证基于主仓库 `c5f8f93` 的隔离工作副本、Fantasy `df4ad5fe5418c8855932de784c7cea6286c4b082`。源码清单包含 570 个文件，SHA256 为 `fbe11a660c7b934e7d412f740111b318606ffaea4da7c3ac18d462460de90cba`；运行前后相同。运行的是未提交修改的开发验证，不是正式发布验收。本文和结果摘要是在验证结束后补写的文档。

| 实际执行 | 结果 |
| --- | --- |
| .NET Release 编译 | 通过；0 警告、0 错误 |
| .NET 测试 | 13 个程序集，532 项通过，0 失败、0 跳过 |
| 真实 PostgreSQL 17.11 | 五项必需测试全部通过，单独检查报告通过；临时数据库已关闭 |
| 独立 KCP 初始化测试 | 新测试进程中的一项测试通过 |
| 架构检查 | 通过 |
| Unity 6000.3.23f1 EditMode | 166 项通过，0 失败、0 跳过 |
| 真实 KCP PlayMode | 三项通过，0 失败、0 跳过 |
| Windows x64 Mono Player | 构建通过；登录、断线、重连和重连后输入确认通过；许可文件一致 |
| 验证工具回归检查 | 七个 PowerShell 测试脚本、八项 PostgreSQL 报告检查测试通过；九个 PowerShell 文件语法通过 |

首轮发现旧协议 `PlayerState.health` 被当成 Arena 扩展字段，导致 64 个旧协议实体的快照被拒绝。新增两项回归测试先失败、修复后通过，原有会话测试也改为覆盖真实的 64 实体快照。第二轮因 Unity 改写渲染配置的换行符而被源码校验拒绝；工具现在只恢复指定配置的换行和自动生成的工程文件，实质配置变化仍会拒绝。第三轮完整通过。各轮失败报告均保留，见 [结果摘要](validation-2026-10-08.json)。

## PR #36 的 CI 补修

Linux CI 两次在“遥测不可用时 Tick P99 增量必须小于 0.25 毫秒”失败，
增量分别为 0.4482 和 0.4841 毫秒。后续生产验收没有运行，不能算通过。

旧版房间快照原来每个客户端重新编码一次，现在每房编码一次，再附各自的输入确认序号。
协议结构、Arena 逻辑和 CI 门槛保持原样。新增四项回归测试后，本地 536 项 .NET 测试
全部通过，包括五项真实 PostgreSQL 测试；架构检查通过。首次本地全量复测使用了错误的
临时数据库端口，修正后重跑通过，失败报告保留。Unity 源码未改，本次未重跑 Unity。

本机编码专项测量确认重复工作减少，但是否解决 Linux CI 仍待新提交验证。
两次失败、修改范围和测量条件见[遥测容量记录](telemetry-capacity-validation.md)。
前面的 532 项完整开发验证仍是原源码快照的历史结果，不能当作本次修改的 Unity 验证。

## 后续顺序

近期建议里程碑：让当前 8 人玩法在真实设备和网络上稳定、顺畅地完成一局。以下为建议执行顺序和角色分工，尚未创建任务或指派具体人员。

| 顺序 | 工作与建议负责角色 | 依赖与验收 |
| --- | --- | --- |
| 1 | 验证负责人：确定验收源码，补当前版本 Linux CI、Windows 完整验证 | P0/P1 已合并；以明确源码身份执行 `Development` 清单，保存结果，调查 `session-expired` 偶发失败，不能以重跑通过代替原因分析 |
| 2 | 客户端负责人：验收训练场动态目标瞄准，完善武器模型、死亡和命中反馈 | PR #44 已修正固定距离瞄准视差；仍需基于新构建真人完整对局确认操作和反馈，重连不改变玩家身份或重复执行旧动作 |
| 3 | 玩法与架构负责人：验收现有静态训练场，明确后续斜坡、动态物体等需求 | 静态墙体与低掩体已实现；验证预测和权威的一致性及地图遮挡，新能力先定义规则与边界，不把本地 PhysX 当作联网权威 |
| 4 | 平台与服务端负责人：Android 真机、Linux/TLS、公网、弱网和回滚验收 | 使用明确的当前版本产物；分别保存完整对局、断线恢复和回滚证据，历史 APK/Editor 通过不能替代 |
| 5 | 性能与框架负责人：单房 64 人及后续扩展 | 单房容量独立测试；资源更新、Jolt/Recast、HybridCLR 按实际玩法需求推进，重要架构变化先补 ADR |

这些步骤不改变已接受 ADR 或原有性能门槛。第 3 步的需求确认可以提前进行；实现和验收必须遵守已有依赖边界。

## 证据怎么查

- 可共享的容量结果保存在 [Markdown 报告](battle-slice-qualification-2026-10-06.md)和 [JSON 摘要](battle-slice-qualification-2026-10-06.json)，保留配置、范围、源码和原始报告哈希。
- [发布验证记录](release-validation-2026-10-03.md)、[拓扑实现记录](server-topology-plan.md)、[Unity 拓扑记录](unity-topology-verification.md)保留各次来源和失败；其中“当前”“待完成”应按记录日期理解。
- `artifacts/` 是忽略的本地原始产物。旧链接需要原执行机器或归档副本；不存在的本地产物不能当成已复查证据，也不能通过生成空文件补齐。
- 本次可共享的 [JSON 摘要](validation-2026-10-08.json)保留源码身份、命令、结果和文件哈希；[完整执行报告](../../artifacts/status-sync-20261008/run-03/validation.json)与失败报告位于忽略的 `artifacts/status-sync-20261008/`，跨机器查看时需同时提供原始归档。

### 协议方向命名（2026-10-20）

已按 ADR-0020 区分 C2S/S2C/S2S 消息与中性业务数据，增加旧版描述文件兼容性检查。
方法、请求体、返回体与源码迁移见 [协议阅读指南](protocol-naming.md)。
本轮验证记录见 [方向命名验证](protocol-naming-validation-2026-10-20.md)。

### 控制消息 opcode 可读性（2026-10-21）

Gate 与服务间控制消息改为共享命名常量，CallAsync 展开三层封装，线上值不变。
本轮 .NET 660 项（655 通过、5 项数据库环境跳过），Unity EditMode 294/294 通过。
见 [验证记录](control-opcode-validation-2026-10-21.md)。
