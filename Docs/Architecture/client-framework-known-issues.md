# 客户端框架集成发现的问题

## CF-001：独立 Fantasy Scene 的 Session.RuntimeId 路由冲突

2026-10-29，真实双客户端 P1 在战斗/击杀/复活后重连失败。证据为 `artifacts/client-framework-20261029/playmode-lifetime-p1.log`：FantasyKcpRealtimeTransport 构造报告同一 RuntimeId 已注册，三次重试均失败；另一客户端保持 Active。此问题属于既有传输适配实现，单独作为框架多实例集成修复，不归入可读性整理。

原因：进程级路由字典仅按 Session.RuntimeId 索引，独立 Scene 的运行时编号不能作为跨 Scene 的全局身份。修复采用实际 Session 对象引用身份；适配接口为 internal，Envelope 仍使用接收消息的 Session 路由，注销发生在释放会话之前。不修改 vendor、公开契约、消息字节或重试时序。

新增回归 `EqualRuntimeIdsInIndependentScenesRouteAndReleaseIndependently`：相同编号的两个独立会话同时注册、消息各自投递，关闭一个后其迟到消息拒绝，另一个继续收包。已有 RuntimeId 在释放时归零回归保留。真实 P1 复跑已通过：`artifacts/client-framework-20261029/p1-fixed.xml`，1 项执行、1 项通过，双客户端实际完成战斗、击杀、复活和重连身份保持。独立会话编号冲突回归已在 `editmode-release-candidate.xml` 的 360/360 批次通过；真实 P1 与传输边界测试分别保留证据。

## CF-002：结算确认后返回大厅未等待匹配终态传播

2026-10-29，真实交互 UI 验证 `artifacts/client-framework-20261029/ui-end-to-end.xml` 执行 1 项失败：双方结算已持久化、Played 各增加 1，结果页的返回按钮可用，点击后仍停留 Finished，错误为 `match-still-active`（tick 1261）。测试没有跳过身份核对，也没有提前请求未保存的结算。

现有边界：

- `client/UnityProject/Assets/AiNative.BattleClient/Runtime/TopologyViewModel.cs:126` 仅用 Completed 和 SettlementConfirmed 允许返回；`TopologyClientFlow.cs:662` 的 ReturnToLobbyAsync 在队伍仍绑定原队列时查询 Match，只接受 failed/cancelled，一次查询 ready 即报错。
- `server/src/Modules/AiNative.Server.Battle/TopologyBattleEngine.cs:714` 先保存结果，再等 TerminalRecorded 才释放房间；`TerminalRoomDelivery.cs:39` 的终态传输有 5 秒超时边界。
- `server/src/Hosts/AiNative.BattleHost/BattleTopologyComposition.cs:86` 每 250 毫秒处理释放，而 `:92` 的库存报告周期为 5000 毫秒。
- `server/src/Modules/AiNative.Server.Rooms/RoomCoordinator.cs:265` 观察到旧 Ready 房间已不在库存才置 Released；`server/src/Modules/AiNative.Server.Backend/MatchService.cs:280` 查询 Coordinator 后才把作业转为终态；`LobbyService.cs:172` 仅在查询到 failed/cancelled 后清空队列。

因此，持久化结算确认不等价于匹配名册已释放；上述传播间隔是既有后端语义。缺口在客户端将允许点击的返回动作当成单次同步完成操作，没有管理其后续等待。作为单独框架集成批处理，不混入排版整理，不修改 Server、终态身份或结果语义。

修复任务交由 readability 执行：返回操作内对同一队伍/队列进行有界、可取消的终态对账，等待期间保留原对局所有权；取消、超时、身份变化及真实后端错误必须可观察，不静默创建新队列或丢弃原会话。仅在队伍确认解除原队列后执行客户端停止和大厅切换。不得通过给测试添加任意延迟或无限重试规避问题。

实现已增加 20 秒生命周期关联上限和 250 毫秒轮询，只等待原队伍/队列；未知状态与不同分配立即失败。新增 5 条 EditMode 案例覆盖传播等待、签票中间窗口、未知状态、分配变化和取消；活跃分配超时继续保留 Battle 与队列。状态：身份、取消与异常边界复审完成；新增 5 条回归在 `editmode-release-candidate.xml` 的 360/360 批次通过，真实 `TwoInteractiveClientsUseBundlesUiReconnectSettlementAndReturnToLobby` 在 `playmode-release-candidate.xml` 的 51/51 批次通过；原失败证据保留。

## CF-003：Editor 连续联机后托管内存仍增长，保留根尚未定位

状态：未解决的稳定性风险。不得仅因功能和拥有权计数通过而写成“无内存泄漏”或“内存稳定”。

`artifacts/client-framework-20261029/soak-final.xml` 的 1 次预热加 20 局完整交互联机通过（634.031 秒），界面、EventSystem、资源句柄、Bundle、绑定、Battle 会话及测试保留客户端计数均回到基线。`soak-analysis.json` 保留 21 个样本：GC 后运行时估计从 922,456,064 增至 1,414,332,416 字节（+469.09 MiB）；最后 5 局仍增加 9.94 MiB。UnityAllocated 同期增加 14.00 MiB。循环功能通过不等价于内存验收通过。

独立 `memory-diagnostics.xml` 两局诊断 1/1 通过（约 59.3 秒），先空闲 60 帧对照，再在停止即刻、2 帧后及 62 帧后采样；采样边界执行完整 GC，不改变产品运行时策略。空闲 GC 估计为 736,169,984 字节；第 1、2 局停止 62 帧后分别为 853,209,088 和 968,331,264 字节。各样本 `GC.GetTotalMemory(false)` 与 `Profiler.GetMonoUsedSizeLong()` 相同，独立的 `GetMonoHeapSizeLong()` 更大，因此本次增长不能解释为仅 reserved heap 增长。GC 后估计仍不是精确对象普查，不能据其单独确认泄漏来源。

已观察的内部计数：routerTransports、schedulerScenes、disposedScheduled、schedulerQueuedActions 和 unitySceneIdCounter 每次为 0；assemblyLifecycles 从首次联网前 0 变为 2，之后稳定。测试只通过反射读取固定版本内部数据，缺失字段会报告 unavailable，没有把这些私有实现引入生产依赖。结果未显示 Fantasy 场景/路由注册或待执行释放队列逐局积压。

只读审查：Fantasy 会话释放会 Post Scene.Dispose，Scene.Close 和 MainScheduler.Remove 继续异步收尾；短诊断已额外等待 60 帧，不能再把本次持续增长简单归为等待帧数不足。vendor 的 MessageObjectPool、Pool<T>、FTask 池有容量上限，FTask<T> 归还时清空结果和回调；尚无证据认定这些池是高保留根。测试嵌套迭代器会从执行栈弹出，fixture 的客户端列表也清空；尚无证据证明保留了每轮完整对象图。

可验证的候选分配路径：当前 catalog 的 character Bundle 为 56,561,096 字节，`LocalAssetBundleService.LoadBundleAsync` 使用完整 `File.ReadAllBytes` 校验后 `LoadFromMemoryAsync`；两个应用各读取一次的 113,122,192 字节接近短诊断两局之间增加的 115,122,176 字节。此数值相关性不是保留根证明。`CollectEntry` 当前会清空 BundleState.Loading，引用归零会移除 NativeBundles 并卸载原生内容，未发现确定漏清的静态资源条目。

下一步应对短诊断前后采集托管内存快照，核对大 byte[] 数量及 GC 根，特别是约 56.6 MB 的 Bundle 缓冲；并与仅启动 UI、不加载角色 Bundle 的同等两轮对照。保留 Editor/Test Runner、异步状态机、原生加载缓冲和第三方缓存等候选，必须以对象根证据区分。还需独立 Player/设备内存结果，不能用 Editor 趋势替代移动容量验收。后续对加载器增加可清空字节容器与完成回调解绑，`CompletedNativeTaskDoesNotRetainVerifiedInputBytes` 已在 361 项 EditMode 中通过。但 `playmode-fixed.xml` 两局诊断仍从 1,074,794,496 增至 1,189,974,016 字节，不能把这个边界修复当作 CF-003 已解决；独立对照结果见下文。vendor 未修改。


最终修复版的 `soak-after-fixes.xml` 再次完成 1 次预热和 20 局（1/1 通过，629.823 秒）；`soak-analysis-final.json` 保存源哈希与 21 个样本，旧分析另存 `soak-analysis-before-fixes.json`。八类拥有权计数始终为零，但 GC 后估计从 929,185,792 增至 1,420,365,824 字节（+468.43 MiB，+52.86%）；UnityAllocated 从 185,606,094 增至 200,299,078 字节（+14.01 MiB）。最后 5 轮（cycle 15→20）仍分别增加 9.21 MiB 和 3.05 MiB。20 轮完整流程耗时 nearest-rank p50=29.93634 秒、p95=30.18083 秒；这是整局流程时长，不是帧或 Tick 性能。

`memory-isolation.xml` 三组诊断均通过，详细原始值、逐轮差值和源哈希保存在 `memory-isolation-analysis.json`。每组两轮，每个采样点均等待 62 帧并完整 GC；三组在同一个 Editor 进程依次运行，因此只比较同轮前后，不拿跨组绝对基线做因果推断。下列均为字节增量：

| 对照路径 | 第 1 轮 GC 估计 / MonoUsed | 第 2 轮 GC 估计 / MonoUsed | 第 1 / 2 轮 UnityAllocated | 第 1 / 2 轮 MonoHeapReserved |
|---|---:|---:|---:|---:|
| 两个独立服务直接加载角色 Bundle 后释放 | +113,197,056 | +113,135,616 | +9,076,001 / +949,064 | +128,155,648 / +2,277,376 |
| 每轮四次已安装内容校验，不加载角色实例 | +113,131,520 | +113,139,712 | -9,640 / -18,144 | +179,322,880 / +119,832,576 |
| 两应用仅启动并关闭真实 UI，不登录、不匹配、不加载角色 | +1,011,712 | +761,856 | +3,368,535 / +362,548 | +54,018,048 / +294,912 |

上述全部采样的 GC 估计等于 MonoUsed，reserved 独立变化；路由、调度场景、待释放场景、待执行动作、程序集生命周期和 sceneId 计数均为零。这说明不联网的角色资源路径和安装校验路径也可复现大幅增长，而本次仅 UI 路径增幅小得多；不能据此归因 Fantasy 会话，也不能据数值接近 Bundle 大小就认定 byte[] 的 GC 保留根。诊断通过表示用例完成，未对内存稳定作通过断言。CF-003 继续保持未解决，下一步仍需对象快照和保留根证据，并补独立 Player/设备验证。


### 本轮生命周期迁移后的同口径观察

`artifacts/client-lifecycle-20261029/playmode-full.xml` 的预热加 20 局用例通过（640.868 秒），21 个样本保存在 `soak-analysis.json` 和 `soak-samples.csv`。仍在显式停止、销毁后执行完整 GC：GC 后估计从 891,899,904 增至 930,930,688 字节（+37.22 MiB），最后 5 局仍增长 12.55 MiB；UnityAllocated 全程净变化为 -6.76 MiB，最后 5 局增长 6.16 MiB。拥有权断言全部通过。

相对于第一期 +468.43 MiB 的观察，本轮净增长较小，但两次处于不同 Editor 进程及测试预热顺序，不能据此把差异归因于某项迁移或推断保留根已经消失。CF-003 仍未解决，尚缺对象快照保留根及独立 Player/真机内存证据。详见[本轮验证记录](client-lifecycle-validation-2026-10-29.md)。

## CF-004：关闭 Domain Reload 的双应用输入框延迟激活串焦点

状态：已修复并通过定向与产品回归。原始失败保留在 `artifacts/client-framework-20261029/domain-reload-disabled-final.xml`：双应用隔离测试在跨帧后期待第一应用 Username，却得到第二应用 Password；不得用降低焦点断言或换成无输入行为的对象绕过。

固定版本 uGUI `InputField.ActivateInputFieldInternal` 在 `LateUpdate` 内使用全局 `EventSystem.current.SetSelectedGameObject`。Input System 的 MultiplayerEventSystem 仅在自身 Update 期间暂设全局上下文，无法覆盖后续 InputField LateUpdate。仅在 UiService/View 注入系统因此不足以隔离原生输入框延迟激活。另一个应用层入口是 Tab 导航的 Selectable.Select，也读取全局 current。

独立集成修复：UI 包新增 `ScopedInputField`，在 LateUpdate、指针按下、选择/取消选择、选中输入处理和停用收尾的必要同步范围内设置借用的所属系统，并 finally 恢复先前有效系统。OnPointerClick、OnSubmit 和 ActivateInputField 只设置延迟标记，实际激活仍进入已限定上下文的 LateUpdate。取消选择与停用覆盖基类 DeactivateInputField 的框架调用入口；未覆写非虚拟基类 API，也不宣称任意外部直接调用 ProcessEvent/DeactivateInputField 或文本排版回调都具备完整多用户输入法隔离。

View 在交互前注入所属系统；Tab 改为对该实例 SetSelectedGameObject。Prefab 生成器改建派生输入框，原 View 的 InputField 序列化字段和旧 Bind 入口保留；现有资产通过 `MigrateExistingInputFields` 使用 SerializedObject 迁移，仅 6 处脚本引用变化；原 GUID、组件 fileID、六个 View 字段与组件总数均通过保存后的重新导入检查，已重建真实 Bundle。完整 Build 入口保留用于显式重建。旧手工资产仍可加载，但多应用资产需要使用 ScopedInputField 并配置所属系统。本次不改 vendor、网络驱动和 Tick 顺序。

新增 `UiFocusPlayModeTests.ScopedInputFieldsKeepDelayedActivationInsideTheirOwnedEventSystems`，覆盖两输入框实际激活、两帧延迟、同步取消选择、全局上下文恢复和一个系统销毁后另一系统保留焦点。既有真实双应用 Domain Reload 关闭用例保持断言不变。实际回归：`domain-reload-disabled-fixed.xml` 2/2 通过；`playmode-final.xml` 52/52 通过，含新增输入框用例和真实交互联机。新增小测试首次漏配 Canvas，导致基类生成光标时访问 null；已补完整 Canvas/Text 夹具并保留全部焦点断言，未忽略异常。


## CF-005：正常 Battle 轮询在房间关闭与结算回执传播之间刷新票据失败

状态：本轮已复现，未修复，必须后续专项处理。此项与 CF-002 共用终态传播背景，但发生在正常 Battle 轮询、结算尚未确认时；CF-002 的返回大厅等待修复不覆盖该入口。

本轮证据：`artifacts/client-lifecycle-20261029/playmode-partial.json` 中 `data.result.result` 的 failures 记录 `TerminalDeliveryPlayModeTests.RealNetworkContinuesWhileApplicationPumpPausesThenConsumesTerminalAfterClose` 执行约 24.95 秒后失败，错误为 `GateCallException: invalid_allocation`。栈从测试第 94 行观察异步结果，进入 `TopologyClientFlow.PollCoreAsync` 第 1344 行的 `Backend.MatchAsync`。该文件是运行中快照，不是整批最终结论；最终验收见[本轮生命周期验证](client-lifecycle-validation-2026-10-29.md)。测试保持真实双客户端、每局 1200 Tick 的拓扑配置，没有改为只查询结算或增加任意等待绕过失败。

已核实的代码边界：

- `TopologyClientFlow.PollOriginalSettlementAsync` 先查询 Profile 和原 Match 的结算；结算尚未确认、客户端尚未消费 Finished 帧且 State 不是 Settling 时返回 false。`PollCoreAsync` 随后调用 MatchAsync 刷新票据。本次堆栈到达该分支，说明该次结算检查未进入终态等待分支。
- `TopologyBattleEngine.FlushResultsAsync` 第 714–723 行在本地 outbox 保存成功且 TerminalRecorded 后释放房间。`DurableResultOutbox.StoreAsync` 保存的是待投递结果；`RetryAsync` 第 340–356 行才向 Player 调用 Settle 并检查确认回执。房间的 durable outbox 保存不等于 Player receipt 已确认。
- `BattleTopologyComposition` 分别以 250 毫秒执行结果保存/释放、1000 毫秒执行 outbox 投递；Match、Coordinator、Lobby 的状态传播另有调度。因此，传输已关闭不能作为 Player 结算已确认的证据。
- `GateService.WithTicketAsync` 对仍为 ready 的 Match 查询结果调用 Player.IssueTicket；`PlayerService` 第 188/206 行会在分配无效或当前权威房间不再为 Ready、身份不符时返回 invalid_allocation。现有证据没有逐请求房间快照，不能断言这次具体命中了哪个检查，也不能把该错误一律解释为可忽略的正常关闭。
- `TopologyClientFlow.RunCoreAsync` 的对局后台临时错误重试列表不包含 invalid_allocation，自动模式会进入 Fail；该缺口不限于测试反射调用的异常观察方式。

与 HEAD 比较，测试的关闭后全量 Poll 顺序，以及生产代码的“原结算查询返回 false 后刷新 Match”逻辑已经存在。本轮测试变化是显式启动应用和适配 UniTask；未发现新生命周期引入上述业务分支的证据，但异步调度变化可能影响既有窗口的命中，不能用历史通过宣称该路径稳定。

后续专项应保留原 Battle、玩家及队列身份，在明确的有界、可取消对账策略下处理结算与释放传播，继续拒绝实际身份变化及其他后端错误；需要覆盖正常轮询入口和真实终态传输用例。本轮不修改协议、服务端或结算行为，不将测试改为绕过全量 Poll，也不因单次复跑通过关闭本项。
