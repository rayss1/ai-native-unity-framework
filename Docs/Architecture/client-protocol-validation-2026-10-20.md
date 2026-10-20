# 客户端协议迁移验证（2026-10-20）

这是开发工作区验证，不是发布或真机验收。源码基于 `aa06c05`，分支 `codex/client-protocol-codecs`，验证时包含本次未提交改动。最终源码清单和日志位于 `artifacts/protocol-validation`。`verified-code.json` 记录 613 项源码/配置，清单 SHA-256 为 `b9be71d537c65c41acbf57ebd9cb38e5eaec092ebf8d904889bf1d59c83e80d4`，清理 Unity 自动生成文件后复核无漂移；生成 DLL 的来源与哈希另外固定在 Protobuf 包的 `ThirdPartyNotices~/dependencies.lock.json`。

## 已执行

| 检查 | 结果与证据 |
| --- | --- |
| 全量 .NET Release | 653 项，648 通过、0 失败、5 跳过；13 个程序集，`dotnet-calibrated/*.trx` |
| 预测/协议 .NET Release | 102/102 通过，包含在上述全量运行内 |
| Unity EditMode | 294/294 通过，0 失败/跳过；`editmode-gc-events.xml`。计数器已通过已知分配与空区间校准，7 项分配专项检查通过 |
| Windows Player | 最终生产源码 Mono 构建及真实 KCP smoke 通过，epoch 11→12、ACK 32→33、丢弃输入 0；`windows-smoke-verified.json`、`windows-build-verified.log` |
| Android ARM64 IL2CPP | 最终 APK 构建与源码一致性检查通过，`android-final/BUILD-VALIDATION.json`；真机未连接，设备通信未验收 |
| Gate .NET | 包含在全量测试内，25/25 通过 |
| 架构检查 | `architecture-final.json`：有效、无诊断 |
| Unity 协议 DLL 重建检查 | `python tools/ProtocolCodegen/package_unity.py --check` 通过；固定版本、源文件及产物哈希一致，无重复基础程序集 |
| 跨路径重建 | Windows 独立目录重建 DLL 哈希一致；Linux CI 尚未运行 |
| 真实 KCP PlayMode | 3/3 通过，无跳过，`real-kcp-verified.xml`、`real-kcp-verified.log` |

数据库跳过为 Rooms 的 3 项和 Backend 的 2 项，缺少 `AINATIVE_TEST_POSTGRES`。这不是数据库功能通过的证据。

Unity 首次运行因迁移遗漏的构造参数和容量常量而编译失败；修复后第二次运行 257 项，255 通过、2 失败。Arena 重连控制通道已修复；无效登录响应现在先丢弃、不发送 Join，等待握手超时后失败，测试已同步明确这项行为。失败日志保留，不以旧成功结果覆盖。279 项复跑通过后，审查还发现异步发送借用缓冲区和极端数值重放边界问题；补丁及独立回归已通过最终 294 项运行。

## 分配与耗时

环境：Windows 10（运行时报告 10.0.19044），Intel Core i7-13700K，24 个逻辑处理器；SDK 10.0.202，.NET 10.0.13，Release。没有随机生成输入，使用测试源码里的固定向量（Tick=100、ACK=42）。生成输入编码预热 10,000 次，替代编码的预测/重放及已解码值复制分发预热各 1,000 次；测量窗口均执行 10,000 次，断言线程托管分配增量为 0。复制分发测试同时运行预测校正和远端表现；不包含网络接收或生成对象树。

接收测量每组预热 1,000 次、采样 10,000 次；`GC.GetAllocatedBytesForCurrentThread` 测分配，Stopwatch 测耗时，排序后按 nearest-rank 取分位数。数据取自 `dotnet-calibrated` 中的预测 TRX。以下是开发机诊断，不是移动平台预算验收；耗时会受 JIT、并行构建和系统负载影响。

| 固定向量 | 帧字节 | B/条 | B/秒（假定 20 条/秒） | 总采样时长 ms | P50/P95/P99 μs |
| --- | ---: | ---: | ---: | ---: | --- |
| 旧版 64 实体，无补给 | 527 | 6664 | 133280 | 413.7206 | 18.4 / 92.9 / 211.5 |
| Arena 8 玩家、4 补给 | 143 | 1528 | 30560 | 21.8624 | 2.0 / 2.9 / 3.4 |
| 重连，Arena 8 玩家、4 补给 | 157 | 1576 | 31520 | 27.4279 | 2.1 / 3.5 / 14.7 |
| 单条开火事件 | 28 | 456 | 9120 | 5.1757 | 0.4 / 1.0 / 1.8 |

每条消息调用一次生成解析器，创建一份生成对象树。额外的结构校验读取不会创建第二份树，但会创建库读取器。上述 bytes/条包含这些接收侧分配。事件测试构建器同时创建了未使用的补给样例，测量窗口只包含 28 字节事件帧，不含那份样例的构建成本。20 Hz 是换算条件，事件和重连的实际发送频率不固定。

Unity Mono 的 `GC.GetAllocatedBytesForCurrentThread` 在本机对明显分配对象的接收解析也返回 0。早期 Unity GC 断言因此不能作为证据；本次要求计数器先通过已知分配校准，不能把无效计数器的 0 当作零分配。

校准后 Unity 使用 Profiler 的 GC.Alloc 事件次数（不是字节数）；空区间必须为 0、已知数组分配必须至少产生 1 个事件。.NET 仍使用线程分配字节计数，并要求已知数组至少增加 1024 字节。校准失败会明确跳过分配专项测试，行为测试继续运行。

最终 Unity Profiler 事件计数测得：旧版 64 实体 73 次分配/条、Arena 8 玩家加 4 补给 19 次/条、重连 20 次/条、事件 4 次/条。输入、预测重放及域数据分发为 0 次。Unity 字节量未验收；不能把事件次数换算成字节。最终 294 项没有因校准而跳过测试。Unity 6000.3.23f1 Editor Mono（Development 测试，非 Player）使用同样的预热和采样次数，耗时如下；与 .NET 同时有构建负载，不作为隔离性能基准。

| Unity 向量 | 总采样时长 ms | P50/P95/P99 μs |
| --- | ---: | --- |
| 旧版 64 实体 | 1113.8784 | 109.4 / 114.5 / 162.2 |
| Arena 8 玩家、4 补给 | 262.6563 | 24.8 / 34.2 / 53.6 |
| 重连 | 261.4385 | 25.4 / 31.6 / 36.0 |
| 事件 | 36.3233 | 3.2 / 3.7 / 4.1 |

## 验收边界和下一步

Android 最终源码清单 SHA-256：`60a1774f9949c3e36e882e2bbf57c98c3e6d33d5ac028b7262921c932b6a3287`；APK SHA-256：`9dd7caf8e4116217376f0af3c0215257442540a99ef5a447cffe65db2ab012cc`。构建附带 Google.Protobuf 和 Fantasy 许可证以及依赖锁文件。早期构建遇到临时测试资源删除导致源码校验失败，已保留失败证据；最终在测试结束后使用固定源码重建并通过。

Android 真机未连接，因此设备通信未验收。真实双客户端拓扑需要固定 PostgreSQL 和完整服务环境，本次尚未完成；不能拿旧版本的双客户端成绩替代。Windows Player 和 Android ARM64 IL2CPP 的本次构建已通过，不能替代真机通信与性能验收。公网、弱网、长时间负载及 Linux 跨平台 DLL 可复现性仍需对应环境验证。

完成剩余平台检查后再按负责人要求提交和发起 PR。整体回滚方式见 [ADR-0019](../ADR/0019-client-protocol-codecs.md)。

## 提交时的打包复现修正

提交后发现 SDK 自动把 Git SHA 写入生成协议 DLL 的 AssemblyInformationalVersion，导致每次提交都会改变 DLL 哈希。打包脚本现已禁用这个字段的 Git SHA 后缀，源码身份继续由依赖锁保存；协议生成源码、运行库和消息行为未变化。重新生成的 DLL 只有构建元数据变化，提交后再次执行打包检查。上述 Unity/Player/APK 证据对应修正前的程序集信息版本；未因这项元数据修正重跑平台测试，不宣称这些旧产物是最新提交的字节级相同构建。
