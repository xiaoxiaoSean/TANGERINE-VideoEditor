using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.SystemIntegration;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Structured editing commands use stable model IDs, explicit paths and typed
/// patches instead of interacting with modal dialogs. All serializable model
/// fields remain available through schema discovery and an atomic project patch.
/// </summary>
public partial class MainWindow
{
    private async Task<object?> ExecuteMcpCommandAsync(string command, JsonObject args, CancellationToken token)
    {
        switch (command)
        {
            case "editor_get_state": return McpState(McpBool(args, "includeProject", true));
            case "editor_wait_for_change":
            {
                UpdateMcpInfo();
                long after = args["afterUiRevision"]?.GetValue<long>() ?? _mcpUiRevision;
                int timeout = McpInt(args, "timeoutMilliseconds", 10000);
                long deadline = Environment.TickCount64 + timeout;
                while (_mcpUiRevision <= after && Environment.TickCount64 < deadline)
                {
                    await Task.Delay((int)Math.Min(50, Math.Max(1, deadline - Environment.TickCount64)), token);
                    UpdateMcpInfo();
                }
                return new { changed = _mcpUiRevision > after, state = McpState(false) };
            }
            case "model_get_schema": return JsonModelTools.SchemaFor(McpModelType(McpString(args, "model", "project")));
            case "editor_select":
            {
                TimelineClip[] clips = args["clipIds"] is JsonArray ? McpStrings(args, "clipIds").Select(McpClip).Distinct().ToArray() : [];
                TimelineTrack? track = args["trackId"] is null ? clips.FirstOrDefault() is TimelineClip first ? McpOwner(first) : null : McpTrack(McpString(args, "trackId"));
                if (clips.Length > 0 && !ReferenceEquals(track, McpOwner(clips[0])))
                    throw new StageException("MCPRT0008", "trackId 必须是首个所选片段所在的轨道。");
                string? assetId = args["assetId"]?.GetValue<string>();
                if (assetId is not null && !_project.Media.Any(asset => asset.Id == assetId))
                    throw new StageException("MCPRT0009", "素材不存在：" + assetId);
                ClearTimelineSelection();
                foreach (var clip in clips) _selectedClips.Add(clip);
                _selectedClip = clips.FirstOrDefault();
                _selectedTrack = track;
                if (assetId is not null)
                {
                    CategoryList.SelectedIndex = 0; RefreshLibrary();
                    AssetList.SelectedItem = AssetList.Items.Cast<object>().OfType<LibraryItem>().FirstOrDefault(item => item.Id == assetId);
                }
                DrawTimeline(); DrawTextOverlay();
                if (_selectedClip is not null) ShowSelectedObjectSettings();
                return McpState(false);
            }
            case "project_new":
            case "project_close":
            {
                McpDiscardAllowed(args); McpEnsureIdle();
                ProjectDocument previous = _project;
                _project = ProjectDocument.CreateDefault(); _project.Name = McpString(args, "name", "未命名项目");
                _projectPath = null; _projectCreated = command == "project_new"; _isProjectDirty = false;
                _projectGeneration++; _projectRevision++; ResetEditorSession();
                RefreshMcpUi("新建项目"); _ = ReleaseProjectCacheSafelyAsync(previous);
                return McpState();
            }
            case "project_open":
            {
                McpDiscardAllowed(args); McpEnsureIdle();
                long revision = _projectRevision; ProjectDocument previous = _project;
                string path = Path.GetFullPath(McpString(args, "path"));
                ProjectDocument loaded = await ProjectFileService.LoadAsync(path, token);
                if (token.IsCancellationRequested) { await ProjectFileService.ReleaseCachedMediaAsync(loaded); token.ThrowIfCancellationRequested(); }
                if (_projectRevision != revision || !ReferenceEquals(previous, _project))
                { await ProjectFileService.ReleaseCachedMediaAsync(loaded); throw new StageException("MCPRT0003", "加载期间项目已修改，请重新打开。"); }
                _project = loaded; _projectPath = path; _projectCreated = true; _isProjectDirty = false;
                _projectGeneration++; _projectRevision++; ResetEditorSession(); RefreshMcpUi("已打开项目");
                _ = ReleaseProjectCacheSafelyAsync(previous); return McpState();
            }
            case "project_save":
            {
                McpEnsureIdle(); string path = Path.GetFullPath(McpString(args, "path", _projectPath));
                if (!path.EndsWith(".tvep", StringComparison.OrdinalIgnoreCase)) throw new StageException("MCPRT0008", "项目路径必须以 .tvep 结尾。");
                long revision = _projectRevision; ProjectDocument source = _project;
                ProjectDocument snapshot = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(source))!;
                snapshot.Name = Path.GetFileNameWithoutExtension(path);
                await ProjectFileService.SaveAsync(snapshot, path, token);
                bool unchanged = ReferenceEquals(source, _project) && _projectRevision == revision;
                if (unchanged) { _projectPath = path; _project.Name = snapshot.Name; _isProjectDirty = false; RefreshProjectChrome(); }
                return new { path, saved = true, currentSaved = unchanged, revision };
            }
            case "project_validate": McpValidate(_project); return new { valid = true };
            case "project_patch":
            {
                ProjectDocument candidate = JsonModelTools.Patch(_project, args["operations"]!.AsArray());
                // A generic patch is an explicit full-model operation. Locked
                // tracks still require a separate unlock before modifying them.
                foreach (TimelineTrack locked in _project.Tracks.Concat(_project.Sequences.SelectMany(sequence => sequence.Tracks)).Where(track => track.IsLocked))
                    if (!JsonNode.DeepEquals(JsonModelTools.ToNode(locked), JsonModelTools.ToNode(candidate.Tracks.Concat(candidate.Sequences.SelectMany(sequence => sequence.Tracks)).FirstOrDefault(track => track.Id == locked.Id))))
                        throw new StageException("MCPRT0010", "补丁修改了锁定轨道，请先解锁。");
                return McpMutate("项目补丁", () => { ProjectFileService.TransferCachedMedia(_project, candidate); _project = candidate; _projectGeneration++; });
            }
            case "history_undo": return McpHistory(_undoStates, _redoStates, "撤销");
            case "history_redo": return McpHistory(_redoStates, _undoStates, "重做");
            case "sequence_list": return new[] { new ProjectSequenceWorkspace { Sequence = _project.Sequence, Tracks = _project.Tracks, Markers = _project.Markers } }.Concat(_project.Sequences).ToArray();
            case "sequence_create":
            {
                var sequence = new ProjectSequence { Name = McpString(args, "name", "新序列"), Width = McpInt(args, "width", 1920),
                    Height = McpInt(args, "height", 1080), FramesPerSecond = McpDouble(args, "framesPerSecond", 30) };
                McpMutate("创建序列", () => _project.Sequences.Add(new() { Sequence = sequence, Tracks = DefaultSequenceTracks() })); return sequence;
            }
            case "sequence_select":
            {
                string id = McpString(args, "sequenceId");
                if (id != _project.Sequence.Id) McpMutate("切换序列", () => McpSwitchSequence(id));
                return McpState();
            }
            case "sequence_update":
            {
                string id = McpString(args, "sequenceId", _project.Sequence.Id);
                return McpMutate("修改序列", () =>
                {
                    ProjectSequenceWorkspace workspace = _project.FindSequence(id) ?? throw new StageException("MCPRT0009", "序列不存在。");
                    ProjectSequence updated = JsonModelTools.Merge(workspace.Sequence, McpObject(args, "patch"));
                    if (updated.Id != id) throw new StageException("MCPRT0008", "不能修改序列 ID。");
                    if (id == _project.Sequence.Id) _project.Sequence = updated; else workspace.Sequence = updated;
                });
            }
            case "sequence_remove": return McpMutate("删除序列", () =>
            {
                string id = McpString(args, "sequenceId");
                if (_project.Tracks.Concat(_project.Sequences.SelectMany(item => item.Tracks)).SelectMany(track => track.Clips).Any(clip => clip.NestedSequenceId == id))
                    throw new StageException("MCPRT0008", "序列仍被嵌套片段引用。");
                if (id == _project.Sequence.Id)
                {
                    var other = _project.Sequences.FirstOrDefault() ?? throw new StageException("MCPRT0008", "不能删除最后一个序列。");
                    McpSwitchSequence(other.Sequence.Id);
                }
                if (_project.Sequences.RemoveAll(sequence => sequence.Sequence.Id == id) == 0) throw new StageException("MCPRT0009", "序列不存在。");
            });
            case "sequence_nest":
            {
                var workspace = _project.FindSequence(McpString(args, "sequenceId")) ?? throw new StageException("MCPRT0009", "序列不存在。");
                TimelineTrack track = McpTrack(McpString(args, "trackId")); McpWritable(track);
                var clip = new TimelineClip { Kind = ClipKind.Sequence, NestedSequenceId = workspace.Sequence.Id, Name = workspace.Sequence.Name,
                    StartSeconds = McpDouble(args, "startSeconds"), DurationSeconds = Math.Max(1 / _project.Sequence.FramesPerSecond,
                        workspace.Tracks.SelectMany(item => item.Clips).Select(item => item.StartSeconds + item.DurationSeconds).DefaultIfEmpty(5).Max()) };
                McpMutate("嵌套序列", () => track.Clips.Add(clip)); return clip;
            }
            case "media_list": return _project.Media;
            case "media_import": return await McpImportAsync(args, token);
            case "media_image_sequence": return McpImageSequence(args);
            case "media_update": return McpUpdateList(_project.Media, McpString(args, "assetId"), McpObject(args, "patch"), item => item.Id, "修改素材");
            case "media_relink":
            {
                string path = Path.GetFullPath(McpString(args, "path"));
                if (!await Task.Run(() => File.Exists(path), token)) throw new StageException("MCPRT0008", "源文件不存在。");
                return McpUpdateList(_project.Media, McpString(args, "assetId"), new JsonObject { ["sourcePath"] = path }, item => item.Id, "重新链接素材");
            }
            case "media_remove": return McpMutate("移除素材", () =>
            {
                string id = McpString(args, "assetId");
                var tracks = _project.Tracks.Concat(_project.Sequences.SelectMany(sequence => sequence.Tracks)).ToArray();
                if (!McpBool(args, "removeClips") && tracks.SelectMany(track => track.Clips).Any(clip => clip.MediaAssetId == id))
                    throw new StageException("MCPRT0008", "素材仍被片段引用，请设置 removeClips。");
                foreach (var track in tracks.Where(track => track.Clips.Any(clip => clip.MediaAssetId == id)))
                { McpWritable(track); track.Clips.RemoveAll(clip => clip.MediaAssetId == id); }
                _project.Subclips.RemoveAll(item => item.ParentAssetId == id);
                if (_project.Media.RemoveAll(item => item.Id == id) == 0) throw new StageException("MCPRT0009", "素材不存在。");
            });
            case "media_proxy": return McpProxy(args);
            case "bin_create":
            {
                var bin = new ProjectBin { Name = McpString(args, "name"), ParentId = args["parentId"]?.GetValue<string>() };
                McpMutate("创建素材箱", () => _project.Bins.Add(bin)); return bin;
            }
            case "bin_update": return McpUpdateList(_project.Bins, McpString(args, "binId"), McpObject(args, "patch"), item => item.Id, "修改素材箱");
            case "bin_remove": return McpMutate("删除素材箱", () =>
            {
                string id = McpString(args, "binId"); ProjectBin bin = _project.Bins.FirstOrDefault(item => item.Id == id) ?? throw new StageException("MCPRT0009", "素材箱不存在。");
                foreach (var asset in _project.Media.Where(item => item.BinId == id)) asset.BinId = bin.ParentId;
                foreach (var child in _project.Bins.Where(item => item.ParentId == id)) child.ParentId = bin.ParentId;
                _project.Bins.Remove(bin);
            });
            case "subclip_create":
            {
                var subclip = new ProjectSubclip { ParentAssetId = McpString(args, "assetId"), Name = McpString(args, "name", "子片段"),
                    InSeconds = McpDouble(args, "inSeconds"), OutSeconds = McpDouble(args, "outSeconds") };
                McpMutate("创建子片段", () => _project.Subclips.Add(subclip)); return subclip;
            }
            case "subclip_update": return McpUpdateList(_project.Subclips, McpString(args, "subclipId"), McpObject(args, "patch"), item => item.Id, "修改子片段");
            case "subclip_remove": return McpMutate("删除子片段", () => _project.Subclips.RemoveAll(item => item.Id == McpString(args, "subclipId")));
            case "track_create":
            {
                var track = new TimelineTrack { Name = McpString(args, "name", "新轨道"), Kind = Enum.Parse<TrackKind>(McpString(args, "kind", "Video"), true) };
                McpMutate("创建轨道", () => { _project.Tracks.Insert(Math.Clamp(McpInt(args, "order", _project.Tracks.Count), 0, _project.Tracks.Count), track); McpReorderTracks(); }); return track;
            }
            case "track_update": return McpUpdateList(_project.Tracks, McpString(args, "trackId"), McpObject(args, "patch"), item => item.Id, "修改轨道");
            case "track_remove": return McpMutate("删除轨道", () => { var track = McpTrack(McpString(args, "trackId")); McpWritable(track); _project.Tracks.Remove(track); McpReorderTracks(); });
            case "track_reorder": return McpMutate("调整轨道顺序", () =>
            {
                string[] ids = McpStrings(args, "trackIds");
                if (ids.Length != _project.Tracks.Count || ids.Distinct().Count() != ids.Length) throw new StageException("MCPRT0008", "trackIds 必须包含每条轨道且不重复。");
                _project.Tracks = ids.Select(McpTrack).ToList(); McpReorderTracks();
            });
            case "clip_create":
            {
                var clip = JsonModelTools.Merge(new TimelineClip(), McpObject(args, "clip"));
                TimelineTrack track = McpTrack(McpString(args, "trackId")); McpWritable(track);
                if (!IsCompatible(track, clip)) throw new StageException("MCPRT0008", "片段与轨道类型不兼容。");
                McpMutate("创建片段", () => track.Clips.Add(clip)); return clip;
            }
            case "clip_update":
            case "clip_trim":
            {
                var clip = McpClip(McpString(args, "clipId")); var track = McpOwner(clip); McpWritable(track);
                var patch = command == "clip_update" ? McpObject(args, "patch") : new JsonObject();
                if (command == "clip_trim") foreach (string key in new[] { "startSeconds", "durationSeconds", "sourceInSeconds" })
                    if (args.ContainsKey(key)) patch[key] = args[key]?.DeepClone();
                return McpUpdateList(track.Clips, clip.Id, patch, item => item.Id, "修改片段");
            }
            case "clip_remove": return McpMutate("删除片段", () =>
            {
                foreach (var clip in McpStrings(args, "clipIds").Select(McpClip).OrderByDescending(item => item.StartSeconds).ToArray())
                {
                    var track = McpOwner(clip); McpWritable(track); track.Clips.Remove(clip);
                    if (McpBool(args, "ripple")) foreach (var later in track.Clips.Where(item => item.StartSeconds >= clip.StartSeconds + clip.DurationSeconds)) later.StartSeconds -= clip.DurationSeconds;
                }
            });
            case "clip_move": return McpMove(args);
            case "clip_split":
            {
                var clip = McpClip(McpString(args, "clipId")); var track = McpOwner(clip); McpWritable(track);
                double time = McpDouble(args, "timeSeconds");
                if (time <= clip.StartSeconds || time >= clip.StartSeconds + clip.DurationSeconds) throw new StageException("MCPRT0008", "分割点必须位于片段内部。");
                TimelineClip? right = null;
                McpMutate("分割片段", () => { right = SplitForEdit(clip, time); track.Clips.Add(right); }); return new { leftId = clip.Id, rightId = right!.Id };
            }
            case "clip_duplicate":
            {
                var copies = new List<TimelineClip>();
                McpMutate("复制片段", () =>
                {
                    var originals = McpStrings(args, "clipIds").Select(McpClip).ToArray();
                    foreach (var clip in originals)
                    {
                        var track = args["trackId"] is null ? McpOwner(clip) : McpTrack(McpString(args, "trackId")); McpWritable(track);
                        var copy = CloneClip(clip); McpRenewClipIds(copy); copy.StartSeconds += McpDouble(args, "offsetSeconds", clip.DurationSeconds);
                        track.Clips.Add(copy); copies.Add(copy);
                    }
                }); return copies;
            }
            case "clip_group":
            case "clip_link": return McpMutate("编组或链接", () =>
            {
                string? id = McpBool(args, command == "clip_group" ? "grouped" : "linked", true) ? Guid.NewGuid().ToString("N") : null;
                foreach (var clip in McpStrings(args, "clipIds").Select(McpClip))
                { McpWritable(McpOwner(clip)); if (command == "clip_group") clip.GroupId = id; else clip.LinkGroupId = id; }
            });
            case "title_apply": return McpApplyTitle(args);
            case "title_style_update":
            case "title_motion_update":
            case "shape_update":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                if (clip.Kind != ClipKind.Text) throw new StageException("MCPRT0008", "请选择文字或图形片段。");
                return McpMutate("修改文字或图形", () =>
                {
                    clip.Text ??= new ClipText();
                    if (command == "title_style_update") clip.Text = JsonModelTools.Merge(clip.Text, McpObject(args, "patch"));
                    else if (command == "title_motion_update") clip.Text.Motion = JsonModelTools.Merge(clip.Text.Motion ?? new TitleMotion(), McpObject(args, "patch"));
                    else JsonModelTools.Merge(GraphicShape.FromClip(clip) ?? new GraphicShape(), McpObject(args, "patch")).Save(clip);
                });
            }
            case "effect_add":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip)); string id = McpString(args, "presetId");
                var effect = new ClipEffect { PresetId = id, Name = BuiltinCatalog.Effects.FirstOrDefault(item => item.Id == id)?.Name ?? id };
                if (args["parameters"] is JsonObject parameters) effect.Parameters = McpParameters(parameters);
                McpMutate("添加效果", () => clip.Effects.Add(effect)); return effect;
            }
            case "effect_update":
            case "effect_remove":
            case "effect_reorder":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                if (command == "effect_update") return McpUpdateList(clip.Effects, McpString(args, "effectId"), McpObject(args, "patch"), item => item.Id, "修改效果");
                return McpMutate("修改效果列表", () =>
                {
                    if (command == "effect_remove") McpRemoveById(clip.Effects, McpString(args, "effectId"), item => item.Id);
                    else { string[] ids = McpStrings(args, "effectIds"); McpRequirePermutation(ids, clip.Effects.Select(item => item.Id)); clip.Effects = ids.Select(id => clip.Effects.First(item => item.Id == id)).ToList(); }
                });
            }
            case "transition_add":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                if (!BoundaryCrossfade.IsVisible(clip)) throw new StageException("MCPRT0008", "只有可见对象可添加转场。");
                var transition = new ClipTransition { PresetId = McpString(args, "presetId"), Edge = McpString(args, "edge", "In"),
                    DurationSeconds = McpDouble(args, "durationSeconds", Math.Min(1, clip.DurationSeconds / 2)) };
                transition.Name = BuiltinCatalog.Transitions.FirstOrDefault(item => item.Id == transition.PresetId)?.Name ?? transition.PresetId;
                if (args["parameters"] is JsonObject parameters) transition.Parameters = McpParameters(parameters);
                McpMutate("添加转场", () => { clip.Transitions.RemoveAll(item => item.Edge == transition.Edge); clip.Transitions.Add(transition); }); return transition;
            }
            case "transition_update":
            case "transition_remove":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                if (command == "transition_update") return McpUpdateList(clip.Transitions, McpString(args, "transitionId"), McpObject(args, "patch"), item => item.Id, "修改转场");
                return McpMutate("移除转场", () => McpRemoveById(clip.Transitions, McpString(args, "transitionId"), item => item.Id));
            }
            case "keyframe_set":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                var key = JsonModelTools.Merge(new ClipKeyframe(), McpObject(args, "keyframe"));
                McpMutate("设置关键帧", () =>
                {
                    var previous = clip.Keyframes.FirstOrDefault(item => item.Id == key.Id || item.Property == key.Property && Math.Abs(item.TimeSeconds - key.TimeSeconds) < .000001);
                    if (previous is not null) { key.Id = previous.Id; clip.Keyframes.Remove(previous); } clip.Keyframes.Add(key);
                }); return key;
            }
            case "keyframe_remove":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                return McpMutate("删除关键帧", () => McpRemoveById(clip.Keyframes, McpString(args, "keyframeId"), item => item.Id));
            }
            case "remap_set":
            {
                var clip = McpClip(McpString(args, "clipId")); McpWritable(McpOwner(clip));
                return McpMutate("设置变速曲线", () => { clip.TimeRemapPoints = JsonModelTools.Deserialize<List<ClipTimeRemapPoint>>(args["points"]); clip.FrameInterpolation = McpString(args, "frameInterpolation", clip.FrameInterpolation); });
            }
            case "marker_create":
            {
                var marker = new TimelineMarker { Name = McpString(args, "name", "标记"), TimeSeconds = McpDouble(args, "timeSeconds"), Notes = McpString(args, "notes", ""), Color = McpString(args, "color", "#FF9957") };
                McpMutate("创建标记", () => _project.Markers.Add(marker)); return marker;
            }
            case "marker_update": return McpUpdateList(_project.Markers, McpString(args, "markerId"), McpObject(args, "patch"), item => item.Id, "修改标记");
            case "marker_remove": return McpMutate("删除标记", () => McpRemoveById(_project.Markers, McpString(args, "markerId"), item => item.Id));
            case "preview_seek": StopCatalogPreview(); SeekPreview(McpDouble(args, "timeSeconds")); return McpState(false);
            case "preview_play":
                StopCatalogPreview();
                double speed = McpDouble(args, "speed", 1); if (speed == 0) { if (_isPlaying) TogglePlayback(); }
                else SetPlaybackDirection(speed < 0 ? -1 : 1);
                return McpState(false);
            case "preview_pause": if (_isPlaying) TogglePlayback(); return McpState(false);
            case "catalog_list": return McpCatalogList(McpString(args, "category", "all"));
            case "catalog_schema": return McpCatalogSchema(McpString(args, "presetId"));
            case "catalog_preview":
            {
                string id = McpString(args, "presetId");
                _ = McpCatalogSchema(id);
                return McpStartJob("示例预览", async (progress, cancellation) =>
                {
                    string? path = null;
                    bool playing = false;
                    try
                    {
                    Action<double> report = value => progress.Report(value / 100);
                    if (id.StartsWith("transition.")) await CatalogPreviewService.PlayTransitionPreviewAsync(id, TimelineExporter.ResolveEncoderPath(), value => path = value, cancellation, report);
                    else if (id.StartsWith("title.") || id.StartsWith("sticker.")) await CatalogPreviewService.PlayTextPreviewAsync(id, "example", TimelineExporter.ResolveEncoderPath(), CreateTitleStyle, value => path = value, cancellation, report);
                    else await CatalogPreviewService.PlayEffectPreviewAsync(id, TimelineExporter.ResolveEncoderPath(), value => path = value, cancellation, report);
                    cancellation.ThrowIfCancellationRequested();
                    if (path is null) throw new StageException("MCPJB0001", "示例预览已被新的请求替代。");
                    await McpOnUiAsync(() => { cancellation.ThrowIfCancellationRequested(); PlayCatalogPreview(path); playing = true; return true; });
                    return new { path, presetId = id };
                    }
                    finally { if (!playing && path is not null) DeleteCatalogPlaybackFile(path); }
                });
            }
            case "jobs_list": return _mcpJobs.Values.OrderByDescending(job => job.CreatedUtc).Select(McpJobState).ToArray();
            case "jobs_status": return McpJobState(_mcpJobs.GetValueOrDefault(McpString(args, "jobId")) ?? throw new StageException("MCPRT0009", "任务不存在。"));
            case "jobs_cancel":
            {
                var job = _mcpJobs.GetValueOrDefault(McpString(args, "jobId")) ?? throw new StageException("MCPRT0009", "任务不存在。"); job.Cancellation.Cancel(); return McpJobState(job);
            }
            case "workspace_get": return McpWorkspace();
            case "workspace_set": return await McpWorkspaceSetAsync(args);
            case "system_association_get": return new { progId = await Task.Run(() => DefaultAppAssociationService.QueryCurrentProgId(".tvep"), token) };
            case "system_association_set":
            {
                string action = McpString(args, "action");
                await Task.Run(() =>
                {
                    if (action == "register") DefaultAppAssociationService.RegisterHandler(".tvep", DefaultAppAssociationService.GetExecutablePath());
                    else if (action == "setDefault") DefaultAppAssociationService.SetThisAppDefault(".tvep", DefaultAppAssociationService.GetExecutablePath());
                    else if (action == "unregister") DefaultAppUnregistrationService.Unregister(_ => { });
                    else if (action == "openSettings") DefaultAppAssociationService.OpenWindowsSettings(true);
                    else throw new StageException("MCPRT0008", "无效的关联操作。");
                }, token); return new { action, completed = true };
            }
            case "ui_list_commands": return McpUiCommands.Keys.ToArray();
            case "ui_invoke":
            {
                string name = McpString(args, "command");
                if (!McpUiCommands.TryGetValue(name, out Action? action)) throw new StageException("MCPRT0008", "不支持的界面命令。");
                action(); return McpState(false);
            }
            default: return await ExecuteMcpFeatureAsync(command, args, token);
        }
    }

    private void McpDiscardAllowed(JsonObject args)
    { if (_isProjectDirty && !McpBool(args, "discardChanges")) throw new StageException("MCPRT0011", "有未保存修改。先保存项目或显式设置 discardChanges=true。"); }
    private object McpHistory(Stack<string> source, Stack<string> destination, string label)
    {
        McpEnsureIdle();
        if (source.Count == 0) return McpState(false);
        string before = JsonSerializer.Serialize(_project);
        ProjectDocument restored = JsonSerializer.Deserialize<ProjectDocument>(source.Peek())
            ?? throw new StageException("MCPRT0012", "编辑历史为空。");
        McpMutate(label, () =>
        {
            ProjectFileService.TransferCachedMedia(_project, restored);
            _project = restored; _projectGeneration++;
            ClearTimelineSelection(); _pendingDropBatches.Clear();
        }, recordHistory: false);
        source.Pop(); destination.Push(before);
        return McpState(false);
    }
    private void McpSwitchSequence(string id)
    {
        var target = _project.Sequences.FirstOrDefault(sequence => sequence.Sequence.Id == id) ?? throw new StageException("MCPRT0009", "序列不存在。");
        _project.Sequences.Remove(target); SaveActiveSequenceInCollection();
        _project.Sequence = target.Sequence; _project.Tracks = target.Tracks; _project.Markers = target.Markers;
        ClearTimelineSelection(); _pendingDropBatches.Clear();
    }
    private void McpReorderTracks() { for (int index = 0; index < _project.Tracks.Count; index++) _project.Tracks[index].Order = index; }
    private static void McpRequirePermutation(string[] requested, IEnumerable<string> current)
    { if (requested.Distinct().Count() != requested.Length || !requested.Order().SequenceEqual(current.Order())) throw new StageException("MCPRT0008", "顺序数组必须包含全部标识且不重复。"); }
    private static void McpRenewClipIds(TimelineClip clip)
    {
        clip.Id = Guid.NewGuid().ToString("N"); clip.GroupId = null; clip.LinkGroupId = null;
        foreach (var effect in clip.Effects)
        {
            string previous = effect.Id; effect.Id = Guid.NewGuid().ToString("N");
            foreach (var key in clip.Keyframes.Where(key => key.Property.StartsWith("Effect:" + previous + ":")))
                key.Property = "Effect:" + effect.Id + key.Property[("Effect:" + previous).Length..];
        }
        foreach (var item in clip.Transitions) item.Id = Guid.NewGuid().ToString("N");
        foreach (var item in clip.Keyframes) item.Id = Guid.NewGuid().ToString("N");
    }
    private object McpUpdateList<T>(List<T> list, string id, JsonObject patch, Func<T, string> getId, string label)
    {
        int index = list.FindIndex(item => getId(item) == id);
        if (index < 0) throw new StageException("MCPRT0009", "对象不存在：" + id);
        T updated = JsonModelTools.Merge(list[index], patch);
        if (getId(updated) != id) throw new StageException("MCPRT0008", "不能通过 update 修改对象 ID。");
        if (list[index] is TimelineTrack { IsLocked: true } locked && updated is TimelineTrack proposed &&
            (locked.Kind != proposed.Kind || !JsonNode.DeepEquals(JsonModelTools.ToNode(locked.Clips), JsonModelTools.ToNode(proposed.Clips))))
            throw new StageException("MCPRT0010", "轨道已锁定，请先单独解锁后修改轨道内容。");
        McpMutate(label, () => list[index] = updated); return updated!;
    }
    private static void McpRemoveById<T>(List<T> list, string id, Func<T, string> getId)
    { if (list.RemoveAll(item => getId(item) == id) == 0) throw new StageException("MCPRT0009", "对象不存在：" + id); }
    private static Dictionary<string, JsonElement> McpParameters(JsonObject value) =>
        value.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));

    private object McpMove(JsonObject args)
    {
        TimelineClip[] clips = McpStrings(args, "clipIds").Select(McpClip).Distinct().ToArray();
        if (clips.Length == 0) throw new StageException("MCPRT0008", "请选择片段。");
        double start = McpDouble(args, "startSeconds");
        var target = args["trackId"] is null ? McpOwner(clips[0]) : McpTrack(McpString(args, "trackId")); McpWritable(target);
        double min = clips.Min(clip => clip.StartSeconds);
        if (McpBool(args, "snap", true) && target.IsSnapping)
            start = SnapNewClipStart(new TimelineTrack { IsSnapping = true, Clips = target.Clips.Except(clips).ToList() }, start);
        double delta = start - min;
        return McpMutate("移动片段", () =>
        {
            foreach (var clip in clips)
            {
                var owner = McpOwner(clip); McpWritable(owner);
                if (args["trackId"] is not null && !IsCompatible(target, clip)) throw new StageException("MCPRT0008", "片段与目标轨道不兼容。");
                clip.StartSeconds += delta;
                if (args["trackId"] is not null) { owner.Clips.Remove(clip); target.Clips.Add(clip); }
            }
        });
    }

    private object McpApplyTitle(JsonObject args)
    {
        string id = McpString(args, "presetId");
        BuiltinPreset preset = BuiltinCatalog.Titles.Concat(BuiltinCatalog.Stickers).FirstOrDefault(preset => preset.Id == id)
            ?? throw new StageException("MCPRT0008", "文字或图形样式不存在。");
        TimelineClip? clip = args["clipId"] is null ? null : McpClip(McpString(args, "clipId"));
        McpMutate("应用文字样式", () =>
        {
            if (clip is null)
            {
                var track = args["trackId"] is null ? GetOrCreateTextTrack(McpDouble(args, "startSeconds"), McpDouble(args, "durationSeconds", 5)) : McpTrack(McpString(args, "trackId"));
                McpWritable(track);
                clip = new TimelineClip { Kind = ClipKind.Text, Name = preset.Name,
                    StartSeconds = McpDouble(args, "startSeconds"), DurationSeconds = McpDouble(args, "durationSeconds", 5) }; track.Clips.Add(clip);
            }
            else McpWritable(McpOwner(clip));
            clip.Kind = ClipKind.Text; clip.TemplateId = id; clip.Text = CreateTitleStyle(id);
            if (args["content"] is not null) clip.Text.Content = McpString(args, "content");
            if (id.StartsWith("sticker.", StringComparison.Ordinal))
            {
                GraphicShape.FromPreset(preset, _project.Sequence.Width, _project.Sequence.Height).Save(clip);
                clip.Text.Content = string.Empty;
            }
        }); return clip!;
    }

    private async Task<object> McpImportAsync(JsonObject args, CancellationToken token)
    {
        ProjectDocument owner = _project; long revision = _projectRevision;
        var scan = await Task.Run(() => ScanImportPaths(McpStrings(args, "paths")), token);
        var assets = new List<MediaAsset>();
        foreach (var candidate in scan.Candidates)
        {
            var asset = new MediaAsset { SourcePath = candidate.Path, Kind = candidate.Kind, Name = Path.GetFileName(candidate.Path), BinId = args["binId"]?.GetValue<string>() };
            var metadata = await MediaMetadataProbe.TryReadAsync(candidate.Path, token);
            if (metadata is not null)
            {
                asset.DurationSeconds = metadata.DurationSeconds; asset.Width = metadata.Width; asset.Height = metadata.Height; asset.FramesPerSecond = metadata.FramesPerSecond;
                asset.HasAudio = metadata.HasAudio; asset.VideoCodec = metadata.VideoCodec; asset.AudioCodec = metadata.AudioCodec;
                asset.AudioChannels = metadata.AudioChannels; asset.AudioSampleRate = metadata.AudioSampleRate;
                asset.FileSizeBytes = metadata.FileSizeBytes; asset.FileModifiedUtc = metadata.FileModifiedUtc; asset.Metadata = new(metadata.Tags);
            }
            assets.Add(asset);
        }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(owner, _project) || revision != _projectRevision) throw new StageException("MCPRT0003", "导入期间项目已修改，请重试。");
        McpMutate("导入素材", () =>
        {
            foreach (var asset in assets)
                if (!_project.Media.Any(item => item.SourcePath.Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase))) _project.Media.Add(asset);
        }); return new { assets = _project.Media.Where(asset => assets.Any(item => item.SourcePath.Equals(asset.SourcePath, StringComparison.OrdinalIgnoreCase))).ToArray(), scan.Skipped };
    }

    private object McpProxy(JsonObject args)
    {
        MediaAsset asset = _project.Media.FirstOrDefault(item => item.Id == McpString(args, "assetId")) ?? throw new StageException("MCPRT0009", "素材不存在。");
        if (!McpBool(args, "generate")) return McpMutate("设置代理", () =>
        {
            if (args["path"] is not null) { string path = Path.GetFullPath(McpString(args, "path")); if (!File.Exists(path)) throw new StageException("MCPRT0008", "代理文件不存在。"); asset.ProxyPath = path; }
            asset.UseProxy = McpBool(args, "useProxy", true); asset.SetProxyAvailability(asset.ProxyPath is not null && File.Exists(asset.ProxyPath));
        });
        string output = McpFeatureOutputPath(args, ".mp4"); string source = asset.SourcePath; string id = asset.Id;
        ProjectDocument owner = _project; long revision = _projectRevision;
        return McpStartJob("生成代理", async (progress, cancellation) =>
        {
            string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".proxy-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await McpFeatureProcessAsync(TimelineExporter.ResolveEncoderPath(), ["-y", "-i", source,
                    "-vf", "scale='min(1280,iw)':-2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "28", "-c:a", "aac", "-b:a", "128k", temporary], cancellation);
                File.Move(temporary, output, true);
                return await McpOnUiAsync<object>(() =>
                {
                    McpRequireCurrent(owner, revision);
                    McpMutate("代理已生成", () => { asset.ProxyPath = output; asset.UseProxy = McpBool(args, "useProxy", true); asset.SetProxyAvailability(true); });
                    return new { assetId = id, path = output };
                });
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }

    private object McpImageSequence(JsonObject args)
    {
        string first = Path.GetFullPath(McpString(args, "firstFrame"));
        string output = McpFeatureOutputPath(args, ".mp4");
        double fps = McpDouble(args, "framesPerSecond", _project.Sequence.FramesPerSecond);
        string name = McpString(args, "name", Path.GetFileNameWithoutExtension(first) + " · 图像序列");
        string? bin = args["binId"]?.GetValue<string>();
        ProjectDocument owner = _project; long revision = _projectRevision;
        return McpStartJob("导入图像序列", async (progress, cancellation) =>
        {
            string[] frames = await Task.Run(() => DiscoverImageSequence(first), cancellation);
            if (frames.Length < 2) throw new StageException("MCPRT0008", "至少需要两个连续编号的图像。");
            await ProjectFileService.EnsureImportCapacityAsync(frames, cancellation);
            await ImageSequenceEncoder.EncodeAsync(frames, output, fps, cancellation);
            var metadata = await MediaMetadataProbe.TryReadAsync(output, cancellation);
            return await McpOnUiAsync<object>(() =>
            {
                cancellation.ThrowIfCancellationRequested(); McpRequireCurrent(owner, revision);
                var asset = new MediaAsset { Name = name, SourcePath = output, Kind = MediaKind.Video, BinId = bin,
                    DurationSeconds = frames.Length / fps, FramesPerSecond = fps, Width = metadata?.Width, Height = metadata?.Height,
                    HasAudio = false, VideoCodec = metadata?.VideoCodec, Description = $"{frames.Length} 帧 · {fps} fps", Tags = ["图像序列"] };
                McpMutate("导入图像序列", () => _project.Media.Add(asset));
                return new { asset, frameCount = frames.Length, path = output };
            });
        });
    }

    private static Type McpModelType(string name) => name switch
    {
        "project" => typeof(ProjectDocument), "sequence" => typeof(ProjectSequence), "track" => typeof(TimelineTrack), "clip" => typeof(TimelineClip),
        "text" => typeof(ClipText), "motion" => typeof(TitleMotion), "transform" => typeof(ClipTransform), "mask" => typeof(ClipMask), "audio" => typeof(AudioSettings),
        "effect" => typeof(ClipEffect), "transition" => typeof(ClipTransition), "keyframe" => typeof(ClipKeyframe), "media" => typeof(MediaAsset),
        "marker" => typeof(TimelineMarker), "shape" => typeof(GraphicShape), "subclip" => typeof(ProjectSubclip), "bin" => typeof(ProjectBin), "multicam" => typeof(MulticamGroup),
        _ => throw new StageException("MCPRT0008", "模型不存在：" + name)
    };
    private static object McpCatalogList(string category) => category switch
    {
        "titles" or "title" => BuiltinCatalog.Titles, "effects" or "effect" => BuiltinCatalog.Effects,
        "transitions" or "transition" => BuiltinCatalog.Transitions, "stickers" or "sticker" => BuiltinCatalog.Stickers, "filters" => BuiltinCatalog.Filters,
        _ => new { titles = BuiltinCatalog.Titles, effects = BuiltinCatalog.Effects, transitions = BuiltinCatalog.Transitions, stickers = BuiltinCatalog.Stickers, filters = BuiltinCatalog.Filters }
    };
    private static object McpCatalogSchema(string id)
    {
        if (!BuiltinCatalog.Titles.Concat(BuiltinCatalog.Effects).Concat(BuiltinCatalog.Transitions).Concat(BuiltinCatalog.Stickers)
            .Any(preset => preset.Id == id)) throw new StageException("MCPRT0009", "预设不存在：" + id);
        if (id.StartsWith("transition.")) return new { presetId = id, parameters = TransitionSettings.Describe(id) };
        if (id.StartsWith("effect."))
        {
            var fields = new JsonObject { ["intensity"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 200, ["default"] = 100, ["description"] = "效果总强度（%）" } };
            foreach (var parameter in VisualEffectPipeline.ParametersFor(id))
                fields[parameter.Id] = new JsonObject { ["type"] = "number", ["minimum"] = parameter.Minimum, ["maximum"] = parameter.Maximum,
                    ["default"] = parameter.Default, ["description"] = parameter.Name };
            if (id == "effect.key") fields["color"] = new JsonObject { ["type"] = "string", ["default"] = "#00FF00", ["description"] = "要去除的颜色" };
            if (id == "effect.lut") fields["path"] = new JsonObject { ["type"] = "string", ["default"] = "", ["description"] = "本地 .cube LUT 文件的完整路径" };
            return new { presetId = id, parameters = VisualEffectPipeline.ParametersFor(id),
                parameterSchema = new JsonObject { ["type"] = "object", ["properties"] = fields, ["additionalProperties"] = false } };
        }
        if (id.StartsWith("title.")) return new { presetId = id, defaults = CreateTitleStyle(id), schema = JsonModelTools.GetSchema<ClipText>() };
        if (id.StartsWith("sticker.")) return new { presetId = id, schema = JsonModelTools.GetSchema<GraphicShape>(),
            defaults = GraphicShape.FromPreset(BuiltinCatalog.Stickers.First(preset => preset.Id == id), 1920, 1080) };
        throw new StageException("MCPRT0009", "预设不存在。");
    }
    private Dictionary<string, Action> McpUiCommands => new()
    {
        ["fit_timeline"] = FitTimeline, ["reset_workspace"] = ResetEditorWorkspace,
        ["show_settings"] = ShowSelectedObjectSettings, ["show_transform"] = () => { if (_selectedClip is not null) OpenObjectToolPage(_selectedClip, "变换", BuildTransformTool); },
        ["show_effects"] = () => { if (_selectedClip is not null) OpenObjectToolPage(_selectedClip, "效果", BuildEffectControls); },
        ["show_audio"] = () => { if (_selectedClip is not null) OpenObjectToolPage(_selectedClip, "音频", BuildAudioTool); },
        ["toggle_mute"] = ToggleGlobalMute, ["solo_track"] = SoloSelectedTrack,
        ["copy"] = CopySelectedClip, ["cut"] = CutSelectedClip, ["paste"] = PasteClipboardClip,
        ["insert_clipboard"] = InsertClipboardAtPlayhead, ["overwrite_clipboard"] = OverwriteClipboardAtPlayhead,
        ["ripple_trim_start"] = RippleTrimStartToPlayhead, ["ripple_trim_end"] = RippleTrimEndToPlayhead,
        ["trim_start"] = TrimSelectedStart, ["trim_end"] = TrimSelectedEnd
    };
}
