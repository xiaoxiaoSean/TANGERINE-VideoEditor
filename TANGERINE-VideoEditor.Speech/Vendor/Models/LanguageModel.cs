using System.Buffers;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace ElBruno.QwenTTS.Models;

internal sealed class GpuGraphException(string graph, string phase, Exception inner)
    : Exception($"DirectML 图 {graph} {phase}失败：{inner.Message}", inner)
{
    public string Graph { get; } = graph;
    public string Phase { get; } = phase;
}

/// <summary>
/// ONNX language model for Qwen3-TTS.
/// Runs autoregressive inference with KV-cache to generate audio codes
/// from tokenized text input.
/// </summary>
internal sealed class LanguageModel : IDisposable
{
    private readonly EmbeddingStore _embeddings;
    private readonly string _modelDir;
    private readonly Func<string, SessionOptions> _sessionOptionsFactory;
    private readonly Func<string, bool> _gpuGraph;
    private readonly Lazy<InferenceSession> _prefillSession;
    private readonly Lazy<InferenceSession> _decodeSession;
    private readonly Lazy<InferenceSession> _cpSession;

    // Dimensions from config — set once after EmbeddingStore loads config.json
    private readonly int _hiddenSize;       // talker hidden_size (1024 for 0.6B, 2048 for 1.7B)
    private readonly int _textHiddenSize;   // text embedding dim (2048 for both)
    private readonly int _cpHiddenSize;     // CP embedding dim (1024 for both)
    private readonly int _numLayers;        // talker num_hidden_layers
    private readonly int _numKvHeads;       // talker num_key_value_heads
    private readonly int _headDim;          // talker head_dim
    private readonly int _cpNumLayers;      // code_predictor num_hidden_layers
    private readonly int _cpNumKvHeads;     // code_predictor num_key_value_heads
    private readonly int _cpHeadDim;        // code_predictor head_dim

    public LanguageModel(string modelDir, EmbeddingStore embeddings,
        Func<string, SessionOptions>? sessionOptionsFactory = null, Func<string, bool>? gpuGraph = null)
    {
        _embeddings = embeddings;
        _modelDir = modelDir;
        _sessionOptionsFactory = sessionOptionsFactory ?? (_ => CreateDefaultOptions());
        _gpuGraph = gpuGraph ?? (_ => false);
        _prefillSession = CreateSessionLazy("talker_prefill.onnx");
        _decodeSession = CreateSessionLazy("talker_decode.onnx");
        _cpSession = CreateSessionLazy("code_predictor.onnx");

        // Read dimensions from config.json (loaded by EmbeddingStore)
        var cfg = embeddings.Config;
        _hiddenSize = cfg.talker.hidden_size;
        _textHiddenSize = cfg.talker.text_hidden_size;
        _cpHiddenSize = embeddings.CpHiddenSize;
        _numLayers = cfg.talker.num_hidden_layers;
        _numKvHeads = cfg.talker.num_key_value_heads;
        _headDim = cfg.talker.head_dim;
        _cpNumLayers = cfg.code_predictor.num_hidden_layers;
        _cpNumKvHeads = cfg.code_predictor.num_key_value_heads;
        _cpHeadDim = cfg.code_predictor.head_dim;
    }

    private static SessionOptions CreateDefaultOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Environment.ProcessorCount,
        InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        EnableMemoryPattern = true,
        EnableCpuMemArena = true
    };

    private Lazy<InferenceSession> CreateSessionLazy(string fileName) =>
        new(() => CreateSession(fileName), LazyThreadSafetyMode.ExecutionAndPublication);

    private InferenceSession CreateSession(string fileName)
    {
        // SEC-3: File size pre-check to prevent out-of-memory attacks
        var modelPath = Path.Combine(_modelDir, fileName);
        var fileInfo = new FileInfo(modelPath);
        const long maxOnnxSize = 8_000_000_000; // 8 GB (1.7B models are ~5.4 GB)
        if (fileInfo.Length > maxOnnxSize)
            throw new InvalidOperationException($"ONNX file too large ({fileInfo.Length / 1e9:F2} GB). Maximum allowed: {maxOnnxSize / 1e9:F2} GB.");

        using var options = _sessionOptionsFactory(fileName);
        // generation_steps contains one current residual-code group index.
        if (fileName == "code_predictor.onnx") options.AddFreeDimensionOverrideByName("num_steps", 1);
        string inferencePath = modelPath;
        if (_gpuGraph(fileName))
        {
            string applicationRoot = Environment.GetEnvironmentVariable("TANGERINE_APP_ROOT") is { Length: > 0 } configured
                ? Path.GetFullPath(configured)
                : Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? AppContext.BaseDirectory;
            try
            {
                inferencePath = DmlGraphCompatibility.Prepare(modelPath,
                    Path.Combine(applicationRoot, "cache", "onnx-dml"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or
                                      ArgumentException or NotSupportedException or InvalidOperationException or
                                      OverflowException or OutOfMemoryException or System.Security.SecurityException)
            {
                throw new GpuGraphException(fileName, "兼容缓存准备", e);
            }
        }
        return new InferenceSession(inferencePath, options);
    }

    private T RunGraph<T>(string fileName, Func<T> run)
    {
        try { return run(); }
        catch (Exception e) when (_gpuGraph(fileName) &&
            e is (OnnxRuntimeException or OutOfMemoryException or InvalidOperationException or DllNotFoundException or
                EntryPointNotFoundException or IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or System.Security.SecurityException or OverflowException))
        { throw new GpuGraphException(fileName, "推理", e); }
    }

    public long[,,] Generate(int[] tokenIds, string speaker, string language,
                             int maxNewTokens = 2048, float temperature = 0.9f,
                             int topK = 50, float topP = 1.0f,
                             float repetitionPenalty = 1.05f,
                             CancellationToken cancellationToken = default, Action<int>? onFrame = null,
                             Action? onGpuPredictor = null, Action<int, int>? onGpuGroup = null,
        Action<string, bool, bool, int, int>? onGraphProgress = null)
    {
        var speakerId = _embeddings.GetSpeakerId(speaker.ToLowerInvariant());
        return GenerateWithSpeakerId(tokenIds, speakerId, language, maxNewTokens, temperature, topK, topP, repetitionPenalty,
            cancellationToken, onFrame, onGpuPredictor, onGpuGroup, onGraphProgress);
    }

    /// <summary>
    /// Generate audio codes using an explicit speaker ID. Pass -1 to omit the speaker token
    /// from the codec prefix (used by the Base model where voice identity comes from a speaker embedding).
    /// </summary>
    public long[,,] GenerateWithSpeakerId(int[] tokenIds, int speakerId, string language,
                             int maxNewTokens = 2048, float temperature = 0.9f,
                             int topK = 50, float topP = 1.0f,
                             float repetitionPenalty = 1.05f,
                             CancellationToken cancellationToken = default, Action<int>? onFrame = null,
                             Action? onGpuPredictor = null, Action<int, int>? onGpuGroup = null,
                             Action<string, bool, bool, int, int>? onGraphProgress = null)
    {
        return GenerateInternal(tokenIds, speakerId, language, speakerEmbedding: null,
            maxNewTokens, temperature, topK, topP, repetitionPenalty,
            cancellationToken: cancellationToken, onFrame: onFrame,
            onGpuPredictor: onGpuPredictor, onGpuGroup: onGpuGroup, onGraphProgress: onGraphProgress);
    }

    /// <summary>
    /// Generate audio codes using a speaker embedding vector (e.g., from ECAPA-TDNN).
    /// The 1024-dim embedding is injected at the speaker position in the codec prefix,
    /// replacing the preset speaker token lookup. Used for voice cloning with the Base model.
    /// </summary>
    public long[,,] GenerateWithSpeakerEmbedding(int[] tokenIds, float[] speakerEmbedding, string language,
                             int maxNewTokens = 2048, float temperature = 0.9f,
                             int topK = 50, float topP = 1.0f,
                             float repetitionPenalty = 1.05f,
                             CancellationToken cancellationToken = default, Action<int>? onFrame = null,
                             Action? onGpuPredictor = null, Action<int, int>? onGpuGroup = null,
                             Action<string, bool, bool, int, int>? onGraphProgress = null)
    {
        return GenerateInternal(tokenIds, speakerId: -1, language, speakerEmbedding,
            maxNewTokens, temperature, topK, topP, repetitionPenalty,
            cancellationToken: cancellationToken, onFrame: onFrame,
            onGpuPredictor: onGpuPredictor, onGpuGroup: onGpuGroup, onGraphProgress: onGraphProgress);
    }

    /// <summary>
    /// Generate audio codes using a speaker embedding and optional ICL reference data.
    /// When refTokenIds and refAudioCodes are provided, enables In-Context Learning mode
    /// where the model uses both reference text and reference audio codes for higher-quality
    /// voice cloning.
    /// </summary>
    /// <param name="tokenIds">Target text token IDs from the prompt builder.</param>
    /// <param name="speakerEmbedding">1024-dim ECAPA-TDNN speaker embedding.</param>
    /// <param name="language">Language code (e.g., "english", "russian", "chinese", "auto").</param>
    /// <param name="refTokenIds">Optional reference text token IDs for ICL mode.</param>
    /// <param name="refAudioCodes">Optional reference audio codes [1, T, 16] for ICL mode.</param>
    /// <param name="maxNewTokens">Maximum number of audio frames to generate.</param>
    /// <param name="temperature">Sampling temperature.</param>
    /// <param name="topK">Top-k sampling parameter.</param>
    /// <param name="topP">Top-p (nucleus) sampling parameter.</param>
    /// <param name="repetitionPenalty">Repetition penalty factor.</param>
    /// <param name="cancellationToken">Cancellation token checked at safe inference loop boundaries.</param>
    public long[,,] GenerateWithSpeakerEmbeddingAndRefText(
        int[] tokenIds, float[] speakerEmbedding, string language,
        int[]? refTokenIds = null, long[,,]? refAudioCodes = null,
        int maxNewTokens = 2048, float temperature = 0.9f,
        int topK = 50, float topP = 1.0f,
        float repetitionPenalty = 1.05f,
        CancellationToken cancellationToken = default, Action<int>? onFrame = null,
        Action? onGpuPredictor = null, Action<int, int>? onGpuGroup = null,
                             Action<string, bool, bool, int, int>? onGraphProgress = null)
    {
        return GenerateInternal(tokenIds, speakerId: -1, language, speakerEmbedding,
            maxNewTokens, temperature, topK, topP, repetitionPenalty,
            refTokenIds, refAudioCodes, cancellationToken, onFrame, onGpuPredictor, onGpuGroup, onGraphProgress);
    }

    private long[,,] GenerateInternal(int[] tokenIds, int speakerId, string language,
                             float[]? speakerEmbedding,
                             int maxNewTokens, float temperature,
                             int topK, float topP,
                             float repetitionPenalty,
                             int[]? refTokenIds = null,
                             long[,,]? refAudioCodes = null,
                             CancellationToken cancellationToken = default, Action<int>? onFrame = null,
                             Action? onGpuPredictor = null, Action<int, int>? onGpuGroup = null,
                             Action<string, bool, bool, int, int>? onGraphProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cfg = _embeddings.Config;
        using var runOptions = new RunOptions();
        using var cancelRun = cancellationToken.Register(() => runOptions.Terminate = true);

        InferenceSession LoadGraph(string graph, Lazy<InferenceSession> lazy)
        {
            if (lazy.IsValueCreated) return lazy.Value;
            cancellationToken.ThrowIfCancellationRequested();
            onGraphProgress?.Invoke(graph, _gpuGraph(graph), false, -1, 0);
            var session = RunGraph(graph, () => lazy.Value);
            onGraphProgress?.Invoke(graph, _gpuGraph(graph), true, -1, 0);
            return session;
        }

        IDisposableReadOnlyCollection<OrtValue> Infer(string graph, InferenceSession session,
            string[] inputNames, OrtValue[] inputs, IReadOnlyCollection<string> outputNames, int frame, int group = 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onGraphProgress?.Invoke(graph, _gpuGraph(graph), false, frame, group);
            var outputs = RunGraph(graph, () => session.Run(runOptions, inputNames, inputs, outputNames));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                onGraphProgress?.Invoke(graph, _gpuGraph(graph), true, frame, group);
                return outputs;
            }
            catch { outputs.Dispose(); throw; }
        }

        var (inputsEmbeds, trailingTextHidden) = BuildPrefillEmbedding(
            tokenIds, speakerId, language, cfg, speakerEmbedding, refTokenIds, refAudioCodes);
        int prefillLen = inputsEmbeds.GetLength(1);
        var flatEmbeds = ArrayPool<float>.Shared.Rent(prefillLen * _hiddenSize);
        var flatMask = ArrayPool<long>.Shared.Rent(prefillLen);
        var flatPosIds = ArrayPool<long>.Shared.Rent(3 * prefillLen);
        float[] logits, hiddenStates, pastKeys, pastValues;
        try
        {
            Buffer.BlockCopy(inputsEmbeds, 0, flatEmbeds, 0, prefillLen * _hiddenSize * sizeof(float));
            flatMask.AsSpan(0, prefillLen).Fill(1);
            for (int ax = 0; ax < 3; ax++)
                for (int i = 0; i < prefillLen; i++) flatPosIds[ax * prefillLen + i] = i;
            var prefillSession = LoadGraph("talker_prefill.onnx", _prefillSession);
            using var embedsOrt = OrtValue.CreateTensorValueFromMemory<float>(flatEmbeds, [1, prefillLen, _hiddenSize]);
            using var maskOrt = OrtValue.CreateTensorValueFromMemory<long>(flatMask, [1, prefillLen]);
            using var posOrt = OrtValue.CreateTensorValueFromMemory<long>(flatPosIds, [3, 1, prefillLen]);
            // Use explicit names: exported models need not retain metadata ordering.
            var prefillOutputsNames = new List<string> { "logits", "hidden_states" };
            for (int layer = 0; layer < _numLayers; layer++)
            {
                prefillOutputsNames.Add($"present_key_{layer}");
                prefillOutputsNames.Add($"present_value_{layer}");
            }
            using var outputs = Infer("talker_prefill.onnx", prefillSession,
                ["inputs_embeds", "attention_mask", "position_ids"], [embedsOrt, maskOrt, posOrt], prefillOutputsNames, 0);
            logits = outputs[0].GetTensorDataAsSpan<float>()[^cfg.talker.vocab_size..].ToArray();
            hiddenStates = outputs[1].GetTensorDataAsSpan<float>()[^_hiddenSize..].ToArray();
            (pastKeys, pastValues) = StackPrefillKVFromOrtValues(outputs, prefillLen);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(flatEmbeds);
            ArrayPool<long>.Shared.Return(flatMask);
            ArrayPool<long>.Shared.Return(flatPosIds);
        }

        // Keep native output tensors alive as the next call's KV inputs. Default
        // Run outputs are CPU tensors; this removes managed copies, not transfers
        // performed internally by DirectML. Never dereference a device buffer.
        using var talkerCache = new RetainedKv(pastKeys, pastValues,
            [_numLayers, 1, _numKvHeads, prefillLen, _headDim]);
        pastKeys = pastValues = [];
        var generatedCodes = new List<long[]>();
        var generatedTokens = new List<int>();
        Span<float> textEmbedding = stackalloc float[2048];
        var ttsPadEmbed = new float[_hiddenSize];
        _embeddings.TextEmbedding(cfg.tts.tts_pad_token_id, textEmbedding);
        _embeddings.TextProjection(textEmbedding, ttsPadEmbed);
        var group0EmbedBuf = new float[_hiddenSize];
        var cpEmbedBuf = new float[_cpHiddenSize];
        var nextInputBuf = new float[_hiddenSize];
        int cpInputDim = _embeddings.HasCpProjection ? _embeddings.CpModelHiddenSize : _hiddenSize;
        var cpInputs = new float[2 * cpInputDim];
        var generationStep = new long[1];
        var decodePositions = new long[3];
        var mask = new long[prefillLen + maxNewTokens + 1];
        Array.Fill(mask, 1L);
        using var cpFirstInput = OrtValue.CreateTensorValueFromMemory<float>(cpInputs, [1, 2, cpInputDim]);
        using var cpNextInput = OrtValue.CreateTensorValueFromMemory<float>(cpInputs, [1, 1, cpInputDim]);
        using var cpStepInput = OrtValue.CreateTensorValueFromMemory<long>(generationStep, [1]);
        using var decodeInput = OrtValue.CreateTensorValueFromMemory<float>(nextInputBuf, [1, 1, _hiddenSize]);
        using var decodePositionInput = OrtValue.CreateTensorValueFromMemory<long>(decodePositions, [3, 1, 1]);
        string[] cpInputNames = ["inputs_embeds", "generation_steps", "past_keys", "past_values"];
        string[] cpOutputNames = ["logits", "present_keys", "present_values"];
        string[] decodeInputNames = ["inputs_embeds", "attention_mask", "position_ids", "past_keys", "past_values"];
        string[] decodeOutputNames = ["logits", "hidden_states", "present_keys", "present_values"];
        bool gpuPredictor = _gpuGraph("code_predictor.onnx");
        long lastGroupProgress = Stopwatch.GetTimestamp();
        InferenceSession? cpSession = null, decodeSession = null;
        for (int step = 0; step < maxNewTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (step < 2) logits[cfg.talker.codec_eos_token_id] = float.NegativeInfinity;
            int group0Token = SampleToken(logits, temperature, topK, topP, repetitionPenalty, generatedTokens, cfg);
            if (group0Token == cfg.talker.codec_eos_token_id) break;
            generatedTokens.Add(group0Token);
            var codes = new long[16];
            codes[0] = group0Token;
            _embeddings.TalkerCodecEmbedding(group0Token, group0EmbedBuf);
            if (_embeddings.HasCpProjection)
            {
                _embeddings.CpProjection(hiddenStates, cpInputs.AsSpan(0, cpInputDim));
                _embeddings.ProjectedTalkerCodecEmbedding(group0Token, cpInputs.AsSpan(cpInputDim, cpInputDim));
            }
            else BuildCpPrefillDirect(cpInputs, hiddenStates, 0, group0EmbedBuf, cpInputDim);

            using (var cpCache = new RetainedKv([], [], [_cpNumLayers, 1, _cpNumKvHeads, 0, _cpHeadDim]))
            {
                if (cpSession is null)
                {
                    if (gpuPredictor && onGraphProgress is null) onGpuPredictor?.Invoke();
                    cpSession = LoadGraph("code_predictor.onnx", _cpSession);
                }
                for (int group = 1; group < 16; group++)
                {
                    generationStep[0] = group - 1;
                    var outputs = Infer("code_predictor.onnx", cpSession, cpInputNames,
                        [group == 1 ? cpFirstInput : cpNextInput, cpStepInput, cpCache.Keys, cpCache.Values], cpOutputNames, step + 1, group);
                    int cpToken;
                    try
                    {
                        cpToken = SampleTokenSimple(outputs[0].GetTensorDataAsSpan<float>(), temperature);
                        cpCache.Replace(outputs, 1, 2);
                    }
                    catch { outputs.Dispose(); throw; }
                    codes[group] = cpToken;
                    if (gpuPredictor && onGraphProgress is null && onGpuGroup is not null &&
                        ((step == 0 && group == 1) || Stopwatch.GetElapsedTime(lastGroupProgress) >= TimeSpan.FromSeconds(10)))
                    {
                        onGpuGroup(step + 1, group);
                        lastGroupProgress = Stopwatch.GetTimestamp();
                    }
                    if (group < 15)
                    {
                        if (_embeddings.HasCpProjection)
                            _embeddings.ProjectedCpCodecEmbedding(group - 1, cpToken, cpInputs.AsSpan(0, cpInputDim));
                        else
                        {
                            _embeddings.CpCodecEmbedding(group - 1, cpToken, cpEmbedBuf);
                            cpInputs.AsSpan(0, cpInputDim).Clear();
                            cpEmbedBuf.AsSpan(0, Math.Min(_cpHiddenSize, cpInputDim)).CopyTo(cpInputs);
                        }
                    }
                }
            }
            generatedCodes.Add(codes);
            if (step == 0 || (step + 1) % 4 == 0) onFrame?.Invoke(step + 1);
            if (step + 1 == maxNewTokens) break;
            _embeddings.TalkerCodecEmbedding((int)codes[0], nextInputBuf);
            for (int g = 1; g < 16; g++)
            {
                _embeddings.CpCodecEmbedding(g - 1, (int)codes[g], cpEmbedBuf);
                AccumulateCpEmbedding(nextInputBuf, cpEmbedBuf, _cpHiddenSize);
            }
            for (int j = 0; j < _hiddenSize; j++)
                nextInputBuf[j] += step < trailingTextHidden.GetLength(0) ? trailingTextHidden[step, j] : ttsPadEmbed[j];
            int newLen = prefillLen + step + 1;
            Array.Fill(decodePositions, (long)(prefillLen + step));
            using var decodeMaskInput = OrtValue.CreateTensorValueFromMemory<long>(mask, [1, newLen]);
            decodeSession ??= LoadGraph("talker_decode.onnx", _decodeSession);
            var decoded = Infer("talker_decode.onnx", decodeSession, decodeInputNames,
                [decodeInput, decodeMaskInput, decodePositionInput, talkerCache.Keys, talkerCache.Values], decodeOutputNames, step + 1);
            try
            {
                decoded[0].GetTensorDataAsSpan<float>()[^cfg.talker.vocab_size..].CopyTo(logits);
                decoded[1].GetTensorDataAsSpan<float>()[^_hiddenSize..].CopyTo(hiddenStates);
                talkerCache.Replace(decoded, 2, 3);
            }
            catch { decoded.Dispose(); throw; }
        }
        int count = generatedCodes.Count;
        var result = new long[1, 16, count];
        for (int t = 0; t < count; t++)
            for (int g = 0; g < 16; g++) result[0, g, t] = generatedCodes[t][g];
        return result;
    }

    private sealed class RetainedKv : IDisposable
    {
        private OrtValue? _initialKeys, _initialValues;
        private IDisposableReadOnlyCollection<OrtValue>? _outputs;
        public OrtValue Keys { get; private set; }
        public OrtValue Values { get; private set; }
        public RetainedKv(float[] keys, float[] values, long[] shape)
        {
            Keys = _initialKeys = OrtValue.CreateTensorValueFromMemory<float>(keys, shape);
            try { Values = _initialValues = OrtValue.CreateTensorValueFromMemory<float>(values, shape); }
            catch { _initialKeys.Dispose(); throw; }
        }
        public void Replace(IDisposableReadOnlyCollection<OrtValue> outputs, int keyIndex, int valueIndex)
        {
            OrtValue keys = outputs[keyIndex], values = outputs[valueIndex];
            var previous = _outputs;
            _outputs = outputs;
            Keys = keys; Values = values;
            _initialKeys?.Dispose(); _initialKeys = null;
            _initialValues?.Dispose(); _initialValues = null;
            previous?.Dispose();
        }
        public void Dispose()
        {
            _outputs?.Dispose();
            _initialKeys?.Dispose(); _initialValues?.Dispose();
        }
    }

    private (float[,,], float[,]) BuildPrefillEmbedding(int[] tokenIds, int speakerId, string language, ModelConfig cfg,
        float[]? speakerEmbeddingOverride = null,
        int[]? refTokenIds = null, long[,,]? refAudioCodes = null)
    {
        // Role embed: tokens [0:3]
        var roleEmbeds = new List<float[]>();
        var roleTextEmbBuf = new float[2048];
        var roleProjBuf = new float[_hiddenSize];
        for (int i = 0; i < 3; i++)
        {
            _embeddings.TextEmbedding(tokenIds[i], roleTextEmbBuf);
            _embeddings.TextProjection(roleTextEmbBuf, roleProjBuf);
            roleEmbeds.Add((float[])roleProjBuf.Clone());
        }

        // Codec prefix — track speaker position for embedding override
        var codecPrefix = new List<int>();
        int speakerPositionInPrefix = -1; // index of speaker token in codecPrefix
        if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            codecPrefix.Add(cfg.talker.codec_think_id);
            codecPrefix.Add(cfg.talker.codec_think_bos_id);
            var normalizedLanguage = language.ToLowerInvariant();
            if (!cfg.language_ids.TryGetValue(normalizedLanguage, out var languageId))
            {
                var supportedLanguages = string.Join(", ", cfg.language_ids.Keys.OrderBy(x => x));
                throw new ArgumentException(
                    $"Unsupported language '{language}'. Supported languages: {supportedLanguages}",
                    nameof(language));
            }
            codecPrefix.Add(languageId);
            codecPrefix.Add(cfg.talker.codec_think_eos_id);
        }
        else
        {
            codecPrefix.Add(cfg.talker.codec_nothink_id);
            codecPrefix.Add(cfg.talker.codec_think_bos_id);
            codecPrefix.Add(cfg.talker.codec_think_eos_id);
        }
        
        // Speaker token: add when speakerId >= 0, or when we have a speaker embedding override
        if (speakerId >= 0)
        {
            codecPrefix.Add(speakerId);
        }
        else if (speakerEmbeddingOverride != null)
        {
            // Use pad_id as placeholder — the embedding will be overridden below
            speakerPositionInPrefix = codecPrefix.Count;
            codecPrefix.Add(cfg.talker.codec_pad_id);
        }
        codecPrefix.Add(cfg.talker.codec_pad_id);
        codecPrefix.Add(cfg.talker.codec_bos_id);

        // TTS token embeds
        Span<float> ttsPadTextEmbed = stackalloc float[2048];
        Span<float> ttsBosTextEmbed = stackalloc float[2048];
        Span<float> ttsEosTextEmbed = stackalloc float[2048];
        var ttsPadProj = new float[_hiddenSize];
        var ttsBosProj = new float[_hiddenSize];
        var ttsEosProj = new float[_hiddenSize];
        
        _embeddings.TextEmbedding(cfg.tts.tts_pad_token_id, ttsPadTextEmbed);
        _embeddings.TextProjection(ttsPadTextEmbed, ttsPadProj);
        _embeddings.TextEmbedding(cfg.tts.tts_bos_token_id, ttsBosTextEmbed);
        _embeddings.TextProjection(ttsBosTextEmbed, ttsBosProj);
        _embeddings.TextEmbedding(cfg.tts.tts_eos_token_id, ttsEosTextEmbed);
        _embeddings.TextProjection(ttsEosTextEmbed, ttsEosProj);

        // _talker_input_embed = concat(tts_pad.expand(codec_prefix_len - 2), tts_bos) + codec_prefix[:-1]
        var talkerInputEmbeds = new List<float[]>();
        int codecPrefixLen = codecPrefix.Count;
        var codecEmbBuf = new float[_hiddenSize];
        
        for (int i = 0; i < codecPrefixLen - 2; i++)
        {
            var combined = new float[_hiddenSize];
            if (i == speakerPositionInPrefix && speakerEmbeddingOverride != null)
            {
                // Inject the speaker embedding directly instead of codec table lookup.
                // Embedding may be shorter than _hiddenSize (e.g., 1024-dim ECAPA-TDNN
                // with 2048-dim talker on 1.7B) — zero-pad the upper dimensions.
                int embLen = speakerEmbeddingOverride.Length;
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = ttsPadProj[j] + (j < embLen ? speakerEmbeddingOverride[j] : 0f);
            }
            else
            {
                _embeddings.TalkerCodecEmbedding(codecPrefix[i], codecEmbBuf);
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = ttsPadProj[j] + codecEmbBuf[j];
            }
            talkerInputEmbeds.Add(combined);
        }

        // Last position: tts_bos + codec_prefix[-2] (always included, both ICL and non-ICL)
        {
            _embeddings.TalkerCodecEmbedding(codecPrefix[codecPrefixLen - 2], codecEmbBuf);
            var combined = new float[_hiddenSize];
            for (int j = 0; j < _hiddenSize; j++)
                combined[j] = ttsBosProj[j] + codecEmbBuf[j];
            talkerInputEmbeds.Add(combined);
        }

        // ICL section: ref_text + target_text + eos (+ codec_pad), then codec_bos + ref_audio (+ tts_pad)
        // Matches official Qwen3-TTS generate_icl_prompt (non-streaming mode).
        bool hasIclData = refTokenIds != null && refAudioCodes != null;
        if (hasIclData)
        {
            var codecPadEmbed = new float[_hiddenSize];
            _embeddings.TalkerCodecEmbedding(cfg.talker.codec_pad_id, codecPadEmbed);

            // Ref text tokens: textProj(textEmbed(refToken)) + codec_pad
            var iclTextEmbBuf = new float[2048];
            var iclProjBuf = new float[_hiddenSize];
            for (int i = 0; i < refTokenIds!.Length; i++)
            {
                _embeddings.TextEmbedding(refTokenIds[i], iclTextEmbBuf);
                _embeddings.TextProjection(iclTextEmbBuf, iclProjBuf);
                var combined = new float[_hiddenSize];
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = iclProjBuf[j] + codecPadEmbed[j];
                talkerInputEmbeds.Add(combined);
            }

            // Target text tokens (tokenIds[3:-5]): textProj(textEmbed(token)) + codec_pad
            for (int i = 3; i < tokenIds.Length - 5; i++)
            {
                _embeddings.TextEmbedding(tokenIds[i], iclTextEmbBuf);
                _embeddings.TextProjection(iclTextEmbBuf, iclProjBuf);
                var combined = new float[_hiddenSize];
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = iclProjBuf[j] + codecPadEmbed[j];
                talkerInputEmbeds.Add(combined);
            }

            // tts_eos + codec_pad
            {
                var combined = new float[_hiddenSize];
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = ttsEosProj[j] + codecPadEmbed[j];
                talkerInputEmbeds.Add(combined);
            }

            // codec_bos + tts_pad
            {
                var codecBosEmbed = new float[_hiddenSize];
                _embeddings.TalkerCodecEmbedding(cfg.talker.codec_bos_id, codecBosEmbed);
                var combined = new float[_hiddenSize];
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = ttsPadProj[j] + codecBosEmbed[j];
                talkerInputEmbeds.Add(combined);
            }

            // Ref audio codes: tts_pad + sum_g(embedding_g(codes[t][g]))
            // Group 0: TalkerCodecEmbedding, Groups 1-15: CpCodecEmbedding
            var cpEmbBuf = new float[_cpHiddenSize];
            int tFrames = refAudioCodes!.GetLength(1);
            for (int t = 0; t < tFrames; t++)
            {
                var combined = new float[_hiddenSize];
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] = ttsPadProj[j];

                // Group 0: talker codec embedding
                _embeddings.TalkerCodecEmbedding((int)refAudioCodes[0, t, 0], codecEmbBuf);
                for (int j = 0; j < _hiddenSize; j++)
                    combined[j] += codecEmbBuf[j];

                // Groups 1-15: CP codec embeddings
                for (int g = 1; g < 16; g++)
                {
                    _embeddings.CpCodecEmbedding(g - 1, (int)refAudioCodes[0, t, g], cpEmbBuf);
                    for (int j = 0; j < _cpHiddenSize; j++)
                        combined[j] += cpEmbBuf[j];
                }
                talkerInputEmbeds.Add(combined);
            }
        }

        // Combine: role_embed + _talker_input_embed
        var allEmbeds = new List<float[]>();
        allEmbeds.AddRange(roleEmbeds);
        allEmbeds.AddRange(talkerInputEmbeds);

        // Non-ICL: append first_text_token + codec_bos
        if (!hasIclData)
        {
            Span<float> textEmbed = stackalloc float[2048];
            var projected = new float[_hiddenSize];
            _embeddings.TextEmbedding(tokenIds[3], textEmbed);
            _embeddings.TextProjection(textEmbed, projected);
            
            var codecBosEmbed = new float[_hiddenSize];
            _embeddings.TalkerCodecEmbedding(cfg.talker.codec_bos_id, codecBosEmbed);
            
            var combined = new float[_hiddenSize];
            for (int j = 0; j < _hiddenSize; j++)
                combined[j] = projected[j] + codecBosEmbed[j];
            allEmbeds.Add(combined);
        }

        // Trailing text hidden
        var trailingList = new List<float[]>();
        if (hasIclData)
        {
            // ICL non-streaming: all text is in prefill, trailing is just tts_pad
            trailingList.Add(ttsPadProj.ToArray());
        }
        else
        {
            // Standard: tokens[4:-5] + tts_eos
            var trailTextEmbBuf = new float[2048];
            var trailProjBuf = new float[_hiddenSize];
            for (int i = 4; i < tokenIds.Length - 5; i++)
            {
                _embeddings.TextEmbedding(tokenIds[i], trailTextEmbBuf);
                _embeddings.TextProjection(trailTextEmbBuf, trailProjBuf);
                trailingList.Add((float[])trailProjBuf.Clone());
            }
            trailingList.Add(ttsEosProj.ToArray());
        }

        // Convert to arrays
        var inputsEmbeds = new float[1, allEmbeds.Count, _hiddenSize];
        for (int i = 0; i < allEmbeds.Count; i++)
            for (int j = 0; j < _hiddenSize; j++)
                inputsEmbeds[0, i, j] = allEmbeds[i][j];

        var trailingTextHidden = new float[trailingList.Count, _hiddenSize];
        for (int i = 0; i < trailingList.Count; i++)
            for (int j = 0; j < _hiddenSize; j++)
                trailingTextHidden[i, j] = trailingList[i][j];

        return (inputsEmbeds, trailingTextHidden);
    }

    private (float[], float[]) StackPrefillKVFromOrtValues(IReadOnlyList<OrtValue> outputs, int seqLen)
    {
        // Output order: [0]=logits, [1]=hidden_states, then 56 KV tensors alternating key/value per layer
        // present_key_0 at [2], present_value_0 at [3], present_key_1 at [4], ...
        var keys = new float[_numLayers * 1 * _numKvHeads * seqLen * _headDim];
        var values = new float[_numLayers * 1 * _numKvHeads * seqLen * _headDim];
        int layerSize = 1 * _numKvHeads * seqLen * _headDim;

        for (int layer = 0; layer < _numLayers; layer++)
        {
            var keySpan = outputs[2 + layer * 2].GetTensorDataAsSpan<float>();
            var valSpan = outputs[2 + layer * 2 + 1].GetTensorDataAsSpan<float>();
            keySpan.CopyTo(keys.AsSpan(layer * layerSize));
            valSpan.CopyTo(values.AsSpan(layer * layerSize));
        }

        return (keys, values);
    }

    private int SampleToken(float[] logits, float temperature, int topK, float topP, float repPenalty,
                            List<int> previousTokens, ModelConfig cfg)
    {
        // Logits are (1, 1, 3072), flatten to (3072,)
        var vocabSize = cfg.talker.vocab_size;
        var probs = ArrayPool<float>.Shared.Rent(vocabSize);
        
        try
        {
            Array.Copy(logits, logits.Length - vocabSize, probs, 0, vocabSize);

            // Apply repetition penalty (positive logits → divide, negative → multiply)
            foreach (var token in previousTokens.Distinct())
            {
                if (probs[token] > 0)
                    probs[token] /= repPenalty;
                else
                    probs[token] *= repPenalty;
            }

            // Suppress upper codec range except codec_eos_token_id
            int cpVocabSize = cfg.code_predictor.vocab_size;
            for (int i = cpVocabSize; i < vocabSize; i++)
            {
                if (i != cfg.talker.codec_eos_token_id)
                    probs[i] = float.NegativeInfinity;
            }

            // Temperature
            if (temperature > 0)
            {
                for (int i = 0; i < vocabSize; i++)
                    probs[i] /= temperature;
            }

            // Top-k
            if (topK > 0 && topK < vocabSize)
            {
                var indexed = probs.AsSpan(0, vocabSize).ToArray().Select((p, i) => (p, i)).OrderByDescending(x => x.p).ToArray();
                for (int i = topK; i < vocabSize; i++)
                    probs[indexed[i].i] = float.NegativeInfinity;
            }

            // Softmax
            float maxLogit = float.NegativeInfinity;
            for (int i = 0; i < vocabSize; i++)
            {
                if (probs[i] > maxLogit) maxLogit = probs[i];
            }
            float sumExp = 0;
            for (int i = 0; i < vocabSize; i++)
            {
                probs[i] = MathF.Exp(probs[i] - maxLogit);
                sumExp += probs[i];
            }
            for (int i = 0; i < vocabSize; i++)
                probs[i] /= sumExp;

            // Multinomial sample
            float rand = Random.Shared.NextSingle();
            float cumSum = 0;
            for (int i = 0; i < vocabSize; i++)
            {
                cumSum += probs[i];
                if (rand < cumSum)
                    return i;
            }

            return vocabSize - 1;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probs);
        }
    }

    private int SampleTokenSimple(ReadOnlySpan<float> logits, float temperature, int topK = 50)
    {
        var vocabSize = _embeddings.Config.code_predictor.vocab_size;
        var probs = ArrayPool<float>.Shared.Rent(vocabSize);
        
        try
        {
            logits[^vocabSize..].CopyTo(probs);

            // Top-k filtering (subtalker_top_k=50 from Python generation_config.json)
            if (topK > 0 && topK < vocabSize)
            {
                var sorted = ArrayPool<float>.Shared.Rent(vocabSize);
                try
                {
                    Array.Copy(probs, 0, sorted, 0, vocabSize);
                    Array.Sort(sorted, 0, vocabSize);
                    float threshold = sorted[vocabSize - topK];
                    for (int i = 0; i < vocabSize; i++)
                    {
                        if (probs[i] < threshold)
                            probs[i] = float.NegativeInfinity;
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(sorted);
                }
            }

            if (temperature > 0)
            {
                for (int i = 0; i < vocabSize; i++)
                    probs[i] /= temperature;
            }

            // Softmax
            float maxLogit = float.NegativeInfinity;
            for (int i = 0; i < vocabSize; i++)
            {
                if (probs[i] > maxLogit) maxLogit = probs[i];
            }
            float sumExp = 0;
            for (int i = 0; i < vocabSize; i++)
            {
                probs[i] = MathF.Exp(probs[i] - maxLogit);
                sumExp += probs[i];
            }
            for (int i = 0; i < vocabSize; i++)
                probs[i] /= sumExp;

            // Sample
            float rand = Random.Shared.NextSingle();
            float cumSum = 0;
            for (int i = 0; i < vocabSize; i++)
            {
                cumSum += probs[i];
                if (rand < cumSum)
                    return i;
            }

            return vocabSize - 1;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(probs);
        }
    }

    /// <summary>
    /// Builds CP prefill buffer by copying hidden state and group0 embedding directly (no projection).
    /// Buffer layout: [hidden[0..cpInputDim], group0[0..cpInputDim]]
    /// </summary>
    internal static void BuildCpPrefillDirect(
        float[] buffer, ReadOnlySpan<float> hiddenStates, int hOffset,
        ReadOnlySpan<float> group0Embed, int cpInputDim)
    {
        Debug.Assert(buffer.Length >= 2 * cpInputDim);
        Debug.Assert(hiddenStates.Length >= hOffset + cpInputDim);
        Debug.Assert(group0Embed.Length >= cpInputDim);
        
        for (int i = 0; i < cpInputDim; i++)
        {
            buffer[i] = hiddenStates[hOffset + i];
            buffer[cpInputDim + i] = group0Embed[i];
        }
    }

    /// <summary>
    /// Accumulates a CP codec embedding into the next talker input buffer.
    /// Only the first cpHiddenSize elements are accumulated (CP space may be smaller than talker space).
    /// </summary>
    internal static void AccumulateCpEmbedding(
        float[] nextInputBuf, ReadOnlySpan<float> cpEmbed, int cpHiddenSize)
    {
        Debug.Assert(nextInputBuf.Length >= cpHiddenSize);
        Debug.Assert(cpEmbed.Length >= cpHiddenSize);
        
        for (int i = 0; i < cpHiddenSize; i++)
            nextInputBuf[i] += cpEmbed[i];
    }

    public void Dispose()
    {
        DisposeSession(_prefillSession);
        DisposeSession(_decodeSession);
        DisposeSession(_cpSession);
    }

    private static void DisposeSession(Lazy<InferenceSession> session)
    {
        if (session.IsValueCreated)
            session.Value.Dispose();
    }
}
