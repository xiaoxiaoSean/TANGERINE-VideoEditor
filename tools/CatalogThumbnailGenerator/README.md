# 内置预设缩略图离线生成

此工具仅供开发时运行。它直接使用已编译的编辑器渲染管线，提前生成全部文字、效果和转场缩略图。用户打开编辑器时仅加载资源图片，不执行此工具。

先构建主程序，再运行工具，最后重新构建主程序以复制资源：

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -c Debug
dotnet run --project tools/CatalogThumbnailGenerator -- --output=TANGERINE-VideoEditor/Resources/CatalogThumbnails
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -c Debug
```

工具引用现有 Debug 程序集，不会自动构建主项目。可通过 `-p:EditorAssembly=<绝对 DLL 路径>` 选择其他已编译版本。

- 文字使用与单击示例相同的 `example`、样式工厂和视频导出管线，在 2.5 秒截取一帧。
- 转场使用真实的 5 秒示例视频，转场发生在 2～3 秒，缩略图取 2.5 秒。
- 效果直接处理整张内置风景图，明确设置界面中的总强度 `intensity=100`；每个效果各自的参数保留预设默认值。
- 效果与旧滤镜目录按稳定 ID 合并去重，文字、效果和转场共 98 项。
- `manifest.json` 记录 ID、类型、示例时间、效果强度、默认参数、尺寸及资源、渲染器校验值。
- 主程序按可见卡片加载预渲染图片，不启动 FFmpeg，也不创建用户侧缩略图缓存。

只验证资源，不渲染：

```powershell
dotnet run --project tools/CatalogThumbnailGenerator -- --output=TANGERINE-VideoEditor/Resources/CatalogThumbnails --verify-only
```

使用 `--force` 强制重建；使用 `--id=transition.rotate` 只重建指定预设。生成中断后可重新执行，校验通过的同版本资源会复用。更换编辑器程序集或 FFmpeg 后会自动重建；修改生成器的图像处理规则后，使用 `--force` 重建。

错误阶段：`THMBG0001` 参数或预设、`THMBG0002` 渲染接口、`THMBG0003` 渲染编码、`THMBG0004` 资源验证、`THMBG0005` 其他工具错误。
