using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Nonmodal automation adapters for workflows which normally collect options
/// in a dialog. All edits enter the same validated undo transaction as other
/// MCP commands; expensive work runs against detached snapshots and returns
/// through the dispatcher only when its original project revision still exists.
/// </summary>
public partial class MainWindow
{
    private WaveInRecorder? _mcpRecorder;
    private ProjectDocument? _mcpRecordingProject;
    private string? _mcpRecordingTrackId;
    private double _mcpRecordingStart;
    private bool _mcpRecordingBusy;
    private bool _mcpFeaturesDisposed;

    private async Task<object?> ExecuteMcpFeatureAsync(string command, JsonObject args,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "export_start": return McpExportStart(args);
            case "preview_capture": return McpCaptureStart(args);
            case "clip_freeze_frame": return McpFreezeFrameStart(args);
            case "subtitle_list":
                return McpSubtitleTracks(args).SelectMany(track => track.Clips
                    .Where(IsSubtitleClip).Select(clip => new
                    {
                        trackId = track.Id, clipId = clip.Id, clip.StartSeconds,
                        endSeconds = clip.StartSeconds + clip.DurationSeconds,
                        text = clip.Text!.Content, style = clip.Text
                    })).OrderBy(cue => cue.StartSeconds).ToArray();
            case "subtitle_import":
            {
                ProjectDocument owner = _project;
                long revision = _projectRevision;
                IReadOnlyList<SubtitleCue> cues = await SubtitleService.ReadSrtAsync(
                    McpString(args, "path"), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                McpRequireCurrent(owner, revision);
                return McpInsertSubtitles(cues, args);
            }
            case "subtitle_export":
            {
                string path = McpFeatureOutputPath(args, ".srt");
                SubtitleCue[] cues = McpSubtitleTracks(args).SelectMany(track => track.Clips)
                    .Where(IsSubtitleClip).Select(clip => new SubtitleCue(clip.StartSeconds,
                        clip.StartSeconds + clip.DurationSeconds, clip.Text!.Content)).ToArray();
                await SubtitleService.WriteSrtAsync(path, cues, cancellationToken);
                return new { path, cueCount = cues.Length };
            }
            case "subtitle_recognize": return McpRecognizeStart(args);
            case "graphic_template_read":
                return await GraphicTemplateDocument.ReadAsync(McpString(args, "path"), cancellationToken);
            case "graphic_template_save":
            {
                TimelineClip clip = McpClip(McpString(args, "clipId"));
                if (clip.Kind != ClipKind.Text)
                    throw McpFeatureError("图形模板只能保存文字或图形对象。");
                string path = McpFeatureOutputPath(args, GraphicTemplateDocument.Extension);
                GraphicTemplateDocument template = GraphicTemplateDocument.FromClip(clip);
                await template.WriteAsync(path, cancellationToken);
                return new { path, template.Name };
            }
            case "graphic_template_insert":
            {
                ProjectDocument owner = _project;
                long revision = _projectRevision;
                GraphicTemplateDocument template = await GraphicTemplateDocument.ReadAsync(
                    McpString(args, "path"), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                McpRequireCurrent(owner, revision);
                TimelineClip clip = template.Instantiate(McpDouble(args, "startSeconds",
                    _project.Sequence.PlayheadSeconds));
                string trackId = "";
                McpMutate("MCP 插入图形模板", () =>
                {
                    TimelineTrack track = McpFeatureTextTrack(args, clip.StartSeconds, clip.DurationSeconds);
                    track.Clips.Add(clip);
                    trackId = track.Id;
                    SelectClip(clip);
                });
                return new { trackId, clipId = clip.Id };
            }
            case "sequence_exchange_export":
            {
                string format = McpExchangeFormat(args);
                string path = McpFeatureOutputPath(args, format == "aafxml" ? ".xml" : "." + format);
                ProjectDocument snapshot = McpFeatureSnapshot();
                return McpStartJob("导出项目交换文件", async (_, token) =>
                {
                    switch (format)
                    {
                        case "xml": await ProjectExchangeService.ExportXmlAsync(snapshot, path, token); break;
                        case "edl": await ProjectExchangeService.ExportEdlAsync(snapshot, path, token); break;
                        case "otio": await McpFeatureExportOtioAsync(snapshot, path, token); break;
                        case "aafxml": await AafInterchange.ExportAafXmlAsync(snapshot, path, token); break;
                    }
                    return new { path, format };
                });
            }
            case "sequence_exchange_import":
            {
                string path = McpString(args, "path");
                string format = McpExchangeFormat(args);
                if (format == "aafxml") throw McpFeatureError("AAF-XML 当前仅支持导出。");
                ProjectDocument owner = _project;
                long revision = _projectRevision;
                ProjectDocument imported = format switch
                {
                    "xml" => await ProjectExchangeService.ImportXmlAsync(path, cancellationToken),
                    "edl" => await ProjectExchangeService.ImportEdlAsync(path, cancellationToken),
                    _ => await McpFeatureImportOtioAsync(path, cancellationToken)
                };
                string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                foreach (MediaAsset asset in imported.Media)
                {
                    if (!Path.IsPathFullyQualified(asset.SourcePath))
                        asset.SourcePath = Path.GetFullPath(Path.Combine(directory, asset.SourcePath));
                    if (!string.IsNullOrWhiteSpace(asset.ProxyPath) && !Path.IsPathFullyQualified(asset.ProxyPath))
                        asset.ProxyPath = Path.GetFullPath(Path.Combine(directory, asset.ProxyPath));
                }
                cancellationToken.ThrowIfCancellationRequested();
                McpRequireCurrent(owner, revision);
                McpMutate("MCP 导入项目交换文件", () =>
                {
                    _project = imported;
                    _projectPath = null;
                    _projectCreated = true;
                    _projectGeneration++;
                    _sourceMonitor?.Close();
                    _selectedClip = null;
                    _selectedClips.Clear();
                    _selectedTrack = null;
                });
                return new { path, format, imported.Name };
            }
            case "audio_sync": return McpAudioSync(args);
            case "audio_tempo": return McpAudioTempo(args);
            case "media_analyze": return McpMediaAnalyze(args);
            case "media_reframe": return McpReframeStart(args);
            case "mask_track": return McpMaskTrackStart(args);
            case "scope_capture": return McpScopeCaptureStart(args);
            case "audio_settings_get": return McpAudioSettings(args, update: false);
            case "audio_settings_update": return McpAudioSettings(args, update: true);
            case "audio_remix": return McpAudioRemix(args);
            case "multicam_list": return _project.MulticamGroups;
            case "multicam_create": return McpMulticamCreate(args);
            case "multicam_switch": return McpMulticamSwitch(args);
            case "paper_edit": return McpPaperEdit(args);
            case "voiceover_devices": return await Task.Run(WaveInRecorder.GetDevices, cancellationToken);
            case "voiceover_start": return await McpVoiceoverStartAsync(args, cancellationToken);
            case "voiceover_stop": return await McpVoiceoverStopAsync(args, cancellationToken);
            case "voiceover_cancel":
                McpCancelVoiceover();
                StatusText.Text = "MCP 已取消录音";
                return new { recording = false };
            default: throw new StageException("MCPRT0004", $"不支持的 MCP 命令：{command}");
        }
    }

    private object McpExportStart(JsonObject args)
    {
        string path = McpFeatureOutputPath(args, ".mp4");
        ProjectDocument snapshot = PrepareExportSequence(McpFeatureSnapshot(),
            McpString(args, "sequenceId", _project.Sequence.Id));
        VideoExportSettings settings = args["settings"]?.Deserialize<VideoExportSettings>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new VideoExportSettings();
        return McpStartJob("导出视频", async (progress, token) =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"tangerine-mcp-export-{Guid.NewGuid():N}");
            try
            {
                Dictionary<string, string> titles = await RenderTitlesAsync(snapshot, temporary, token);
                await TimelineExporter.ExportMp4Async(snapshot, path, titles, progress, token, settings);
                return new { path, sequenceId = snapshot.Sequence.Id };
            }
            finally { McpFeatureDeleteDirectory(temporary); }
        });
    }

    private object McpCaptureStart(JsonObject args)
    {
        string path = McpFeatureOutputPath(args, ".png");
        double time = McpDouble(args, "timeSeconds", _project.Sequence.PlayheadSeconds);
        ProjectDocument snapshot = PrepareExportSequence(McpFeatureSnapshot(),
            McpString(args, "sequenceId", _project.Sequence.Id));
        string[] protectedPaths = _projectPath is string projectPath ? [projectPath] : [];
        return McpStartJob("捕获预览画面", async (progress, token) =>
        {
            await StillFrameExporter.ExportCompositedFrameAsync(snapshot, time, path, token, protectedPaths);
            progress.Report(1);
            return new { path, timeSeconds = time, snapshot.Sequence.Width, snapshot.Sequence.Height };
        });
    }

    private object McpFreezeFrameStart(JsonObject args)
    {
        TimelineClip live = McpClip(McpString(args, "clipId"));
        TimelineTrack track = McpFeatureTrackFor(live); McpFeatureEditable(track);
        if (live.Kind != ClipKind.Video) throw McpFeatureError("冻结帧需要视频片段。");
        string path = McpFeatureOutputPath(args, ".png");
        TimelineClip clip = CloneClip(live);
        MediaAsset asset = JsonSerializer.Deserialize<MediaAsset>(JsonSerializer.Serialize(McpFeatureAssetFor(live)))!;
        ProjectSequence sequence = JsonSerializer.Deserialize<ProjectSequence>(JsonSerializer.Serialize(_project.Sequence))!;
        double time = McpDouble(args, "timeSeconds", sequence.PlayheadSeconds);
        double duration = McpDouble(args, "durationSeconds", 1);
        bool replace = McpBool(args, "replace");
        if (!double.IsFinite(time) || time < 0 || !double.IsFinite(duration) || duration <= 0 ||
            time + duration > 24 * 3600)
            throw McpFeatureError("冻结帧插入时间或持续时长无效。");
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        string? projectPath = _projectPath;
        string[] protectedPaths = _project.Media.SelectMany(media => new[] { media.SourcePath, media.ProxyPath })
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
        return McpStartJob("冻结视频帧", async (progress, token) =>
        {
            await StillFrameExporter.ExportSourceFrameAsync(clip, asset, sequence, time, path, protectedPaths, token);
            progress.Report(.8);
            string[] projected = [.. protectedPaths, path];
            if (projectPath is not null) await ProjectFileService.EnsureImportCapacityAsync(projectPath, projected, token);
            else await ProjectFileService.EnsureImportCapacityAsync(projected, token);
            return await McpOnUiAsync<object?>(() =>
            {
                token.ThrowIfCancellationRequested(); McpRequireCurrent(owner, revision);
                var image = new MediaAsset { Name = Path.GetFileName(path), Kind = MediaKind.Image,
                    SourcePath = path, Width = sequence.Width, Height = sequence.Height };
                var still = new TimelineClip { Kind = ClipKind.Image, Name = image.Name,
                    MediaAssetId = image.Id, StartSeconds = replace ? clip.StartSeconds : time,
                    DurationSeconds = replace ? clip.DurationSeconds : duration };
                McpMutate("MCP 冻结帧", () =>
                {
                    McpFeatureEditable(track);
                    _project.Media.Add(image);
                    if (replace) track.Clips.Remove(live);
                    track.Clips.Add(still); SelectClip(still);
                });
                return new { path, assetId = image.Id, clipId = still.Id, replaced = replace };
            });
        });
    }

    private IEnumerable<TimelineTrack> McpSubtitleTracks(JsonObject args)
    {
        string id = McpString(args, "trackId", "");
        return id.Length == 0 ? _project.Tracks : [McpTrack(id)];
    }

    private object McpInsertSubtitles(IReadOnlyList<SubtitleCue> cues, JsonObject args)
    {
        double offset = McpDouble(args, "offsetSeconds");
        if (!double.IsFinite(offset) || cues.Any(cue => cue.StartSeconds + offset < 0 ||
            cue.EndSeconds + offset > 24 * 3600))
            throw McpFeatureError("字幕偏移后超出时间线范围。");
        var clips = cues.Select(cue => new TimelineClip
        {
            Kind = ClipKind.Text, Name = cue.Text.Split('\n')[0],
            StartSeconds = cue.StartSeconds + offset,
            DurationSeconds = cue.EndSeconds - cue.StartSeconds,
            Text = CreateSubtitleStyle(cue.Text)
        }).ToList();
        if (clips.Count == 0) return new { cueCount = 0 };
        string trackId = "";
        McpMutate("MCP 导入字幕", () =>
        {
            string requested = McpString(args, "trackId", "");
            TimelineTrack track;
            if (requested.Length == 0)
            {
                track = new TimelineTrack { Kind = TrackKind.Video, Name = "字幕" };
                _project.Tracks.Insert(0, track);
                McpFeatureReorderTracks();
            }
            else
            {
                track = McpTrack(requested);
                McpFeatureEditable(track);
                if (track.Kind == TrackKind.Audio) throw McpFeatureError("字幕需要视频轨道。");
            }
            track.Clips.AddRange(clips);
            _selectedTrack = track;
            trackId = track.Id;
            SelectClip(clips[0]);
        });
        return new { trackId, cueCount = clips.Count, clipIds = clips.Select(clip => clip.Id).ToArray() };
    }

    private object McpAudioSync(JsonObject args)
    {
        TimelineClip reference = McpClip(McpString(args, "referenceClipId"));
        TimelineClip moving = McpClip(McpString(args, "movingClipId"));
        if (reference.Id == moving.Id) throw McpFeatureError("同步需要两个不同片段。");
        McpFeatureEditable(McpFeatureTrackFor(moving));
        MediaAsset firstAsset = McpFeatureAssetFor(reference);
        MediaAsset secondAsset = McpFeatureAssetFor(moving);
        McpFeatureNormalSpeed(reference);
        McpFeatureNormalSpeed(moving);
        string method = McpString(args, "method", "Waveform");
        if (method == "Timecode")
        {
            double delta = McpFeatureTimecodeDelta(reference, firstAsset, moving, secondAsset);
            return McpCommitAudioSync(reference, moving, delta);
        }
        if (method != "Waveform") throw McpFeatureError("同步方式应为 Waveform 或 Timecode。");
        if ((reference.Kind == ClipKind.Video && !firstAsset.HasAudio) ||
            (moving.Kind == ClipKind.Video && !secondAsset.HasAudio))
            throw McpFeatureError("波形同步需要两个含音频的片段。");
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        TimelineClip first = CloneClip(reference), second = CloneClip(moving);
        string firstPath = firstAsset.EffectiveExportPath, secondPath = secondAsset.EffectiveExportPath;
        return McpStartJob("音频波形同步", async (_, token) =>
        {
            double delta = await AudioWaveformAnalysis.FindOffsetAsync(first, firstPath, second, secondPath, token);
            return await McpOnUiAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                McpRequireCurrent(owner, revision);
                return McpCommitAudioSync(reference, moving, delta);
            });
        });
    }

    private object McpCommitAudioSync(TimelineClip reference, TimelineClip moving, double delta)
    {
        double start = reference.StartSeconds + delta;
        if (!double.IsFinite(start) || start < 0 || start + moving.DurationSeconds > 24 * 3600)
            throw McpFeatureError("同步结果超出时间线范围。");
        McpMutate("MCP 同步音频", () =>
        {
            McpFeatureEditable(McpFeatureTrackFor(moving));
            moving.StartSeconds = start;
            SelectClip(moving);
        });
        return new { clipId = moving.Id, startSeconds = start, offsetSeconds = delta };
    }

    private object McpAudioTempo(JsonObject args)
    {
        TimelineClip clip = CloneClip(McpClip(McpString(args, "clipId")));
        MediaAsset asset = McpFeatureAssetFor(clip);
        string path = asset.EffectiveExportPath;
        double duration = Math.Min(clip.DurationSeconds, (asset.DurationSeconds ?? clip.DurationSeconds) - clip.SourceInSeconds);
        return McpStartJob("分析音乐节拍", async (_, token) => new
        {
            clipId = clip.Id,
            bpm = await AudioWaveformAnalysis.EstimateTempoAsync(path, clip.SourceInSeconds, duration, token)
        });
    }

    private object McpAudioRemix(JsonObject args)
    {
        TimelineClip clip = McpClip(McpString(args, "clipId"));
        TimelineTrack track = McpFeatureTrackFor(clip);
        McpFeatureEditable(track);
        McpFeatureNormalSpeed(clip);
        MediaAsset asset = McpFeatureAssetFor(clip);
        if (clip.Kind != ClipKind.Audio || asset.DurationSeconds is not > 0)
            throw McpFeatureError("音乐重排需要已读取时长的音频片段。");
        double target = McpDouble(args, "targetDurationSeconds");
        double bpm = McpDouble(args, "bpm", 120);
        var segments = MusicRemixPlanner.Plan(clip.StartSeconds, clip.SourceInSeconds,
            asset.DurationSeconds.Value, target, bpm);
        var pieces = segments.Select((segment, index) =>
        {
            TimelineClip piece = CloneClip(clip);
            piece.Id = Guid.NewGuid().ToString("N");
            piece.Name = $"{clip.Name} · {index + 1}";
            piece.Keyframes.Clear(); piece.Transitions.Clear();
            piece.GroupId = null; piece.LinkGroupId = null;
            piece.StartSeconds = segment.StartSeconds;
            piece.SourceInSeconds = segment.SourceInSeconds;
            piece.DurationSeconds = segment.DurationSeconds;
            piece.AdditionalData ??= [];
            piece.AdditionalData[AudioProcessing.RemixFadeInKey] = JsonSerializer.SerializeToElement(segment.FadeInSeconds);
            piece.AdditionalData[AudioProcessing.RemixFadeOutKey] = JsonSerializer.SerializeToElement(segment.FadeOutSeconds);
            return piece;
        }).ToList();
        McpMutate("MCP 重排音乐", () =>
        {
            track.Clips.Remove(clip); track.Clips.AddRange(pieces);
            SelectClip(pieces[0]);
        });
        return new { trackId = track.Id, clipIds = pieces.Select(piece => piece.Id).ToArray(), targetDurationSeconds = target, bpm };
    }

    private object McpAudioSettings(JsonObject args, bool update)
    {
        string clipId = McpString(args, "clipId", ""), trackId = McpString(args, "trackId", "");
        if ((clipId.Length > 0) == (trackId.Length > 0))
            throw McpFeatureError("请指定 clipId 或 trackId 中的一个目标。");
        TimelineClip? clip = clipId.Length > 0 ? McpClip(clipId) : null;
        TimelineTrack track = clip is not null ? McpFeatureTrackFor(clip) : McpTrack(trackId);
        AudioSettings settings = clip is not null ? AudioSettings.FromClip(clip) : AudioSettings.FromTrack(track);
        if (update)
        {
            McpFeatureEditable(track);
            settings = JsonModelTools.Merge(settings,
                args["patch"] as JsonObject ?? throw McpFeatureError("需要音频设置 patch 对象。"));
            settings.Validate();
            McpMutate("MCP 修改音频设置", () =>
            {
                if (clip is not null) settings.Save(clip);
                else settings.Save(track);
            });
        }
        return new { clipId = clip?.Id, trackId = track.Id, settings };
    }

    private object McpReframeStart(JsonObject args)
    {
        TimelineClip live = McpClip(McpString(args, "clipId"));
        McpFeatureEditable(McpFeatureTrackFor(live));
        TimelineClip clip = CloneClip(live);
        MediaAsset asset = JsonSerializer.Deserialize<MediaAsset>(JsonSerializer.Serialize(McpFeatureAssetFor(live)))!;
        ProjectSequence sequence = JsonSerializer.Deserialize<ProjectSequence>(JsonSerializer.Serialize(_project.Sequence))!;
        if (clip.Kind is not (ClipKind.Video or ClipKind.Image) || asset.Width is not > 0 || asset.Height is not > 0)
            throw McpFeatureError("重构图需要已读取尺寸的视频或图片片段。");
        int samples = McpInt(args, "samples", (int)Math.Min(61, Math.Max(2, Math.Ceiling(clip.DurationSeconds) + 1)));
        if (samples is < 2 or > 61) throw McpFeatureError("重构图采样数量必须为 2 至 61。");
        bool apply = McpBool(args, "apply", true);
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        return McpStartJob("自动重构图", async (progress, token) =>
        {
            (int width, int height) = McpFeatureSampleSize(asset, sequence);
            var keys = new List<ClipKeyframe>();
            double fittedRatio = Math.Min(sequence.Width / (double)asset.Width.Value,
                sequence.Height / (double)asset.Height.Value);
            double fittedWidth = fittedRatio * asset.Width.Value, fittedHeight = fittedRatio * asset.Height.Value;
            double scale = Math.Clamp(Math.Max(sequence.Width / fittedWidth, sequence.Height / fittedHeight), .05, 8);
            double travelX = Math.Max(0, fittedWidth * scale - sequence.Width);
            double travelY = Math.Max(0, fittedHeight * scale - sequence.Height);
            for (int index = 0; index < samples; index++)
            {
                token.ThrowIfCancellationRequested();
                double local = clip.DurationSeconds * index / (samples - 1);
                BitmapSource frame = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, sequence,
                    clip.StartSeconds + Math.Min(local, Math.Max(0, clip.DurationSeconds - 1 / sequence.FramesPerSecond)),
                    width, height, token, includeVisualEffects: false);
                (double x, double y) = await AutoReframeAnalyzer.AnalyzeAsync(frame);
                keys.Add(new ClipKeyframe { Property = "PositionX", TimeSeconds = local,
                    Value = (.5 - x) * travelX, Interpolation = "EaseInOut" });
                keys.Add(new ClipKeyframe { Property = "PositionY", TimeSeconds = local,
                    Value = (.5 - y) * travelY, Interpolation = "EaseInOut" });
                progress.Report((index + 1d) / samples);
            }
            if (apply) await McpOnUiAsync(() =>
            {
                token.ThrowIfCancellationRequested(); McpRequireCurrent(owner, revision);
                return McpMutate("MCP 自动重构图", () =>
                {
                    McpFeatureEditable(McpFeatureTrackFor(live));
                    live.Keyframes.RemoveAll(key => key.Property is "PositionX" or "PositionY" or "ScaleX" or "ScaleY");
                    live.Keyframes.AddRange(keys);
                    live.Transform.ScaleX = scale; live.Transform.ScaleY = scale;
                });
            });
            return new { clipId = clip.Id, applied = apply, scale, keyframes = keys };
        });
    }

    private object McpMaskTrackStart(JsonObject args)
    {
        TimelineClip live = McpClip(McpString(args, "clipId"));
        McpFeatureEditable(McpFeatureTrackFor(live));
        if (live.Kind != ClipKind.Video || live.Mask is null)
            throw McpFeatureError("运动跟踪需要带蒙版的视频片段。");
        TimelineClip clip = CloneClip(live);
        ClipMask mask = clip.Mask!;
        MediaAsset asset = JsonSerializer.Deserialize<MediaAsset>(JsonSerializer.Serialize(McpFeatureAssetFor(live)))!;
        ProjectSequence sequence = JsonSerializer.Deserialize<ProjectSequence>(JsonSerializer.Serialize(_project.Sequence))!;
        bool forward = McpBool(args, "forward", true), apply = McpBool(args, "apply", true);
        int frames = McpInt(args, "frames", 60);
        double time = McpDouble(args, "timeSeconds", _project.Sequence.PlayheadSeconds);
        if (frames is < 1 or > 300 || !double.IsFinite(time) || time < clip.StartSeconds ||
            time >= clip.StartSeconds + clip.DurationSeconds)
            throw McpFeatureError("跟踪时间必须位于片段内，帧数必须为 1 至 300。");
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        return McpStartJob("跟踪蒙版", async (progress, token) =>
        {
            double interval = 1 / sequence.FramesPerSecond;
            double start = Math.Floor((time - clip.StartSeconds) * sequence.FramesPerSecond + 1e-9) / sequence.FramesPerSecond;
            (int width, int height) = McpFeatureSampleSize(asset, sequence);
            double x = ClipAnimation.Evaluate(clip, "Mask.CenterX", time);
            double y = ClipAnimation.Evaluate(clip, "Mask.CenterY", time);
            BitmapSource previous = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, sequence,
                clip.StartSeconds + start, width, height, token, includeVisualEffects: false);
            var keys = new List<ClipKeyframe>
            {
                new() { Property = "Mask.CenterX", TimeSeconds = start, Value = x },
                new() { Property = "Mask.CenterY", TimeSeconds = start, Value = y }
            };
            for (int index = 1; index <= frames; index++)
            {
                token.ThrowIfCancellationRequested();
                double local = start + (forward ? index : -index) * interval;
                if (local < 0 || local >= clip.DurationSeconds) break;
                BitmapSource current = await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, sequence,
                    clip.StartSeconds + local, width, height, token, includeVisualEffects: false);
                (x, y) = await MaskMotionTracker.TrackStepAsync(previous, current, x, y, mask.Width, mask.Height);
                keys.Add(new ClipKeyframe { Property = "Mask.CenterX", TimeSeconds = local, Value = x, Interpolation = "Linear" });
                keys.Add(new ClipKeyframe { Property = "Mask.CenterY", TimeSeconds = local, Value = y, Interpolation = "Linear" });
                previous = current;
                progress.Report(index / (double)frames);
            }
            if (apply) await McpOnUiAsync(() =>
            {
                token.ThrowIfCancellationRequested(); McpRequireCurrent(owner, revision);
                return McpMutate("MCP 跟踪蒙版", () =>
                {
                    McpFeatureEditable(McpFeatureTrackFor(live));
                    foreach (ClipKeyframe key in keys)
                    {
                        live.Keyframes.RemoveAll(existing => existing.Property == key.Property &&
                            Math.Abs(existing.TimeSeconds - key.TimeSeconds) < interval * .25);
                        live.Keyframes.Add(key);
                    }
                });
            });
            return new { clipId = clip.Id, applied = apply, trackedFrames = keys.Count / 2, keyframes = keys };
        });
    }

    private object McpScopeCaptureStart(JsonObject args)
    {
        string path = McpFeatureOutputPath(args, ".png");
        string mode = McpString(args, "mode", "waveform");
        int index = mode switch { "waveform" => 0, "parade" => 1, "vectorscope" => 2, "histogram" => 3,
            _ => throw McpFeatureError("示波器模式无效。") };
        double time = McpDouble(args, "timeSeconds", _project.Sequence.PlayheadSeconds);
        ProjectDocument snapshot = McpFeatureSnapshot();
        return McpStartJob("生成颜色示波器", async (progress, token) =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"tangerine-mcp-scope-{Guid.NewGuid():N}.png");
            try
            {
                await StillFrameExporter.ExportCompositedFrameAsync(snapshot, time, temporary, token);
                await McpOnUiAsync(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    var frame = new BitmapImage(); frame.BeginInit();
                    frame.CacheOption = BitmapCacheOption.OnLoad;
                    frame.UriSource = new Uri(temporary); frame.EndInit(); frame.Freeze();
                    BitmapSource scope = await ColorScopeRenderer.RenderBitmapAsync(frame, index);
                    token.ThrowIfCancellationRequested();
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(scope));
                    using var stream = File.Create(path); encoder.Save(stream);
                    return true;
                }).Unwrap();
                progress.Report(1);
                return new { path, mode, timeSeconds = time };
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        });
    }

    private static (int Width, int Height) McpFeatureSampleSize(MediaAsset asset, ProjectSequence sequence)
    {
        double aspect = asset.Width is > 0 && asset.Height is > 0
            ? asset.Width.Value / (double)asset.Height.Value : sequence.Width / (double)sequence.Height;
        return aspect >= 1 ? (320, Math.Clamp((int)Math.Round(320 / aspect), 64, 320))
            : (Math.Clamp((int)Math.Round(320 * aspect), 64, 320), 320);
    }

    private object McpPaperEdit(JsonObject args)
    {
        TimelineTrack source = McpTrack(McpString(args, "trackId"));
        McpFeatureEditable(source);
        JsonArray ranges = args["ranges"] as JsonArray ?? throw McpFeatureError("请提供 ranges 时间范围数组。");
        if (ranges.Count is < 1 or > 10000) throw McpFeatureError("时间范围数量必须为 1 至 10000。");
        var selected = ranges.Select(node => node as JsonObject ?? throw McpFeatureError("时间范围必须为对象。"))
            .Select(range => (McpDouble(range, "startSeconds"), McpDouble(range, "endSeconds"))).ToArray();
        bool keep = McpBool(args, "keepSelected", true);
        double cursor = McpDouble(args, "startSeconds", _project.Sequence.PlayheadSeconds);
        var plan = PaperEditPlanner.Plan(source, selected, keep);
        var pieces = new List<TimelineClip>();
        foreach (PaperEditPiece piece in plan)
        {
            TimelineClip clone = CloneClip(piece.SourceClip);
            clone.Id = Guid.NewGuid().ToString("N");
            ClipAnimation.KeepRange(clone, piece.LocalStartSeconds, piece.DurationSeconds);
            AdvanceMulticamLocalStart(clone, piece.LocalStartSeconds);
            if (clone.TimeRemapPoints.Count > 0)
                ClipTimeMap.KeepRange(clone, piece.LocalStartSeconds, piece.DurationSeconds);
            else if (clone.IsReversed)
                clone.SourceInSeconds += (piece.SourceClip.DurationSeconds - piece.LocalStartSeconds - piece.DurationSeconds) * clone.Speed;
            else clone.SourceInSeconds += piece.LocalStartSeconds * clone.Speed;
            clone.StartSeconds = keep ? cursor : piece.StartSeconds;
            clone.DurationSeconds = piece.DurationSeconds;
            clone.GroupId = null; clone.LinkGroupId = null; clone.Transitions.Clear();
            clone.AdditionalData?.Remove(AudioProcessing.RemixFadeInKey);
            clone.AdditionalData?.Remove(AudioProcessing.RemixFadeOutKey);
            pieces.Add(clone);
            if (keep) cursor += clone.DurationSeconds;
        }
        if (!double.IsFinite(cursor) || cursor < 0 || cursor > 24 * 3600)
            throw McpFeatureError("文本粗剪结果超出时间线范围。");
        TimelineTrack destination = source;
        McpMutate("MCP 按文本剪辑", () =>
        {
            if (keep)
            {
                destination = new TimelineTrack { Name = $"文本粗剪 {source.Name}", Kind = source.Kind, Clips = pieces };
                _project.Tracks.Insert(_project.Tracks.IndexOf(source), destination);
                McpFeatureReorderTracks();
            }
            else
            {
                source.Clips.RemoveAll(clip => source.Kind == TrackKind.Video ? clip.Kind == ClipKind.Video : clip.Kind == ClipKind.Audio);
                source.Clips.AddRange(pieces);
            }
            _selectedTrack = destination;
            if (pieces.Count > 0) SelectClip(pieces[0]);
        });
        return new { trackId = destination.Id, clipIds = pieces.Select(piece => piece.Id).ToArray() };
    }

    private object McpMulticamCreate(JsonObject args)
    {
        string[] ids = (args["clipIds"] as JsonArray ?? throw McpFeatureError("需要 clipIds 数组。"))
            .Select(node => node?.GetValue<string>() ?? "").Distinct().ToArray();
        if (ids.Length is < 2 or > 16) throw McpFeatureError("多机位需要 2 至 16 个不同视频片段。");
        var sources = ids.Select(id =>
        {
            TimelineClip clip = McpClip(id);
            TimelineTrack track = McpFeatureTrackFor(clip);
            MediaAsset asset = McpFeatureAssetFor(clip);
            McpFeatureEditable(track); McpFeatureNormalSpeed(clip);
            if (track.Kind != TrackKind.Video || clip.Kind != ClipKind.Video ||
                asset.Kind != MediaKind.Video || clip.MulticamGroupId is not null ||
                asset.DurationSeconds is double duration && clip.SourceInSeconds + clip.DurationSeconds > duration + .001)
                throw McpFeatureError("机位必须是未分组的正向原速视频，且源入出点有效。");
            return (Clip: CloneClip(clip), TrackId: track.Id,
                Asset: JsonSerializer.Deserialize<MediaAsset>(JsonSerializer.Serialize(asset))!);
        }).ToArray();
        if (sources.Select(source => source.Asset.Id).Distinct().Count() < 2)
            throw McpFeatureError("多机位至少需要两个不同视频源。");
        string method = McpString(args, "syncMethod", "Timeline");
        if (method is not ("Timeline" or "Timecode" or "Waveform"))
            throw McpFeatureError("多机位同步方式应为 Timeline、Timecode 或 Waveform。");
        string name = McpString(args, "name", "多机位");
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        double fps = _project.Sequence.FramesPerSecond;
        return McpStartJob("建立多机位", async (progress, token) =>
        {
            var starts = new double[sources.Length];
            starts[0] = sources[0].Clip.StartSeconds;
            for (int index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(sources[index].Asset.EffectiveExportPath))
                    throw McpFeatureError($"机位源文件不存在：{sources[index].Asset.Name}");
                if (index == 0) continue;
                starts[index] = method switch
                {
                    "Timeline" => sources[index].Clip.StartSeconds,
                    "Timecode" => starts[0] + McpFeatureTimecodeDelta(sources[0].Clip,
                        sources[0].Asset, sources[index].Clip, sources[index].Asset),
                    _ => starts[0] + await AudioWaveformAnalysis.FindOffsetAsync(sources[0].Clip,
                        sources[0].Asset.EffectiveExportPath, sources[index].Clip,
                        sources[index].Asset.EffectiveExportPath, token)
                };
                progress.Report((double)index / sources.Length);
            }
            double shift = Math.Max(0, -starts.Min());
            for (int index = 0; index < starts.Length; index++) starts[index] += shift;
            double start = starts.Max();
            double end = sources.Select((source, index) => starts[index] + source.Clip.DurationSeconds).Min();
            double duration = Math.Floor((end - start) * fps + 1e-9) / fps;
            if (!double.IsFinite(duration) || duration < 1 / fps - 1e-6)
                throw McpFeatureError("所选机位没有共同的完整帧区间。");
            var group = new MulticamGroup { Name = name, SyncMethod = method, DurationSeconds = duration };
            for (int index = 0; index < sources.Length; index++)
            {
                TimelineClip normalized = sources[index].Clip;
                double skip = start - starts[index];
                ClipAnimation.KeepRange(normalized, skip, duration);
                normalized.SourceInSeconds += skip; normalized.StartSeconds = 0;
                normalized.DurationSeconds = duration;
                normalized.GroupId = null; normalized.LinkGroupId = null;
                group.Angles.Add(new MulticamAngle { Name = normalized.Name, SourceClip = normalized });
            }
            TimelineClip cut = MakeMulticamCut(group, group.Angles[0], start, 0, duration);
            return await McpOnUiAsync<object?>(() =>
            {
                token.ThrowIfCancellationRequested();
                McpRequireCurrent(owner, revision);
                TimelineTrack destination = McpTrack(sources[0].TrackId);
                if (destination.Clips.Any(clip => !ids.Contains(clip.Id) &&
                    clip.StartSeconds < start + duration && clip.StartSeconds + clip.DurationSeconds > start))
                    throw McpFeatureError("多机位目标区间已有其他对象。");
                McpMutate("MCP 创建多机位", () =>
                {
                    foreach (var source in sources)
                    {
                        TimelineTrack track = McpTrack(source.TrackId); McpFeatureEditable(track);
                        track.Clips.RemoveAll(clip => clip.Id == source.Clip.Id);
                    }
                    _project.MulticamGroups.Add(group); destination.Clips.Add(cut);
                    SelectClip(cut);
                });
                return new { groupId = group.Id, clipId = cut.Id, angleIds = group.Angles.Select(angle => angle.Id).ToArray() };
            });
        });
    }

    private object McpMulticamSwitch(JsonObject args)
    {
        TimelineClip clip = McpClip(McpString(args, "clipId"));
        TimelineTrack track = McpFeatureTrackFor(clip); McpFeatureEditable(track);
        McpFeatureNormalSpeed(clip);
        MulticamGroup group = _project.MulticamGroups.FirstOrDefault(item => item.Id == clip.MulticamGroupId)
            ?? throw McpFeatureError("片段不属于多机位组。");
        MulticamAngle angle = group.Angles.FirstOrDefault(item => item.Id == McpString(args, "angleId"))
            ?? throw McpFeatureError("机位不存在。");
        if (clip.MulticamAngleId == angle.Id)
            return new { clipId = clip.Id, angleId = angle.Id, clip.StartSeconds };
        double requested = McpDouble(args, "timeSeconds", _project.Sequence.PlayheadSeconds);
        double frame = 1 / _project.Sequence.FramesPerSecond;
        if (requested < clip.StartSeconds || requested >= clip.StartSeconds + clip.DurationSeconds)
            throw McpFeatureError("切换时间必须落在指定多机位片段中。");
        double left = Math.Floor((requested - clip.StartSeconds) / frame + 1e-9) * frame;
        double right = clip.DurationSeconds - left;
        if (right < frame - 1e-6 || clip.MulticamLocalStartSeconds + left + right > group.DurationSeconds + .001)
            throw McpFeatureError("切换位置超出机位共享范围。");
        TimelineClip replacement = MakeMulticamCut(group, angle, clip.StartSeconds + left,
            clip.MulticamLocalStartSeconds + left, right);
        McpMutate("MCP 切换机位", () =>
        {
            if (left < frame - 1e-6) track.Clips.Remove(clip);
            else { ClipAnimation.KeepRange(clip, 0, left); clip.DurationSeconds = left; }
            track.Clips.Add(replacement); SelectClip(replacement);
        });
        return new { clipId = replacement.Id, angleId = angle.Id, replacement.StartSeconds };
    }

    private async Task<object> McpVoiceoverStartAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (_mcpFeaturesDisposed) throw McpFeatureError("编辑窗口正在关闭。");
        if (_mcpRecorder is not null || _mcpRecordingBusy)
            throw McpFeatureError("已经有 MCP 录音正在进行。");
        string trackId = McpString(args, "trackId", "");
        if (trackId.Length > 0)
        {
            TimelineTrack track = McpTrack(trackId); McpFeatureEditable(track);
            if (track.Kind != TrackKind.Audio) throw McpFeatureError("画外音需要音频轨道。");
        }
        double start = McpDouble(args, "startSeconds", _project.Sequence.PlayheadSeconds);
        if (!double.IsFinite(start) || start < 0 || start >= 24 * 3600)
            throw McpFeatureError("录音插入时间无效。");
        uint deviceId = checked((uint)McpInt(args, "deviceId"));
        ProjectDocument owner = _project;
        var recorder = new WaveInRecorder();
        _mcpRecordingBusy = true;
        try
        {
            await Task.Run(() => recorder.Start(deviceId), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_mcpFeaturesDisposed) throw McpFeatureError("编辑窗口正在关闭，录音已中止。");
            _mcpRecorder = recorder; _mcpRecordingProject = owner;
            _mcpRecordingTrackId = trackId; _mcpRecordingStart = start;
            StatusText.Text = "MCP 正在录制画外音";
            return new { recording = true, deviceId, startSeconds = start };
        }
        catch { recorder.Dispose(); throw; }
        finally { _mcpRecordingBusy = false; }
    }

    private async Task<object> McpVoiceoverStopAsync(JsonObject args, CancellationToken cancellationToken)
    {
        string path = McpFeatureOutputPath(args, ".wav");
        WaveInRecorder recorder = _mcpRecorder ?? throw McpFeatureError("没有正在进行的 MCP 录音。");
        _mcpRecorder = null;
        _mcpRecordingBusy = true;
        try
        {
            byte[] samples = await Task.Run(recorder.Stop, cancellationToken);
            double duration = samples.Length / 96000d;
            await EnsureVoiceOverSpaceAsync(path, samples.LongLength);
            await WaveInRecorder.WriteWaveAsync(path, samples, cancellationToken);
            if (!McpBool(args, "insert", true)) return new { path, durationSeconds = duration, inserted = false };
            if (!ReferenceEquals(_mcpRecordingProject, _project))
                throw McpFeatureError("录音已保存；项目已切换，未插入时间线。");
            if (duration < .01 || _mcpRecordingStart + duration > 24 * 3600)
                throw McpFeatureError("录音已保存；时长不足或超出时间线范围。");
            string[] projected = _project.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
                .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Append(path).ToArray();
            if (_projectPath is string projectPath)
                await ProjectFileService.EnsureImportCapacityAsync(projectPath, projected, cancellationToken);
            else await ProjectFileService.EnsureImportCapacityAsync(projected, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_mcpRecordingProject, _project))
                throw McpFeatureError("录音已保存；空间检查期间项目已切换，未插入时间线。");
            var asset = new MediaAsset
            {
                Name = Path.GetFileName(path), Kind = MediaKind.Audio, SourcePath = path,
                DurationSeconds = duration, HasAudio = true, AudioChannels = 1,
                AudioSampleRate = 48000, AudioCodec = "pcm_s16le", FileSizeBytes = samples.LongLength + 44
            };
            var clip = new TimelineClip
            {
                Name = asset.Name, Kind = ClipKind.Audio, MediaAssetId = asset.Id,
                StartSeconds = _mcpRecordingStart, DurationSeconds = duration
            };
            McpMutate("MCP 插入画外音", () =>
            {
                TimelineTrack? track = _mcpRecordingTrackId is { Length: > 0 } id
                    ? McpTrack(id) : _project.Tracks.FirstOrDefault(item => item.Kind == TrackKind.Audio && !item.IsLocked);
                if (track is null)
                {
                    track = new TimelineTrack { Name = "画外音", Kind = TrackKind.Audio };
                    _project.Tracks.Add(track); McpFeatureReorderTracks();
                }
                McpFeatureEditable(track);
                if (track.Kind != TrackKind.Audio)
                    throw McpFeatureError("录音已保存；目标轨道已不再是音频轨道，未插入时间线。");
                _project.Media.Add(asset); track.Clips.Add(clip); SelectClip(clip);
            });
            return new { path, durationSeconds = duration, inserted = true, assetId = asset.Id, clipId = clip.Id };
        }
        finally { recorder.Dispose(); _mcpRecordingBusy = false; _mcpRecordingProject = null; }
    }

    private object McpRecognizeStart(JsonObject args)
    {
        MediaAsset asset = _project.Media.FirstOrDefault(item => item.Id == McpString(args, "assetId"))
            ?? throw McpFeatureError("语音转写素材不存在。");
        string mediaPath = asset.EffectiveExportPath;
        string modelPath = Path.GetFullPath(McpString(args, "modelPath"));
        string whisperPath = Path.GetFullPath(McpString(args, "whisperPath",
            Path.Combine(AppContext.BaseDirectory, "whisper", "whisper.exe")));
        if (!File.Exists(modelPath) || !File.Exists(whisperPath))
            throw McpFeatureError("本地 Whisper 可执行文件或模型不存在。");
        string language = McpString(args, "language", "zh");
        ProjectDocument owner = _project;
        long revision = _projectRevision;
        JsonObject insertArgs = (JsonObject)args.DeepClone();
        return McpStartJob("本地语音转写", async (progress, token) =>
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"tangerine-mcp-speech-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporary);
            try
            {
                string wav = Path.Combine(temporary, "audio.wav"), prefix = Path.Combine(temporary, "transcript");
                await McpFeatureProcessAsync(TimelineExporter.ResolveEncoderPath(),
                    ["-v", "error", "-i", mediaPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-y", wav], token);
                progress.Report(.15);
                // Whisper's SRT serializer is its public timestamp contract.
                // Parsing that output avoids fabricating word timings or
                // depending on incompatible JSON timestamp representations.
                await McpFeatureProcessAsync(whisperPath,
                    ["-m", modelPath, "-l", language, "-f", wav, "-osrt", "-of", prefix], token);
                IReadOnlyList<SubtitleCue> cues = await SubtitleService.ReadSrtAsync(prefix + ".srt", token);
                token.ThrowIfCancellationRequested();
                return await McpOnUiAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    McpRequireCurrent(owner, revision);
                    return McpInsertSubtitles(cues, insertArgs);
                });
            }
            finally { McpFeatureDeleteDirectory(temporary); }
        });
    }

    private object McpMediaAnalyze(JsonObject args)
    {
        MediaAsset asset = _project.Media.FirstOrDefault(item => item.Id == McpString(args, "assetId"))
            ?? throw McpFeatureError("分析素材不存在。");
        string path = asset.EffectiveExportPath;
        string kind = McpString(args, "kind");
        JsonObject options = args["options"] as JsonObject ?? new JsonObject();
        double start = McpDouble(options, "startSeconds");
        double duration = McpDouble(options, "durationSeconds", Math.Min(600, asset.DurationSeconds ?? 60));
        if (!double.IsFinite(start) || start < 0 || !double.IsFinite(duration) || duration <= 0 || duration > 600)
            throw McpFeatureError("分析范围无效；每次最多分析 600 秒。");
        if (kind is not ("loudness" or "waveform" or "sceneCuts" or "tempo"))
            throw McpFeatureError("分析类型应为 loudness、waveform、sceneCuts 或 tempo。");
        double threshold = McpDouble(options, "threshold", .3);
        int bins = McpInt(options, "bins", 320);
        if (!double.IsFinite(threshold) || threshold is < .01 or > 1 || bins is < 8 or > 2000)
            throw McpFeatureError("场景阈值需要在 0.01 至 1，波形分箱需要在 8 至 2000。");
        return McpStartJob("分析媒体", async (progress, token) =>
        {
            if (kind == "tempo")
                return new { assetId = asset.Id, kind, bpm = await AudioWaveformAnalysis.EstimateTempoAsync(path, start, duration, token) };
            string[] input = ["-hide_banner", "-nostdin", "-ss", start.ToString("R", CultureInfo.InvariantCulture),
                "-t", duration.ToString("R", CultureInfo.InvariantCulture), "-i", path];
            if (kind == "loudness")
            {
                ProcessResult result = await McpFeatureProcessAsync(TimelineExporter.ResolveEncoderPath(),
                    [.. input, "-vn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", "-"], token);
                int begin = result.StandardError.LastIndexOf('{');
                int end = result.StandardError.LastIndexOf('}');
                if (begin < 0 || end <= begin) throw McpFeatureError("编码器没有返回响度分析数据。");
                JsonNode? loudness = JsonNode.Parse(result.StandardError[begin..(end + 1)]);
                return new { assetId = asset.Id, kind, startSeconds = start, durationSeconds = duration, loudness };
            }
            if (kind == "sceneCuts")
            {
                ProcessResult result = await McpFeatureProcessAsync(TimelineExporter.ResolveEncoderPath(),
                    [.. input, "-an", "-vf", $"select=gt(scene\\,{threshold.ToString("R", CultureInfo.InvariantCulture)}),metadata=print:file=-", "-f", "null", "-"], token);
                // FFmpeg emits a frame header followed by a separate score
                // line. Retain that header until its metadata arrives.
                var cuts = new List<object>();
                double? time = null;
                foreach (string line in result.StandardOutput.Split('\n'))
                {
                    Match header = Regex.Match(line, @"pts_time:(?<time>[-+0-9.eE]+)", RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                    if (header.Success && double.TryParse(header.Groups["time"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double current)) time = current;
                    Match score = Regex.Match(line, @"lavfi.scene_score=(?<score>[-+0-9.eE]+)", RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                    if (score.Success && time.HasValue && double.TryParse(score.Groups["score"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    {
                        cuts.Add(new { timeSeconds = start + time.Value, score = value });
                        time = null;
                    }
                }
                return new { assetId = asset.Id, kind, startSeconds = start, durationSeconds = duration, cuts };
            }
            string temporary = Path.Combine(Path.GetTempPath(), $"tangerine-mcp-wave-{Guid.NewGuid():N}.pcm");
            try
            {
                await McpFeatureProcessAsync(TimelineExporter.ResolveEncoderPath(),
                    [.. input, "-vn", "-ac", "1", "-ar", "8000", "-f", "f32le", "-y", temporary], token);
                byte[] pcm = await File.ReadAllBytesAsync(temporary, token);
                int samples = pcm.Length / sizeof(float);
                if (samples == 0) throw McpFeatureError("素材中没有可分析的音频样本。");
                var envelope = new List<object>(bins);
                for (int bin = 0; bin < bins; bin++)
                {
                    token.ThrowIfCancellationRequested();
                    int first = (int)((long)samples * bin / bins), last = (int)((long)samples * (bin + 1) / bins);
                    double peak = 0, energy = 0;
                    for (int index = first; index < last; index++)
                    {
                        float sample = BitConverter.ToSingle(pcm, index * sizeof(float));
                        peak = Math.Max(peak, Math.Abs(sample)); energy += sample * sample;
                    }
                    envelope.Add(new { timeSeconds = start + first / 8000d,
                        peak, rms = Math.Sqrt(energy / Math.Max(1, last - first)) });
                }
                progress.Report(1);
                return new { assetId = asset.Id, kind, startSeconds = start, durationSeconds = samples / 8000d, envelope };
            }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        });
    }

    private static async Task<ProcessResult> McpFeatureProcessAsync(string executable, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw McpFeatureError("无法启动本地处理程序。");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token);
            await Task.WhenAll(output, error);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            string detail = (await error).Trim();
            throw new StageException("MCPRT0008", $"本地处理失败（{process.ExitCode}）：{detail[^Math.Min(1500, detail.Length)..]}");
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private TimelineTrack McpFeatureTextTrack(JsonObject args, double start, double duration)
    {
        string id = McpString(args, "trackId", "");
        if (id.Length == 0) return GetOrCreateTextTrack(start, duration);
        TimelineTrack track = McpTrack(id); McpFeatureEditable(track);
        if (track.Kind == TrackKind.Audio) throw McpFeatureError("文字和图形需要视频轨道。");
        return track;
    }

    private TimelineTrack McpFeatureTrackFor(TimelineClip clip) =>
        _project.Tracks.FirstOrDefault(track => track.Clips.Any(item => item.Id == clip.Id))
        ?? throw McpFeatureError("片段所属轨道不存在。");

    private MediaAsset McpFeatureAssetFor(TimelineClip clip) =>
        _project.Media.FirstOrDefault(asset => asset.Id == clip.MediaAssetId)
        ?? throw McpFeatureError("片段的源媒体不存在。");

    private static void McpFeatureEditable(TimelineTrack track)
    {
        if (track.IsLocked) throw McpFeatureError("目标轨道已锁定。");
    }

    private static void McpFeatureNormalSpeed(TimelineClip clip)
    {
        if (clip.Kind is not (ClipKind.Audio or ClipKind.Video) || clip.IsReversed ||
            Math.Abs(clip.Speed - 1) > .000001 || clip.TimeRemapPoints.Count > 0)
            throw McpFeatureError("此操作需要正向、原速的音视频片段。");
    }

    private static double McpFeatureTimecodeDelta(TimelineClip reference, MediaAsset first,
        TimelineClip moving, MediaAsset second)
    {
        if (!TrySourceTimecode(first, out double firstTime, out int firstRate, out bool firstDrop) ||
            !TrySourceTimecode(second, out double secondTime, out int secondRate, out bool secondDrop) ||
            firstRate != secondRate || firstDrop != secondDrop ||
            Math.Abs(first.FramesPerSecond.GetValueOrDefault() - second.FramesPerSecond.GetValueOrDefault()) > .01)
            throw McpFeatureError("两个源文件需要相同帧率和丢帧方式的有效 timecode。");
        return secondTime + moving.SourceInSeconds - firstTime - reference.SourceInSeconds;
    }

    private ProjectDocument McpFeatureSnapshot() =>
        JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(_project))
        ?? throw McpFeatureError("无法复制项目快照。");

    private void McpRequireCurrent(ProjectDocument owner, long revision)
    {
        if (!ReferenceEquals(_project, owner) || _projectRevision != revision)
            throw new StageException("MCPRT0003", "操作期间项目已改变；结果未写入，请基于最新状态重试。");
    }

    private string McpFeatureOutputPath(JsonObject args, string extension)
    {
        string path = Path.GetFullPath(McpString(args, "path"));
        if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw McpFeatureError($"输出文件需要 {extension} 扩展名。");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw McpFeatureError("输出目录不存在。");
        IEnumerable<string> protectedPaths = _project.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>();
        if (_projectPath is not null) protectedPaths = protectedPaths.Append(_projectPath);
        if (protectedPaths.Any(value => Path.GetFullPath(value).Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw McpFeatureError("输出不能覆盖项目或源媒体文件。");
        return path;
    }

    private static string McpExchangeFormat(JsonObject args)
    {
        string format = McpString(args, "format", Path.GetExtension(McpString(args, "path")).TrimStart('.')).ToLowerInvariant();
        return format is "xml" or "edl" or "otio" or "aafxml" ? format
            : throw McpFeatureError("交换格式支持 xml、edl、otio 或 aafXml（仅导出）。");
    }

    private static async Task McpFeatureExportOtioAsync(ProjectDocument project, string path, CancellationToken token)
    {
        await OtioInterchange.ExportOtioAsync(project, path, token);
        JsonObject timeline = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        JsonObject metadata = timeline["metadata"] as JsonObject ?? new JsonObject();
        if (timeline["metadata"] is null) timeline["metadata"] = metadata;
        // Standard OTIO fields remain usable by other editors. The optional
        // metadata copy preserves every Tangerine-only title, mask, effect,
        // nested sequence and multicamera parameter on a local round trip.
        metadata["tangerine_project"] = JsonSerializer.SerializeToNode(project);
        metadata["tangerine_project_schema"] = ProjectDocument.CurrentVersion;
        token.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(path, timeline.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static Task<ProjectDocument> McpFeatureImportOtioAsync(string path, CancellationToken token) =>
        Task.Run(async () =>
        {
            var info = new FileInfo(path);
            if (info.Length > 32L * 1024 * 1024) throw McpFeatureError("OTIO 文件超过 32 MB。");
            JsonObject timeline = JsonNode.Parse(await File.ReadAllTextAsync(path, token)) as JsonObject
                ?? throw McpFeatureError("OTIO 顶层必须是时间线对象。");
            if (timeline["OTIO_SCHEMA"]?.GetValue<string>()?.StartsWith("Timeline.", StringComparison.Ordinal) != true)
                throw McpFeatureError("OTIO 文件不包含受支持的 Timeline。");
            if (timeline["metadata"]?["tangerine_project"] is JsonObject preserved)
                return preserved.Deserialize<ProjectDocument>() ?? throw McpFeatureError("OTIO 项目元数据无效。");
            JsonArray tracks = timeline["tracks"]?["children"] as JsonArray
                ?? throw McpFeatureError("OTIO 时间线缺少轨道数组。");
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var imported = new ProjectDocument { Name = Path.GetFileNameWithoutExtension(path) };
            imported.Sequence.Name = timeline["name"]?.GetValue<string>() ?? imported.Name;
            double fps = timeline["global_start_time"]?["rate"]?.GetValue<double>() ?? 30;
            if (!double.IsFinite(fps) || fps <= 0 || fps > 120) throw McpFeatureError("OTIO 时间线帧率无效。");
            imported.Sequence.FramesPerSecond = fps;
            var assets = new Dictionary<string, MediaAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonNode? trackNode in tracks)
            {
                token.ThrowIfCancellationRequested();
                if (trackNode is not JsonObject sourceTrack ||
                    sourceTrack["OTIO_SCHEMA"]?.GetValue<string>()?.StartsWith("Track.", StringComparison.Ordinal) != true)
                    throw McpFeatureError("目前支持 OTIO 的平面 Track 轨道，请先在来源软件展开嵌套 Stack。");
                var track = new TimelineTrack
                {
                    Name = sourceTrack["name"]?.GetValue<string>() ?? "OTIO 轨道",
                    Kind = string.Equals(sourceTrack["kind"]?.GetValue<string>(), "Audio", StringComparison.OrdinalIgnoreCase)
                        ? TrackKind.Audio : TrackKind.Video,
                    Order = imported.Tracks.Count
                };
                imported.Tracks.Add(track);
                double cursor = 0;
                JsonArray children = sourceTrack["children"] as JsonArray ?? new JsonArray();
                foreach (JsonNode? node in children)
                {
                    token.ThrowIfCancellationRequested();
                    if (node is not JsonObject source) throw McpFeatureError("OTIO 轨道内容必须为对象。");
                    string schema = source["OTIO_SCHEMA"]?.GetValue<string>() ?? "";
                    JsonNode? range = source["source_range"] ?? source["media_reference"]?["available_range"];
                    double duration = McpFeatureOtioTime(range?["duration"]);
                    if (!double.IsFinite(duration) || duration <= 0) throw McpFeatureError("OTIO 对象缺少有效的持续时长。");
                    if (schema.StartsWith("Gap.", StringComparison.Ordinal)) { cursor += duration; continue; }
                    if (!schema.StartsWith("Clip.", StringComparison.Ordinal))
                        throw McpFeatureError($"OTIO 对象 {schema} 需要先在来源软件展开为 Clip 或 Gap。");
                    JsonObject reference = source["media_reference"] as JsonObject
                        ?? throw McpFeatureError("OTIO 片段缺少媒体引用。");
                    var clip = new TimelineClip
                    {
                        Name = source["name"]?.GetValue<string>() ?? "OTIO 片段", StartSeconds = cursor,
                        SourceInSeconds = McpFeatureOtioTime(range?["start_time"]), DurationSeconds = duration,
                        Opacity = source["metadata"]?["tangerine_opacity"]?.GetValue<double>() ?? 1
                    };
                    string referenceSchema = reference["OTIO_SCHEMA"]?.GetValue<string>() ?? "";
                    if (referenceSchema.StartsWith("ExternalReference.", StringComparison.Ordinal))
                    {
                        string target = reference["target_url"]?.GetValue<string>() ?? throw McpFeatureError("OTIO 外部媒体引用缺少路径。");
                        string localPath;
                        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri))
                        {
                            if (!uri.IsFile) throw McpFeatureError("OTIO 仅支持本地文件媒体引用。");
                            localPath = uri.LocalPath;
                        }
                        else localPath = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(target)));
                        bool image = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff" }
                            .Contains(Path.GetExtension(localPath), StringComparer.OrdinalIgnoreCase);
                        MediaKind kind = track.Kind == TrackKind.Audio ? MediaKind.Audio : image ? MediaKind.Image : MediaKind.Video;
                        string assetKey = kind + ":" + localPath;
                        if (!assets.TryGetValue(assetKey, out MediaAsset? asset))
                        {
                            asset = new MediaAsset { Name = reference["name"]?.GetValue<string>() ?? Path.GetFileName(localPath),
                                Kind = kind, SourcePath = localPath, HasAudio = kind == MediaKind.Audio };
                            assets.Add(assetKey, asset); imported.Media.Add(asset);
                        }
                        clip.MediaAssetId = asset.Id;
                        clip.Kind = kind == MediaKind.Audio ? ClipKind.Audio : kind == MediaKind.Image ? ClipKind.Image : ClipKind.Video;
                    }
                    else if (referenceSchema.StartsWith("GeneratorReference.", StringComparison.Ordinal) &&
                        reference["generator_kind"]?.GetValue<string>() == "Text")
                    {
                        clip.Kind = ClipKind.Text;
                        clip.Text = CreateSubtitleStyle(reference["parameters"]?["text"]?.GetValue<string>() ?? "");
                        clip.Text.FontFamily = reference["parameters"]?["font_family"]?.GetValue<string>() ?? clip.Text.FontFamily;
                        clip.Text.FontSize = reference["parameters"]?["font_size"]?.GetValue<double>() ?? clip.Text.FontSize;
                        clip.SourceInSeconds = 0;
                    }
                    else throw McpFeatureError($"OTIO 媒体引用 {referenceSchema} 不受支持；没有导入不可预览的占位片段。");
                    track.Clips.Add(clip);
                    cursor += duration;
                }
            }
            return imported;
        }, token);

    private static double McpFeatureOtioTime(JsonNode? time)
    {
        if (time is null) return 0;
        double value = time["value"]?.GetValue<double>() ?? 0;
        double rate = time["rate"]?.GetValue<double>() ?? 0;
        if (!double.IsFinite(value) || !double.IsFinite(rate) || rate <= 0)
            throw McpFeatureError("OTIO RationalTime 数值或帧率无效。");
        return value / rate;
    }

    private void McpFeatureReorderTracks()
    {
        for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index;
    }

    private static StageException McpFeatureError(string message) => new("MCPRT0008", message);

    private static void McpFeatureDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Debug.WriteLine($"MCPRT0008 temporary cleanup: {exception.Message}"); }
    }

    private void DisposeMcpFeatures()
    {
        _mcpFeaturesDisposed = true;
        McpCancelVoiceover();
    }

    private void McpCancelVoiceover()
    {
        _mcpRecorder?.Dispose();
        _mcpRecorder = null; _mcpRecordingProject = null;
    }
}
