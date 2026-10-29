# Unity 客户端运行时

依照 [ADR-0021](../../Docs/ADR/0021-client-framework-foundation.md)，提供 `ClientSceneService`、`ClientFrameEvents` 和 `UnityClientDiagnostics`。引用 Core 与 UniTask 适配包；所有场景调用限定 Unity 主线程。

```csharp
var scenes = new ClientSceneService(id => scenePaths[id.Value]);
await scenes.SwitchAsync(new SceneId("lobby"), lifetimeToken);
await scenes.UnloadAsync();
```

`scenePaths` 是应用拥有的逻辑 ID 到构建场景路径的映射。应用保留持久启动场景，服务只拥有自己加载的 Additive 内容场景。切换串行执行，新场景成功后才卸载旧场景；取消不会强行终止 Unity 原生加载，而是等待完成、卸载迟到场景后报告取消。已由其他所有者加载的目标场景会被拒绝。

停止时先取消并等待进行中的切换，再显式等待 `UnloadAsync`，最后销毁启动根。服务没有全局单例或自动 OnDestroy 清理。`UnityClientDiagnostics` 将脱敏事件与异常输出 Unity 日志；开发面板由应用实现。`ClientFrameEvents` 组件同步转发 Update/FixedUpdate/LateUpdate 和焦点/暂停通知，参数时间单位为秒。订阅在所属作用域解绑，销毁时清空全部监听。它不会推进网络或预测，应用必须避免重复注册现有驱动；FixedUpdate 不等同服务端固定 Tick。

EditMode 验证输入边界；Editor PlayMode 测试构建临时真实场景并恢复原 Build Settings。平台构建结果需查本次验证记录，不能以 Editor 测试代替 Player/IL2CPP 验收。
