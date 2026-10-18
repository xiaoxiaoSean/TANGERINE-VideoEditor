# Preview Encoding Check

从仓库根目录运行硬件探测和模拟超时检查：

```powershell
dotnet run --project tests/PreviewEncodingCheck/PreviewEncodingCheck.csproj
```

项目只链接实际 `PreviewEncodingProfile.cs`，不构建主程序。测试探测真实本机编码器；模拟卡死驱动时使用测试程序自身作为子进程，验证取消、5 秒超时和进程清理。

构建主程序后可额外检查实际导出器的硬件失败回退、滤镜错误和取消处理：

```powershell
dotnet run --project tests/PreviewEncodingCheck/PreviewEncodingCheck.csproj -- C:/path/to/ffmpeg.exe --integration C:/path/to/TANGERINE-VideoEditor.dll
```

集成检查通过反射读取所指定 DLL，生成小型纯色项目；测试专用子进程记录启动次数，并向真实 FFmpeg 传递滤镜错误以验证两个编码尝试均失败时不会禁用 GPU。
