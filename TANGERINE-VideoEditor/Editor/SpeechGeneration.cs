using System.IO;
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
            int pending = data.Sentences.Count(s => forceSentenceId == s.Id || s.Fingerprint != Fingerprint(s, data) ||
                s.RawAssetId is not { } id || !existing.TryGetValue(id, out MediaAsset? raw) || !File.Exists(raw.EffectiveExportPath));
            int requestedWorkers = data.Device == "cpu" ? SpeechPerformance.SentenceWorkers(data.ModelSize) : 1;
            int concurrentSentences = Math.Max(1, Math.Min(pending, requestedWorkers));
            int cpuThreads = SpeechPerformance.ThreadsPerWorker(concurrentSentences);
            var paths = new string[total];
            var rawAssets = new MediaAsset?[total];
            var backends = new string?[total];
            await Parallel.ForEachAsync(Enumerable.Range(0, total), new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrentSentences,
                CancellationToken = token
            }, async (i, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested(); SpeechSentence s = data.Sentences[i];
                string fingerprint = Fingerprint(s, data);
                progress?.Report(new($"准备第 {i + 1}/{total} 句", .8 * Volatile.Read(ref completed) / total, data.Backend));
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
                        var sentenceProgress = new InlineProgress(p => progress?.Report(new(
                            $"第 {i + 1}/{total} 句 · {p.Message}", .8 * Volatile.Read(ref completed) / total, p.Backend)));
                        var message = await worker.SendAsync(new SpeechRequest { ModelPath = model, Device = data.Device, CpuThreads = cpuThreads,
                            Text = part.Text, Speaker = voice.Id, ReferencePath = voice.ReferencePath,
                            ReferenceText = voice.ReferenceText, Language = part.Language == "auto" ? s.Language ?? data.Defaults.Language : part.Language,
                            OutputPath = raw }, sentenceProgress, cancellation).ConfigureAwait(false);
                        backends[i] = message.Backend ?? backends[i];
                        double initialRate = part.HasInitialRate ? part.InitialRate / (s.Rate ?? data.Defaults.Rate) : 1;
                        await SpeechAudio.ConvertAsync(raw, adjusted, initialRate, 0, 0, null, 48000, cancellation,
                            fraction => progress?.Report(new($"第 {i + 1}/{total} 句 · 初始语速处理 {fraction:P0}",
                                .8 * Volatile.Read(ref completed) / total, backends[i]))).ConfigureAwait(false);
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
                progress?.Report(new($"已完成 {Interlocked.Increment(ref completed)}/{total} 句原始语音",
                    .8 * Volatile.Read(ref completed) / total, backends[i]));
            }).ConfigureAwait(false);
            for (int i = 0; i < total; i++)
            {
                rawPaths.Add(data.Sentences[i].Id, paths[i]);
                if (rawAssets[i] is { } rawAsset) assets.Add(rawAsset);
            }
            string[] usedBackends = backends.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct().ToArray();
            if (usedBackends.Length > 0) data.Backend = string.Join(" / ", usedBackends);
            SpeechText.Arrange(data);
            progress?.Report(new("语音推理已结束，正在等待模型工作进程退出", .8, data.Backend));
            await worker.RetireIdleInferenceWorkersAsync().ConfigureAwait(false);
            progress?.Report(new("模型内存已释放，正在并行处理音频", .8, data.Backend));
            var processedAssets = new MediaAsset?[total];
            int processed = 0;
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
                    await SpeechAudio.ConvertAsync(rawPaths[sentence.Id], audio, sentence.EffectiveRate, pitch, 0, null,
                        48000, cancellation, fraction => progress?.Report(new(
                            $"正在处理音频 {Volatile.Read(ref processed) + 1}/{total} · {fraction:P0}",
                            .8 + .2 * Volatile.Read(ref processed) / total, data.Backend))).ConfigureAwait(false);
                    if (sentence.Anchor == SpeechAnchor.Window) SpeechAudio.FitWindow(audio, sentence.End - sentence.Start);
                    processedAssets[i] = Asset(audio, sentence.Text, SpeechAudio.Duration(audio), "sentence");
                    sentence.AudioAssetId = processedAssets[i]!.Id;
                }
                sentence.Duration = SpeechAudio.Duration(audio);
                sentence.AudioFingerprint = acoustic;
                int done = Interlocked.Increment(ref processed);
                progress?.Report(new($"正在处理音频 {done}/{total}", .8 + .2 * done / total, data.Backend));
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
            progress?.Report(new("语音生成完成", 1, data.Backend)); return result;
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
