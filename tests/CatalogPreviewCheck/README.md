# Catalog Preview Check

从仓库根目录检查真实 GPU 编码、示例输出完整解码、缓存和快速切换：

```powershell
dotnet run --project tests/CatalogPreviewCheck/CatalogPreviewCheck.csproj -- --cold --report=tests/CatalogPreviewCheck/artifacts/result.json
```

`--cold` 仅删除每个即将测量的示例对应的缓存项；测试不会清空用户的整个缓存目录。报告记录首次生成和第二次读取缓存的时间，耗时不包含播放器加载。检查保持 960×540、30 fps、5 秒和 H.264 输出，并确认各播放副本互不影响、缓存视频字节一致。

可指定已经构建的旧版 DLL 运行基线。该模式不会构建主程序：

```powershell
dotnet run --project tests/CatalogPreviewCheck/CatalogPreviewCheck.csproj -p:BaselineAssembly=C:/path/to/old/TANGERINE-VideoEditor.dll -p:OutputPath=bin/Baseline/ -- --baseline --report=tests/CatalogPreviewCheck/artifacts/baseline.json
```

比较不同构建必须分别指定输出目录，防止旧 DLL 与新版项目引用互相覆盖。`artifacts` 是忽略的本机生成目录。
