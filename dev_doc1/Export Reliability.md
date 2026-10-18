# 导出可靠性检查与修复

## 原生崩溃原因

原始 26 对象、20 文字图层、1280×720、24 fps 的项目，在延后出现的模糊溶解与多层合成时，FFmpeg 4.4 退出码为 `0xC0000005`，标准错误可能为空。单独测试转场或去掉转场后无法复现，因此只验证单个示例不足以证明组合项目可导出。

Windows 原生调试定位到 `av_frame_ref` 接收空帧，调用路径来自 `tpad` 的末帧克隆。原实现用最后一帧的理论时间戳截取一个帧时长；非整数帧边界、时间基转换和上游滤镜帧同步会导致这个窄区间没有任何帧。随后 `tpad=stop_mode=clone` 尝试克隆空缓存并崩溃。

修复在片段末尾保留最多约三个帧时长的有界尾段，反转尾段并按帧索引取第一帧，得到最后实际存在的帧，再延长用于对象边界转场。不会反转整个片段或缓存整段长视频。输入解码线程和滤镜线程另设资源上限，防止多图层时线程数量随输入膨胀；线程限制本身不能修复此次空帧问题。

## 末帧与短范围

进一步完整解码发现另一处问题：FFmpeg 4.4 在多输入合成后发出的 EOF 时间戳会使最终 fps 滤镜少输出一帧，例如 1 秒 12 fps 只有 11 帧，24 秒 24 fps 只有 575 帧。单独设置 `eof_action=pass` 无法修复。最终输出级先延长最后实际帧，再采样并按 `ceil(导出时长 × 输出帧率)` 限制帧数，避免末帧丢失或填充导致长度增长。

小于一个序列帧的导出范围可能位于所有帧时间戳之间。此时先延长片尾并选取范围所处的序列帧，再处理输出时钟；覆盖片段中部和结尾的 1 毫秒范围，避免空流再次进入末帧克隆。音频仍使用原始精确导出区间，视频最小表达单位为一帧。

## 错误诊断

编码失败仍使用 `EXPRT0004`。错误信息增加十进制、十六进制退出码；无标准错误时说明编码器异常或原生访问冲突。诊断尽力写入 `%LOCALAPPDATA%\TANGERINE-VideoEditor\diagnostics\export`，包含实际编码器路径、完整 JSON 参数、标准错误和临时滤镜图。诊断写入失败不会覆盖原始错误，临时文件仍按原流程清理。

## 检查范围与命令

以下开发检查使用独立输出目录，避免覆盖仍然打开的旧版编辑器。先构建主项目，再给检查项目传入相同的 `OutputPath` 和 `BuildProjectReferences=false`。同一检查程序运行期间不可覆盖其 DLL；并行运行不同配置时使用独立输出目录。

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false
dotnet run --project tests/RenderSmokeCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false
dotnet run --project tests/TransitionVisualCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false -- artifacts/export-audit/transition-visual
dotnet run --project tests/ExportCoverageCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false -- --features artifacts/export-audit/features-preview-export
```

`RenderSmokeCheck` 覆盖 189 项：全部内置特效、转场开头/末尾/对象边界、特效关键帧、LUT、文字样式及示例视频取消/切换。最终版本 189 项全部通过，0 失败，日志位于 `artifacts/export-audit/render-final.log`。单个预设的通过不能替代完整多图层组合回归。

`ExportCoverageCheck --features` 覆盖 72 项功能，逐项生成实际预览、编码 MP4，并用 FFmpeg 完整解码及 FFprobe 校验精确帧数：视频/图片/音频/颜色/文字/调整层、10 种混合模式、三种蒙版及反转羽化、正反变速与插帧、时间重映射、关键帧、裁切旋转锚点、声道/均衡/压缩/限幅/降噪/回声/闪避/淡入淡出、全部贴纸、完整文字样式、嵌套序列、隐藏静音、导出范围/尺寸/帧率、1 毫秒短范围、色彩空间，以及延后模糊溶解和非整数帧边界回归。结果为 72 项通过、0 失败，JSON 位于 `artifacts/export-audit/features-final/results.json`。

`TransitionVisualCheck` 为 50 项画面检查：全部转场生成非对称图案帧，检查 630 对效果差异，并验证旋转斜边、抖动位移、模糊强度。8 种代表性转场逐帧比较预览和实际导出，全部通过。

`McpFeaturesCheck` 81 项高级功能检查通过，包含实际媒体分析、贴纸预览、冻结帧、图像序列、导出任务、序列交换与多机位元数据等。

最终完整项目回归：原始 26 对象（含 20 个文字图层）项目在 1280×720、24 fps 下完整导出 24 秒，576 个视频帧，音频完整，全部流解码通过。结果位于 `artifacts/export-audit/full-final/full-project.mp4`，FFprobe 元数据位于同目录 `validation.json`。这是故障修复回归，不作为继续制作宣传片的交付。

这些检查覆盖已列功能和具体参数样例，不代表穷举所有参数组合、所有外部媒体编码格式、硬件驱动或需外部语音模型的流程。
