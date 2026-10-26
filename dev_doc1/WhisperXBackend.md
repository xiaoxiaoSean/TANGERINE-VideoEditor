# WhisperX Backend

## 结构

- `Editor/WhisperXModels.cs`：识别选项、结果协议、语言与模型目录、本地模型导入登记。
- `Editor/WhisperXService.cs`：提取媒体选定音轨和源范围、启动独立 Python 工作进程、解析详细进度、终止进程树、验证结果。
- `Resources/WhisperX/whisperx_runner.py`：真实 WhisperX 批量识别、使用 faster-whisper 内置 ONNX Silero 检测人声、Wav2Vec2 CTC 强制对齐。
- `Resources/WhisperX/model_download.py`：开发和预置时使用的纯标准库 Hugging Face 下载器。32 个全局工作线程，HTTP 分片、重试、断点恢复，按固定提交版本下载并校验 SHA256 / Git blob。

界面仅提供选择已安装模型和导入第三方本地目录；识别不自动下载模型。工作进程强制启用 Hugging Face 和 Transformers 离线模式，不执行远程模型代码。模型缺失或语言不匹配时明确失败，不生成占位文字。

## 运行环境

Python 优先使用 `WhisperXOptions.PythonPath`，再使用 `TANGERINE_WHISPERX_PYTHON`，然后检查 exe 同级的 `data/WhisperX/venv/Scripts/python.exe` 及随应用分发的专用环境，最后使用 PATH 中的 `python`。执行 `tools/WhisperX/Install-Runtime.ps1` 可创建隔离环境并安装依赖和离线句子切分数据。

识别模型采用 faster-whisper / CTranslate2 格式，至少包含 `config.json`、`model.bin`、`tokenizer.json`。对齐模型采用 Wav2Vec2ForCTC，包含配置、音频预处理器、词表及 `model.safetensors` 或 `pytorch_model.bin`。导入模型仅登记本地路径，不修改用户原文件。

## 时间与进度协议

服务以 FFmpeg 提取选定源音轨，按 `SourceStartSeconds` 和 `DurationSeconds` 裁切为 16 kHz 单声道 16-bit PCM WAV。Python 直接读取 WAV，不依赖另一份全局 FFmpeg。返回的句子、词、字时间都加上源范围偏移，即原始媒体时钟；时间线变速、裁切和媒体绑定由 `SubtitleTrackService` 统一映射。

stdout 仅输出 UTF-8 JSON Lines，包含 `type=progress`、`stage`、`message`、0–1 的 `fraction`，可附处理秒数、总时长和下载字节数。第三方库诊断输出到 stderr，服务在内存中保留最后 16 KB，同时把完整 stdout、stderr、启动参数及十六进制退出码写入 exe 同级的 `data/diagnostics/WhisperX/`。独立结果文件经原子替换写入，成功退出后才解析，不应用半份结果。

中文字幕输出分别提供简体和繁体选择。为避免简体词表的中文 Wav2Vec2 对齐模型漏掉繁体字符，识别结果先转换为简体后强制对齐，再按指定字形做上下文转换；逐字时间、置信度和未对齐标记按原字符映射，长度不同的转换字符保持未对齐，不插值。

WhisperX 的人声活动区间最多 8 秒，`transcribe(chunk_size=8)` 保护无静音的长讲话。识别并对齐后由 `caption_postprocess.py` 按句末/分句标点、对齐字词之间的停顿、8 秒时长、24 个中日韩字符、64 个拉丁字母数字字符和 12 词上限全局选择自然边界；优先保留完整英文单词、标点/引号附着和上下文。每条保留真实字/词时间。无足够真实对齐时，最长段会明确失败，不生成均匀伪时间或超长句子。

字符时间来自强制对齐。不能对齐的标点或字符保持 `startSeconds/endSeconds=null`、`isAligned=false`，绝不按句长均分。结果的 Python / NumPy 标量统一转为原生布尔和浮点值，防止 JSON 序列化失败。

强制停止调用取消令牌，在取消回调中 `Kill(entireProcessTree:true)`，随后等待输出管道关闭再清理临时目录。识别结果返回前再次检查取消状态，由界面同时验证工程、序列、修订号和绑定媒体，避免把旧任务应用到新工程。

## 验证

- 独立 C# 编译：`artifacts/WhisperXBackendCompile/Check.csproj`，0 错误、0 警告。
- 协议和下载器回归：`tests/WhisperXBackendCheck/check_backend.py`，7 项通过；覆盖分片断点恢复及校验、缺失模型拒绝、源偏移、未知对齐时间、NumPy JSON 类型、非法时间、权重选择。
- 实际 WhisperX 3.8.6 / CPU PyTorch 2.8.0 识别：`tools/WhisperX/smoke_models.py`，Tiny 英文 3 句 / 99 个对齐字符、Base 英文 3 句 / 99 个、Belle 中文 1 句 / 42 个。原始事件、结果和诊断保存在 `artifacts/whisperx-smoke/`，汇总为 `smoke-summary.json`。
- 真实 C# 服务集成：`dotnet run --project tests/WhisperXServiceCheck/WhisperXServiceCheck.csproj`，验证双音轨媒体选择第 2 个音轨、源范围裁切、原媒体时间偏移、详细进度单调、反序列化后的逐字时间；实际进入识别时强制取消并在约 0.032 秒终止并清理工作目录；静音返回空结果；缺失模型和非法时长立即拒绝。结果保存在 `artifacts/whisperx-service-check/summary.json`。

模型下载和安装环境的操作记录及第三方许可记录保留在模型目录的清单中。未使用真实语音的检查不能替代上述实际识别验证。

## 2026-10-22 长音频崩溃修复

实际用户故障由 Windows Application 事件 1000 和原生转储确认：CTranslate2 的 `std::thread` 工作线程仅保留 2,031,616 字节栈，长音频批次申请 2,688,000 字节原生栈，进程以 `0xC00000FD` 退出。TorchCodec 的导入警告也存在于成功的识别中，不能用来判断此次故障。调整 OpenMP 栈变量无法扩大 CT2 主工作线程的栈，已用真实识别及原生回归分别验证。

CPU 默认批次改为 2；C# 服务和独立 Python worker 均将 CPU 请求限制为最多 2，完整遍历所有音频批次，GPU 请求保持其指定批次。原生退出异常包含退出码与完整日志路径，避免再把依赖警告显示成唯一故障信息。

- 同一段 288.72 秒中文，旧版 Base / BELLE 在批次 8 时均复现 `0xC00000FD`。
- 默认 Debug 目录中的修复版 worker 接收批次 8 请求，实际按 2 执行；Base / BELLE 均成功完成 11 段识别及对齐，分别得到 1008 / 714 个对齐字符。报告：`artifacts/whisperx-long-batch-fixed/summary.json`。
- C# 服务使用当前应用默认参数完整执行 FFmpeg 提取、WhisperX 识别及对齐，288.72 秒音频以约 62.5 秒完成，11 段、1008 字，末段到 287.836 秒。报告：`artifacts/whisperx-service-check/long-default-summary.json`。
- `--diagnostic-check` 验证原生退出码识别与完整持久日志；协议回归现为 9 项通过。
- 原生线程回归：`tests/WhisperXNativeStackCheck/check_native_stack.py`，原始大分配与仅调整 OpenMP 栈均复现故障，限制分配后成功。

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj --configuration Debug
& "TANGERINE-VideoEditor/bin/Debug/net10.0-windows/data/WhisperX/venv/Scripts/python.exe" tools/WhisperX/check_long_batch.py
dotnet run --project tests/WhisperXServiceCheck/WhisperXServiceCheck.csproj --configuration Debug -- . --long-default
dotnet run --project tests/WhisperXServiceCheck/WhisperXServiceCheck.csproj --configuration Debug -- . --diagnostic-check
```

按用户要求，每次代码修改后均编译到默认 `TANGERINE-VideoEditor/bin/Debug/net10.0-windows/`；规则登记在项目根 `AGENTS.md`。

## 2026-10-22 缩放预览只读异常修复

已用旧 Debug 程序在命令行复现 `SUBTL0910`：空格等无轮廓字符的 `FormattedText.BuildGeometry` 返回冻结的空几何，缩放预览设置其 `Transform` 时抛出只读异常。修复跳过空几何的绘制并保留排版间距，对冻结的非空几何先克隆再缩放。单独测试原尺寸 PNG 无法覆盖此故障。

实际界面识别检查从双击工具和点击“开始识别”进入真实 WhisperX，识别英文音频并生成媒体绑定字幕轨道，在约 0.254 倍缩放绘制非空字幕预览，18 项通过；另有 507 项渲染与播放检查、52 项字幕域检查、9 项真实 MP4 导出检查通过。具体证据路径见 `AiSubtitleContract.md`。界面层异常也将完整堆栈写入上述诊断目录，便于定位后续故障。
