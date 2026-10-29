# ADR-0021：客户端生命周期、MVVM 与随包资源底座

Status: Accepted
Date: 2026-10-29
Decision source: 项目负责人批准客户端框架第一期实施计划

## 决定

遵守 ADR-0002/0005/0008/0019。Core 不引用 Unity，所有服务显式注册。
应用、流程、界面采用嵌套所有权，异步启动失败逆序释放，停止幂等。
公共接口保留 ValueTask 与 CancellationToken，不暴露 UniTask 或 VContainer。
这两个库分别固定为 2.5.11、1.19.0，置于可选适配包，应用选择它们。

Core 的 IClientModule 提供 StartAsync(CancellationToken)、StopAsync(CancellationToken)。
ClientApplication 按构造时传入的模块顺序启动，逆序停止；失败也清理正在启动的模块。
运行中停止先取消该次启动，等待启动收尾。每次 Start 创建新生命周期，重复 Stop 无副作用。
不向模拟 Tick 注入新的调度器。

Core MVVM 使用 INotifyPropertyChanged、BindableProperty<T>、AsyncCommand。
异步命令执行时拒绝重入，关闭时取消，错误可观察；BindingScope 统一释放监听。
uGUI 绑定在主线程执行，双向输入使用无通知回写。ViewModel 不引用控件。

资源包实现既定 IAssetService。AssetId/AssetGroupId 为字符串值类型；
AssetHandle<T>.Value 提供资源，基类 AssetHandle 承载每次获取的独立释放令牌。
TryGetLoaded 成功获取新的所有权；Preload 由当前服务作用域持有直至服务释放。
AssetBundle 实现维护加载合并、依赖引用与实例寿命，取消调用方不取消共享加载。

随包 catalog 固定逻辑 ID、Bundle、资产名、依赖、平台、大小和 SHA-256。
本期仅信任随应用交付的 catalog；哈希用于完整性，不声称抵御恶意替换应用包。
不接受外部 catalog，不在线激活版本。未来在线内容仍须满足 ADR-0008 签名和原子切换。

## 权衡与迁移

保留现有网络/预测包及协议。先整理自有客户端代码，再逐步迁移应用流程；
不在整理阶段改公共 API、序列化名称、固定字节与时序。
选择显式强类型 MVVM 绑定，以编译检查替代字符串反射；不引入自动绑定编辑器。
选择随包 Bundle，以真实构建验证代替 Resources 回退，不声称已完成在线更新。

## 验证与回滚

模块失败/取消/逆序清理、命令重入/取消、资源并发/依赖/损坏、界面解绑、
Additive 场景切换及重复进入退出必须有测试。现有协议与联机行为保留回归。
依赖升级独立提交并重跑平台检查；回滚恢复包锁、组合根和对应资源构建，
不得回退服务端权威或丢弃用户工作区修改。
