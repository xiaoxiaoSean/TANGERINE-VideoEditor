# WhisperX 模型与本机验证

模型下载工具位于 `tools/WhisperX/download_test_models.py`。它只使用 Python 标准库，固定使用 32 个工作线程；每个大于 1 MiB 的文件拆成 32 个精确 HTTP Range 分片，验证 206 状态与 Content-Range。下载固定至 Hugging Face 返回的 commit，完成后校验 LFS SHA256，普通 Git 文件校验 Git blob SHA1，校验通过后原子替换目标文件。已完成的分片可续传；最终校验失败则清理损坏分片，避免反复复用损坏数据。

```powershell
python tools/WhisperX/download_test_models.py
python tools/WhisperX/download_test_models.py --all-languages
python tools/WhisperX/download_test_models.py --repo Systran/faster-whisper-base --destination C:/MyModels
```

默认模型在仓库根 `whisperx_models/作者--模型名/`。权重不应提交 Git；每个模型文件夹中的 `download-manifest.json` 保存仓库 ID、固定 commit、文件大小、官方校验和、许可、32 线程数和验证时间。重新运行会重新校验已有文件，校验通过后复用。模型 ID 不包含访问令牌，下载公开、非 gated 仓库。

## 已实际下载并核验的模型

2026-10-22 已完成下列 14 个模型，合计 **13,295,194,650 字节（约 12.38 GiB）**，全部权重 SHA256 与 Hugging Face API 一致；全部使用 32 个下载工作线程。默认命令下载五个核心测试模型，`--all-languages` 下载全部 14 个。四种识别模型覆盖多语言基础模型及中文定制模型，十种对齐模型覆盖汉语、英语、日语、韩语、法语、德语、西班牙语、意大利语、葡萄牙语、俄语：

| 用途 | Hugging Face 仓库 | 实际目录大小 | 许可 |
|---|---|---:|---|
| 多语言识别 | Systran/faster-whisper-tiny | 78,205,610 B | MIT |
| 多语言识别 | Systran/faster-whisper-base | 147,884,932 B | MIT |
| 多语言识别 | Systran/faster-whisper-small | 486,214,370 B | MIT |
| 第三方中文识别 | wolfofbackstreet/faster-whisper-belle-whisper-large-v3-turbo-zh-ct2-int8 | 817,835,681 B | Apache-2.0 |
| 英文对齐 | facebook/wav2vec2-base-960h | 377,614,626 B | Apache-2.0 |
| 中文对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-chinese-zh-cn | 1,276,350,040 B | Apache-2.0 |
| 日语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-japanese | 1,271,571,669 B | Apache-2.0 |
| 韩语对齐 | kresnik/wav2vec2-large-xlsr-korean | 1,266,770,694 B | Apache-2.0 |
| 法语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-french | 1,262,183,684 B | Apache-2.0 |
| 德语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-german | 1,262,097,816 B | Apache-2.0 |
| 西班牙语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-spanish | 1,262,109,798 B | Apache-2.0 |
| 意大利语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-italian | 1,262,122,269 B | Apache-2.0 |
| 葡萄牙语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-portuguese | 1,262,130,314 B | Apache-2.0 |
| 俄语对齐 | jonatasgrosman/wav2vec2-large-xlsr-53-russian | 1,262,103,147 B | Apache-2.0 |

识别模型导入完整 CTranslate2 目录（`config.json`、`model.bin`、`tokenizer.json`、词表）；原始 OpenAI/Transformers Whisper 权重须先使用 CTranslate2 转换。对齐模型导入 Wav2Vec2 CTC 目录，需模型权重、`config.json`、`preprocessor_config.json`、词表和 tokenizer 配置。不要将识别模型用作对齐模型。

## 隔离运行时

安装脚本要求明确指定 Python 3.10–3.13，创建独立 venv，默认不改动全局 Python。此次使用 Codex bundled Python 3.12.14，环境位于 `TANGERINE-VideoEditor/bin/Debug/net10.0-windows/data/WhisperX/venv`，CPU PyTorch 2.8.0、torchaudio 2.8.0、torchvision 0.23.0、WhisperX 3.8.6、faster-whisper 1.2.1、Transformers 4.57.6。GPU 可使用用户自行配置的匹配运行时。

```powershell
powershell -ExecutionPolicy Bypass -File tools/WhisperX/Install-Runtime.ps1 -PythonExecutable C:/Python312/python.exe
```

WhisperX 对齐还需要 NLTK `punkt_tab` 分句数据。`install_nltk_data.py` 从 NLTK 官方数据仓库固定 commit 下载 ZIP，校验 SHA256 并检查解压路径后安装至专用 venv 的 `nltk_data`。该步骤避免首次识别期间隐式联网及 NLTK 自动下载失败。音频检测使用随 faster-whisper 安装的 Silero ONNX 权重，不从 torch.hub 下载或执行远程仓库。

## 验证命令

```powershell
python tools/WhisperX/test_downloader.py
powershell -ExecutionPolicy Bypass -File tools/WhisperX/Create-SmokeAudio.ps1
& "TANGERINE-VideoEditor/bin/Debug/net10.0-windows/data/WhisperX/venv/Scripts/python.exe" tools/WhisperX/smoke_models.py --ffmpeg C:/path/to/ffmpeg.exe
& "TANGERINE-VideoEditor/bin/Debug/net10.0-windows/data/WhisperX/venv/Scripts/python.exe" tools/WhisperX/verify_installed_models.py
dotnet run --project tests/WhisperXIntegrationCheck/WhisperXIntegrationCheck.csproj --artifacts-path artifacts/whisperx-integration-build
```

下载器的本地 HTTP 测试覆盖恰好 32 个不重叠 Range、已验证文件复用、分片中断续传、路径逃逸拒绝和损坏校验拒绝。真实识别测试使用本机 Windows SAPI 合成的中英文录音，未使用用户媒体；通过实际应用 worker 验证 Tiny/Base 英文、Belle 中文及两种对齐模型，检查详细阶段事件、源时间偏移、有限时间范围和强制对齐字符。测试输出位于忽略的 `artifacts/whisperx-smoke/`。

2026-10-22 实际 CPU 离线识别全部通过：Tiny 英文 18.83 秒、Base 英文 17.84 秒，分别得到 3 句、99 个有效对齐字符；第三方 Belle 中文 27.80 秒，得到 1 段、42 个有效对齐字符。英文识别内容与合成文本一致；中文识别为“大家好，欢迎使用智能字幕工具。这是一段中文语音测试，每一个字都应该在正确的时间出现。”。各案例均完成环境、模型载入、人声检测、识别、对齐、整理、完成阶段事件，输出时间均包含测试设置的 3.5 秒源时间偏移。小样例只验证链路及时间数据完整性，不代表真实场景识别准确率评测。

C# 端到端 `WhisperXIntegrationCheck` 通过 32 项检查：真实调用 `WhisperXService.TranscribeAsync` 提取并裁切源音频、运行 Tiny/Base/Belle、反序列化实际对齐结果，再调用 `SubtitleTrackService.CreateTrack` 创建绑定原媒体的专用字幕轨、校验字幕范围、移动原片段并确认字幕同步、JSON 工程序列化及恢复。强制停止测试在实际 Python 工作进程存活时取消，记录新启动的 Python PID，返回后验证这些进程全部退出且临时音频工作目录已清理。测试 FFmpeg 及 DLL 从项目现有 NuGet 依赖复制至独立测试输出，不影响正在运行的程序。`pip check` 无依赖冲突。

全部 14 个模型又通过 `verify_installed_models.py` 的二次逐文件校验及离线加载验证：四个识别模型以 CTranslate2 CPU int8 实际加载，十个对齐模型的 processor、词表和 Wav2Vec2ForCTC 权重均以 `local_files_only=True` 实际加载。报告保存至 `artifacts/whisperx-smoke/models-verified.json`，逐项记录固定 commit、字节数、线程数、加载耗时和词表大小。模型文件没有放入 AppData；隔离 Python 运行时位于主程序 exe 同级 `data/WhisperX/venv`，NLTK 数据位于该 venv 的 `nltk_data`，预置模型位于项目根 `whisperx_models/`。
