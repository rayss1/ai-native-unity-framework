# ADR-0020: 协议消息名称表达通信方向

Status: Accepted
Date: 2026-10-20
Decision source: 项目负责人确认“收发消息带方向、共用数据保持中性名称”
Related: ADR-0009, ADR-0017, ADR-0019

## 背景与决定

原来的 `LoginRequest`、`Snapshot` 和 `ControlRequest` 无法直接说明发送方向；
同一个控制信封还同时用于客户端接入和内部 RPC。

独立收发的协议消息使用 `C2S_`、`S2C_`、`S2S_` 前缀，分别表示客户端到服务器、
服务器到客户端、服务器之间。`Request`/`Response` 表示请求和回复，`Push` 表示
主动推送，`Command` 表示无需逐条 RPC 回复的客户端输入。消息编号的名称同步改名，
数值不变。项目拥有的 Fantasy 外层消息也标明方向，vendor 不修改。

Gate 使用独立的客户端信封与内部服务信封。内部信封继续进行签名验证，客户端的
delegated_player_id 不产生内部身份。共用业务数据保持中性名称，例如
`AccountCredentials`、`PartyOperation`、`PartyState`，仍可直接转发 body 字节。
不为同一个业务请求体维护 C2S 和 S2S 两套副本。

## 权衡

只补注释不能让调用代码直接表达方向；为每一跳复制业务数据会引入字段漂移风险。
本方案只拆分边界信封，并用兼容性测试固定两者布局。名字不是安全边界，真实权限
仍由连接入口、服务身份验证和业务授权决定。

## 迁移、验证与回滚

这是生成类型及消息枚举名称的源码 API 变更，仓库内调用者同步迁移，不保留旧名别名。
字段编号、类型、消息编号、通道、Fantasy opcode、路由字符串不变。
二进制协议兼容不代表反射全名、Protobuf Any type URL 或 JSON 枚举名称兼容；
外部使用这些名称的工具必须同步迁移。本项目现有通信使用编号和二进制 payload。

通过旧版描述文件比较、固定字节测试、.NET/Unity 测试验证。生成物、Unity DLL
及哈希一起更新。回滚恢复 schema、调用者、生成物与依赖锁；不迁移持久化数据。
命名与业务路由表见 [协议阅读指南](../Architecture/protocol-naming.md)。
