# P1：联网角色表现与权威战斗反馈

日期：2026-10-16。范围：将已有 VBOT / Invector 动画与第三人称相机接入 Arena；保持 ADR-0002、0005、0007 的依赖、60 Hz 权威与客户端表现边界。

## 实现与接口

- `TopologyClientFlow` 在动态帧读取设备，在固定 Tick 提交预测输入。`NetworkCharacterInput` 将相机相对方向转成 Arena 世界轴，发送相机目标角相对预测角的差值。跳跃和武器边沿仅在 prepare 成功后消费；失焦、UI 阻塞、暂停与重连丢弃待提交动作。
- `CharacterAnimationDriver.SetPresentationState` 和 `CharacterOrbitCamera.ConfigurePresentation` / `SetPresentationState` 是 Unity 客户端主线程表现接口，不引用网络或 Shared。原有 Motor 配置继续服务单机演示。
- `NetworkCharacterPresenter` 把预测/插值 Arena 状态映射到动画。位置仍由 `BattleClientCompositionRoot` / 远端池控制，根运动关闭。联网 Prefab 只有模型、武器和表现组件，不含 CharacterController、Motor、InputReader、Rifle 或本地 HP。
- `ArenaRemotePose.State` 是追加字段：移动连续量插值，离散生命/武器状态在渲染时间到达下一快照时切换；既有死亡/重生/传送历史重置保留。
- 复用已有 `ReliableEvent.combat_event`，不改 Schema。客户端严格解析、按 epoch 和房间序号去重，`BattleClientSession.DrainCombatEvents` 在拥有线程消费容量 256 的队列；满时丢最旧事件并计数。重连清队列，保留同房序号水位。事件只触发动画、枪口闪光与提示，绝不在客户端扣血。

应用拥有 `Resources/BattleClient/NetworkCharacter.prefab`。编辑器菜单 **AiNative > Battle Client > Build Network Character** 从已有 CharacterDemoRig 提取视觉子树，保留动画控制器、材质、枪械挂点和 IK 姿势。源角色演示不变。场景组合根运行时加载资源，因此现有 LocalMultiplayer 场景无需重新创建。

## 操作与限制

打开 `Assets/AiNative.BattleClient/Scenes/LocalMultiplayer.unity`，正常登录、组队/匹配。WASD 移动；按住鼠标右键转向/瞄准；左键开火；Space 跳跃；1/2/3 切换机枪、霰弹枪和火箭。鼠标保持解锁以支持大厅和战斗菜单。

当前保持 Arena 的 7 m/s、跳跃、命中球体、弹药与下一 Tick 重生规则。本地演示的翻滚、蹲伏、近战、冲刺与本地步枪伤害没有进入网络规则。三种武器目前共用步枪模型。准星显示服务端 +0.6m 射线起点沿当前朝向 30m 的投影；第三人称相机存在近距离视差，没有承诺屏幕中心即命中点。死亡和重生可以发生在两个快照之间，可靠事件提示保留反馈，不能靠快照生命位推断每次死亡。

## 验证与后续

原始执行记录在 `artifacts/p1-validation-20261016/`；完成后以当前状态入口中的实际结果为准。真实战斗测试需已有本地拓扑（Gate 23001、两人匹配、至少 7200 ticks），精确执行 `P1NetworkCombatPlayModeTests.TwoClientsObserveAuthorityCombatRespawnAndReconnectIdentity`。测试创建独立账号和对局，不重启服务器。

本次不改变服务端、协议、共享玩法或第三方版本。Windows Player、Android 真机、弱网/公网与平台构建须分别验收，Editor 和 .NET 测试不能替代它们。后续应补武器独立模型、近距离瞄准视差处理、死亡动作时长与更丰富的命中特效；修改玩法规则需单独定义验收。
