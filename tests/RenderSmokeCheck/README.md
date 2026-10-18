# 渲染回归检查

Windows + .NET 10：

```powershell
dotnet run --project tests/RenderSmokeCheck/RenderSmokeCheck.csproj
```

直接调用应用源码中的 `CompositeFrameRenderer`、`TimelineExporter` 和示例服务，使用随应用打包的 FFmpeg。测试画布为 160×90，项目片段各 1 秒，示例视频使用服务原始规格，失败时返回非零退出码。

覆盖内置效果、全部转场的入场/出场及相邻片段、效果关键帧、文字位图与动态文字、复古效果的零强度和法语小数区域设置、200% 键控强度、示例生成/取消/切换，以及已配置的 HSL 和生成的 .cube LUT 文件。

该检查验证渲染及编码成功，不替代视觉比较、实时播放器交互或任意素材组合测试。输出保存在系统临时目录，最终报告会显示路径。
