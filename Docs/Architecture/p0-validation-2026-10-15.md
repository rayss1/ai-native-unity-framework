# P0 开发基线与验证修复

日期：2026-10-15。基础提交 `0ed2e01c7282e31a14b0a233676e515858149365`；验证执行时修改未提交。环境为 macOS ARM64、SDK 10.0.202、Unity 6000.3.23f1、PostgreSQL 17.11。这是本机工作区开发验证，不是 CleanCommit 发布验收。

## 交付

- 保留根 `global.json`。使用已有 `artifacts/dotnet/dotnet`，备份并移除本地未跟踪的 `server/global.json`，使服务端继承根策略。未改全局 PATH 或 IDE 配置。
- Fantasy 从本地 `0f5d800` 恢复到仓库 gitlink `df4ad5fe5418c8855932de784c7cea6286c4b082`。两者 Git tree 均为 `a76cdb33301ce87458cd37f917070bb6ae2c8d19`；子模块无源码改动，主仓库未更新依赖 pin。
- 拓扑 socket 测试使用真实临时 TCP/UDP 端口，不再检查开发者正在运行的固定拓扑端口。生产入口的完整端口清单和占用拒绝行为保留并增加回归检查。
- 容量报告测试统一真实路径，修复 macOS `/var` 到 `/private/var` 的断言差异。缺少 PowerShell 的计划测试明确跳过；CI 先要求 `pwsh` 存在，再运行拓扑工具测试。
- Unity CI 递归获取并检查 Fantasy 子模块。
- Android 构建器按 Windows/macOS/Linux 主机定位模块和工具文件，保留 API35、NDK r27c、JDK17 要求；缺文件、错误版本仍拒绝。新增六项跨平台布局／完整性回归用例。未构建 APK，不据此声称 Android 真机通过。
- 新增 `Development` 配置：23 个 fixture、198 项 EditMode；两个正式入口共用清单。旧 `Current` 166 项及更早配置保持不变。更新状态入口、验证说明及角色包中已过时的输入系统说明。

## 实际执行

| 检查 | 结果 |
| --- | --- |
| SDK 10.0.202 restore／Release build | 通过，0 警告、0 错误 |
| 全量 .NET Release 测试 | 13 个程序集，546 项发现，541 通过、0 失败、5 跳过 |
| 独立进程 KCP 初始化 | 1 项通过，不计入全量测试总数 |
| 真实 PostgreSQL 必需用例 | 单独报告 5 项通过，严格报告门禁通过 |
| 架构检查 | 通过，Android Editor 修改后再次检查通过 |
| 协议生成及 Git 漂移检查 | 通过，生成物无差异 |
| Unity EditMode，AiNative 程序集筛选 | 最终 198/198 通过；实际 fixture 清单与 Development 配置完全一致 |
| Unity Character 包 PlayMode | 24/24 通过 |
| Python 拓扑工具 | 109/109 通过，无跳过 |
| Python PostgreSQL 报告工具 | 8/8 通过 |
| PowerShell 验证工具 | 7 个脚本全部通过：source-profiles、nunit-report、validation-contracts、entry-boundaries、worktree-entry、unity-generated-state、windows-process-cleanup |
| 修改范围 whitespace 检查 | 通过 |

五项 .NET 跳过均为现有 Windows 专属测试：只读结算文件权限 1 项、输入线程优先级 1 项、原生 Windows 等待器 3 项。它们在 macOS 上不算通过；正式严格 TRX 门禁没有放宽。Windows 的预期发现数为 546，尚未在本次运行中验证全部通过。

使用独立临时数据库、动态分配端口和独立数据目录；结束后已通过 `pg_ctl` 关闭。原有监听 23001 的服务 PID 70923 保持运行。Unity 使用现有无脏场景的 Editor 执行，未关闭开发者 Editor；测试后的序列化设置已恢复到测试前已知内容，保留原有 Standalone 应用标识。既有 Unity 生成项目文件仍保留为工作区改动。

## 首轮失败与修复

1. 审查阶段拓扑测试出现 5 个错误：路径断言 1 项、固定端口被已有服务占用 1 项、缺少 `pwsh` 3 项。修复后无 PowerShell 时为 102 通过／7 跳过；补齐 PowerShell 后 109 项全部执行并通过。
2. 初次 PostgreSQL 门禁错误地接收全量 solution TRX 目录，被其中 Windows 专属跳过用例拒绝。保留失败报告；重新按 CI 的两个 fixture 筛选生成独立数据库报告后通过，没有修改门禁。
3. Unity 首轮 192 项中 189 通过、3 失败：Android 工具链定位硬编码 Windows 布局及可执行文件名。修复并新增 6 项回归后，全量 198 项通过。

PowerShell 7.5.4 仅解压到忽略的验证目录，来自 Microsoft PowerShell GitHub release，按同一 release 的 `hashes.sha256` 校验；归档 SHA256 为 `3aaadd7ca62f1e4dbe59145b6af24e926d61f8da8a4782bc535e500c184135f0`。工具及其 MIT 许可保留在本地目录，不加入产品依赖。升级需显式换版本、校验对应摘要并重跑工具测试；回滚时移除本次进程 PATH 前缀即可。

## 证据与限制

原始证据位于忽略目录 [`artifacts/p0-validation-20261015`](../../artifacts/p0-validation-20261015/)：编译日志、13 份 TRX、独立 KCP／PostgreSQL 报告、Unity 失败与成功 JSON、Python／PowerShell 日志、工作区初始状态与局部 SDK 备份。交付时的文件哈希和源文件清单见该目录的 `summary.json`、`source-manifest.json`。清单在交付时采集，不能冒充正式入口执行前后的冻结源码校验；跨机器复查需携带该目录归档。

本次没有提交、推送、远端 CI 执行、Windows Player 构建、真实 KCP PlayMode、双客户端完整对局、Android APK／真机、弱网、容量、Linux 部署或回滚验收。没有将本地测试通过扩展为这些平台／场景通过。

下一步：审查并按负责人要求提交 P0；使用 `Development` 配置运行对应平台的干净提交验收。随后进入角色输入／表现接入权威战斗的 P1。
