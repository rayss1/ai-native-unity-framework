# 客户端协议适配层

对应决策：[ADR-0019](../ADR/0019-client-protocol-codecs.md)。

## 现在怎么走

Gate：业务请求 → `GateProtocolCodec` → `.proto` 生成消息 → `GateFrame` TCP 帧头 → 服务器。返回方向相反。账号、队伍、匹配仍通过 `GateBackendSession` 使用，业务不接触 Fantasy 或生成消息类型。

战斗：网络字节 → `IBattleProtocolCodec.TryDecode` → `BattlePacketBuffer` → 本地预测、远端插值、比赛状态。一次消息只建立一份生成对象树。为了保留错误字段类型、重复字段和显式零值的检查，会先用 Protobuf 库的读取器做结构校验，再调用生成解析器；这有两次遍历，但不会重复创建消息树。战斗帧复制到会话复用的固定数组一次，不按字段创建临时数组。接收处先检查完整消息和数据范围，再交给消费者；消费者还检查通道、连接代次及状态是否回退。

| 代码 | 职责 |
| --- | --- |
| `packages/com.ainative.client.protocol/Runtime/BattleProtocolContracts.cs` | 接口、项目自己的数据、固定容量接收缓冲区 |
| `packages/com.ainative.client.protocol.protobuf/Runtime/ProtobufBattleProtocolCodec.cs` | 战斗 Protobuf 编解码及校验 |
| `packages/com.ainative.client.protocol.protobuf/Runtime/GateProtocolCodec.cs` | Gate 生成消息与业务值之间转换 |
| `packages/com.ainative.client.fantasy/Runtime/GateFrame.cs` | Fantasy TCP 帧头，不解析业务字段 |
| `packages/com.ainative.client.prediction/Runtime` | 预测、重放、校正、远端插值 |
| `client/UnityProject/Assets/AiNative.BattleClient/Runtime/BattleClientSession.cs` | 一个会话内的注入、收包、分发与释放 |

## 调用迁移

组合根创建 `new ProtobufBattleProtocolCodec()`，作为 `BattleClientSession` 第一个参数传入。独立使用预测适配器时，构造顺序为 `(transport, codec, entityId, ...)`。旧的自动选择实现构造方式已移除，仓库内调用一起迁移。

`RequiredInputBufferBytes` 改为实例属性；帧和通道需求来自 codec。`ArenaRemotePresentation.ApplySnapshot` 接收 `ReadOnlySpan<ArenaSnapshotPlayer>`、Tick 和本地实体 ID，不再接受网络字节。`ArenaSnapshotPlayer`、`DecodedArenaSnapshot`、`ArenaPresentationEvent` 等类型位于 `AiNative.Client.Protocol`。

一个会话一个 codec，只在拥有该会话的线程使用。会话负责 Dispose；预测适配器只借用，不释放 codec。重连复用 codec 并调用 Reset，不在对局中换编码器。外部自建适配器的调用方需要自己负责 codec 生命周期。

`BattlePacketBuffer` 由调用方创建，最多容纳 64 个旧版实体、8 个 Arena 玩家和 32 个补给。数组只在创建时分配。解码失败不能改动上一次有效数据。只读视图在下一次成功解码后失效；远端和 UI 必须复制要保存的值，不能缓存视图或数组引用。直接调用 `ApplyDecoded` 时，调用方负责提供已完整校验的数据。

## GC 和失败处理

输入消息对象复用，直接写 Span；编码失败不推进输入序号或预测。发送失败需要按背压规则重试已经编码的原帧，不能重新执行预测。应用持有有界发送队列和单个预分配发送缓冲区。异步发送完成前不重复发送、不复用这块内存；重连清队列后，旧发送完成也不能删除新连接的输入。极端 Tick、位置和速度在修改预测或重连代次之前检查数值范围。

快照和事件允许生成解析对象分配，放在固定 Tick 外。生成对象不进入预测模型，也不跨消息反复 MergeFrom。旧的手写字段实现仅保留在 Tests/Support 作为兼容测试样例，生产路径不引用它们。

## 生成与依赖

Schema 只维护 `shared/schemas`。执行现有生成流程后，运行：

```text
python tools/ProtocolCodegen/package_unity.py
python tools/ProtocolCodegen/package_unity.py --check
```

Unity 的 `AiNative.Protocol.dll` 和 `Google.Protobuf.dll` 位于 Protobuf 包的 `Plugins`。版本、来源及 SHA-256 位于 `ThirdPartyNotices~/dependencies.lock.json`；许可证同目录。运行库固定为 3.35.1，生成工具固定为 2.83.0。不要手改 DLL、生成文件或哈希。脚本检查源文件漂移、重复程序集和基础程序集重复打包；`link.xml` 提供 IL2CPP 保留项。升级和回滚需要把版本、生成源码、DLL、锁文件及平台验证一起更新。

换成其他库只需实现接口并在组合根替换注入，但这不等于服务器自动理解新格式。网络格式不兼容时，必须同步升级服务器。本次只有 Protobuf 生产实现；另一种测试编码仅用于证明依赖隔离。

验证结果见 [本次验证记录](client-protocol-validation-2026-10-20.md)。

## 协议方向名称

生成消息已按 ADR-0020 使用 C2S/S2C/S2S 前缀；共用业务数据保持中性名称。
类型迁移和请求体/返回体对照见 [协议阅读指南](protocol-naming.md)。
