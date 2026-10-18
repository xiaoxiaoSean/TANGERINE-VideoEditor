using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Owns the live automation boundary. Pipe workers never touch WPF or the
/// mutable project: requests are serialized and dispatched to the actual window.
/// An acknowledged edit has passed validation, gained an undo entry and rebuilt
/// the timeline, inspector and preview from the same live document.
/// </summary>
public partial class MainWindow
{
    private AutomationInstanceServer? _mcpServer;
    private bool _mcpReady;
    private AutomationInstanceInfo _mcpInfo = AutomationInstanceInfo.CreateForCurrentProcess();
    private readonly SemaphoreSlim _mcpRequestGate = new(1, 1);
    private readonly CancellationTokenSource _mcpLifetime = new();
    private readonly DispatcherTimer _mcpHeartbeat = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ConcurrentDictionary<string, McpJob> _mcpJobs = new();
    private long _mcpUiRevision;
    private string _mcpUiFingerprint = "";
    private Action? _mcpInspectorRefresh;

    private sealed class McpJob(string name)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Name { get; } = name;
        public string Status { get; set; } = "running";
        public double Progress { get; set; }
        public object? Result { get; set; }
        public string? Error { get; set; }
        public string? StageCode { get; set; }
        public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;
        public string? OwnerSessionId { get; set; }
    }

    private void InitializeMcp()
    {
        _mcpHeartbeat.Tick += (_, _) => { CheckMcpControlIdle(); UpdateMcpInfo(); };
        _mcpHeartbeat.Start();
        UpdateMcpInfo();
        Loaded += async (_, _) =>
        {
            try
            {
                _mcpServer = new AutomationInstanceServer(HandleMcpRequestAsync, () => Volatile.Read(ref _mcpInfo));
                await _mcpServer.StartAsync(_mcpLifetime.Token);
                _mcpReady = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { StatusText.Text = $"MCP 服务启动失败：{exception.Message} · MCPRT0001"; }
        };
        Closed += async (_, _) =>
        {
            _mcpHeartbeat.Stop();
            _mcpReady = false;
            _mcpLifetime.Cancel();
            if (_mcpControlOwnerSessionId is not null) ReleaseMcpControl("MCP 服务已停止");
            foreach (McpJob job in _mcpJobs.Values.Where(job => job.Status == "running")) job.Cancellation.Cancel();
            DisposeMcpFeatures();
            if (_mcpServer is not null) await _mcpServer.DisposeAsync();
        };
        McpConnectionMenuItem.Click += (_, _) => ShowMcpConnection();
    }

    private void UpdateMcpInfo()
    {
        string fingerprint = $"{_mcpControlOwnerSessionId}|{_projectRevision}|{_projectGeneration}|{_project.Sequence.PlayheadSeconds:R}|" +
            $"{string.Join(',', _selectedClips.Select(clip => clip.Id).Order())}|{_selectedTrack?.Id}|{SelectedMedia()?.Id}|{_isPlaying}|" +
            $"{_renderedPreviewInFlight}|{_renderedPreviewPending}|{_renderedPreviewGeneration}|{_renderedPreviewFailed}|" +
            $"{TimelineZoomSlider.Value}|{UpperWorkspaceRow.Height}|{LibraryWorkspaceColumn.Width}|{_autoSaveEnabled}|" +
            $"{string.Join(',', _customShortcutBindings.OrderBy(pair => pair.Key).Select(pair => pair.Key + ':' + (pair.Value is System.Windows.Input.KeyBinding key ? key.Key.ToString() + key.Modifiers : pair.Value.ToString())))}|" +
            $"{_snapEnabled}|{_previewGpuMode}|{_globalMute}|{_soloTrackId}|{StatusText.Text}|" +
            string.Join('|', _mcpJobs.Values.OrderBy(job => job.Id).Select(job => $"{job.Id}:{job.Status}:{job.Progress:R}"));
        if (fingerprint != _mcpUiFingerprint) { _mcpUiRevision++; _mcpUiFingerprint = fingerprint; }
        Volatile.Write(ref _mcpInfo, _mcpInfo with
        {
            DisplayName = Title, ProjectName = _project.Name, ProjectPath = _projectPath,
            Revision = _projectRevision, UpdatedUtc = DateTimeOffset.UtcNow,
            CommandCount = AutomationCatalog.Commands.Count, AppVersion = AppVersion.Number
        });
    }

    private async Task<AutomationResponse> HandleMcpRequestAsync(AutomationRequest request, CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mcpLifetime.Token);
            bool controlCommand = request.Command is "editor_control_acquire" or "editor_control_release" or "editor_control_status";
            if (!controlCommand && request.Command != "editor_wait_for_change")
            {
                await _mcpRequestGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                entered = true;
            }
            return await Dispatcher.InvokeAsync(async () =>
            {
                UpdateMcpInfo();
                if (request.ExpectedRevision is long expected && expected != _projectRevision)
                    throw new StageException("MCPRT0003", $"项目已变化：预期版本 {expected}，当前版本 {_projectRevision}。请读取最新状态后重试。");
                if (AutomationCatalog.Find(request.Command) is null)
                    throw new StageException("MCPRT0004", "不支持的 MCP 命令：" + request.Command);
                AutomationCatalog.ValidateArguments(request.Command, request.Arguments ?? new JsonObject());
                lifetime.Token.ThrowIfCancellationRequested();
                if (!controlCommand) McpRequireControl(request.SessionId);
                bool active = !controlCommand;
                if (active) { _mcpActiveOperations++; _mcpControlLastActivityUtc = DateTimeOffset.UtcNow; }
                try
                {
                object? result = controlCommand ? ExecuteMcpControl(request)
                    : await ExecuteMcpCommandAsync(request.Command, request.Arguments ?? new JsonObject(), lifetime.Token);
                UpdateMcpInfo();
                JsonNode? data = JsonModelTools.ToNode(result);
                long revision = _projectRevision;
                // Let WPF process the rebuilt visual tree before acknowledging
                // the edit. Async FFmpeg preview completion remains observable
                // through editor_get_state.preview.rendering.
                // Cancellation can abort work before its commit. After the
                // command returns, acknowledge the actual outcome even if its
                // caller disconnects while WPF completes the visual refresh.
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                return AutomationResponse.Ok(request.Id, data, revision);
                }
                finally
                {
                    if (active)
                    {
                        _mcpActiveOperations--;
                        _mcpControlLastActivityUtc = DateTimeOffset.UtcNow;
                        UpdateMcpInfo();
                    }
                }
            }, DispatcherPriority.Normal, lifetime.Token).Task.Unwrap().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { return AutomationResponse.Fail(request.Id, "cancelled", "MCP 操作已取消。", "MCPRT0002", _mcpInfo.Revision); }
        catch (Exception exception)
        {
            string stage = exception is JsonModelException model ? StageCodes.Of("MCPJS", (int)model.StageCode + 1)
                : StageCodes.Resolve(exception, "MCPRT0005");
            string error = stage switch { "MCPRT0003" => "revision_conflict", "MCPCT0001" => "missing_session",
                "MCPCT0002" => "control_required", "MCPCT0003" => "control_owned", "MCPCT0004" => "control_busy", _ => "operation_failed" };
            return AutomationResponse.Fail(request.Id, error,
                exception.Message, stage, _mcpInfo.Revision);
        }
        finally { if (entered) _mcpRequestGate.Release(); }
    }

    private object McpState(bool includeProject = true)
    {
        UpdateMcpInfo();
        return new
        {
        processId = Environment.ProcessId, version = AppVersion.DisplayName, revision = _projectRevision,
        uiRevision = _mcpUiRevision, projectPath = _projectPath, dirty = _isProjectDirty,
        control = McpControlState(_mcpControlOwnerSessionId),
        project = includeProject ? JsonModelTools.ToNode(_project) : null,
        selection = new { clipIds = _selectedClips.Select(clip => clip.Id).ToArray(), clipId = _selectedClip?.Id,
            trackId = _selectedTrack?.Id, assetId = SelectedMedia()?.Id },
        preview = new { timeSeconds = _project.Sequence.PlayheadSeconds, playing = _isPlaying,
            rendering = _renderedPreviewInFlight, pending = _renderedPreviewPending, failed = _renderedPreviewFailed,
            generation = _renderedPreviewGeneration, gpuMode = _previewGpuMode },
        history = new { undo = _undoStates.Count, redo = _redoStates.Count },
        jobs = _mcpJobs.Values.OrderByDescending(job => job.CreatedUtc).Select(McpJobState).ToArray(),
            status = StatusText.Text
        };
    }

    private object McpMutate(string label, Action edit, bool recordHistory = true)
    {
        McpEnsureIdle();
        string before = JsonSerializer.Serialize(_project);
        string? path = _projectPath;
        long generation = _projectGeneration;
        long revision = _projectRevision;
        string? selected = _selectedClip?.Id;
        string[] selection = _selectedClips.Select(clip => clip.Id).ToArray();
        string? trackId = _selectedTrack?.Id;
        bool dirty = _isProjectDirty;
        bool created = _projectCreated;
        try
        {
            edit();
            McpValidate(_project);
            MarkProjectDirty();
            RefreshMcpUi(label, _selectedClip?.Id);
            // History is changed only after both model validation and the
            // visual refresh succeed. Failed edits leave both stacks intact.
            if (recordHistory)
            {
                _undoStates.Push(before);
                TrimHistoryCapacity();
                _redoStates.Clear();
            }
            return new { revision = _projectRevision, updated = true };
        }
        catch
        {
            ProjectDocument restored = JsonSerializer.Deserialize<ProjectDocument>(before)!;
            ProjectFileService.TransferCachedMedia(_project, restored);
            _project = restored;
            _projectPath = path;
            _projectGeneration = generation;
            _projectRevision = revision;
            _isProjectDirty = dirty;
            _projectCreated = created;
            _selectedClips.Clear();
            foreach (var clip in restored.Tracks.SelectMany(track => track.Clips).Where(clip => selection.Contains(clip.Id))) _selectedClips.Add(clip);
            _selectedTrack = restored.Tracks.FirstOrDefault(track => track.Id == trackId);
            try { RefreshMcpUi("MCP 修改未提交", selected); }
            catch { StatusText.Text = "MCP 修改已回滚 · MCPRT0012"; UpdateMcpInfo(); }
            throw;
        }
    }

    private void RefreshMcpUi(string label, string? selectedId = null)
    {
        Action? refreshInspector = _selectedClip?.Id == selectedId ? _mcpInspectorRefresh : null;
        StopCatalogPreview();
        if (_isPlaying) TogglePlayback();
        string[] selectedIds = _selectedClips.Select(clip => clip.Id).ToArray();
        _selectedClip = _project.Tracks.SelectMany(track => track.Clips).FirstOrDefault(clip => clip.Id == selectedId);
        _selectedClips.Clear();
        foreach (var clip in _project.Tracks.SelectMany(track => track.Clips).Where(clip => selectedIds.Contains(clip.Id))) _selectedClips.Add(clip);
        if (_selectedClip is not null) { _selectedClips.Add(_selectedClip); _selectedTrack = _project.Tracks.First(track => track.Clips.Contains(_selectedClip)); }
        else if (_selectedTrack is not null) _selectedTrack = _project.Tracks.FirstOrDefault(track => track.Id == _selectedTrack.Id);
        _lastTitleLayoutKey = null;
        _lastMediaTransformKey = null;
        RefreshEditorFromProject();
        RefreshProjectChrome();
        InvalidateCompositedPreview();
        if (refreshInspector is not null && (_selectedClip is not null || selectedId is null)) refreshInspector();
        else if (_selectedClip is not null) ShowSelectedObjectSettings();
        else CloseSettingsTabs();
        StatusText.Text = "MCP · " + label;
        UpdateMcpInfo();
    }

    private void McpEnsureIdle()
    {
        if (_projectIoInProgress || _historyOperationInProgress || _autoSaveInProgress || _exportInProgress || _imageSequenceImportInProgress ||
            _mediaImportGate.CurrentCount == 0 || _scrubbingPlayhead || _movingClip ||
            _trimmingStart || _trimmingEnd || _draggingTitleClip is not null || _draggingMediaClip is not null ||
            _rubberBandDrag is not null || _bezierHandleDrag is not null)
            throw new StageException("MCPRT0006", "界面正在执行编辑或项目读写，请稍后重试。");
    }

    private static void McpValidate(ProjectDocument project)
    {
        ProjectFileService.Validate(project);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (TimelineTrack track in project.Tracks.Concat(project.Sequences.SelectMany(sequence => sequence.Tracks)))
        {
            if (string.IsNullOrWhiteSpace(track.Id) || !ids.Add(track.Id)) throw new StageException("MCPRT0007", "轨道标识为空或重复。");
            AudioSettings.FromTrack(track).Validate();
            foreach (TimelineClip clip in track.Clips)
            {
                if (string.IsNullOrWhiteSpace(clip.Id) || !ids.Add(clip.Id)) throw new StageException("MCPRT0007", "片段标识为空或重复。");
                if (!IsCompatible(track, clip)) throw new StageException("MCPRT0007", "片段与轨道类型不兼容。");
                if (!double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
                    !double.IsFinite(clip.Opacity) || clip.Opacity is < 0 or > 1 ||
                    !double.IsFinite(clip.Volume) || clip.Volume < 0)
                    throw new StageException("MCPRT0007", "源入点、透明度或音量无效。");
                AudioSettings.FromClip(clip).Validate();
                if (clip.Text is not null) GraphicTemplateDocument.FromClip(clip).Validate();
                if (clip.Text is ClipText text &&
                    (!TryColorValue(text.ForegroundColor) || !TryColorValue(text.BackgroundColor ?? "", true) ||
                    !TryColorValue(text.OutlineColor) || !TryColorValue(text.ShadowColor) || !TryColorValue(text.GlowColor)))
                    throw new StageException("MCPRT0007", "文字样式包含无效颜色。");
                var effectIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (ClipEffect effect in clip.Effects)
                {
                    if (string.IsNullOrWhiteSpace(effect.Id) || !effectIds.Add(effect.Id) ||
                        !BuiltinCatalog.Effects.Any(item => item.Id == effect.PresetId))
                        throw new StageException("MCPRT0007", "效果类型或标识无效：" + effect.PresetId);
                    foreach (var parameter in VisualEffectPipeline.ParametersFor(effect.PresetId))
                        if (effect.Parameters.TryGetValue(parameter.Id, out JsonElement value) &&
                            (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
                            !double.IsFinite(number) || number < parameter.Minimum || number > parameter.Maximum))
                            throw new StageException("MCPRT0007", "效果参数超出范围：" + parameter.Id);
                    if (effect.Parameters.TryGetValue("intensity", out JsonElement intensity) &&
                        (intensity.ValueKind != JsonValueKind.Number || !intensity.TryGetDouble(out double strength) || !double.IsFinite(strength) || strength is < 0 or > 200))
                        throw new StageException("MCPRT0007", "效果总强度必须为 0 到 200。");
                    if (effect.PresetId == "effect.key" && effect.Parameters.TryGetValue("color", out JsonElement color) &&
                        (color.ValueKind != JsonValueKind.String || !TryColorValue(color.GetString()!)))
                        throw new StageException("MCPRT0007", "色度键控颜色无效。");
                }
                var transitionIds = new HashSet<string>(StringComparer.Ordinal);
                var edges = new HashSet<string>(StringComparer.Ordinal);
                foreach (ClipTransition transition in clip.Transitions)
                {
                    if (string.IsNullOrWhiteSpace(transition.Id) || !transitionIds.Add(transition.Id) ||
                        !edges.Add(transition.Edge) || !BoundaryCrossfade.IsVisible(clip) ||
                        !BuiltinCatalog.Transitions.Any(item => item.Id == transition.PresetId))
                        throw new StageException("MCPRT0007", "转场类型无效：" + transition.PresetId);
                    if (!double.IsFinite(transition.DurationSeconds) || transition.DurationSeconds < 0 ||
                        transition.Edge is not ("In" or "Out")) throw new StageException("MCPRT0007", "转场时长或位置无效。");
                    foreach (var parameter in TransitionSettings.Describe(transition.PresetId))
                        if (transition.Parameters.TryGetValue(parameter.Key, out JsonElement value) &&
                            (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) ||
                            !double.IsFinite(number) || number < parameter.Minimum || number > parameter.Maximum ||
                            parameter.Integer && Math.Abs(number - Math.Round(number)) > .000001))
                            throw new StageException("MCPRT0007", "转场参数超出范围：" + parameter.Key);
                }
            }
        }
    }

    private object McpStartJob(string name, Func<IProgress<double>, CancellationToken, Task<object?>> work)
    {
        // Bound completed history without discarding an active job's cancellation
        // source. Jobs return immediately so agents can keep editing or poll.
        foreach (McpJob old in _mcpJobs.Values.Where(job => job.Status != "running")
            .OrderByDescending(job => job.CreatedUtc).Skip(50))
            if (_mcpJobs.TryRemove(old.Id, out _)) old.Cancellation.Dispose();
        var job = new McpJob(name) { OwnerSessionId = _mcpControlOwnerSessionId };
        _mcpJobs[job.Id] = job;
        job.Completion = Task.Run(async () =>
        {
            using var token = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, _mcpLifetime.Token);
            var progress = new McpProgress(value => { if (job.Status == "running") job.Progress = Math.Clamp(value, 0, 1); });
            try { job.Result = await work(progress, token.Token); job.Progress = 1; job.Status = "completed"; }
            catch (OperationCanceledException) { job.Status = "cancelled"; }
            catch (Exception exception) { job.Error = exception.Message; job.StageCode = StageCodes.Resolve(exception, "MCPJB0001"); job.Status = "failed"; }
            finally
            {
                job.CompletedUtc = DateTimeOffset.UtcNow;
                if (!Dispatcher.HasShutdownStarted)
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_mcpControlOwnerSessionId is not null && job.OwnerSessionId == _mcpControlOwnerSessionId)
                            _mcpControlLastActivityUtc = DateTimeOffset.UtcNow;
                        StatusText.Text = $"MCP · {job.Name} · {job.Status}"; UpdateMcpInfo();
                    });
            }
        });
        return McpJobState(job);
    }

    private async Task McpWaitForProjectJobsAsync(ProjectDocument document)
    {
        // History can remove an asset from the live document while an older
        // snapshot still reads its extracted file. Conservatively defer cache
        // deletion until existing jobs exit; project switching itself remains
        // immediate because cleanup is launched separately by its caller.
        Task[] readers = _mcpJobs.Values.Select(job => job.Completion).ToArray();
        await Task.WhenAll(readers);
    }

    private static object McpJobState(McpJob job) => new
    {
        jobId = job.Id, job.Name, job.Status, job.Progress, job.Result, job.Error, job.StageCode, job.CreatedUtc, job.CompletedUtc
    };
    private sealed class McpProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private Task<T> McpOnUiAsync<T>(Func<T> action) => Dispatcher.InvokeAsync(action).Task;
    private static string McpString(JsonObject args, string key, string? fallback = null) =>
        args[key]?.GetValue<string>() ?? fallback ?? throw new StageException("MCPRT0008", "缺少参数：" + key);
    private static double McpDouble(JsonObject args, string key, double fallback = 0) => args[key]?.GetValue<double>() ?? fallback;
    private static int McpInt(JsonObject args, string key, int fallback = 0) => args[key]?.GetValue<int>() ?? fallback;
    private static bool McpBool(JsonObject args, string key, bool fallback = false) => args[key]?.GetValue<bool>() ?? fallback;
    private static string[] McpStrings(JsonObject args, string key) => args[key]?.AsArray().Select(item => item!.GetValue<string>()).ToArray()
        ?? throw new StageException("MCPRT0008", "缺少参数：" + key);
    private static JsonObject McpObject(JsonObject args, string key) => args[key]?.AsObject()
        ?? throw new StageException("MCPRT0008", "缺少对象参数：" + key);
    private TimelineClip McpClip(string id) => _project.Tracks.SelectMany(track => track.Clips).FirstOrDefault(clip => clip.Id == id)
        ?? throw new StageException("MCPRT0009", "找不到片段：" + id);
    private TimelineTrack McpTrack(string id) => _project.Tracks.FirstOrDefault(track => track.Id == id)
        ?? throw new StageException("MCPRT0009", "找不到轨道：" + id);
    private TimelineTrack McpOwner(TimelineClip clip) => _project.Tracks.First(track => track.Clips.Contains(clip));
    private static void McpWritable(TimelineTrack track)
    { if (track.IsLocked) throw new StageException("MCPRT0010", "轨道已锁定，请先通过 track_update 解锁。"); }

    private void ShowMcpConnection()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "mcp", "TANGERINE-VideoEditor.Mcp.exe");
        string json = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
            { ["tangerine-video-editor"] = new { command = executable, args = Array.Empty<string>() } } }, new JsonSerializerOptions { WriteIndented = true });
        var dialog = new Window { Owner = this, Title = "MCP 连接", WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = SystemParameters.WorkArea.Width * .5, Height = SystemParameters.WorkArea.Height * .5,
            Background = Brush("#191B20"), Foreground = System.Windows.Media.Brushes.White };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var status = new TextBlock { Text = $"进程 {Environment.ProcessId} · {_project.Name}\n{AutomationCatalog.Commands.Count} 个命令 · " +
            (_mcpReady ? "服务已启动" : "服务尚未启动，请检查底部状态提示"), Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(status, Dock.Top); panel.Children.Add(status);
        panel.Children.Add(new TextBox { Text = json, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel; dialog.ShowDialog();
    }
}
