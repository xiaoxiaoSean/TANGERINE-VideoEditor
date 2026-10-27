using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tangerine.VideoEditor.Speech;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public sealed class GeneratedSpeech(string directory, SpeechTrackData data, List<MediaAsset> assets) : IDisposable
{
    public SpeechTrackData Data { get; } = data;
    public List<MediaAsset> Assets { get; } = assets;
    public bool Committed { get; set; }
    public bool PreserveFiles { get; set; }
    public void Dispose() { if (!Committed && !PreserveFiles) SpeechAssets.DeleteDirectory(directory); }
}

/// <summary>Builds a detached, atomic generation result without reading live UI state.</summary>
public static class SpeechGeneration
{
    public static string Fingerprint(SpeechSentence s, SpeechTrackData data)
    {
        var parts = s.Parts.Count > 0 ? s.Parts : [new SpeechPart { Text = s.Text }];
        string source = JsonSerializer.Serialize(new { Engine = "qwen3-slaney-unique-penalty-v2", s.Text, Voice = s.VoiceId ?? data.Defaults.VoiceId,
            Language = s.Language ?? data.Defaults.Language, data.ModelSize,
            Parts = parts.Select(p => new { p.Text, p.Language }) }, SpeechJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
    public static async Task<GeneratedSpeech> GenerateAsync(SpeechWorkerClient worker, SpeechTrackData input,
        IReadOnlyDictionary<string, MediaAsset> existing, string root, IProgress<SpeechProgress>? progress,
        CancellationToken token, string? forceSentenceId = null)
    {
        SpeechTrackData data = SpeechJson.Copy(input); SpeechText.Validate(data);
        string work = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        var assets = new List<MediaAsset>(); var rawPaths = new Dictionary<string, string>();
        var result = new GeneratedSpeech(work, data, assets);
        try
        {
            var voices = SpeechAssets.Voices().ToDictionary(v => v.Id);
            int total = data.Sentences.Count, completed = 0;
            object progressLock = new();
            var active = new Dictionary<int, (string Message, string? Backend)>();
            void ReportInference(int index, string message, string? backend, bool finished = false)
            {
                lock (progressLock)
                {
                    if (finished) { active.Remove(index); completed++; }
                    else active[index] = (message, backend);
                    string detail = string.Join("\n", active.OrderBy(item => item.Key)
                        .Select(item => $"第 {item.Key + 1}/{total} 句 · {item.Value.Message}"));
                    string[] devices = active.Values.Select(item => item.Backend)
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Select(value => value!).ToArray();
                    string summary = $"原始语音已完成 {completed}/{total} 句";
                    progress?.Report(new(detail.Length > 0 ? summary + "\n" + detail : summary, -1,
                        devices.Length > 0 ? string.Join(" / ", devices) : null)
                        { Phase = "inference", CompletedUnits = completed, TotalUnits = total });
                }
            }
            bool[] needsInference = data.Sentences.Select(s => forceSentenceId == s.Id || s.Fingerprint != Fingerprint(s, data) ||
                s.RawAssetId is not { } id || !existing.TryGetValue(id, out MediaAsset? raw) || !File.Exists(raw.EffectiveExportPath)).ToArray();
            int pending = needsInference.Count(required => required);
            string[] models = data.Sentences.Where((_, index) => needsInference[index]).Select(s =>
            {
                if (!voices.TryGetValue(s.VoiceId ?? data.Defaults.VoiceId, out SpeechVoice? voice))
                    throw new SpeechFailure("VOICE0006", "音色不存在，请重新选择音色。");
                return SpeechAssets.ModelPath(voice.Builtin ? "custom-" + data.ModelSize : "base-0.6b");
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var residency = data.Device == "auto" && models.Length == 1
                ? await worker.AutomaticLaneResidencyAsync(models[0], SpeechPerformance.ThreadsPerWorker(2), token)
                    .ConfigureAwait(false)
                : (GpuAvailable: true, GpuWarm: false, CpuWarm: 0);
            int requestedWorkers = InferenceWorkers(data.ModelSize, data.Device, residency);
            int concurrentSentences = Math.Max(1, Math.Min(pending, requestedWorkers));
            // Keep auto workers on the same ORT thread configuration when a
            // later one-sentence edit reuses their already-loaded model.
            int cpuThreads = SpeechPerformance.ThreadsPerWorker(data.Device == "auto" ? 2 : concurrentSentences);
            var paths = new string[total];
            var rawAssets = new MediaAsset?[total];
            var backends = new string?[total];
            using var laneCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var queue = new ConcurrentQueue<int>(Enumerable.Range(0, total));
            async Task RunLaneAsync(string device)
            {
                try
                {
                    while (queue.TryDequeue(out int i))
                        await GenerateSentenceAsync(i, device, laneCancellation.Token).ConfigureAwait(false);
                }
                catch { laneCancellation.Cancel(); throw; }
            }
            async Task GenerateSentenceAsync(int i, string device, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); SpeechSentence s = data.Sentences[i];
                string fingerprint = Fingerprint(s, data);
                ReportInference(i, "准备中", device == "cpu" ? "CPU" : "DirectML 自动选择中");
                if (forceSentenceId != s.Id && s.Fingerprint == fingerprint && s.RawAssetId is { } rawId &&
                    existing.TryGetValue(rawId, out MediaAsset? reusable) && File.Exists(reusable.EffectiveExportPath))
                    paths[i] = reusable.EffectiveExportPath;
                else
                {
                    string voiceId = s.VoiceId ?? data.Defaults.VoiceId;
                    if (!voices.TryGetValue(voiceId, out SpeechVoice? voice)) throw new SpeechFailure("VOICE0006", "音色不存在，请重新选择音色。");
                    string modelId = voice.Builtin ? "custom-" + data.ModelSize : "base-0.6b";
                    string model = SpeechAssets.ModelPath(modelId);
                    SpeechAssets.ValidateModel(model, voice.Builtin ? "custom" : "base", modelId);
                    List<SpeechPart> parts = s.Parts.Count > 0 ? s.Parts : [new SpeechPart { Text = s.Text }];
                    var partPaths = new List<string>(); int partIndex = 0;
                    foreach (SpeechPart part in parts)
                    {
                        if (string.IsNullOrWhiteSpace(part.Text)) continue;
                        string raw = Path.Combine(work, $"{s.Id}-part-{partIndex}.wav");
                        string adjusted = Path.Combine(work, $"{s.Id}-initial-{partIndex++}.wav");
                        var sentenceProgress = new InlineProgress(p => ReportInference(i, p.Message, p.Backend));
                        var message = await worker.SendAsync(new SpeechRequest { ModelPath = model, Device = device, CpuThreads = cpuThreads,
                            Text = part.Text, Speaker = voice.Id, ReferencePath = voice.ReferencePath,
                            ReferenceText = voice.ReferenceText, Language = part.Language == "auto" ? s.Language ?? data.Defaults.Language : part.Language,
                            OutputPath = raw }, sentenceProgress, cancellation).ConfigureAwait(false);
                        backends[i] = message.Backend ?? backends[i];
                        double initialRate = part.HasInitialRate ? part.InitialRate / (s.Rate ?? data.Defaults.Rate) : 1;
                        await SpeechAudio.ConvertAsync(raw, adjusted, initialRate, 0, 0, null, 48000, cancellation,
                            outputSeconds => ReportInference(i, $"初始语速处理 · 已输出音频 {outputSeconds:0.0} 秒", backends[i]))
                            .ConfigureAwait(false);
                        partPaths.Add(adjusted);
                    }
                    string combined = Path.Combine(work, s.Id + "-raw.wav");
                    SpeechAudio.Join(partPaths, combined);
                    s.RawDuration = SpeechAudio.Duration(combined);
                    MediaAsset rawAsset = Asset(combined, s.Text, s.RawDuration, "raw"); rawAssets[i] = rawAsset;
                    s.RawAssetId = rawAsset.Id; paths[i] = combined;
                    // Discard the command-bearing plan. The rendered raw baseline
                    // retains its initial rates; subsequent text edits start clean.
                    foreach (SpeechPart part in parts) { part.InitialRate = 1; part.HasInitialRate = false; }
                    s.Parts = MergeParts(parts); s.Fingerprint = Fingerprint(s, data);
                }
                ReportInference(i, "", backends[i], finished: true);
            }
            // Auto can run one DirectML session alongside CPU sessions. An
            // explicit adapter never silently sends sentences to the CPU.
            Task[] lanes = Enumerable.Range(0, concurrentSentences)
                .Select(index => RunLaneAsync(data.Device == "auto" && index > 0 ? "cpu" : data.Device)).ToArray();
            await Task.WhenAll(lanes).ConfigureAwait(false);
            for (int i = 0; i < total; i++)
            {
                rawPaths.Add(data.Sentences[i].Id, paths[i]);
                if (rawAssets[i] is { } rawAsset) assets.Add(rawAsset);
            }
            string[] usedBackends = backends.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct().ToArray();
            if (usedBackends.Length > 0) data.Backend = string.Join(" / ", usedBackends);
            SpeechText.Arrange(data);
            // The worker pool already retires sessions after two idle minutes.
            // Keep a successful session warm for sentence edits and regeneration.
            progress?.Report(new("原始语音已完成，正在并行处理音频", -1, data.Backend)
                { Phase = "audio", CompletedUnits = 0, TotalUnits = total });
            var processedAssets = new MediaAsset?[total];
            int processed = 0;
            object audioProgressLock = new();
            void ReportAudio(int index, double? outputSeconds = null, bool finished = false)
            {
                lock (audioProgressLock)
                {
                    if (finished) processed++;
                    string detail = outputSeconds is double seconds
                        ? $" · 第 {index + 1} 句已输出音频 {seconds:0.0} 秒" : "";
                    progress?.Report(new($"音频已处理 {processed}/{total} 句{detail}", -1, data.Backend)
                        { Phase = "audio", CompletedUnits = processed, TotalUnits = total });
                }
            }
            await Parallel.ForEachAsync(Enumerable.Range(0, total), new ParallelOptions
            {
                MaxDegreeOfParallelism = SpeechPerformance.MaxWorkers,
                CancellationToken = token
            }, async (i, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                SpeechSentence sentence = data.Sentences[i];
                double pitch = sentence.Pitch ?? data.Defaults.Pitch;
                string acoustic = JsonSerializer.Serialize(new { sentence.RawAssetId, Rate = sentence.EffectiveRate, Pitch = pitch,
                    Window = sentence.Anchor == SpeechAnchor.Window ? sentence.End - sentence.Start : (double?)null }, SpeechJson.Options);
                bool reusable = sentence.AudioFingerprint == acoustic && sentence.AudioAssetId is { } finalId &&
                    existing.TryGetValue(finalId, out MediaAsset? finalAsset) && File.Exists(finalAsset.EffectiveExportPath);
                string audio;
                if (reusable) audio = existing[sentence.AudioAssetId!].EffectiveExportPath;
                else
                {
                    audio = Path.Combine(work, sentence.Id + "-audio.wav");
                    ReportAudio(i);
                    await SpeechAudio.ConvertAsync(rawPaths[sentence.Id], audio, sentence.EffectiveRate, pitch, 0, null,
                        48000, cancellation, outputSeconds => ReportAudio(i, outputSeconds))
                        .ConfigureAwait(false);
                    if (sentence.Anchor == SpeechAnchor.Window) SpeechAudio.FitWindow(audio, sentence.End - sentence.Start);
                    processedAssets[i] = Asset(audio, sentence.Text, SpeechAudio.Duration(audio), "sentence");
                    sentence.AudioAssetId = processedAssets[i]!.Id;
                }
                sentence.Duration = SpeechAudio.Duration(audio);
                sentence.AudioFingerprint = acoustic;
                ReportAudio(i, finished: true);
            }).ConfigureAwait(false);
            assets.AddRange(processedAssets.OfType<MediaAsset>());
            double previousEnd = data.Origin;
            for (int i = 0; i < data.Sentences.Count; i++)
            {
                token.ThrowIfCancellationRequested(); SpeechSentence s = data.Sentences[i];
                if (s.Anchor == SpeechAnchor.Auto) s.Start = i == 0 ? data.Origin + (s.Gap ?? 0)
                    : previousEnd + (s.Gap ?? data.Defaults.Gap);
                else if (s.Anchor == SpeechAnchor.End) s.Start = s.End - s.Duration;
                if (s.Start < 0 || i > 0 && s.Start < previousEnd - .001 || s.Start + s.Duration > 86400)
                    throw new SpeechFailure("VOICE0004", "实际语音时长与固定句位置冲突，请调整语速或起止点；旧音频已保留。");
                s.End = s.Start + s.Duration; previousEnd = s.End;
            }
            // Keep only media referenced by the transaction, not intermediate
            // chunks. Old generations remain under the document's cache lease.
            var retained = assets.Select(a => a.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(work)) if (!retained.Contains(file)) File.Delete(file);
            progress?.Report(new("音频已处理完成，正在校验排布", -1, data.Backend)
                { Phase = "arrangement", CompletedUnits = total, TotalUnits = total });
            return result;
        }
        catch
        {
            try { await worker.RetireIdleInferenceWorkersAsync().ConfigureAwait(false); }
            catch (SpeechFailure) { result.PreserveFiles = true; }
            result.Dispose();
            throw;
        }
    }
    private sealed class InlineProgress(Action<SpeechProgress> report) : IProgress<SpeechProgress>
    {
        public void Report(SpeechProgress value) => report(value);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static int InferenceWorkers(string modelSize, string device,
        (bool GpuAvailable, bool GpuWarm, int CpuWarm) residency)
    {
        if (device != "auto" && device != "cpu") return 1;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return 1;
        const ulong gib = 1024UL * 1024 * 1024;
        // The local 0.6B session reached 8.5 GiB private bytes. Allow modest
        // paging for two independent lanes while keeping runtime KV headroom.
        ulong replica = modelSize == "1.7b" ? 20 * gib : 15 * gib / 2;
        ulong available = status.AvailablePhysical > gib / 2 ? status.AvailablePhysical - gib / 2 : 0;
        int limit = device == "auto" ? Math.Min(2, SpeechPerformance.SentenceWorkers(modelSize))
            : SpeechPerformance.SentenceWorkers(modelSize);
        if (device != "auto") return (int)Math.Clamp(available / replica, 1UL, (ulong)limit);
        ulong first = residency.GpuAvailable
            ? (residency.GpuWarm ? gib : replica)
            : (residency.CpuWarm > 0 ? gib : replica);
        ulong second = residency.CpuWarm > (residency.GpuAvailable ? 0 : 1) ? gib : replica;
        return limit > 1 && available >= first + second ? 2 : 1;
    }
    public static List<SpeechPart> MergeParts(IEnumerable<SpeechPart> parts)
    {
        var result = new List<SpeechPart>();
        foreach (SpeechPart part in parts)
        {
            if (result.LastOrDefault() is { } prior && prior.Language == part.Language && prior.InitialRate == part.InitialRate && prior.HasInitialRate == part.HasInitialRate) prior.Text += part.Text;
            else result.Add(SpeechJson.Copy(part));
        }
        return result;
    }
    private static MediaAsset Asset(string path, string text, double duration, string role) => new()
    {
        Name = text[..Math.Min(48, text.Length)] + (role == "raw" ? " · 原始语音" : " · 语音"),
        Kind = MediaKind.Audio, SourcePath = path, DurationSeconds = duration, HasAudio = true,
        AudioChannels = 1, AudioSampleRate = 48000, AudioCodec = "pcm_s16le", FileSizeBytes = new FileInfo(path).Length,
        Metadata = new Dictionary<string, string> { ["SpeechRole"] = role }
    };
    public static void Apply(TimelineTrack track, SpeechTrackData data)
    {
        var old = track.Clips.ToDictionary(c => c.Id);
        track.Kind = TrackKind.Audio; track.IsSnapping = false; track.Speech = data;
        track.Clips = data.Sentences.Select(s =>
        {
            TimelineClip clip = old.TryGetValue(s.Id, out var present) ? present : new() { Id = s.Id };
            clip.Kind = ClipKind.Audio; clip.Name = s.Text; clip.MediaAssetId = s.AudioAssetId; clip.SpeechSentenceId = s.Id;
            clip.StartSeconds = s.Start; clip.DurationSeconds = s.Duration; clip.SourceInSeconds = 0; clip.Speed = 1;
            clip.TimeRemapPoints.Clear(); clip.IsReversed = false;
            return clip;
        }).ToList();
    }
}
