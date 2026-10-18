using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private static readonly string[] McpShortcutNames = ["分割片段", "删除片段", "复制片段", "剪切片段", "粘贴片段", "冻结帧", "上一帧", "下一帧", "波纹删除", "波纹修剪开头", "波纹修剪结尾", "切换吸附", "切换磁性模式", "剃刀分割", "播放暂停", "停止播放"];
    private static readonly string[] McpGpuModes = ["自动（GPU 优先）", "CPU 软件渲染", "NVIDIA CUDA", "Intel QSV", "AMD AMF"];

    private WorkspaceLayout McpCaptureWorkspace()
    {
        WorkspaceLayout actual = CaptureWorkspaceLayout();
        // Star values update synchronously; ActualWidth/ActualHeight can still
        // describe the preceding layout until the next WPF measure pass.
        double upper = UpperWorkspaceRow.Height.IsStar && TimelineWorkspaceRow.Height.IsStar
            ? UpperWorkspaceRow.Height.Value / Math.Max(.000001, UpperWorkspaceRow.Height.Value + TimelineWorkspaceRow.Height.Value)
            : actual.UpperRatio;
        double library = LibraryWorkspaceColumn.Width.IsStar && PreviewWorkspaceColumn.Width.IsStar
            ? LibraryWorkspaceColumn.Width.Value / Math.Max(.000001, LibraryWorkspaceColumn.Width.Value + PreviewWorkspaceColumn.Width.Value)
            : actual.LibraryRatio;
        return actual with { UpperRatio = upper, LibraryRatio = library };
    }

    private object McpWorkspace()
    {
        WorkspaceLayout layout = McpCaptureWorkspace();
        return new
        {
            upperRatio = layout.UpperRatio, libraryRatio = layout.LibraryRatio, zoom = layout.TimelineZoom,
            snap = _snapEnabled, gpuMode = layout.PreviewGpuMode, autoSave = layout.AutoSave,
            previewViewZoom = layout.PreviewViewZoom, mediaViewIndex = layout.MediaViewIndex,
            previewQualityIndex = layout.PreviewQualityIndex, categoryIndex = layout.CategoryIndex,
            globalMute = _globalMute, soloTrackId = _soloTrackId, shortcuts = layout.Shortcuts,
            availableShortcutCommands = McpShortcutNames, workspacePath = WorkspaceSettingsPath
        };
    }

    /// <summary>
    /// Applies preferences without opening modal error dialogs. Validation is
    /// completed before changing controls, and persistence uses an atomic file
    /// replacement under the same settings gate as the native workspace UI.
    /// </summary>
    private async Task<object> McpWorkspaceSetAsync(JsonObject args)
    {
        McpEnsureIdle();
        string action = McpString(args, "action", "apply");
        WorkspaceLayout candidate = McpCaptureWorkspace();
        var patch = args["patch"]?.DeepClone().AsObject() ?? new JsonObject();
        foreach (string key in new[] { "upperRatio", "libraryRatio", "zoom", "shortcuts", "previewViewZoom", "mediaViewIndex" })
            if (args.TryGetPropertyValue(key, out JsonNode? value))
            {
                if (patch.ContainsKey(key)) throw new StageException("MCPWS0001", "同一工作区参数不能同时出现在顶层与 patch 中：" + key);
                patch[key] = value?.DeepClone();
            }
        bool beforeSnap = _snapEnabled, beforeMute = _globalMute;
        string? beforeSolo = _soloTrackId;
        await _workspaceSettingsGate.WaitAsync(_mcpLifetime.Token);
        string? temporary = null;
        try
        {
            if (action == "restore")
            {
                if (patch.Count != 0 || args["preset"] is not null)
                    throw new StageException("MCPWS0001", "恢复保存的布局时不能同时指定新的参数。");
                candidate = await Task.Run(() =>
                {
                    if (!File.Exists(WorkspaceSettingsPath)) throw new StageException("MCPWS0002", "尚未保存工作区布局。");
                    return JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(WorkspaceSettingsPath))
                        ?? throw new StageException("MCPWS0002", "保存的工作区为空。");
                });
            }
            else if (action == "reset")
                candidate = new WorkspaceLayout(.56, .44, 0, 0, 1, true, [], "自动（GPU 优先）");
            else if (action is not ("apply" or "save"))
                throw new StageException("MCPWS0001", "未知工作区操作。");

            if (args["preset"] is not null)
                candidate = McpString(args, "preset") switch
                {
                    "editing" => candidate with { UpperRatio = .56, LibraryRatio = .44, CategoryIndex = 0 },
                    "color" => candidate with { UpperRatio = .67, LibraryRatio = .33, CategoryIndex = 2 },
                    "audio" => candidate with { UpperRatio = .47, LibraryRatio = .42, CategoryIndex = 0 },
                    _ => throw new StageException("MCPWS0001", "未知工作区预设。")
                };
            candidate = candidate with
            {
                UpperRatio = McpDouble(patch, "upperRatio", candidate.UpperRatio),
                LibraryRatio = McpDouble(patch, "libraryRatio", candidate.LibraryRatio),
                TimelineZoom = McpDouble(patch, "zoom", candidate.TimelineZoom),
                PreviewViewZoom = McpDouble(patch, "previewViewZoom", candidate.PreviewViewZoom),
                MediaViewIndex = McpInt(patch, "mediaViewIndex", candidate.MediaViewIndex),
                AutoSave = McpBool(patch, "autoSave", candidate.AutoSave),
                PreviewGpuMode = McpString(patch, "gpuMode", candidate.PreviewGpuMode),
                PreviewQualityIndex = McpInt(patch, "previewQualityIndex", candidate.PreviewQualityIndex),
                CategoryIndex = McpInt(patch, "categoryIndex", candidate.CategoryIndex)
            };
            var shortcuts = new Dictionary<string, string>(candidate.Shortcuts ?? []);
            if (patch["shortcuts"] is JsonObject shortcutPatch)
                foreach ((string name, JsonNode? gesture) in shortcutPatch)
                {
                    if (!McpShortcutNames.Contains(name, StringComparer.Ordinal)) throw new StageException("MCPWS0003", "未知快捷键命令：" + name);
                    if (gesture is null) shortcuts.Remove(name);
                    else shortcuts[name] = gesture.GetValue<string>();
                }
            candidate = candidate with { Shortcuts = shortcuts };
            McpValidateWorkspace(candidate);
            string? solo = patch.ContainsKey("soloTrackId") ? patch["soloTrackId"]?.GetValue<string>() : beforeSolo;
            if (solo is not null && !_project.Tracks.Any(track => track.Id == solo)) throw new StageException("MCPWS0001", "独奏轨道不存在。");

            // Write the validated detached layout first. A failure must not
            // leave successful-looking controls with preferences lost on disk.
            if (action != "restore")
            {
                string directory = Path.GetDirectoryName(WorkspaceSettingsPath)!;
                temporary = Path.Combine(directory, ".workspace-" + Guid.NewGuid().ToString("N") + ".tmp");
                string stagedPath = temporary;
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(stagedPath, JsonSerializer.Serialize(candidate));
                    File.Move(stagedPath, WorkspaceSettingsPath, overwrite: true);
                });
            }
            McpApplyWorkspace(candidate);
            _snapEnabled = McpBool(patch, "snap", action == "reset" || beforeSnap);
            SnapButton.Content = _snapEnabled ? "⊞ 吸附" : "□ 吸附";
            _globalMute = McpBool(patch, "globalMute", action != "reset" && beforeMute);
            _soloTrackId = action == "reset" && !patch.ContainsKey("soloTrackId") ? null : solo;
            _monitorStateRevision++;
            if (_isPlaying) UpdateAudioPreview(_project.Sequence.PlayheadSeconds);
            InvalidateCompositedPreview();
            StatusText.Text = action == "restore" ? "MCP · 已恢复工作区布局" : "MCP · 工作区设置已保存";
            return McpWorkspace();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new StageException("MCPWS0004", "无法读取或保存工作区设置：" + exception.Message);
        }
        finally
        {
            if (temporary is not null)
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _workspaceSettingsGate.Release();
        }
    }

    private void McpValidateWorkspace(WorkspaceLayout layout)
    {
        if (!double.IsFinite(layout.UpperRatio) || layout.UpperRatio is < .15 or > .85 ||
            !double.IsFinite(layout.LibraryRatio) || layout.LibraryRatio is < .15 or > .85 ||
            !double.IsFinite(layout.TimelineZoom) || layout.TimelineZoom < TimelineZoomSlider.Minimum || layout.TimelineZoom > TimelineZoomSlider.Maximum ||
            !double.IsFinite(layout.PreviewViewZoom) || layout.PreviewViewZoom is < .1 or > 4 || layout.MediaViewIndex is < 0 or > 2 ||
            layout.PreviewQualityIndex is < 0 or > 2 || layout.CategoryIndex < 0 || layout.CategoryIndex >= CategoryList.Items.Count ||
            !McpGpuModes.Contains(layout.PreviewGpuMode, StringComparer.Ordinal))
            throw new StageException("MCPWS0001", "工作区比例、缩放、预览模式或分类超出范围。");
        var gestures = new HashSet<(Key, ModifierKeys)>();
        foreach ((string name, string gesture) in layout.Shortcuts ?? [])
        {
            if (!McpShortcutNames.Contains(name, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(gesture) || !TryParseShortcut(gesture, out Key key, out ModifierKeys modifiers))
                throw new StageException("MCPWS0003", "快捷键命令或按键格式无效：" + name);
            if (!gestures.Add((key, modifiers))) throw new StageException("MCPWS0003", "多个自定义命令不能使用同一个快捷键。");
        }
    }

    private void McpApplyWorkspace(WorkspaceLayout layout)
    {
        ApplyWorkspacePreset(layout.UpperRatio, layout.LibraryRatio, layout.CategoryIndex);
        PreviewQualityBox.SelectedIndex = layout.PreviewQualityIndex;
        TimelineZoomSlider.Value = layout.TimelineZoom;
        PreviewZoomSlider.Value = layout.PreviewViewZoom;
        MediaViewBox.SelectedIndex = layout.MediaViewIndex;
        _previewGpuMode = layout.PreviewGpuMode;
        _autoSaveEnabled = layout.AutoSave;
        if (_autoSaveEnabled) _autoSaveTimer.Start(); else _autoSaveTimer.Stop();
        foreach (InputBinding binding in _customShortcutBindings.Values) InputBindings.Remove(binding);
        _customShortcutBindings.Clear();
        foreach ((string name, string gesture) in layout.Shortcuts ?? [])
        {
            TryParseShortcut(gesture, out Key key, out ModifierKeys modifiers);
            ApplyCustomShortcut(name, key, modifiers);
        }
    }
}
