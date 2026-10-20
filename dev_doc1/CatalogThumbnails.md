# 内置预设缩略图

素材区的文字、效果和转场使用随程序打包的 JPG 缩略图。资源以稳定预设 ID 命名，放在 `Resources/CatalogThumbnails`，并由主项目复制到 Debug 与发布目录。

缩略图由 `tools/CatalogThumbnailGenerator` 在开发阶段离线生成。文字沿用实际标题样式和示例导出，在 2.5 秒截帧；转场沿用实际转场预览，在转场中点截帧；效果把示例风景图输入效果管线，显式设总强度 `intensity=100`，其余参数使用该效果的预设默认值。

编辑器只在目录卡片进入可见区域时异步读取 JPG，解码后缓存为冻结的位图对象。该路径不调用示例视频预览服务，也不启动 FFmpeg。缩略图生成器会核对 98 个文字、效果和转场 ID、图片尺寸、图片 SHA-256、主程序渲染程序集、FFmpeg 版本与效果参数。

更新内置预设或其渲染行为后，先构建主程序，再运行生成器，最后重新构建以复制图片：

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -c Debug
dotnet run --project tools/CatalogThumbnailGenerator -- --output=TANGERINE-VideoEditor/Resources/CatalogThumbnails
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -c Debug
dotnet run --project tools/CatalogThumbnailGenerator -- --output=TANGERINE-VideoEditor/Resources/CatalogThumbnails --verify-only
```
