using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElBruno.QwenTTS.Audio;
using ElBruno.QwenTTS.Models;
using ElBruno.QwenTTS.VoiceCloning.Models;
using Microsoft.ML.OnnxRuntime;
using Tangerine.VideoEditor.Speech;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
TextWriter protocol = Console.Out;
// Third-party diagnostics must never corrupt the line-delimited protocol.
Console.SetOut(Console.Error);
void Emit(SpeechEvent message) { protocol.WriteLine(JsonSerializer.Serialize(message, SpeechJson.Options)); protocol.Flush(); }
QwenSession? session = null;
bool runtimeConfigured = false;
int workerCpuThreads = 1;
// Do not retry a GPU that failed inference for this model on every subsequent sentence.
HashSet<string> failedAutomaticGpuModels = new(StringComparer.OrdinalIgnoreCase);
HashSet<string> preferHybridModels = new(StringComparer.OrdinalIgnoreCase);
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        string? activeGpuMode = null;
        SpeechRequest? activeRequest = null;
        void Report(SpeechEvent message)
        {
            message.GpuMode ??= activeGpuMode;
            Emit(message);
        }
        try
        {
            if (line.Length > 2_000_000) throw new SpeechFailure("VOICE0001", "语音请求超过大小限制。");
            var request = JsonSerializer.Deserialize<SpeechRequest>(line, SpeechJson.Options) ?? throw new InvalidDataException("请求为空。");
            activeRequest = request;
            if (request.Command == "devices") { Emit(new() { Type = "result", Devices = GpuDevices.List() }); continue; }
            if (request.Command != "synthesize") throw new SpeechFailure("VOICE0001", "未知的语音命令。");
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 10000 ||
                !SpeechCatalog.Languages.Any(l => l.Code == request.Language)) throw new SpeechFailure("VOICE0001", "文字或语言无效。");
            if (!runtimeConfigured)
            {
                workerCpuThreads = request.CpuThreads > 0
                    ? Math.Clamp(request.CpuThreads, 1, Environment.ProcessorCount)
                    : SpeechPerformance.ThreadsPerWorker(SpeechPerformance.MaxWorkers);
                using var threads = new OrtThreadingOptions
                {
                    GlobalIntraOpNumThreads = workerCpuThreads,
                    GlobalInterOpNumThreads = 1,
                    GlobalSpinControl = false
                };
                var environment = new EnvironmentCreationOptions { threadOptions = threads, logId = "TangerineSpeech" };
                OrtEnv.CreateInstanceWithOptions(ref environment);
                runtimeConfigured = true;
            }
            string device = request.Device;
            List<SpeechDevice>? devices = device == "cpu" ? null : GpuDevices.List();
            if (device == "auto") device = failedAutomaticGpuModels.Contains(request.ModelPath)
                ? "cpu" : devices!.FirstOrDefault(d => d.Id.StartsWith("dml:"))?.Id ?? "cpu";
            // Both talker graphs remain resident. Leave room for activations,
            // compiled graph buffers and the code predictor on larger models.
            ulong Weights(string name)
            {
                string file = Path.Combine(request.ModelPath, name);
                return File.Exists(file) ? (ulong)new FileInfo(file).Length : 0;
            }
            ulong graphWeights = Weights("talker_prefill.onnx.data") + Weights("talker_decode.onnx.data") +
                Weights("code_predictor.onnx") + Weights("code_predictor.onnx.data");
            ulong fullGpuMinimumBytes = Math.Max(6UL * 1024 * 1024 * 1024, graphWeights + graphWeights / 2);
            SpeechDevice? gpuDevice = devices?.FirstOrDefault(d => d.Id == device);
            // DXGI shared-memory capacity is a system-memory ceiling, not a
            // current GPU budget. Do not count it as resident capacity for the
            // full language model; the hybrid path still uses DirectML.
            ulong dedicatedGpuCapacity = gpuDevice?.DedicatedVideoMemory ?? 0;
            bool hybrid = device != "cpu" && (request.PreferHybrid || dedicatedGpuCapacity < fullGpuMinimumBytes || preferHybridModels.Contains(request.ModelPath));
            void Run(string selectedDevice, bool partialGpu)
            {
                activeGpuMode = selectedDevice == "cpu" ? "cpu" : partialGpu ? "hybrid" : "full";
                string key = request.ModelPath + "|" + selectedDevice + "|" + partialGpu;
                if (session?.Key != key) { session?.Dispose(); session = null; }
                if (session is null)
                {
                    Report(new() { Stage = "loading-cpu", Message = "正在准备模型词表与嵌入", Backend = QwenSession.BackendFor(selectedDevice, partialGpu) });
                    session = new QwenSession(request.ModelPath, selectedDevice, partialGpu, workerCpuThreads);
                }
                session.Synthesize(request, Report);
            }
            try
            {
                Run(device, hybrid);
            }
            catch (GpuGraphException failure) when (device != "cpu")
            {
                session?.Dispose(); session = null;
                if (!hybrid)
                {
                    preferHybridModels.Add(request.ModelPath);
                    activeGpuMode = "hybrid";
                    Report(SpeechGraphDiagnostic.Attach(new()
                    {
                        Stage = "loading", Backend = QwenSession.BackendFor(device, true),
                        Message = $"完整 GPU 模式下 {SpeechGraphDiagnostic.GraphName(failure)} 图的{SpeechGraphDiagnostic.FailurePhase(failure)}；改为 GPU 编码预测器 + CPU 语言模型，继续使用所选显卡。详细原因已保存到本地语音日志。"
                    }, failure, request, "GPU_FULL_TO_HYBRID"));
                    try { Run(device, true); }
                    catch (GpuGraphException hybridFailure) when (request.Device == "auto")
                    {
                        session?.Dispose(); session = null;
                        failedAutomaticGpuModels.Add(request.ModelPath);
                        activeGpuMode = "cpu";
                        Report(SpeechGraphDiagnostic.Attach(new()
                        {
                            Stage = "loading-cpu", Backend = "CPU",
                            Message = $"所选显卡上的 {SpeechGraphDiagnostic.GraphName(hybridFailure)} 图的{SpeechGraphDiagnostic.FailurePhase(hybridFailure)}；自动模式改用 CPU。详细原因已保存到本地语音日志。"
                        }, hybridFailure, request, "GPU_HYBRID_TO_CPU"));
                        Run("cpu", false);
                    }
                }
                else if (request.Device == "auto")
                {
                    failedAutomaticGpuModels.Add(request.ModelPath);
                    activeGpuMode = "cpu";
                    Report(SpeechGraphDiagnostic.Attach(new()
                    {
                        Stage = "loading-cpu", Backend = "CPU",
                        Message = $"所选显卡上的 {SpeechGraphDiagnostic.GraphName(failure)} 图的{SpeechGraphDiagnostic.FailurePhase(failure)}；自动模式改用 CPU。详细原因已保存到本地语音日志。"
                    }, failure, request, "GPU_HYBRID_TO_CPU"));
                    Run("cpu", false);
                }
                else throw;
            }
            Report(new() { Type = "result", Backend = session!.DescribeBackend(request), Message = request.OutputPath });
        }
        catch (Exception e)
        {
            session?.Dispose(); session = null;
            var error = new SpeechEvent { Type = "error", StageCode = e is SpeechFailure failure ? failure.StageCode : "VOICE0003", Message = e.Message };
            if (e is GpuGraphException graphFailure)
            {
                error.Message = $"所选显卡上的 {SpeechGraphDiagnostic.GraphName(graphFailure)} 图的{SpeechGraphDiagnostic.FailurePhase(graphFailure)}，已保留显卡选择。详细原因已保存到本地语音日志。";
                SpeechGraphDiagnostic.Attach(error, graphFailure, activeRequest, "GPU_INFERENCE_FAILED");
            }
            Report(error);
        }
    }
}
finally { session?.Dispose(); }
internal sealed class QwenSession : IDisposable
{
    public string Key { get; }
    public string Backend { get; }
    private readonly TextTokenizer _tokenizer;
    private readonly EmbeddingStore _embeddings;
    private readonly LanguageModel _language;
    private readonly Vocoder _vocoder;
    private readonly SpeakerEncoder? _speaker;
    private readonly SpeechTokenizer? _speechTokenizer;
    private readonly bool _gpu;
    private readonly Dictionary<string, float[]> _references = [];
    private readonly Dictionary<string, long[,,]> _referenceCodes = [];
    public static string BackendFor(string device, bool hybrid) => device == "cpu" ? "CPU" : hybrid
        ? $"DirectML GPU {device[4..]} 编码预测器 + CPU 语言模型与声码器"
        : $"DirectML GPU {device[4..]} 语言模型 + CPU 声码器";
    public QwenSession(string path, string device, bool hybrid = false, int cpuThreads = 1)
    {
        Key = path + "|" + device + "|" + hybrid;
        if (device != "cpu" && (!device.StartsWith("dml:") || !int.TryParse(device[4..], out _)))
            throw new SpeechFailure("VOICE0002", "请选择 CPU 或有效的 DirectML 显卡。");
        _gpu = device != "cpu";
        SessionOptions Options(bool gpu)
        {
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableMemoryPattern = !gpu,
                EnableCpuMemArena = !gpu };
            options.DisablePerSessionThreads();
            // Every local inference request contains exactly one utterance.
            // Expose this invariant to the provider without fixing variable text/KV lengths.
            options.AddFreeDimensionOverrideByName("batch_size", 1);
            if (gpu && device != "cpu") options.AppendExecutionProvider_DML(int.Parse(device[4..]));
            return options;
        }
        _tokenizer = new TextTokenizer(Path.Combine(path, "tokenizer"));
        _embeddings = new EmbeddingStore(Path.Combine(path, "embeddings"), Path.Combine(path, "embeddings", "config.json"));
        bool IsGpuGraph(string file) => _gpu && (!hybrid || file == "code_predictor.onnx");
        _language = new LanguageModel(path, _embeddings,
            file => Options(IsGpuGraph(file)), IsGpuGraph);
        _vocoder = new Vocoder(Path.Combine(path, "vocoder.onnx"), () => Options(false), allowCpuFallback: false);
        if (File.Exists(Path.Combine(path, "speaker_encoder.onnx")))
            _speaker = new SpeakerEncoder(Path.Combine(path, "speaker_encoder.onnx"), () => Options(false), cpuThreads);
        string? referenceEncoder = new[] { "speech_tokenizer.onnx", "tokenizer12hz_encode.onnx" }
            .Select(file => Path.Combine(path, file)).FirstOrDefault(File.Exists);
        if (referenceEncoder is not null)
            _speechTokenizer = new SpeechTokenizer(referenceEncoder, () => Options(false));
        Backend = BackendFor(device, hybrid);
    }
    public string DescribeBackend(SpeechRequest request) => request.ReferencePath is null ? Backend
        : Backend + (_speechTokenizer is null ? " · 参考声纹克隆（未安装参考音频编码器）"
            : string.IsNullOrWhiteSpace(request.ReferenceText) ? " · 参考声纹克隆（未提供参考文字）" : " · ICL 参考录音与文字克隆");
    public void Synthesize(SpeechRequest request, Action<SpeechEvent> report, CancellationToken cancellationToken = default)
    {
        void Progress(string stage, string message) => report(new SpeechEvent { Stage = stage, Message = message, Backend = Backend });
        long graphOperation = 0;
        long lastGraphReport = 0;
        string? lastGraphStage = null;
        void GraphProgress(string graph, bool gpu, bool completed, int frame, int group)
        {
            long operation = ++graphOperation;
            string kind = frame < 0 ? "loading" : graph == "talker_prefill.onnx" ? "prefill"
                : graph == "talker_decode.onnx" ? "decode" : "predict";
            string stage = kind + (gpu ? "-gpu" : "-cpu");
            long now = Stopwatch.GetTimestamp();
            // A codec frame runs 15 dependent predictor graphs. Reporting before
            // and after every Run flushes thousands of cross-process JSON lines.
            if (stage == lastGraphStage && frame >= 0 &&
                Stopwatch.GetElapsedTime(lastGraphReport, now) < TimeSpan.FromSeconds(1)) return;
            lastGraphStage = stage;
            lastGraphReport = now;
            string name = graph == "talker_prefill.onnx" ? "语言模型预填充"
                : graph == "talker_decode.onnx" ? "语言模型解码" : "编码预测器";
            string position = frame > 0 ? $" · 帧 {frame}" + (group > 0 ? $" · 编码组 {group}/15" : "") : "";
            Progress(stage, $"{(completed ? "已完成" : "正在运行")} {name}{position} · 步骤 {operation}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string language = SpeechCatalog.Languages.First(l => l.Code == request.Language).EngineName;
        if (language != "auto" && !_embeddings.Config.language_ids.ContainsKey(language))
            throw new SpeechFailure("VOICE0003", "该模型不包含所选语言。");
        int[] tokens = _tokenizer.BuildCustomVoicePrompt(request.Text, request.Speaker, language);
        long[,,] codes;
        long[,,]? referenceCodes = null;
        if (request.ReferencePath is { } reference)
        {
            if (_speaker is null) throw new SpeechFailure("VOICE0003", "克隆音色需要安装 Base 模型。");
            if (!_references.TryGetValue(reference, out float[]? embedding))
            {
                Progress("reference", "正在编码参考录音声纹");
                if (_references.Count >= 16) { _references.Clear(); _referenceCodes.Clear(); }
                _references[reference] = embedding = _speaker.EncodeFromWav(reference);
            }
            if (embedding.Length != _embeddings.HiddenSize || embedding.Any(x => !float.IsFinite(x)))
                throw new SpeechFailure("VOICE0003", "参考声纹无效或与模型维度不符，请重新录制清晰人声并检查 Base 模型。");
            if (!string.IsNullOrWhiteSpace(request.ReferenceText) && _speechTokenizer is not null)
            {
                if (!_referenceCodes.TryGetValue(reference, out long[,,]? audioCodes))
                {
                    Progress("reference", "正在编码参考录音语音");
                    _referenceCodes[reference] = audioCodes = _speechTokenizer.EncodeFromWav(reference);
                }
                if (audioCodes.GetLength(0) != 1 || audioCodes.GetLength(2) != 16 || audioCodes.GetLength(1) < 1)
                    throw new SpeechFailure("VOICE0003", "参考录音编码格式无效。");
                referenceCodes = audioCodes;
                Progress("prefill-cpu", "正在准备文本嵌入");
                codes = _language.GenerateWithSpeakerEmbeddingAndRefText(tokens, embedding, language,
                    refTokenIds: _tokenizer.Encode(request.ReferenceText), refAudioCodes: audioCodes,
                    cancellationToken: cancellationToken, onGraphProgress: GraphProgress);
            }
            else
            {
                Progress("prefill-cpu", "正在准备文本嵌入");
                codes = _language.GenerateWithSpeakerEmbedding(tokens, embedding, language,
                    cancellationToken: cancellationToken, onGraphProgress: GraphProgress);
            }
        }
        else
        {
            Progress("prefill-cpu", "正在准备文本嵌入");
            codes = _language.Generate(tokens, request.Speaker, language,
                cancellationToken: cancellationToken, onGraphProgress: GraphProgress);
        }
        if (codes.GetLength(2) >= 2048) throw new SpeechFailure("VOICE0003", "句子过长，已达到生成上限，请拆分句子。");
        Progress("vocoder", "正在使用声码器合成音频");
        long vocoderStarted = Stopwatch.GetTimestamp();
        float[] samples;
        if (referenceCodes is not null)
        {
            int prefix = referenceCodes.GetLength(1), count = codes.GetLength(2);
            if (referenceCodes.GetLength(0) != 1 || referenceCodes.GetLength(2) != 16 || prefix < 1)
                throw new SpeechFailure("VOICE0003", "参考录音编码格式无效。");
            var joined = new long[1, 16, prefix + count];
            for (int q = 0; q < 16; q++)
            {
                for (int t = 0; t < prefix; t++) joined[0, q, t] = referenceCodes[0, t, q];
                for (int t = 0; t < count; t++) joined[0, q, prefix + t] = codes[0, q, t];
            }
            samples = _vocoder.Decode(joined, cancellationToken)[(prefix * Vocoder.SamplesPerFrame)..];
        }
        else samples = _vocoder.Decode(codes, cancellationToken);
        Progress("vocoder", $"声码器已完成 · {Stopwatch.GetElapsedTime(vocoderStarted).TotalSeconds:F1} 秒 · {codes.GetLength(2)} 帧");
        if (samples.Length == 0 || samples.Any(x => !float.IsFinite(x))) throw new SpeechFailure("VOICE0003", "模型未产生有效语音。");
        cancellationToken.ThrowIfCancellationRequested();
        Progress("writing", "正在写入语音文件");
        WavWriter.Write(request.OutputPath, samples, 24000);
    }
    public void Dispose()
    { _language.Dispose(); _vocoder.Dispose(); _speaker?.Dispose(); _speechTokenizer?.Dispose(); _tokenizer.Dispose(); _embeddings.Dispose(); }
}

/// <summary>Native graph details with request data removed before leaving the worker.</summary>
internal static class SpeechGraphDiagnostic
{
    private static readonly string[] Graphs = ["talker_prefill.onnx", "talker_decode.onnx", "code_predictor.onnx"];
    public static string GraphName(GpuGraphException failure) => Graphs.Contains(failure.Graph, StringComparer.Ordinal)
        ? failure.Graph : "unknown.onnx";

    public static string FailurePhase(GpuGraphException failure) => failure.Phase == "兼容缓存准备"
        ? "兼容缓存准备阶段失败" : "推理执行阶段失败";

    public static SpeechEvent Attach(SpeechEvent message, GpuGraphException failure, SpeechRequest? request, string code)
    {
        Exception native = failure.InnerException ?? failure;
        message.DiagnosticCode = code;
        message.DiagnosticGraph = GraphName(failure);
        message.DiagnosticException = native.GetType().Name;
        string reason = $"阶段={failure.Phase}\nHRESULT=0x{native.HResult:X8}\n{native}";
        // Keep operator names, tensor shapes and native error numbers. Never log
        // source text, reference text, recording paths or private user directories.
        var secrets = new List<string?> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (request is not null)
        {
            secrets.AddRange([request.ModelPath, request.ReferencePath, request.OutputPath, request.Text, request.ReferenceText]);
            foreach (string? file in new[] { request.ReferencePath, request.OutputPath })
                if (!string.IsNullOrWhiteSpace(file)) secrets.Add(Path.GetDirectoryName(file));
        }
        foreach (string value in secrets.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).OrderByDescending(value => value.Length))
        {
            reason = reason.Replace(value, "[redacted]", StringComparison.OrdinalIgnoreCase)
                .Replace(value.Replace('\\', '/'), "[redacted]", StringComparison.OrdinalIgnoreCase)
                .Replace(value.Replace('/', '\\'), "[redacted]", StringComparison.OrdinalIgnoreCase);
        }
        reason = Regex.Replace(reason, @"(?i)[a-z]:[\\/]+Users[\\/]+[^\\/\r\n:""<>|]+", "[user]");
        message.DiagnosticReason = reason.Length <= 4000 ? reason : reason[..4000] + " [truncated]";
        return message;
    }
}
