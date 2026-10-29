# 客户端显式生命周期与 UniTask 迁移验证记录

本轮实现已落地，**整体验收尚未通过**：CF-005 的终局传播窗口仍有真实联机失败，CF-003 内存增长仍存在，工作树架构检查有用户要求保留的解决方案引用问题。Windows 与移动真机未验收。第一期历史结果见[框架验证记录](client-framework-validation-2026-10-29.md)，不计入本轮成绩。

## 实现范围

- `FrameworkBootstrap` 或直接场景的 `ClientSceneEntry` 显式调用 `FrameworkClientRoot.StartAsync`。资源与 UI 获取只等待既有启动，不再隐式启动。
- Core 在任何模块启动前验证 ID、依赖及依赖环，以稳定拓扑顺序启动。游戏依赖链为 `foundation → resources → ui → account → match → presentation → flow`，失败清理包含失败模块，停止按实际启动顺序逆序执行。
- 账号大厅、动态 Battle 子生命周期、表现、UI 与资源分别拥有释放责任。Flow 保留协调与原 Update/FixedUpdate 驱动，不再负责销毁账号与 Battle 服务。
- Bootstrap 与直接场景均协调正常退出：等待根停止后卸载场景或允许进程退出。停止共享结果、释放取消源；重试重新创建运行对象，启动配置失败不会被错误标记为成功。
- Unity 应用及框架实现使用 UniTask，公共 ValueTask 与独立网络 Task 边界保留。信号量直接等待原 Task，保持空闲锁立即进入的时序；资源边界使用 AsValueTask，保留取消状态。
- UniTask `2.5.11`、VContainer `1.19.0` 未升级；Unity `6000.3.23f1`。Unity CLI 经用户批准升级至 `1.0.0-beta.14`。

契约与指南见 [ADR-0022](../ADR/0022-client-module-dependencies-and-async-boundaries.md)、[公共接口](public-api-contracts.md)、[开发指南](client-framework.md)。

## 实际验证

本轮本地证据在忽略目录 `artifacts/client-lifecycle-20261029/`，未上传测试账号、私钥或日志到外部服务。

| 检查 | 结果 | 证据与限制 |
| --- | --- | --- |
| .NET 全解决方案 | 733 通过、10 未执行 | `dotnet/` 中 14 份 TRX；5 项需 Windows，5 项未配置 PostgreSQL 测试连接，不计通过 |
| Core .NET 测试 | 30 通过 | 包含于上述总数，覆盖依赖验证、稳定排序与停止行为 |
| 架构检查器测试 | 57 通过 | 包含于上述总数，覆盖 UniTask 实现与公共边界 |
| Unity Core 生命周期定向 | 21 通过 | `core-unity.json`，亦包含于全 EditMode |
| Unity 全 EditMode | 380 通过、0 失败、0 跳过 | `editmode-release.xml`；含启动配置重试最终修复 |
| 直接场景退出定向 | 2 通过 | `direct-entry.xml`；真实 Bundle/UI，等待释放后允许退出 |
| 显式启动定向 | 4 通过 | `explicit-start.xml`；重复等待、取消重试、停止后重启和多应用隔离 |
| Unity 全 PlayMode | 75 通过、1 失败、0 跳过 | `playmode-full.xml`，共 76 项；失败为 CF-005，不能表述全量通过 |
| Domain Reload 关闭 | 相关 PlayMode 用例通过 | `FrameworkLifetimeTests` 使用关闭域重载的 Setup/Cleanup；清理状态文件已移除 |
| 双客户端 UI、战斗、重连、结算、返回大厅 | 对应用例通过 | 上述 PlayMode 包含真实本机拓扑；不覆盖 CF-005 失败窗口 |
| 预热加 20 局 | 拥有权断言通过，内存稳定性未通过 | 下节记录完整采样与限制 |
| macOS ARM64 Player | 构建及双 Player 联机冒烟通过 | `macos-build.json`、`macos/summary.json`；两个进程同一对局，重连与结算确认，退出码均为 0 |
| Android ARM64 IL2CPP | 构建与 APK 架构检查通过 | `android-build.json`、`android/apk-verification.json`；仅 arm64-v8a，包含 libil2cpp.so；不代表设备运行通过 |
| Windows Player / 移动真机 | 未完成 | 当前宿主不是 Windows；`android-devices.txt` 无连接设备 |
| 客户端格式 | 153 个自有 C# 文件通过 | `format-check.txt`，包含未暂存的新文件 |
| 工作树架构检查 | 失败 3 项 ARC006 | `architecture-final.json`；根 AiNative.sln 新增引用被排除的 Unity 生成 Character 工程，用户明确要求保留并单独记录 |
| 场景与 Prefab | 4 个产品资产均无缺失脚本 | `scene-prefab-audit.json`；BattleClient 原 fileID 与非内置 GUID 全部保留，仅增加两个入口组件；`scene-reference-check.json` |

全 PlayMode 之后补充了启动参数校验失败重试修复；最终 380 项 EditMode、macOS 双 Player 与 Android 构建均包含此修复。没有将此前全 PlayMode 结果描述为对所有后续源变更的重新全量运行。实际 20 局用例直接组装应用根，其采样及拥有权源码哈希保存在分析文件中。

## 20 局与 CF-003

硬件为 Apple M4 Pro / 48 GiB，macOS 26.6.2，Unity OSXEditor。双人、每局 1200 Tick，1 次预热后测量 20 局；服务端种子未固定，账号后缀随机。每次完整停止并销毁后执行完整 GC，保留 21 个样本。整项测试耗时 640.868 秒，不是帧或 Tick 性能指标。

| 采样 | cycle 0 | cycle 20 | 净变化 | 最后 5 局变化 |
| --- | ---: | ---: | ---: | ---: |
| GC 后托管估计（字节） | 891,899,904 | 930,930,688 | +37.22 MiB | +12.55 MiB |
| UnityAllocated（字节） | 427,589,129 | 420,500,627 | -6.76 MiB | +6.16 MiB |

每轮界面、EventSystem、资源句柄、Bundle、页面、绑定、Battle 会话和测试保留客户端计数均回到零。原始输出、样本、分析、结果文件哈希与相关源码哈希见 `soak-output.txt`、`soak-samples.csv`、`soak-analysis.json`。

第一期最终同采样方式的托管净增长为 +468.43 MiB。本轮净增长较小，但 Editor 进程、此前测试与预热顺序不同，不是严格因果对照；最后 5 局仍增长，缺少对象保留根与独立 Player/设备内存证据，**CF-003 未解决**。

## 失败、修复与审查记录

- 首次全 EditMode 因信号量 Task 转 UniTask 增加调度边界而卡住；取消命令超时。用户批准后重开 Editor，修复等待方式，最终全量通过。进程采样保留在 `editor-hang-sample.txt`。
- 首轮可完成的 EditMode 为 360 通过、18 失败；修复了旧夹具直接覆盖已拥有 Battle、把 Flow 当作 Battle 释放者，以及两个新资源测试漏初始化的问题。业务断言未删除或放宽。后续启动配置修复使一个旧 Awake 自动装配夹具失效，已改为验证显式配置，最终 380 项通过。
- CF-005：终局帧尚未被应用消费、原结算回执尚未确认时刷新接入票据，可能收到 invalid_allocation。HEAD 已有同样业务顺序；本轮保持生产逻辑与测试，不用增加等待或缩小查询范围绕过。完整诊断与后续专项要求见[风险登记](client-framework-known-issues.md)。
- 可读性审查：CreateApplication 仅声明依赖图；ConfigureLaunch 只选择模式和验证配置；Flow 的较长 PollCoreAsync 保留一次后端对账的原调用顺序，未为缩短行数改变短路或身份检查。33 处控制块补齐大括号，Roslyn 条件编译与去括号 token 比较确认无行为差异；依据在 `artifacts/client-braces-20261029/`。

## 环境恢复与剩余工作

Android 构建后恢复 StandaloneOSX，并重建桌面 Bundle。ProjectSettings.asset、EditorSettings.asset、EditorBuildSettings.asset 与构建前快照逐字节一致。原测试环境变量已恢复，临时拓扑、PostgreSQL 与 WS26 Host 已关闭，Editor 保持打开。

下一步分别处理 CF-005 的有界原身份结算对账、CF-003 的对象快照保留根，补 Windows 与移动真机证据；解决方案引用问题遵循用户保留决定。本轮未自动暂存、提交或推送，保留用户已有改动。
