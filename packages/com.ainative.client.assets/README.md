# Client Assets

资源标识与句柄不依赖 Unity。每次 Load/TryGetLoaded 都获得独立所有权，必须通过创建服务的 ReleaseAsync 释放；重复释放同一令牌无副作用。释放后访问 Value 会抛出 ObjectDisposedException。Unity 实现必须由主线程调用。
