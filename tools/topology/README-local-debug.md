# Windows／macOS 本机对战与 Rider／Visual Studio 调试

本文用于日常开发：本机启动服务器，用 Unity 的两个或四个窗口测试，也可以让 Rider／VS 单独调试一个服务。不是压力测试或发布验收。

## 1. 准备环境

- Python 3.11+、Git、仓库根目录 `global.json` 支持的 .NET 10 SDK。原生 Python／shell／cmd 入口不需要 PowerShell；仅旧 `.ps1` 包装需要 PowerShell 7。
- PostgreSQL **17.11** 的完整工具目录。通过 `--postgres-bin-directory` 指定，或先从仓库 `artifacts/postgresql/17.11/bin`、再从 PATH／平台常见目录发现。Mac 检查 `/opt/homebrew/opt/postgresql@17/bin`、`/usr/local/opt/postgresql@17/bin`、`/Library/PostgreSQL/17/bin`；Windows 检查既有仓库便携版与 Program Files。工具不会自动安装或接受其他版本。
- Unity 6000.3.23f1；工程已固定 Multiplayer Play Mode 2.0.2，不需要升级到 3.0。
- 调试使用支持项目 .NET 10 目标的 Rider（Mac／Windows）或 Visual Studio（Windows）；打开仓库根目录 `AiNative.sln`，不是 Unity 自动生成的解决方案。
- 初始化仓库固定的 Fantasy 子模块。

以下命令从仓库根目录运行。无需 Docker、云账号或 Unity 网络服务。

## 2. 启动和关闭服务器

Mac 从终端执行：

```sh
./tools/topology/start-manual.sh
# 等价入口；参数均由同一 Python 实现处理
python3 tools/topology/manual.py start --players-per-room 4 --match-ticks 7200
```

Windows 双击 `tools/topology/start-manual.cmd`，或执行：

```bat
py -3 tools/topology/manual.py start
```

原 `./tools/topology/start-manual.ps1 -PlayersPerRoom 4 -MatchTicks 7200` 继续有效，仅转发至 Python。`.cmd` 也保留 `-PlayersPerRoom` 等旧参数拼写。两平台执行同一套构建、配置、数据库和进程管理逻辑，不会运行另一套 PowerShell 初始化流程。

启动器编译当前源码，准备独立数据库，启动 Gate、Player、Lobby、Match、Coordinator 和两个 Battle。看到“服务器已就绪”再操作客户端。默认两人一局、每局十分钟；`--match-ticks 7200` 为两分钟，`--players-per-room 4` 为四人同房。修改配置前先停止原环境。

保持启动窗口打开。按 Ctrl+C 停止；也可在另一终端执行：

```sh
python3 tools/topology/manual.py status
python3 tools/topology/manual.py stop
# Mac 停止包装：./tools/topology/stop-manual.sh
# Windows 将 python3 换为 py -3；旧 stop-manual.ps1 也继续有效
```

停止命令请求监督器清理并立即返回，需等待原启动窗口完成退出。服务先收到退出信号，超时后仅终止本次拥有的进程；数据库通过 `pg_ctl` 关闭。账号保存在 `artifacts/manual-test/postgres`，下次可继续登录；密钥、录像和未结算结果也保留。不会接管已有数据库或按进程名批量杀进程。不要强制结束监督器，否则不能保证执行清理。

参数 `--sdk-path` 指定 dotnet 可执行文件，`--postgres-bin-directory` 指定数据库工具目录，`--postgres-port` 默认 25439，`--run-directory` 必须位于仓库 artifacts 下。路径带空格或中文时用引号包裹。IDE 模式固定使用默认运行目录。

SDK 检查和发布命令统一在仓库根目录执行。根 `global.json` 要求 10.0.202（允许其策略内向前选择），10.0.200 不满足要求。不要在 server 目录创建放宽版本要求的 `global.json`。

启动器优先使用 `artifacts/dotnet/dotnet`（Windows 为 `dotnet.exe`），也可通过 `--sdk-path` 指定。若系统 PATH 的 `dotnet --version` 失败，先检查仓库已有 SDK：

```sh
./artifacts/dotnet/dotnet --version
./artifacts/dotnet/dotnet build AiNative.sln -c Release
```

该目录是可选的本地工具缓存，干净克隆不会自带 SDK。IDE 也应选择同一兼容 SDK，避免终端和 IDE 使用不同工具链。

## 3. Unity 双客户端／四客户端

1. 打开 `client/UnityProject`，等待编译完成。
2. 选择菜单 `AI Native > Local Test > Open Two Clients`；四人测试选 `Open Four Clients`。
3. 在 Unity Play Mode 下拉菜单选择对应的 `LocalTwoClients` 或 `LocalFourClients`，按 Play。菜单打开专用场景并选中配置资产；仍需选择 Play Mode 配置。
4. 主窗口与克隆窗口均应显示登录界面：地址 `127.0.0.1`、端口 `23001`、TLS 关闭。
5. 各窗口使用不同账号。首次点“注册新账号”（成功后自动登录）；账号名使用 3–32 位字母、数字或下划线，密码 12–128 位。后续使用“登录”，不重复注册。密码不会保存到场景或配置。连接地址在“连接设置”中展开；Tab 切换输入，账号／密码框按 Enter 登录。
6. 玩家一创建队伍，玩家二点“复制完整 ID”，玩家一粘贴到邀请框并发送；玩家二接受邀请，双方准备，由队长匹配。界面上的 ID 是缩略显示，不要手抄缩略值。四人测试邀请齐四人并全部准备。
7. 检查房间 ID 相同，用 WASD 移动、鼠标左键触发现有机枪开火。切换窗口查看对方移动；后台窗口保持网络运行，非焦点窗口不继续操作。
8. 点击“重新连接”检查恢复；等对局结束，结果页显示“已保存”后点“返回大厅”。双方重新准备，再次匹配，无需停止 Play。结算确认前返回按钮不可用。

当前是基础验证画面，完整瞄准、跳跃和切枪操作不在本次工具范围。原 BattleClient 场景与旧版自动验证路径保留；已有 Player2 配置也保留。

交互界面使用 UGUI，包含登录、大厅、匹配、战斗 HUD 和结果页。后台查询不禁用整个界面，玩家 ID 仅在值改变时更新。错误直接显示在页面底部；按钮请求期间防止重复提交。小窗口内容可滚动，建议 Game 视图使用 1280×720 或更大尺寸。点击 HUD 按钮不同时开火，在输入框中打字不控制角色。

可编辑界面资产：`Assets/AiNative.BattleClient/Resources/TopologyClientUI.prefab`。应用流程在 `TopologyClientFlow.cs`，界面显示和按钮绑定在 `TopologyClientView.cs`。菜单 `AI Native > Local Test > Rebuild UGUI Prefab` 根据 `TopologyUiPrefabBuilder.cs` 重新生成默认界面，会覆盖对该 Prefab 的手工修改；通常只需直接编辑 Prefab。

## 4. Rider 单服务断点调试（Mac／Windows）

以 Player 为例：

```sh
python3 tools/topology/manual.py start --debug-service player --match-ticks 7200
```

1. Rider 打开根目录 `AiNative.sln`，选择 Debug 构建。
2. 在 Run/Debug Configurations 中选择／创建 **.NET Launch Settings Profile**，项目为 `AiNative.PlayerHost`，Profile 为 `Local Debug player`。不要选择普通 Web Profile。
3. 核对工作目录为仓库根目录、参数为 `--pid 2 -m Release`，环境变量为 `AINATIVE_LOCAL_DEBUG_PROFILE=artifacts/manual-test/vs-debug.json`、`AINATIVE_LOCAL_DEBUG_SERVICE=player`。
4. 若 IDE 未导入 launch profile，可创建 .NET Project 配置并显式填写同样的项目、Debug 构建、工作目录、参数和两个环境变量；无需复制生成文件中的全部配置。
5. 在 `PlayerService.HandleAsync` 设置断点，启动 Debug，等待监督器显示全部服务就绪，再从 Unity 登录。
6. 停止 Rider 调试时，其他六个服务和数据库继续运行。修改代码、再次 Debug 后可重新登录；监督器会持续显示就绪状态。

七种调试目标与下一节表格相同。Battle 参数为 `--pid 100 -m Release`（battle-1）或 `--pid 101 -m Release`（battle-2）；其余 PID 为 Gate 1、Player 2、Lobby 3、Match 4、Coordinator 5。一次只交接一个服务。

生成文件沿用 `vs-debug.json` 名称以兼容 Windows，内容为 IDE 无关配置。文件哈希或监督进程身份不匹配时拒绝启动，Release 构建也拒绝交接。脚本不会终止 Rider 启动的进程，停止整套环境前先停止 IDE 调试。Battle 停顿可能超时判失联；重启后的旧房间终止，应重新匹配，不修改生产超时规避该行为。

以下 VS 流程继续适用于 Windows。

## 5. 用 VS 按 F5 调试一个服务

例如调试登录和结算的 Player 服务：

```powershell
./tools/topology/start-manual.ps1 -DebugService player -MatchTicks 7200
```

1. 保持脚本窗口打开。显示“等待 VS 启动”是正常状态；脚本不会启动 Player。
2. VS 打开 `AiNative.sln`，选择 **Debug** 编译。
3. 将 `AiNative.PlayerHost` 设为启动项目，启动 Profile 选择 **Local Debug player**，不要选原有普通 Web Profile。
4. 在 `PlayerService.HandleAsync` 的注册／登录处理内设置断点，按 F5。
5. 等脚本显示全部服务就绪，再从 Unity 登录，触发断点。
6. VS 停止调试、修改代码、再次 F5；其他服务与数据库保持运行。客户端按实际状态重新登录或重新排队。

| `-DebugService` | VS 启动项目 | VS Profile |
| --- | --- | --- |
| `gate` | AiNative.GateHost | Local Debug gate |
| `player` | AiNative.PlayerHost | Local Debug player |
| `lobby` | AiNative.LobbyHost | Local Debug lobby |
| `match` | AiNative.MatchHost | Local Debug match |
| `coordinator` | AiNative.RoomCoordinatorHost | Local Debug coordinator |
| `battle-1` | AiNative.BattleHost | Local Debug battle-1 |
| `battle-2` | AiNative.BattleHost | Local Debug battle-2 |

一次只排除一个服务，两个 Battle Profile 不能同时 F5。VS 调试模式固定使用 `artifacts/manual-test`，不要同时指定其他 `-RunDirectory`。

脚本生成 `artifacts/manual-test/vs-debug.json`，包含服务环境、文件路径及校验信息。Profile 通过 `AINATIVE_LOCAL_DEBUG_PROFILE` 和 `AINATIVE_LOCAL_DEBUG_SERVICE` 读取它；无需向 VS 全局环境复制配置。文件不包含私钥正文，属于忽略的本地产物，不要提交或分享。

文件缺失、服务选择错误、监督进程已结束或配置输入文件变化都会明确拒绝启动。重新运行对应脚本生成配置。用 Release 编译启动调试 Profile 会被拒绝；普通 Release 部署不读取本地配置。

脚本开始时记录源码身份；随后在 VS 修改代码并 F5，不会重新计算整套服务器的录像身份。因此调试期间的录像只作诊断，不用于容量或正式版本验收。需要绑定新源码的记录时，停止整套环境后重新启动。

**Debug 编译与 Fantasy 的 `-m Release` 是两回事。** Profile 保留 `--pid … -m Release` 是正确的，不要把参数改为 `Develop`。Fantasy 直接读取原始进程参数，所以本地调试选择通过 Profile 环境变量传入。

断点会暂停服务，其他进程仍在运行。Battle 停顿约 20 秒可能被 Coordinator 判失联；Battle 重启也会结束旧房间，应重新匹配。Gate/Player/Lobby/Match/Coordinator 重启各自仍遵循既有恢复规则，不保证原队伍或排队保留。需要持续观察时使用跟踪点或日志，不延长生产超时来掩盖问题。

关闭整套环境前先在 VS 停止调试，再停止脚本。脚本不会接管或结束 VS 启动的服务。

## 6. 附加正在运行的服务

无需调启动代码时，也可以普通脚本启动全部服务，然后在 VS 选择“调试 > 附加到进程”，附加目标 `AiNative.*Host.exe`。

两个 Battle 名字相同，查看 `artifacts/manual-test/battle-1.pid.json` 或 `battle-2.pid.json` 中的 PID 区分。脚本默认发布 Release，优化可能影响变量和单步查看；需要稳定单步调试时使用上一节的 F5 方式。

## 7. 日志和常见问题

- 脚本编译日志：`artifacts/manual-test/build.log`。
- 各脚本服务日志：`<service>.stdout.log`、`<service>.stderr.log`；IDE 服务查看 Rider／VS 输出／控制台。
- 数据库日志：`postgres/server.log`；VS 交接配置：`vs-debug.json`。
- 客户端日志：主 Unity Console 与各克隆窗口日志。不要把密码或入场凭证写入日志。
- 端口占用：先检查现有脚本或 VS 服务是否仍运行，不启动第二套固定端口服务。
- 一直等 Player：确认脚本参数、VS Profile 匹配，使用 Debug 编译，并且没有停在断点。
- 登录连接失败：确认服务全部就绪，地址正确，且本机连接关闭 TLS。
- 一直匹配：确认账号不同、所有人准备，房间人数与客户端数一致。两人配置下开四窗口只会形成两人房间。
- 断点空心：在 VS“模块”窗口检查 DLL/PDB 和源码一致；普通脚本运行的是发布目录里的 Release 程序。
- VS 提示信任 ASP.NET Core SSL 证书：检查是否误选原 Web Profile；本机调试应选择表中的 `Local Debug …`，这条路径不需要 HTTPS 证书。
- 重启 Battle 后旧房间不能恢复：这是既有故障语义，重新匹配。

## 2026-10-15：统一 Python 入口的初次实现验证

初次实现阶段在 macOS ARM64 执行，Python 3.14.7；当时没有安装或升级系统依赖，也没有修改已有 `server/global.json` 或 Fantasy 子模块状态。

- Python 启动器回归 **34 项通过**，含跨进程目录锁、真实 Unix 子进程正常停止、失败清理、过期身份拒绝、模拟七服务监督及 IDE 排除；模拟监督测试不替代真实服务器验收。
- `LocalDebugBootstrapTests`：Debug **18 项**、Release **10 项**通过，包含 Python 实际生成的交接配置与文件篡改拒绝。
- .NET 辅助工具编译通过，实际进程身份连续读取一致、RSA 密钥重复初始化保留原密钥。
- 架构检查、shell 语法和 `git diff --check` 通过。
- 初次预检按预期拒绝本机 10.0.200（要求 10.0.202）；未发现 PostgreSQL 17.11。**七服务真实启停、账号保留、Unity 双客户端、Rider Player／Battle 断点与再次启动均未执行**。无 Windows 执行环境，`.cmd`／PowerShell 实机回归也未执行。

上述 .NET 测试在 `server` 目录使用已有局部配置选择的 SDK 10.0.200；辅助工具和架构检查通过显式调用已安装 10.0.200 的 MSBuild 编译。这只是诊断验证，不代表根目录 SDK 要求已经满足。测试结果位于 `server/tests/AiNative.BattleHost.Tests/TestResults/python-local-debug-{debug,release}.trx`；辅助工具诊断产物位于忽略目录 `artifacts/manual-helper-validation`。

依赖齐全后，从根目录复现自动检查：

```sh
python3 -m unittest discover -s tools/topology/tests -p test_manual.py -v
dotnet test server/tests/AiNative.BattleHost.Tests/AiNative.BattleHost.Tests.csproj -c Debug --filter FullyQualifiedName~LocalDebugBootstrapTests
dotnet test server/tests/AiNative.BattleHost.Tests/AiNative.BattleHost.Tests.csproj -c Release --filter FullyQualifiedName~LocalDebugBootstrapTests
dotnet run --project tools/ArchitectureCheck -- --root .
```

随后执行本指南的原生启动、Unity 流程和 Rider 调试步骤，停止并重启后用同一账号登录。Windows 使用 `py -3` 替代 `python3`，再验证旧入口及参数转发。停止请求发出后必须等待原启动窗口退出再检查端口释放。

## 2026-10-15：授权安装后的 macOS 原生验收

项目负责人随后授权安装依赖。SDK **10.0.202** 已安装到 `artifacts/dotnet`，附带 .NET／ASP.NET Core 10.0.6；PostgreSQL **17.11** 已安装到 `artifacts/postgresql/17.11`。两者均由启动器自动发现，未修改系统 SDK、系统数据库服务、根版本约束或已有 `server/global.json`。

PostgreSQL 来自[官方 17.11 源码](https://ftp.postgresql.org/pub/source/v17.11/)，下载 SHA-256 已核验：`dd27f2b3c59e73ed14aa3324901242bf69a032a6347805f274e6260322d42979`。使用 macOS 原生编译器构建，配置为 `--without-icu --without-readline`，用于现有 C locale 本机测试数据库。许可证保留在 `artifacts/postgres-install/postgresql-17.11/COPYRIGHT`；来源、版本、配置和回滚说明记录在 `artifacts/postgres-install/installation.json`。升级仍需显式变更版本与重新验证；切换安装可用 `--postgres-bin-directory`，先停止所属环境，保留数据库、密钥、录像及未结算结果。

本次实测结果：

- 原生七服务启动成功，七个 `/health/ready` 均返回 200。
- 20 秒短对局（`--match-ticks 1200`）的真实 .NET 客户端验收通过：TCP 注册／登录／组队／匹配、授权检查、KCP 输入确认、重连、错误房间票据拒绝、连续输入、完成对局及 PostgreSQL 结算，共 8 个场景；报告为 `artifacts/manual-test/acceptance.json`。
- `manual.py stop` 完成清理；默认十分钟配置再次启动成功。重启前后两个测试账号及战绩摘要一致，见 `artifacts/postgres-install/before-restart.json`、`after-restart.json`。这验证数据保留，没有额外声称重启后用原密码登录已测试。
- 修复了 Unix TCP `TIME_WAIT` 被预检误判为监听端口占用的问题；仅 TCP 探针使用 `SO_REUSEADDR`，仍通过 bind/listen 拒绝真实监听者，不启用 `SO_REUSEPORT`。立即重启验证通过。
- 向经身份核对的监督器发送 Ctrl+C 等价 SIGINT 后，监督器正常退出，七服务全部退出、数据库停止、TCP／UDP 端口可用，见 `artifacts/postgres-install/shutdown.json`。验收结束后没有保留运行中的测试服务器。
- Python 回归 **36 项通过**；使用仓库 SDK 10.0.202 重跑调试交接测试，Debug **18 项**、Release **10 项**通过；架构检查通过。日志保存在 `artifacts/postgres-install`，TRX 文件名为 `native-sdk-10.0.202-{debug,release}.trx`。

仍未执行：Mac Unity 双客户端实机流程、Rider Player／Battle 实际断点及编辑后再次调试、Windows 兼容入口实机回归。本轮 .NET 客户端结果不能替代这些验收，也不作为性能或生产部署证据。

## 历史验证记录（不代表 Python／macOS 新入口已验收）

2026-10-13，Windows 本机实际执行：

| 项目 | 结果 |
| --- | --- |
| 普通启动 | 七个服务健康检查均返回 200；重复启动和端口占用会明确拒绝 |
| 调试配置 | 七种目标环境与脚本一致、文件校验、停止请求归属、部分就绪状态测试通过 |
| 清理 | 日志被锁和 PID 记录写入失败均不遗留子进程；正常停止保留数据库 |
| 服务端测试 | BattleHost Debug 测试 77 项通过；Release 本地调试入口测试 8 项通过 |
| Unity 测试 | Application EditMode 92 项通过，含 3 项新测试 |
| VS Player | 实际 F5 启动，并命中 `PlayerService.HandleAsync`；附加进程断点也命中 |
| Player 停止与恢复 | 其他服务继续运行；恢复 Player 后，四个客户端均取得原房间结算 |
| 双客户端 | 两个真实编辑器窗口：不同账号、组队、同房、移动同步、重连和结算通过 |
| 四客户端 | 四个真实编辑器窗口进入同一四人房间，每人可见另外三人；重连、后台联网和结算通过 |
| 后台输入 | 无焦点窗口位置保持不变，同时服务器确认序号持续增长 |
| 仓库检查 | PowerShell 语法、架构检查、`git diff --check` 通过 |

多窗口测试通过编辑器接口调用现有客户端按钮处理方法，移动同步使用短暂自动输入；没有逐个手动点击所有按钮。四人同房使用四个单人队伍排队，四人组队按钮流程仍待手动验收。证据在 `artifacts/local-multiplayer`；VS 记录在 `artifacts/vs-managed-check`。

仍待验收：Battle 的实际 F5 战斗断点、停止／再次 F5 后新匹配、旧房间结束；完整的 VS 停止—改代码—再次 F5 流程；持续按住实体键鼠时切窗、正常关闭后使用原账号再次登录。VS 自动化后续遇到原 Web Profile 的证书对话框，未更改系统证书设置；不能把 Player 恢复进程的验证当作完整 F5 重启验收。

这些结果只用于本机功能调试，不作为性能验收证据。

### 2026-10-14：UGUI 改版验证

- 五个页面已接入真实客户端：登录、大厅、匹配、战斗 HUD、结果。Prefab 由 Unity 编辑器生成。
- 两个独立编辑器客户端通过 UGUI 按钮完成注册、复制 ID、邀请、接受、准备和匹配；连续两局进入不同房间，双方结算确认，累计完成场次从 0 增至 2。中途不停止 Play；第一局后保留登录和队伍，准备状态重置。
- 战斗重连成功；640×360、1280×720、1920×1080 的战斗 HUD 已截图检查。玩家 ID 连续 40 次采样，文字、颜色和控件实例没有变化。
- 四个独立编辑器客户端通过邀请组成四人队伍、全员准备，进入同一四人房间，四人均完成并确认结算；单人排队时的取消按钮能返回大厅并重置准备状态。矮窗口中的登录和准备按钮通过滚动事件后可命中。
- Application EditMode 114 项通过；PlayMode 的销毁、重建与旧请求清理测试 1 项通过；架构检查通过。
- 修复了结算后旧匹配记录已过期导致无法返回大厅，以及销毁后迟到的匹配响应可能创建新连接的问题。

证据保存在忽略目录 `artifacts/ugui`：以 `latest-` 开头的是修复后的双客户端记录；早期 `returned-*.json` 是定位问题时的失败记录。按钮验收通过真实 EventSystem 射线命中检查和指针事件触发，未直接调用业务方法绕过界面；这不等于实体键鼠人工验收。Windows 窗口截图接口超时，完整的实体 Tab／Enter、拖动滚动、按住键鼠切窗仍待人工验证。
