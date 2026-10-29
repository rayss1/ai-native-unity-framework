# Unity 异步适配

固定 UniTask 2.5.11；依赖来源与许可证见 [第三方登记](ThirdPartyNotices~/README.md)。公共入口只返回 `ValueTask`，UniTask 类型不进入 Core、Shared、协议或预测契约。

```csharp
await UnityAsync.NextFrameAsync(lifetimeToken);
await UnityAsync.CompleteAsync(operation);
```

从 Unity 主线程调用。NextFrameAsync 可取消等待；CompleteAsync 必须等原生 AsyncOperation 完成，不接受取消令牌。拥有者在完成后检查自身取消状态，并释放已产生的迟到场景/资源。不可仅取消外层等待就遗弃 Unity 操作结果。空 operation 会抛 ArgumentNullException。

应用负责所属生命周期和异常观察，不新增无所有者的后台工作。Editor 编译/测试与 Android ARM64 IL2CPP/裁剪兼容性分别验收。
