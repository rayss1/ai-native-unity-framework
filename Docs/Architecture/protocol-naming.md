# 协议名称怎么读

先看前缀，再看后缀，最后看里面的数据。

| 名称 | 含义 |
| --- | --- |
| `C2S_LoginRequest` | 客户端请求进入战斗连接 |
| `S2C_LoginResponse` | 战斗服务器回复登录请求 |
| `C2S_InputCommand` / `C2S_InputBatch` | 客户端输入，不逐条回复 RPC |
| `S2C_SnapshotPush` | 服务器主动推送快照 |
| `S2C_ReliableEventPush` | 服务器主动推送可靠战斗事件 |
| `C2S_GateRequest` / `S2C_GateResponse` | 客户端与 Gate 的业务信封 |
| `S2C_GatePush` | Gate 主动推送通知 |
| `S2S_ControlRequest` / `S2S_ControlResponse` | 服务器之间的 RPC 信封 |
| `PlayerState`、`PartyState`、`AccountCredentials` | 共用数据，方向由外层信封决定 |

`gameplay.proto` 定义战斗消息；`backend.proto` 定义 Gate、内部 RPC 和共用业务数据；
`gate_envelope.proto` 定义与 Fantasy 外层兼容的传输信封。
`C2S_GateTransportRequest` 内装 `C2S_GateRequest`，
`S2C_GateTransportResponse` 内装 `S2C_GateResponse`。传输信封处理 Fantasy 的封装，
业务信封提供关联 ID、方法、凭据和 body。

## Gate 转发示例

```text
客户端 → Gate：C2S_GateRequest，body = AccountCredentials
Gate → Player：S2S_ControlRequest，body = 同一份账号凭据字节
Player → Gate：S2S_ControlResponse，body = LoginResult
Gate → 客户端：S2C_GateResponse，body = LoginResult
```

Gate 检查请求频率、登录身份和允许的方法；Player 负责账号，Lobby 负责组队。
转发不要求重新解析、编码 body。客户端自报的 delegated_player_id 不可信，
内部调用的身份和委托信息必须通过服务签名验证。

## 方法与业务数据

下面列的是成功回复中的 body；错误由外层 Response 的 error 字段返回。
表中类型均为共用数据，不是独立网络消息。

| method（保持原值） | 请求 body | 成功回复 body | 处理路径 |
| --- | --- | --- | --- |
| `gate.ping` | 空 | `Empty` | Client → Gate |
| `player.register` / `player.login` | `AccountCredentials` | `LoginResult` | Client → Gate → Player |
| `player.session` | `SessionCredentials` | `PlayerIdentity` | Gate → Player |
| `player.profile` | `ProfileQuery` | `PlayerProfile` | Client → Gate → Player |
| `player.ticket` | `TicketSpec` | `EntryTicket` | Gate → Player |
| `player.settle` | `MatchResult` | `SettlementReceipt` | Battle → Player |
| `player.settlement-status` | `SettlementQuery` | `SettlementReceipt` | Client → Gate → Player；Battle → Player |
| `lobby.presence` | `PresenceUpdate` | `PresenceState` | Gate → Lobby |
| `lobby.create/invite/accept/leave/ready/get`（各自独立方法） | `PartyOperation` | `PartyState` | Client → Gate → Lobby |
| `lobby.invites` | 空 | `PartyInvitations` | Client → Gate → Lobby |
| `lobby.queue` | `PartyQueueData` | `MatchStatus` | Client → Gate → Lobby → Match |
| `match.join` | `QueueEntry` | `MatchStatus` | Lobby → Match |
| `match.cancel` / `match.abort` | `MatchQuery` | `MatchStatus` | 取消：Client → Gate → Lobby；cancel/abort：Lobby → Match |
| `match.status` | `MatchQuery` | 内部 `MatchStatus`；Gate 返回 `MatchReady` | Client → Gate → Lobby → Match |
| `match.epoch` | 空 | `ServiceEpoch` | Lobby → Match |
| `room.report` | `BattleNodeReport` | `Empty` | Battle → Coordinator |
| `room.allocate` | `AllocationSpec` | `RoomAllocation` | Match → Coordinator |
| `room.get` | `RoomQuery` | `RoomAllocation` | Match / Player → Coordinator |
| `room.list` | `RoomQuery` | `RoomList` | 内部服务 → Coordinator |
| `battle.fence` | `CoordinatorFence` | `BattleNodeReport` | Coordinator → Battle |
| `battle.reserve` | `RoomReservation` | `RoomAllocation` | Coordinator → Battle |
| `battle.create` | `RoomCreation` | `Empty` | Coordinator → Battle |
| `battle.release` | `RoomRelease` | `Empty` | Coordinator → Battle |
| `battle.drain` | `DrainSpec` | `Empty` | Coordinator → Battle |

Gate 的 `match.ready`、`match.failed`、`match.cancelled` 主动通知使用 `S2C_GatePush`，body 为 `MatchReady`。
`MatchReady` 也能作为查询回复数据，因此不改名为 Push。
表中的路径用于解释调用关系，不替代代码中的权限检查；客户端不能直接调用内部方法。

## 源码迁移与兼容

- 战斗消息添加方向前缀；`Snapshot` → `S2C_SnapshotPush`，
  `ReliableEvent` → `S2C_ReliableEventPush`。`MessageId` 的 C# 成员例如
  `LoginRequest` → `C2SLoginRequest`，数值保持不变。
- `ControlRequest/Response` 按入口替换为 Gate 或 S2S 信封；
  `ControlNotification` → `S2C_GatePush`。
- `AccountRequest` → `AccountCredentials`，`SessionRequest` → `SessionCredentials`，
  `ProfileRequest` → `ProfileQuery`，`PartyCommand` → `PartyOperation`，
  `PartyQueueRequest` → `PartyQueueData`，`AllocationRequest` → `AllocationSpec`，
  `DrainRequest` → `DrainSpec`，`TicketRequest` → `TicketSpec`。
- 业务端口 `IBattleProtocolCodec` 和项目自有状态类型保持原样；生成类型不泄漏进预测核心。
- 类型名称和反射全名变化，需要重新编译源码使用者；不声称 Protobuf Any type URL
  或 JSON 枚举名称兼容。网络二进制、字段编号、消息编号、通道和路由不变。
- `shared/protocol/Tests/Fixtures/before-direction-names.pb` 是提交 `d43796f`
  三份 schema 的描述文件，由 Grpc.Tools 2.83.0 / protoc 35.1 生成。
  兼容性测试检查原有所有消息字段和枚举编号，并固定 Gate/S2S 信封字节。
