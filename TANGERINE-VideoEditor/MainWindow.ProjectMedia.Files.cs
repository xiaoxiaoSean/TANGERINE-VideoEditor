using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Local source preview, proxy generation, relinking, and collection.</summary>
public partial class MainWindow
{
    private async void OpenSelectedSourceMonitor()
    {
        long request = ++_sourceMonitorRequest;
        MediaAsset? asset = _sourceMonitorOverride ?? SelectedMedia();
        _sourceMonitorOverride = null;
        double? initialSeconds = _sourceMonitorInitialSeconds;
        _sourceMonitorInitialSeconds = null;
        if (asset is null) return;
        ProjectDocument sourceProject = _project;
        long sourceGeneration = _projectGeneration;
        long sourceRevision = _projectRevision;
        string sourcePath = asset.SourcePath;
        string? proxyPath = asset.ProxyPath;
        bool usingProxy = asset.UseProxy;
        bool IsCurrentAssetSource() => request == _sourceMonitorRequest &&
            ReferenceEquals(sourceProject, _project) && sourceGeneration == _projectGeneration &&
            sourceProject.Media.Contains(asset) && !_experienceLifetime.IsCancellationRequested &&
            asset.SourcePath == sourcePath && asset.ProxyPath == proxyPath && asset.UseProxy == usingProxy;
        string[] sourceCandidates = asset.UseProxy && !string.IsNullOrWhiteSpace(asset.ProxyPath)
            ? [asset.ProxyPath, asset.SourcePath] : [asset.SourcePath];
        string? path = await Task.Run(() => sourceCandidates.FirstOrDefault(File.Exists));
        if (!IsCurrentAssetSource() || sourceRevision != _projectRevision) return;
        if (path is null)
        { ShowProjectMediaError("PMEDA0010", "源媒体已离线，请重新链接。"); return; }
        if (asset.UseProxy && !string.IsNullOrWhiteSpace(asset.ProxyPath))
            asset.SetProxyAvailability(string.Equals(path, asset.ProxyPath, StringComparison.OrdinalIgnoreCase));
        var window = new Window
        {
            Title = $"源监视器 · {asset.Name}", Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Background = System.Windows.Media.Brushes.Black,
            Foreground = System.Windows.Media.Brushes.White,
            SizeToContent = SizeToContent.Manual,
            WindowState = WindowState.Normal
        };
        bool IsCurrentSource() => IsCurrentAssetSource();
        bool CanEditSource()
        {
            if (IsCurrentSource()) return true;
            window.Close();
            return false;
        }
        // Place the independent source viewer over the library column. The
        // program viewer remains visible beside it while the user marks a
        // source range or inserts that range into the targeted timeline track.
        Point libraryOrigin = UpperWorkspaceGrid.TranslatePoint(new Point(0, 0), this);
        Rect workArea = SystemParameters.WorkArea;
        window.Width = Math.Clamp(LibraryWorkspaceColumn.ActualWidth,
            workArea.Width * .3, workArea.Width * .8);
        window.Height = Math.Clamp(UpperWorkspaceGrid.ActualHeight,
            workArea.Height * .4, workArea.Height * .9);
        window.Left = Math.Clamp(Left + libraryOrigin.X, workArea.Left,
            Math.Max(workArea.Left, workArea.Right - window.Width));
        window.Top = Math.Clamp(Top + libraryOrigin.Y, workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - window.Height));
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        window.Content = layout;
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = System.Windows.Media.Stretch.Uniform };
        if (asset.Kind == MediaKind.Image)
        {
            var image = new Image { Stretch = System.Windows.Media.Stretch.Uniform };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var completion = new TaskCompletionSource<System.Windows.Media.Imaging.BitmapSource>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    var worker = new Thread(() =>
                    {
                        try
                        {
                            using var stream = new FileStream(path, FileMode.Open,
                                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                            bitmap.BeginInit();
                            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                            bitmap.StreamSource = stream;
                            bitmap.EndInit();
                            bitmap.Freeze();
                            completion.TrySetResult(bitmap);
                        }
                        catch (Exception exception) { completion.TrySetException(exception); }
                    }) { IsBackground = true };
                    worker.SetApartmentState(ApartmentState.STA);
                    worker.Start();
                    var bitmap = await completion.Task;
                    if (window.IsVisible && IsCurrentSource()) image.Source = bitmap;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UriFormatException or NotSupportedException)
                { if (window.IsVisible && IsCurrentSource()) ShowProjectMediaError("PMEDA0011", "无法打开源图片。", exception); }
            };
            layout.Children.Add(image);
        }
        else
        {
            try { player.Source = new Uri(path); }
            catch (UriFormatException exception)
            { ShowProjectMediaError("PMEDA0011", "无法打开源媒体。", exception); return; }
            layout.Children.Add(player);
            player.MediaFailed += (_, args) =>
            { if (window.IsVisible && IsCurrentSource()) ShowProjectMediaError("PMEDA0011", "当前系统解码器无法预览该媒体。", args.ErrorException); };
            player.MediaOpened += (_, _) =>
            {
                if (!CanEditSource()) return;
                player.Position = TimeSpan.FromSeconds(initialSeconds ?? asset.SourceInSeconds);
            };
            window.Loaded += (_, _) => player.Play();
            window.Closed += (_, _) => player.Close();
        }
        var controls = new StackPanel { Margin = new Thickness(12) };
        Grid.SetRow(controls, 1);
        layout.Children.Add(controls);
        bool sourcePlaying = asset.Kind != MediaKind.Image;
        var slider = new Slider { Minimum = 0,
            Maximum = asset.DurationSeconds is > 0 ? asset.DurationSeconds.Value : 3600,
            Value = Math.Min(initialSeconds ?? asset.SourceInSeconds, asset.DurationSeconds ?? 3600) };
        controls.Children.Add(slider);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(buttons);
        var clock = new TextBlock { Text = "00:00:00", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0) };
        buttons.Children.Add(clock);
        var ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        ticker.Tick += (_, _) =>
        {
            if (!CanEditSource()) return;
            if (asset.Kind == MediaKind.Image || slider.IsMouseCaptureWithin) return;
            slider.Value = Math.Clamp(player.Position.TotalSeconds, 0, slider.Maximum);
            clock.Text = player.Position.ToString(@"hh\:mm\:ss");
        };
        slider.ValueChanged += (_, _) =>
        {
            if (!CanEditSource()) return;
            if (asset.Kind != MediaKind.Image && slider.IsMouseCaptureWithin)
                player.Position = TimeSpan.FromSeconds(slider.Value);
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!CanEditSource()) return;
            if (asset.Kind != MediaKind.Image) player.Position = TimeSpan.FromSeconds(slider.Value);
        };
        window.Loaded += (_, _) => ticker.Start();
        window.Closed += (_, _) => ticker.Stop();
        buttons.Children.Add(NewToolButton("▶ / 暂停", (_, _) =>
        {
            if (!CanEditSource()) return;
            if (asset.Kind == MediaKind.Image) return;
            if (sourcePlaying) player.Pause(); else player.Play();
            sourcePlaying = !sourcePlaying;
        }));
        buttons.Children.Add(NewToolButton("设为入点", (_, _) =>
        { if (CanEditSource()) SetSourcePoint(asset, true, asset.Kind == MediaKind.Image ? 0 : player.Position.TotalSeconds); }));
        buttons.Children.Add(NewToolButton("设为出点", (_, _) =>
        { if (CanEditSource()) SetSourcePoint(asset, false, asset.Kind == MediaKind.Image ? 0 : player.Position.TotalSeconds); }));
        buttons.Children.Add(NewToolButton("添加源标记", (_, _) =>
        {
            if (!CanEditSource()) return;
            string? name = PromptText("源标记", "名称", "标记");
            if (string.IsNullOrWhiteSpace(name) || !CanEditSource()) return;
            SaveUndoState();
            asset.Markers.Add(new TimelineMarker { Name = name.Trim(), TimeSeconds = player.Position.TotalSeconds });
            CommitProjectMediaChange("已添加源标记");
        }));
        buttons.Children.Add(NewToolButton("插入到目标轨道", (_, _) =>
        {
            if (!CanEditSource()) return;
            // Source In/Out lives on the asset. Reuse the media library drop
            // path so the timeline receives the same non-destructive range.
            LibraryItem? item = _project.Media.Where(media => media.Id == asset.Id)
                .Select(media => new LibraryItem(media.Id, media.Name, string.Empty, media.LabelColor, 0, media))
                .FirstOrDefault();
            if (item is null) return;
            AddLibraryItemToTimeline(item, _project.Sequence.PlayheadSeconds, -1);
            window.Close();
        }));
        _sourceMonitor?.Close();
        _sourceMonitor = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_sourceMonitor, window)) _sourceMonitor = null; };
        window.Show();
    }

    private Window? _sourceMonitor;
    private long _sourceMonitorRequest;

    private void SetSourcePoint(MediaAsset asset, bool isIn, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 ||
            (asset.DurationSeconds is > 0 && seconds > asset.DurationSeconds))
        { ShowProjectMediaError("PMEDA0012", "源时间超出媒体范围。"); return; }
        if (isIn && asset.SourceOutSeconds is double outPoint && seconds >= outPoint ||
            !isIn && seconds <= asset.SourceInSeconds)
        { ShowProjectMediaError("PMEDA0012", "出点必须晚于入点。"); return; }
        SaveUndoState();
        if (isIn) asset.SourceInSeconds = seconds; else asset.SourceOutSeconds = seconds;
        CommitProjectMediaChange(isIn ? "已设置源入点" : "已设置源出点");
    }

    /// <summary>
    /// Measure the archive after one source or proxy path is replaced. Offline
    /// references stay in the editor for later relinking, but cannot be read
    /// or embedded until they are repaired.
    /// </summary>
    private async Task<bool> EnsureMediaReplacementCapacityAsync(MediaAsset replacement,
        bool replacingSource, string newPath, ProjectDocument expectedProject,
        long expectedGeneration, long expectedRevision)
    {
        string[] existing = expectedProject.Media.SelectMany(asset =>
            ReferenceEquals(asset, replacement)
                ? replacingSource ? new[] { asset.ProxyPath } : new[] { asset.SourcePath }
                : new[] { asset.SourcePath, asset.ProxyPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        string? destination = _projectPath;
        string[] projected = await Task.Run(() => existing.Where(File.Exists)
            .Append(newPath).ToArray());
        if (destination is null)
            await ProjectFileService.EnsureImportCapacityAsync(projected);
        else
            await ProjectFileService.EnsureImportCapacityAsync(destination, projected);
        if (IsCurrentMediaOperation(expectedProject, expectedGeneration, expectedRevision) && expectedProject.Media.Contains(replacement) &&
            destination == _projectPath) return true;
        if (IsCurrentMediaOperation(expectedProject, expectedGeneration) && expectedProject.Media.Contains(replacement))
            StatusText.Text = "空间检查期间项目已修改，请重试";
        return false;
    }

    private bool IsCurrentMediaOperation(ProjectDocument owner, long generation, long? revision = null) =>
        ReferenceEquals(owner, _project) && generation == _projectGeneration &&
        (revision is null || revision.Value == _projectRevision) && !_experienceLifetime.IsCancellationRequested;

    /// <summary>Save immediately when every required media path is available.</summary>
    private async Task SaveAfterMediaReplacementAsync()
    {
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        string[] paths = targetProject.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        bool allAvailable = await Task.Run(() => paths.All(File.Exists));
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevision != _projectRevision) return;
        if (!allAvailable)
        {
            StatusText.Text = "媒体引用已更新；仍有离线文件，请全部重新链接后保存项目";
            return;
        }
        if (_projectPath is string destination)
            await SaveProjectToAsync(destination);
        else
            await SaveProjectAsAsync();
    }

    private async void AttachSelectedProxy()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null || asset.Kind == MediaKind.Image) return;
        var dialog = new OpenFileDialog { Title = "附加本地代理文件", CheckFileExists = true,
            Filter = "媒体文件|*.mp4;*.mov;*.mkv;*.webm;*.mp3;*.wav;*.m4a|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        try
        {
            if (!await EnsureMediaReplacementCapacityAsync(asset, false, dialog.FileName,
                targetProject, targetGeneration, targetRevision)) return;
        }
        catch (ProjectFileException exception)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision) && targetProject.Media.Contains(asset))
                ShowProjectError(exception);
            return;
        }
        SaveUndoState();
        asset.ProxyPath = dialog.FileName;
        asset.UseProxy = true;
        asset.SetProxyAvailability(true);
        CommitProjectMediaChange("代理已附加，预览将使用代理", true);
        await SaveAfterMediaReplacementAsync();
    }

    private async void ToggleSelectedProxy()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null || string.IsNullOrWhiteSpace(asset.ProxyPath))
        { ShowProjectMediaError("PMEDA0020", "此素材没有可用的代理文件。"); return; }
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        bool proxyExists = await Task.Run(() => File.Exists(asset.ProxyPath));
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevision != _projectRevision || !targetProject.Media.Contains(asset)) return;
        if (!proxyExists)
        { ShowProjectMediaError("PMEDA0020", "此素材没有可用的代理文件。"); return; }
        SaveUndoState();
        asset.UseProxy = !asset.UseProxy;
        if (asset.UseProxy) asset.SetProxyAvailability(true);
        CommitProjectMediaChange(asset.UseProxy ? "已启用代理预览" : "已切换回原始媒体", true);
    }

    private async Task CreateSelectedProxyAsync()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null || _proxyInProgress || asset.Kind == MediaKind.Image) return;
        ProjectDocument owner = _project;
        long generation = _projectGeneration;
        // Reserve the operation before preflight yields. A slow source check
        // must not admit a second proxy worker that later releases its slot.
        _proxyInProgress = true;
        try { await CreateProxyForAssetAsync(asset); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or NotSupportedException or ProjectFileException)
        {
            if (ReferenceEquals(owner, _project) && generation == _projectGeneration && owner.Media.Contains(asset))
                ShowProjectMediaError("PMEDA0023", "创建本地代理失败。", exception);
        }
        finally { _proxyInProgress = false; }
    }

    private async Task CreateProxyForAssetAsync(MediaAsset asset)
    {
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevisionBeforeSourceCheck = _projectRevision;
        bool sourceExists = await Task.Run(() => File.Exists(asset.SourcePath));
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevisionBeforeSourceCheck != _projectRevision || !targetProject.Media.Contains(asset)) return;
        if (!sourceExists)
        { ShowProjectMediaError("PMEDA0021", "源媒体已离线。"); return; }
        var dialog = new SaveFileDialog { Title = "保存本地代理", DefaultExt = ".mp4", AddExtension = true,
            Filter = "MP4 代理|*.mp4", FileName = Path.GetFileNameWithoutExtension(asset.SourcePath) + ".proxy.mp4",
            OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        string output = dialog.FileName;
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevisionBeforeSourceCheck != _projectRevision || !targetProject.Media.Contains(asset)) return;
        string[] referencedPaths = targetProject.Media.SelectMany(item => new[] { item.SourcePath, item.ProxyPath })
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
        bool outputInUse;
        try
        {
            outputInUse = await Task.Run(() => referencedPaths.Any(path =>
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevisionBeforeSourceCheck) && targetProject.Media.Contains(asset))
                ShowProjectMediaError("PMEDA0022", "无法检查代理目标路径。", exception);
            return;
        }
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevisionBeforeSourceCheck != _projectRevision || !targetProject.Media.Contains(asset)) return;
        if (outputInUse)
        { ShowProjectMediaError("PMEDA0022", "代理目标是当前工程正在使用的媒体文件，请选择其他路径。"); return; }
        bool outputExists = await Task.Run(() => File.Exists(output));
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevisionBeforeSourceCheck != _projectRevision || !targetProject.Media.Contains(asset)) return;
        if (outputExists && MessageBox.Show(this, "目标代理文件已存在，覆盖它吗？", "覆盖代理",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevisionBeforeSourceCheck != _projectRevision || !targetProject.Media.Contains(asset)) return;
        string temporaryOutput = Path.Combine(Path.GetDirectoryName(output)!,
            $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.mp4");
        string sourcePath = asset.SourcePath;
        string? previousProxyPath = asset.ProxyPath;
        bool previouslyUsingProxy = asset.UseProxy;
        bool IsCurrentProxyTarget() => ReferenceEquals(targetProject, _project) &&
            targetGeneration == _projectGeneration && targetProject.Media.Contains(asset) &&
            asset.SourcePath == sourcePath && asset.ProxyPath == previousProxyPath &&
            asset.UseProxy == previouslyUsingProxy && !_experienceLifetime.IsCancellationRequested;
        StatusText.Text = "正在创建本地代理…";
        try
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            if (!File.Exists(executable)) executable = "ffmpeg";
            var start = new ProcessStartInfo(executable) { UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            var options = new List<string> { "-y", "-i", sourcePath };
            if (asset.Kind == MediaKind.Video)
                options.AddRange(["-vf", "scale='min(1280,iw)':-2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "28"]);
            options.AddRange(["-c:a", "aac", "-b:a", "128k", temporaryOutput]);
            foreach (string argument in options)
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            await Task.Run(() => process.Start());
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            string details = await error;
            if (process.ExitCode != 0 || !await Task.Run(() => File.Exists(temporaryOutput)))
                throw new ProjectFileException(ProjectStageCode.Write,
                    "无法生成代理。" + Environment.NewLine + details[^Math.Min(500, details.Length)..]);
            if (!IsCurrentProxyTarget()) return;
            long targetRevision = _projectRevision;
            if (!await EnsureMediaReplacementCapacityAsync(asset, false, temporaryOutput,
                targetProject, targetGeneration, targetRevision)) return;
            if (!IsCurrentProxyTarget()) return;
            string outputFullPath = Path.GetFullPath(output);
            if (targetProject.Media.SelectMany(item => new[] { item.SourcePath, item.ProxyPath })
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Any(path => string.Equals(Path.GetFullPath(path!), outputFullPath, StringComparison.OrdinalIgnoreCase)) ||
                _projectPath is not null && string.Equals(Path.GetFullPath(_projectPath), outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                ShowProjectMediaError("PMEDA0022", "代理目标在生成期间已被当前工程使用，请选择其他路径。");
                return;
            }
            // The final same-directory rename and model commit share one UI
            // turn. No relink or project switch can claim the destination
            // between its last ownership check and publication.
            File.Move(temporaryOutput, output, overwrite: true);
            SaveUndoState();
            asset.ProxyPath = output;
            asset.UseProxy = true;
            asset.SetProxyAvailability(true);
            CommitProjectMediaChange("本地代理已生成", true);
            await SaveAfterMediaReplacementAsync();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ProjectFileException)
        { if (IsCurrentProxyTarget()) ShowProjectMediaError("PMEDA0023", "创建本地代理失败。", exception); }
        finally
        {
            try { await Task.Run(() => { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Debug.WriteLine($"PMEDA0024: {exception}"); }
        }
    }

    private async void RelinkSelectedMedia()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null) return;
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        var dialog = new OpenFileDialog { Title = $"重新链接 · {asset.Name}", CheckFileExists = true,
            Filter = "所有文件|*.*", FileName = Path.GetFileName(asset.SourcePath) };
        if (dialog.ShowDialog(this) != true) return;
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevision != _projectRevision || !targetProject.Media.Contains(asset)) return;
        if (DetectMediaKind(dialog.FileName) != asset.Kind)
        { ShowProjectMediaError("PMEDA0030", "新文件类型与原素材不一致。"); return; }
        try
        {
            if (!await EnsureMediaReplacementCapacityAsync(asset, true, dialog.FileName,
                targetProject, targetGeneration, targetRevision)) return;
        }
        catch (ProjectFileException exception)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision) && targetProject.Media.Contains(asset))
                ShowProjectError(exception);
            return;
        }
        DateTimeOffset? fileModified = null;
        try { fileModified = await Task.Run(() => new DateTimeOffset(File.GetLastWriteTimeUtc(dialog.FileName))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Debug.WriteLine($"MED-RELINK-DATE: {exception}"); }
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevision != _projectRevision || !targetProject.Media.Contains(asset)) return;
        SaveUndoState();
        if (!string.Equals(asset.SourcePath, dialog.FileName, StringComparison.OrdinalIgnoreCase))
            ForgetBuiltinMusicProvenance(asset);
        asset.SourcePath = dialog.FileName;
        if (fileModified is DateTimeOffset modified) asset.FileModifiedUtc = modified;
        CommitProjectMediaChange("媒体已重新链接", true);
        _mediaMetadataCancellation ??= new CancellationTokenSource();
        _ = PopulateMediaMetadataAsync(_project, [asset], _mediaMetadataCancellation.Token);
        await SaveAfterMediaReplacementAsync();
    }

    private async void RelinkProxyMedia()
    {
        MediaAsset? asset = SelectedMedia();
        if (asset is null || string.IsNullOrWhiteSpace(asset.ProxyPath)) return;
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        var dialog = new OpenFileDialog { Title = $"重新链接代理 · {asset.Name}", CheckFileExists = true,
            Filter = "媒体文件|*.mp4;*.mov;*.mkv;*.webm;*.mp3;*.wav;*.m4a|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!ReferenceEquals(targetProject, _project) || targetGeneration != _projectGeneration ||
            targetRevision != _projectRevision || !targetProject.Media.Contains(asset)) return;
        try
        {
            if (!await EnsureMediaReplacementCapacityAsync(asset, false, dialog.FileName,
                targetProject, targetGeneration, targetRevision)) return;
        }
        catch (ProjectFileException exception)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision) && targetProject.Media.Contains(asset))
                ShowProjectError(exception);
            return;
        }
        SaveUndoState();
        asset.ProxyPath = dialog.FileName;
        asset.SetProxyAvailability(true);
        CommitProjectMediaChange("代理媒体已重新链接", true);
        await SaveAfterMediaReplacementAsync();
    }

    private async void RelinkOfflineMedia()
    {
        ProjectDocument mediaProject = _project;
        long mediaGeneration = _projectGeneration;
        long mediaRevision = _projectRevision;
        MediaAsset[] mediaSnapshot = _project.Media.ToArray();
        string[] missingIds;
        try
        {
            missingIds = await Task.Run(() => mediaSnapshot
                .Where(asset => !File.Exists(asset.SourcePath)).Select(asset => asset.Id).ToArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (IsCurrentMediaOperation(mediaProject, mediaGeneration, mediaRevision))
                ShowProjectMediaError("PMEDA0031", "无法检查离线媒体。", exception);
            return;
        }
        if (!ReferenceEquals(mediaProject, _project) || mediaGeneration != _projectGeneration ||
            mediaRevision != _projectRevision) return;
        MediaAsset[] offline = _project.Media.Where(asset => missingIds.Contains(asset.Id)).ToArray();
        if (offline.Length == 0) { StatusText.Text = "没有离线媒体"; return; }
        var dialog = new OpenFolderDialog { Title = "选择包含媒体文件的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        ProjectDocument targetProject = _project;
        long targetGeneration = _projectGeneration;
        long targetRevision = _projectRevision;
        var offlineSnapshot = offline.Select(asset => (asset.Id, asset.SourcePath, asset.Kind, asset.FileSizeBytes)).ToArray();
        StatusText.Text = "正在扫描离线媒体…";
        try
        {
            string searchFolder = dialog.FolderName;
            var matches = await Task.Run(() =>
            {
                var wantedNames = offlineSnapshot.Select(asset => Path.GetFileName(asset.SourcePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in Directory.EnumerateFiles(searchFolder, "*",
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    string fileName = Path.GetFileName(path);
                    if (!wantedNames.Contains(fileName)) continue;
                    if (!byName.TryGetValue(fileName, out List<string>? candidates))
                        byName[fileName] = candidates = [];
                    candidates.Add(path);
                }
                var foundMatches = new List<(string AssetId, string Path)>();
                foreach (var asset in offlineSnapshot)
                {
                    if (!byName.TryGetValue(Path.GetFileName(asset.SourcePath), out List<string>? candidates)) continue;
                    string? found = candidates.FirstOrDefault(path => DetectMediaKind(path) == asset.Kind &&
                        (asset.FileSizeBytes is null || new FileInfo(path).Length == asset.FileSizeBytes));
                    found ??= candidates.FirstOrDefault(path => DetectMediaKind(path) == asset.Kind);
                    if (found is not null) foundMatches.Add((asset.Id, found));
                }
                return foundMatches;
            });
            if (!IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision))
            {
                if (IsCurrentMediaOperation(targetProject, targetGeneration)) StatusText.Text = "扫描期间项目已修改，请重新链接";
                return;
            }
            if (matches.Count == 0) { ShowProjectMediaError("PMEDA0032", "没有找到名称与类型匹配的媒体。"); return; }
            // A relink introduces new source bytes into the portable archive.
            // Verify the destination before changing any project references.
            var replacementById = matches.ToDictionary(match => match.AssetId, match => match.Path,
                StringComparer.Ordinal);
            MediaAsset[] capacityAssets = _project.Media.ToArray();
            string? capacityProjectPath = _projectPath;
            string[] projectedPaths = await Task.Run(() => capacityAssets.SelectMany(asset =>
                    new[] { replacementById.GetValueOrDefault(asset.Id, asset.SourcePath), asset.ProxyPath })
                .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
                .Where(File.Exists).ToArray());
            if (capacityProjectPath is string projectPath)
                await ProjectFileService.EnsureImportCapacityAsync(projectPath, projectedPaths);
            else
                await ProjectFileService.EnsureImportCapacityAsync(projectedPaths);
            if (!IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision) || capacityProjectPath != _projectPath)
            {
                if (IsCurrentMediaOperation(targetProject, targetGeneration)) StatusText.Text = "空间检查期间项目已修改，请重新链接";
                return;
            }
            SaveUndoState();
            foreach ((string id, string path) in matches)
            {
                MediaAsset? current = _project.Media.FirstOrDefault(item => item.Id == id);
                if (current is not null)
                {
                    if (!string.Equals(current.SourcePath, path, StringComparison.OrdinalIgnoreCase))
                        ForgetBuiltinMusicProvenance(current);
                    current.SourcePath = path;
                }
            }
            CommitProjectMediaChange($"已重新链接 {matches.Count} 个媒体；剩余源媒体 {offline.Length - matches.Count} 个离线", true);
            await SaveAfterMediaReplacementAsync();
        }
        catch (ProjectFileException exception)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision)) ShowProjectError(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (IsCurrentMediaOperation(targetProject, targetGeneration, targetRevision))
                ShowProjectMediaError("PMEDA0033", "扫描媒体文件夹失败。", exception);
        }
    }

    private async Task ConsolidateProjectAsync()
    {
        if (_consolidationInProgress) return;
        var dialog = new OpenFolderDialog { Title = "选择整合副本的目标文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        string destination;
        try { destination = Path.GetFullPath(dialog.FolderName); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { ShowProjectMediaError("PMEDA0040", "整合目标文件夹无效。", exception); return; }
        ProjectDocument sourceProject = _project;
        long sourceRevision = _projectRevision;
        long sourceGeneration = _projectGeneration;
        _consolidationInProgress = true;
        StatusText.Text = "正在整合项目副本…";
        try
        {
            ProjectDocument snapshot = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(sourceProject))!);
            if (!IsCurrentMediaOperation(sourceProject, sourceGeneration, sourceRevision))
            {
                if (IsCurrentMediaOperation(sourceProject, sourceGeneration))
                    ShowProjectMediaError("PMEDA0041", "准备整合期间项目已修改，请重试。");
                return;
            }
            string[] sources = snapshot.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
                .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
            bool anyOffline = await Task.Run(() => sources.Any(path => !File.Exists(path)));
            if (!IsCurrentMediaOperation(sourceProject, sourceGeneration, sourceRevision))
            {
                if (IsCurrentMediaOperation(sourceProject, sourceGeneration))
                    ShowProjectMediaError("PMEDA0041", "检查媒体期间项目已修改，请重试。");
                return;
            }
            if (anyOffline)
            { ShowProjectMediaError("PMEDA0042", "项目中有离线媒体，请先重新链接。"); return; }
            string projectFile = Path.Combine(destination,
                $"{SafeFileName(snapshot.Name)}_整合_{Guid.NewGuid():N}.tvep");
            // The archive writer embeds every source and proxy itself; an
            // extra sibling media directory would defeat portability.
            await ProjectFileService.SaveAsync(snapshot, projectFile);
            if (!IsCurrentMediaOperation(sourceProject, sourceGeneration, sourceRevision))
            {
                if (IsCurrentMediaOperation(sourceProject, sourceGeneration))
                    StatusText.Text = $"项目副本已保存到 {projectFile}；当前项目随后已有新修改";
                return;
            }
            if (IsCurrentMediaOperation(sourceProject, sourceGeneration, sourceRevision))
                StatusText.Text = $"项目副本已整合到 {projectFile}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProjectFileException or ArgumentException)
        {
            if (IsCurrentMediaOperation(sourceProject, sourceGeneration, sourceRevision))
                ShowProjectMediaError("PMEDA0043", "整合项目副本失败。", exception);
        }
        finally { _consolidationInProgress = false; }
    }

    private static string SafeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "项目" : name;
    }
}
