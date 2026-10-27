# 工具入口导航

本目录保存开发、生成、架构检查和验收工具，不参与 Client／Server 运行时依赖。
以下命令均从仓库根目录运行；SDK、Unity 和依赖版本遵循仓库清单。
当前验证状态见 [current-status.md](../Docs/Architecture/current-status.md)，历史报告不代表当前源码已通过。

## 按任务选择入口

| 任务 | 主入口 | 用途与详细说明 |
| --- | --- | --- |
| 本机启动七服务、Unity 多客户端、Rider／VS 调试 | `python tools/topology/manual.py start` | 管理独立 PostgreSQL、构建、服务监督和 IDE 交接；[本机开发指南](topology/README-local-debug.md) |
| 查看／停止本机会话 | `python tools/topology/manual.py status`／`stop` | 与启动命令使用相同运行目录，保留数据库、密钥和录像 |
| 检查架构边界 | `dotnet run --project tools/ArchitectureCheck -- --root .` | 验证程序集、项目、包、共享代码和生成物等仓库契约；`--format json` 输出机器可读结果 |
| 生成协议代码及 Unity 包产物 | `dotnet build tools/ProtocolCodegen/ProtocolCodegen.csproj -p:GenerateProtocol=true`，随后 `python tools/ProtocolCodegen/package_unity.py` | 从 `.proto` 生成 C#，同步 Unity 协议产物；`package_unity.py --check` 检查漂移 |
| C# 排版检查 | `python tools/formatting/format_csharp.py --check` | 使用固定版本 CSharpier；去掉 `--check` 才会改写文件，首次先 `dotnet tool restore`；[排版说明](formatting/README.md) |
| 当前源码统一验收 | `tools/validation/run-current-source.ps1` | 按阶段验证 .NET、架构、Unity 等；当前源码必须显式选择 Development 并提供所需参数，见[完整命令与验收规则](validation/README.md#current-commands) |
| Windows Unity 平台验收 | `tools/run-unity-windows-validation.ps1` | EditMode、真实 Host PlayMode、Windows Player 与重连冒烟；使用[当前 Development 命令](validation/README.md#current-commands)，不要直接采用历史默认值 |
| macOS Unity 平台验收 | `tools/run-unity-manual-validation.sh` | macOS Player、真实网络和 Regional 证据；需要 PowerShell 7（`pwsh`）读取共享验证契约，并需要脚本检查的 Unity、Docker／Colima 等依赖；[人工验收说明](../Docs/Architecture/unity-manual-validation.md) |
| Android 构建验证 | `tools/validation/build-android.ps1` | 生成构建证据；构建通过不等于设备验收通过 |
| Windows 自动拓扑验收 | `tools/topology/testserver.ps1` | 构建、初始化、启动七服务和真实 .NET 客户端，可验 Gate 重启／重复结算；[拓扑运维说明](../Docs/Architecture/server-topology-operations.md) |
| 拓扑故障／容量验收 | `tools/topology/faultserver.ps1`、`qualify-capacity.ps1` | 故障场景、容量阶梯与 soak；`qualify-capacity.ps1 -PlanOnly` 仅输出计划 |
| Linux Compose 拓扑 | `sh tools/topology/linux.sh init\|build\|start\|stop\|logs` | 选择一个子命令执行；生成容器配置并操作 Compose，与本机 IDE 会话用途不同 |
| 云端故障验收 | `tools/topology/cloud-fault-acceptance.py` | 指定 Compose 项目的故障注入、恢复和长对局证据；[云端指南](topology/README-cloud-fault.md) |
| 临时公网 TLS 测试 | `tools/topology/public-test-tls.py` | 特定云测试环境的证书、配置准备及显式 apply，非通用部署器；[边界与操作步骤](topology/README-public-test.md) |
| 发布／容量证据校验 | `tools/release/verify-*.sh` | 校验 BattleHost 发布清单、qualification、多房间和遥测容量证据；[发布契约](../Docs/Architecture/battle-host-release.md) |

Windows 可使用 `python` 或已安装的 `py -3`；macOS 通常使用 `python3`。
日常本机 Python 启动器不需要 PowerShell，macOS Unity 验收脚本需要 `pwsh`；二者不是同一工作流。
`start-manual.cmd`／`.sh` 和 `stop-manual.cmd`／`.sh` 是平台快捷入口，均转发 Python 实现；Python 入口继续兼容 `-PlayersPerRoom`、`-RunDirectory` 等旧参数拼写。

## 内部辅助与离线审计

| 组件 | 职责及调用关系 |
| --- | --- |
| `topology/manual_runtime.py` | `manual.py` 的实现：依赖发现、进程归属、数据库、配置、IDE profile 和会话清理 |
| `LocalDevelopment` | Python 启动器调用的 .NET 辅助程序：精确进程身份和 RSA 密钥生成／复用；不是另一套服务器启动器 |
| `topology/common.ps1`、`build.ps1`、`initialize.ps1`、`start.ps1`、`stop.ps1`、`acceptance.ps1` | Windows 自动验收链的环境、构建、密钥、子进程及客户端调用；仍被 testserver／faultserver／容量流程使用 |
| `topology/initialize-compose.py` | `linux.sh init` 使用，生成 Compose 私钥、公钥、peers 和容器网络配置 |
| `topology/replay-identities.ps1` | 生成拓扑回放的源码、Fantasy 与协议身份 |
| `topology/verify-replay.ps1`／`verify-all-replays.ps1` | 分别验证最近回放与全部完成回放；后者绑定容量验收的完整清单 |
| `topology/accepted-input-audit.py`／`capacity-input-audit.py` | 审计回放中的实际接受输入，以及容量测量窗口与独立回放证据的绑定 |
| `topology/host-headroom-audit.py` | 离线审计容量各阶段的整机余量与二进制／报告身份 |
| `topology/database-policy.ps1` | 记录容量验收实际数据库连接的 pooling 策略，不输出连接秘密 |
| `validation/source-identity.ps1` | Worktree 源文件清单及哈希，验证期间的源码漂移检查 |
| `validation/nunit-report.ps1`、`unity-test-contract.ps1` | 共享测试 profile、依赖 pin、NUnit/TRX 结果门禁及 macOS Shell 使用的 JSON 桥接 |
| `validation/unity-generated-state.ps1` | Unity 生成文件状态记录与受限恢复 |
| `validation/verify-postgres-tests.py` | 验证独立 PostgreSQL 测试报告，不允许把缺失或跳过用例算作通过 |
| `release/resolve-battle-host-release.sh` | 验证指定版本发布清单，再解析其镜像引用 |

自动拓扑验收使用调用者提供的隔离数据库；日常本机启动器拥有自己的数据库生命周期。
这两组流程以及 Linux Compose 流程不能仅因都能启动服务而互相替代。
运行目录、生成物和证据保存在 `artifacts`；不要将工具源码清理当作删除数据库或历史证据的理由。

## 工具自身测试

| 测试组 | 命令／入口 |
| --- | --- |
| 架构规则与反例 | `dotnet test tools/ArchitectureCheck.Tests/ArchitectureCheck.Tests.csproj` |
| Python 拓扑、输入审计、TLS／云故障边界 | `python -m unittest discover -s tools/topology/tests -v` |
| 日常本机启动器专项 | `python -m unittest discover -s tools/topology/tests -p test_manual.py -v` |
| Windows 子进程失败清理 | `pwsh -NoProfile -File tools/topology/tests/test-manual-child.ps1`；名称中的 manual 不表示它已废弃，它验证仍在使用的 `common.ps1` |
| 源码/profile、NUnit、入口边界、Unity 生成状态与 Windows 清理 | `tools/validation/tests/test-*.ps1`，按各脚本的平台与依赖条件执行 |
| PostgreSQL 报告门禁 | `python -m unittest discover -s tools/validation/tests -p test_postgres_gate.py -v` |
| macOS 共享测试契约桥接 | `pwsh -NoProfile -File tools/validation/tests/test-unity-test-contract.ps1`，不启动 Unity；CI 同时检查 Bash 语法 |
| 发布证据反例 | `tools/release/test-*.sh`，与相应 `verify-*.sh` 配套 |

Python 测试中依赖 PowerShell 的用例需要 `pwsh`；缺少依赖或平台不匹配导致的 skip 应单独报告。
工具单元测试不替代七服务、Unity 客户端、真实数据库、设备、云端故障和性能验收。
