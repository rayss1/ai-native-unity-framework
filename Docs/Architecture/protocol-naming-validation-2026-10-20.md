# 协议方向命名验证（2026-10-20）

本报告对应 `d43796f` 上的协议方向命名工作区改动；不是上一轮协议迁移或格式整理的
测试结果。设计见 [ADR-0020](../ADR/0020-directional-protocol-names.md)，使用方法见
[协议阅读指南](protocol-naming.md)。

## 环境与结果

Windows，本仓库固定的 .NET SDK 10.0.202、Release 配置；
Unity 6000.3.23f1，Unity CLI 1.0.0-beta.13。

| 检查 | 本轮结果 |
| --- | --- |
| 全量 .NET Release | 655 项：650 通过、0 失败、5 项 PostgreSQL 环境相关测试跳过 |
| Unity CLI EditMode | 294/294 通过，0 跳过 |
| 协议兼容性程序集 | 10/10 通过，含新增 2 项方向命名兼容性测试 |
| 旧版 schema 描述比较 | 所有原有消息字段、类型、编号、保留范围和枚举编号一致；仅声明的类型与消息枚举名称变化 |
| Gate/S2S 信封字节 | 固定字节向量及结构一致性通过 |
| Unity 协议 DLL / 锁文件 | `package_unity.py --check` 通过 |
| 架构检查 | `isValid: true`，无诊断 |
| 格式与空白检查 | CSharpier、`git diff --check` 通过 |

新增描述比较测试首次运行发现 protoc 描述文件会写入推导出的 json_name，而生成的
C# 描述信息会省略它。测试现先比较运行时有效 JSON 字段名，再归一化这个表示差异，
不跳过任何字段的实际名称、类型或编号检查。修正后重新执行了全量 .NET 测试。

测试保留现有客户端 codec、输入编码/预测 GC、Gate 限制、内部服务签名、消息通道、
重连和回放测试。此次不重新发布性能基准，不把功能测试通过表述为完整网络零 GC。

## 执行入口与证据

在仓库根目录执行：

```powershell
dotnet test AiNative.sln -c Release --no-restore --logger trx --results-directory artifacts/naming-validation/final-dotnet
unity test client/UnityProject --mode EditMode --output artifacts/naming-validation/editmode.xml --timeout 600 -- -nographics
python tools/ProtocolCodegen/package_unity.py --check
dotnet run --project tools/ArchitectureCheck -- --root . --format json
python tools/formatting/format_csharp.py --check
dotnet csharpier check shared/protocol/Tests/DirectionNamingCompatibilityTests.cs
```

本地忽略目录 `artifacts/naming-validation/` 保存 TRX、Unity XML/日志、检查输出、
源码身份清单及汇总。旧版 `.pb` 描述基准随兼容性测试纳入版本控制。
Unity 生成的 csproj/slnx 和测试修改的 PlayerSettings 在备份后恢复，不纳入功能改动。

## 未验收范围

本轮未重跑真实 KCP PlayMode、完整双客户端拓扑、Windows Player、Android ARM64
IL2CPP 构建和设备通信；不引用此前运行结果代替本轮结果。数据库相关测试因未配置
环境跳过，不能据此宣布完整发布验收通过。

二进制兼容测试不保证旧源码、反射全名、Any type URL 或 JSON 枚举名称兼容。
仓库外直接使用生成类型名称的工具需要按迁移表更新。普通业务数据的字段和现有
持久化数据格式不变，本次不改变回放 GameplayFingerprint。
