# Local AssetBundle content

本期只加载随应用交付的固定本地内容目录，不下载或切换远端目录。

构建：`LocalAssetBundleBuilder.Build(emptyOutputDirectory, BuildTarget, definitions)`。
每项定义包含逻辑 ID、Assets 下源路径、全小写 Bundle 名与 Group。输出 catalog.json 包含平台、SHA-256、大小和直接依赖。跨 Bundle 的共享隐式依赖必须显式声明到一个 Bundle，避免重复打包。构建不修改 importer 标签；输出必须为空，失败目录不作为有效版本使用。

运行：`new LocalAssetBundleService(directory, LocalAssetBundleService.CurrentPlatform)`。
Editor 验证使用 `EditorUserBuildSettings.activeBuildTarget.ToString()`。Load/TryGetLoaded 返回独立句柄；Preload 令牌归服务作用域。共享原生加载不因单一调用方取消而中断。实例通过 InstantiateAsync 创建并持有 Prefab；必须 await instance.DisposeAsync 后再关闭服务。所有调用位于 Unity 主线程。

本地完整性检查不等于在线内容签名。目录必须来自应用随包内容，哈希不抵御恶意整体替换。在线更新仍受 ADR-0008 限制。Android StreamingAssets 不直接提供文件路径，应用先调用 `LocalContentInstaller.PrepareAsync(streamingRoot, destinationRoot, platform, cancellationToken)` 将随包目录提取到 persistentDataPath 内的私有目录，再用返回路径构造服务。提取器只接受本应用 StreamingAssets 下的源，不提供远端更新入口；按 catalog 哈希发布不可变目录，取消不激活半成品。服务不静默改用 Resources。

重复构建使用 `BuildManaged`。它只替换有自身产物清单的目录，若出现未知文件则拒绝操作。构建失败保留旧输出。

Tests/EditMode 通过真实构建验证加载、共享所有权、取消隔离、损坏和平台校验；实际执行结果由统一验证记录提供。

同进程多个服务共享按原生 Bundle 名与内容/依赖哈希确认的底层加载，各自持有独立引用。最后一个服务引用释放后才卸载。同名 Bundle 的不同内容或依赖版本同时使用会明确拒绝，不把新目录资源误绑定到旧版本。

## 最小调用示例

以下代码在 Unity 主线程、已有异步启动方法中执行；业务不要保存底层 Bundle 路径。

```csharp
string directory = await LocalContentInstaller.PrepareAsync(
    Path.Combine(Application.streamingAssetsPath, "AiNativeContent"),
    Path.Combine(Application.persistentDataPath, "AiNativeContent"),
    LocalAssetBundleService.CurrentPlatform,
    cancellationToken);
var assets = new LocalAssetBundleService(directory, LocalAssetBundleService.CurrentPlatform);
try
{
    AssetInstance instance = await assets.InstantiateAsync(
        new AssetId("character.network"), parent, cancellationToken);
    try
    {
        // 将 instance.Value 交给场景表现；源 Prefab 由实例租约保留。
    }
    finally
    {
        await instance.DisposeAsync();
    }
}
finally
{
    await assets.DisposeAsync();
}
```

使用命名空间 `System.IO`、`UnityEngine`、`AiNative.Client.Assets` 和 `AiNative.Client.Assets.AssetBundle`。
`parent` 为应用拥有的 Transform，`cancellationToken` 来自当前流程作用域。
资源包没有新增第三方运行时库；Bundle 中美术、字体等源内容的许可与发布通知仍由应用构建负责，
目录的哈希与依赖验证不代表内容许可审计。
