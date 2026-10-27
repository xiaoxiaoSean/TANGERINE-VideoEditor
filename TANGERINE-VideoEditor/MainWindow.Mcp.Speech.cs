using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Tangerine.VideoEditor.Automation;
using Tangerine.VideoEditor.Speech;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;
public partial class MainWindow
{
    private string? _speechMcpRecordingOwner;
    private string? _speechRecordingGender;
    private string? _speechMcpRecordingPrompt;
    private static string McpSpeechGender(JsonObject args) => McpString(args, "gender") is "male" or "female" ? McpString(args, "gender")
        : throw new SpeechFailure("VOICE0006", "创建克隆音色前必须选择 gender: male 或 female。");
    private static readonly HashSet<string> SpeechSentenceEditable = new(StringComparer.Ordinal)
    { "text", "voiceId", "language", "rate", "pitch", "gap", "parts", "anchor", "start", "end" };

    private object McpSpeechJob(string title, Func<IProgress<SpeechProgress>, CancellationToken, Task<object?>> work)
    {
        return McpStartJob(title, async (job, progress, token) =>
        {
            // Construct and update WPF only on its dispatcher. The heavy work
            // delegates either use Task.Run or the detached stdio worker.
            return await (await McpOnUiAsync(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                var message = new TextBlock { Text = "正在准备…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                var meter = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 10) };
                var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 6, 16, 6) };
                cancel.Click += (_, _) => linked.Cancel();
                var panel = new StackPanel { Margin = new Thickness(18) }; panel.Children.Add(message); panel.Children.Add(meter); panel.Children.Add(cancel);
                var window = new Window { Owner = this, Title = title + " · MCP", Content = panel,
                    Width = Math.Max(1, ActualWidth * .4), SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#191B20"), Foreground = System.Windows.Media.Brushes.White };
                window.Closing += (_, _) => linked.Cancel();
                bool finished = false;
                var report = new Progress<SpeechProgress>(value =>
                {
                    if (finished || linked.IsCancellationRequested) return;
                    job.Phase = value.Phase;
                    job.Detail = value.Message;
                    job.CompletedUnits = value.CompletedUnits;
                    job.TotalUnits = value.TotalUnits;
                    message.Text = value.Message + (value.Backend is null ? "" : "\n" + value.Backend);
                    meter.IsIndeterminate = value.Fraction < 0;
                    if (value.Fraction >= 0) { meter.Value = value.Fraction; progress.Report(value.Fraction); }
                });
                window.Show();
                try
                {
                    object? result = await work(report, linked.Token);
                    job.Phase = "completed";
                    job.Detail = title + "已完成";
                    if (job.TotalUnits is int total) job.CompletedUnits = total;
                    progress.Report(1);
                    return result;
                }
                finally { finished = true; if (window.IsVisible) window.Close(); }
            }));
        });
    }
    private TimelineTrack SpeechTrack(JsonObject args)
    {
        TimelineTrack track = McpTrack(McpString(args, "trackId"));
        if (track.Speech is null || track.IsLocked) throw new SpeechFailure("VOICE0004", "语音轨道不存在或已锁定。");
        return track;
    }
    private object McpSpeechGenerate(SpeechTrackData data, TimelineTrack? track, string? force = null)
    {
        ProjectDocument owner = _project;
        long generation = _projectGeneration;
        string sequence = _project.Sequence.Id;
        string? controlOwner = _mcpControlOwnerSessionId;
        string? signature = track is null ? null : JsonSerializer.Serialize(track, SpeechJson.Options);
        SpeechTrackData requested = SpeechJson.Copy(data);
        return McpSpeechJob("语音生成", async (progress, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(owner, _project) || generation != _projectGeneration || sequence != _project.Sequence.Id ||
                controlOwner != _mcpControlOwnerSessionId || track is not null &&
                (!_project.Tracks.Contains(track) || track.IsLocked || signature != JsonSerializer.Serialize(track, SpeechJson.Options)))
                throw new SpeechFailure("VOICE0009", "排队期间工程、句子或操作权已改变，未开始生成语音。");
            TimelineTrack result = await GenerateSpeechAsync(requested, track, progress, token, force);
            return new { trackId = result.Id, captionTrackId = _project.Tracks.FirstOrDefault(item => item.Subtitle?.SpeechSourceTrackId == result.Id)?.Id,
                sentences = result.Speech!.Sentences, result.Speech.Backend };
        });
    }
    private bool IsSpeechMedia(string id) => _project.Tracks.Concat(_project.Sequences.SelectMany(s => s.Tracks))
        .Any(t => t.Speech?.Sentences.Any(s => s.RawAssetId == id || s.AudioAssetId == id) == true);
    private static void RequireSpeechMediaUnchanged(MediaAsset original, MediaAsset? candidate)
    {
        // Presentation metadata may change. Source identity and playback data
        // belong to the speech transaction so measured durations stay valid.
        var before = JsonModelTools.ToNode(original)!.AsObject();
        var after = candidate is null ? null : JsonModelTools.ToNode(candidate)!.AsObject();
        foreach (string key in new[] { "name", "binId", "tags", "labelColor", "description", "markers" })
        { before.Remove(key); after?.Remove(key); }
        if (!JsonNode.DeepEquals(before, after))
            throw new SpeechFailure("VOICE0004", "语音轨道引用的音频来源、时长及播放参数不能通过通用素材命令修改，请使用 speech_* 命令重新生成。");
    }
    private SpeechSentence McpSpeechSentencePatch(SpeechSentence sentence, JsonObject patch)
    {
        foreach (string key in patch.Select(p => p.Key))
            if (!SpeechSentenceEditable.Contains(key)) throw new SpeechFailure("VOICE0001", "不可直接修改生成状态字段：" + key);
        if (patch["parts"] is JsonArray parts && parts.OfType<JsonObject>().Any(p => p.ContainsKey("initialRate")))
            throw new SpeechFailure("VOICE0001", "initialRate 仅用于首次生成；后续更新请使用 rate，语言片段不要设置 initialRate。");
        var options = SpeechJson.Options;
        JsonObject node = JsonSerializer.SerializeToNode(sentence, options)!.AsObject();
        foreach ((string key, JsonNode? value) in patch) node[key] = value?.DeepClone();
        var updated = node.Deserialize<SpeechSentence>(options)!;
        if (updated.Text != sentence.Text && !patch.ContainsKey("parts")) updated.Parts = [new() { Text = updated.Text }];
        return updated;
    }
    private async Task<object?> ExecuteMcpSpeechAsync(string command, JsonObject args, CancellationToken token)
    {
        switch (command)
        {
            case "speech_devices":
                return (await _speechWorker.SendAsync(new SpeechRequest { Command = "devices" }, null, token)).Devices;
            case "speech_models":
                return await Task.Run(() => new { root = SpeechAssets.ModelsRoot,
                    models = SpeechCatalog.Models.Select(m => new { m.Id, m.Name, m.Kind, path = SpeechAssets.ModelPath(m.Id), sizeBytes = SpeechAssets.ModelBytes(m.Id), installed = SpeechAssets.Installed(m.Id) }).ToArray() }, token);
            case "speech_model_download":
            {
                string id = McpString(args, "modelId");
                return McpSpeechJob("下载语音模型", async (p, t) =>
                { await Task.Run(() => SpeechAssets.DownloadAsync(id, p, t), t); return new { modelId = id, path = SpeechAssets.ModelPath(id) }; });
            }
            case "speech_model_import":
            {
                string id = McpString(args, "modelId"), path = McpString(args, "path");
                return McpSpeechJob("导入语音模型", async (p, t) =>
                { await Task.Run(() => SpeechAssets.ImportModelAsync(id, path, p, t), t); return new { modelId = id, path = SpeechAssets.ModelPath(id) }; });
            }
            case "speech_voices": return new { voices = SpeechAssets.Voices(), languages = SpeechCatalog.Languages };
            case "speech_clone_prompt":
            {
                string language = McpString(args, "language", "zh");
                if (language is not ("zh" or "en")) throw new SpeechFailure("VOICE0006", "克隆录音支持中文或英语。");
                return new { language, text = SpeechCatalog.ClonePrompt(language), presetId = SpeechCatalog.ClonePromptId(language), editable = false,
                    instructions = SpeechCatalog.CloneRecordingInstructions, coverage = SpeechCatalog.ClonePromptCoverage(language), readAloudField = "text",
                    minimumSeconds = 3, maximumSeconds = 120, extension = ".tvevcv", importedAudioRequiresPreset = true,
                    confirmationField = "confirmedPreset" };
            }
            case "speech_voice_create":
            {
                string path = McpString(args, "path"), name = McpString(args, "name"), language = McpString(args, "language", "zh");
                string presetId = McpString(args, "presetId");
                if (language is not ("zh" or "en") || presetId != SpeechCatalog.ClonePromptId(language) || args["confirmedPreset"]?.GetValue<bool>() != true)
                    throw new SpeechFailure("VOICE0006", "请先读取 speech_clone_prompt，确认音频完整朗读该固定文案，再提供对应 presetId 和 confirmedPreset:true。");
                string text = SpeechCatalog.ClonePrompt(language);
                if (args["referenceText"]?.GetValue<string>() is string suppliedText && suppliedText != text)
                    throw new SpeechFailure("VOICE0006", "referenceText 必须与固定朗读文案完全一致，不允许自定义转写。");
                double start = McpDouble(args, "startSeconds"); double? end = args["endSeconds"]?.GetValue<double>();
                string? export = args["outputPath"]?.GetValue<string>();
                string gender = McpSpeechGender(args);
                return McpSpeechJob("创建克隆音色", async (_, t) =>
                {
                    SpeechVoice voice = await Task.Run(() => SpeechAssets.CreateVoiceAsync(path, name, language, text, start, end, t, gender, export, presetId), t);
                    return voice;
                });
            }
            case "speech_voice_import":
            {
                string path = McpString(args, "path");
                return McpSpeechJob("导入克隆音色", async (_, t) => await Task.Run(() => SpeechAssets.ImportVoiceAsync(path, t), t));
            }
            case "speech_voice_delete":
            {
                string id = McpString(args, "voiceId"); StopCatalogPreview();
                return McpSpeechJob("删除克隆音色", async (_, t) =>
                { await Task.Run(() => SpeechAssets.DeleteVoiceAsync(id, t), t); return new { voiceId = id, deleted = true }; });
            }
            case "speech_voice_preview":
            {
                var data = new SpeechTrackData { Device = McpString(args, "device", "auto"),
                    Defaults = new() { VoiceId = McpString(args, "voiceId") }, Sentences = [new() { Text = McpString(args, "text") }] };
                SpeechText.Validate(data);
                ProjectDocument owner = _project; long generation = _projectGeneration;
                return McpSpeechJob("合成试听音色", async (p, t) =>
                {
                    t.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(owner, _project) || generation != _projectGeneration)
                        throw new SpeechFailure("VOICE0009", "排队期间工程已改变，未开始旧的试听。");
                    await GenerateSpeechAuditionAsync(data, p, t); return new { previewing = true };
                });
            }
            case "speech_voice_export":
            {
                string id = McpString(args, "voiceId"), path = McpString(args, "path");
                return McpSpeechJob("导出克隆音色", async (_, t) => { await Task.Run(() => SpeechAssets.ExportVoiceAsync(id, path, t), t); return new { path }; });
            }
            case "speech_clone_record_start":
            {
                if (_speechRecorder is not null || _mcpRecorder is not null || _speechUiBusy) throw new SpeechFailure("VOICE0006", "已有录音或语音任务。");
                string language = McpString(args, "language", "zh");
                if (language is not ("zh" or "en")) throw new SpeechFailure("VOICE0006", "请选择中文或英语。");
                string gender = McpSpeechGender(args);
                string prompt = SpeechCatalog.ClonePrompt(language);
                var recorder = new WaveInRecorder();
                try { recorder.Start(checked((uint)McpInt(args, "deviceId"))); _speechRecorder = recorder; }
                catch { recorder.Dispose(); throw; }
                _speechRecordingLanguage = language; _speechRecordingGender = gender; _speechMcpRecordingPrompt = prompt; _speechMcpRecordingOwner = _mcpControlOwnerSessionId;
                return new { recording = true, language, text = prompt, presetId = SpeechCatalog.ClonePromptId(language), editable = false,
                    instructions = SpeechCatalog.CloneRecordingInstructions, coverage = SpeechCatalog.ClonePromptCoverage(language), readAloudField = "text" };
            }
            case "speech_clone_record_cancel":
                _speechRecorder?.Dispose(); _speechRecorder = null; _speechMcpRecordingOwner = null; _speechMcpRecordingPrompt = null; return new { recording = false };
            case "speech_clone_record_stop":
            {
                if (_speechRecorder is not { } recorder || _speechMcpRecordingOwner != _mcpControlOwnerSessionId)
                    throw new SpeechFailure("VOICE0006", "当前会话没有正在进行的克隆录音。");
                string language = _speechRecordingLanguage ?? "zh", name = McpString(args, "name"), output = McpString(args, "path");
                string gender = _speechRecordingGender ?? throw new SpeechFailure("VOICE0006", "录音缺少声音性别，请重新开始录音。");
                string prompt = _speechMcpRecordingPrompt ?? throw new SpeechFailure("VOICE0006", "录音缺少固定示例，请重新开始录音。");
                if (string.IsNullOrWhiteSpace(name) || !output.EndsWith(".tvevcv", StringComparison.OrdinalIgnoreCase))
                    throw new SpeechFailure("VOICE0006", "请提供音色名称和 .tvevcv 输出路径，当前录音已保留。");
                _speechRecorder = null; _speechMcpRecordingOwner = null; _speechMcpRecordingPrompt = null;
                return McpSpeechJob("保存克隆音色", async (_, t) =>
                {
                    string temporary = AppStorage.TempPath(Guid.NewGuid().ToString("N") + ".wav");
                    try
                    {
                        byte[] audio = await Task.Run(recorder.Stop, t);
                        await WaveInRecorder.WriteWaveAsync(temporary, audio, t);
                        SpeechVoice voice = await Task.Run(() => SpeechAssets.CreateVoiceAsync(temporary, name, language,
                            prompt, 0, null, t, gender, output, SpeechCatalog.ClonePromptId(language)), t);
                        return new { voice, path = output };
                    }
                    finally { recorder.Dispose(); if (File.Exists(temporary)) File.Delete(temporary); }
                });
            }
            case "speech_split": return SpeechText.Parse(McpString(args, "text"), McpDouble(args, "gapSeconds", .2));
            case "speech_tracks": return _project.Tracks.Where(t => t.Speech is not null).Select(t => new { trackId = t.Id, t.Name, t.IsLocked, settings = t.Speech }).ToArray();
            case "speech_generate":
            {
                var data = new SpeechTrackData { Origin = McpDouble(args, "startSeconds", _project.Sequence.PlayheadSeconds),
                    Device = McpString(args, "device", "auto"), ModelSize = McpString(args, "modelSize", "0.6b") };
                if (args.ContainsKey("text") == args.ContainsKey("sentences")) throw new SpeechFailure("VOICE0001", "text 与 sentences 必须且只能提供一项。");
                if (args["defaults"] is JsonObject defaults) data.Defaults = defaults.Deserialize<SpeechParameters>(SpeechJson.Options)!;
                if (args["captions"] is JsonObject captionSettings)
                    data.Captions = captionSettings.Deserialize<SpeechCaptionSettings>(SpeechJson.Options);
                if (args["sentences"] is JsonArray array)
                {
                    data.Sentences = array.Deserialize<List<SpeechSentence>>(SpeechJson.Options)!;
                    SpeechText.Validate(data);
                    for (int sentenceIndex = 0; sentenceIndex < data.Sentences.Count; sentenceIndex++)
                    {
                        SpeechSentence s = data.Sentences[sentenceIndex];
                        s.Id = Guid.NewGuid().ToString("N"); s.AudioAssetId = s.RawAssetId = s.Fingerprint = s.AudioFingerprint = null; s.RawDuration = 0;
                        JsonArray? supplied = (array[sentenceIndex] as JsonObject)?["parts"] as JsonArray;
                        for (int partIndex = 0; partIndex < s.Parts.Count; partIndex++)
                            s.Parts[partIndex].HasInitialRate = supplied?[partIndex] is JsonObject part && part.ContainsKey("initialRate");
                    }
                }
                else data.Sentences = SpeechText.Parse(McpString(args, "text"), data.Defaults.Gap);
                SpeechText.Validate(data); return McpSpeechGenerate(data, null);
            }
            case "speech_track_update":
            {
                TimelineTrack track = SpeechTrack(args); SpeechTrackData data = SpeechJson.Copy(track.Speech!);
                if (args["defaults"] is JsonObject defaults) data.Defaults = JsonModelMergeSpeech(data.Defaults, defaults);
                data.Device = McpString(args, "device", data.Device); data.ModelSize = McpString(args, "modelSize", data.ModelSize);
                if (args.ContainsKey("captions")) data.Captions = args["captions"] is JsonObject captionSettings
                    ? captionSettings.Deserialize<SpeechCaptionSettings>(SpeechJson.Options) : null;
                data.Origin = McpDouble(args, "startSeconds", data.Origin); SpeechText.Validate(data);
                return McpSpeechGenerate(data, track);
            }
            case "speech_sentence_update":
            case "speech_sentence_regenerate":
            case "speech_sentence_remove":
            {
                TimelineTrack track = SpeechTrack(args); SpeechTrackData data = SpeechJson.Copy(track.Speech!);
                string id = McpString(args, "sentenceId"); int index = data.Sentences.FindIndex(s => s.Id == id);
                if (index < 0) throw new SpeechFailure("VOICE0004", "句子不存在。");
                if (command == "speech_sentence_remove") data.Sentences.RemoveAt(index);
                else if (command == "speech_sentence_update")
                {
                    data.Sentences[index] = McpSpeechSentencePatch(data.Sentences[index], McpObject(args, "patch"));
                    SpeechText.Validate(data);
                    if (data.Sentences[index].Parts.Any(p => p.InitialRate != 1 || p.HasInitialRate))
                        throw new SpeechFailure("VOICE0001", "一次性速度标记只用于初次生成；后续更新请使用 rate，语言片段不要设置 initialRate。");
                }
                SpeechText.Validate(data); return McpSpeechGenerate(data, track, command == "speech_sentence_regenerate" ? id : null);
            }
            case "speech_sentence_reorder":
            {
                TimelineTrack track = SpeechTrack(args); SpeechTrackData data = SpeechJson.Copy(track.Speech!);
                string[] order = McpStrings(args, "sentenceIds"); McpRequirePermutation(order, data.Sentences.Select(s => s.Id));
                data.Sentences = order.Select(id => data.Sentences.First(s => s.Id == id)).ToList(); return McpSpeechGenerate(data, track);
            }
            case "speech_track_remove":
            {
                TimelineTrack track = SpeechTrack(args);
                return McpMutate("删除语音轨道", () =>
                {
                    _project.Tracks.Remove(track);
                    _project.Tracks.RemoveAll(item => item.Subtitle?.SpeechSourceTrackId == track.Id);
                    McpReorderTracks();
                }, applyTrackSnapping: false);
            }
            case "speech_preview":
            {
                TimelineTrack track = McpTrack(McpString(args, "trackId"));
                SpeechSentence sentence = track.Speech?.Sentences.FirstOrDefault(s => s.Id == McpString(args, "sentenceId"))
                    ?? throw new SpeechFailure("VOICE0004", "语音句子不存在。");
                MediaAsset asset = _project.Media.First(a => a.Id == sentence.AudioAssetId); PreviewSpeechAudio(asset, sentence.Text);
                return McpState(false);
            }
            case "speech_preview_stop": StopCatalogPreview(); return McpState(false);
            default: throw new StageException("MCPRT0004", "不支持的 MCP 命令：" + command);
        }
    }
    private static SpeechParameters JsonModelMergeSpeech(SpeechParameters source, JsonObject patch)
    {
        var node = JsonSerializer.SerializeToNode(source, SpeechJson.Options)!.AsObject();
        foreach ((string key, JsonNode? value) in patch) node[key] = value?.DeepClone();
        return node.Deserialize<SpeechParameters>(SpeechJson.Options)!;
    }
}
