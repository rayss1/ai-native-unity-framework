# 控制消息 opcode 整理验证（2026-10-21）

基于 `38344da` 上的工作区改动，Windows / .NET SDK 10.0.202 Release /
Unity 6000.3.23f1。仅整理 Gate 和服务间控制信封的 opcode，不改变数值、字段、
路由、鉴权或实时战斗信封。共享定义与帧结构见 [协议指南](protocol-naming.md)。

| 检查 | 本轮结果 |
| --- | --- |
| 全量 .NET Release | 660 项：655 通过、0 失败、5 项 PostgreSQL 环境相关测试跳过 |
| Unity CLI EditMode | 294/294 通过，0 跳过 |
| Fantasy 服务端测试（加强注册表断言后重跑） | 43/43 通过 |
| Gate 测试（加强固定帧头断言后重跑） | 25/25 通过 |
| 架构检查 | isValid: true，无诊断 |
| 差异空白检查 | 通过 |

新增五组兼容性用例，验证历史 opcode 数值、共享常量、服务端消息返回值和 Fantasy
生成的注册表一致。Gate 测试使用共享 Request 常量构造帧，以独立的历史字节向量
校验消息类型、RPC ID 和保留路由位置。原有连接测试保留历史数字作为模拟对端，
避免测试双方同时随生产常量改变而掩盖协议不兼容。

测试入口：

```powershell
 dotnet test AiNative.sln -c Release --no-restore --logger trx --results-directory artifacts/opcode-validation/dotnet
 unity test client/UnityProject --mode EditMode --output artifacts/opcode-validation/editmode.xml --timeout 600 -- -nographics
 dotnet test server/tests/AiNative.Server.Fantasy.Tests -c Release --no-restore
 dotnet test packages/com.ainative.client.fantasy/Tests/AiNative.Client.Gate.Tests.csproj -c Release --no-restore
 dotnet run --project tools/ArchitectureCheck -- --root . --format json
```

TRX、Unity XML/日志和源码身份保存在本地 `artifacts/opcode-validation/`。
Unity 测试产生的工程文件及设置变动已备份，并恢复测试前内容。
未重跑真实 KCP PlayMode、完整服务拓扑、Player 或 Android 设备验收。
本次没有重新做性能基准；封装步骤展开不增加编码调用或数组分配，不据此声称整个网络栈零 GC。
