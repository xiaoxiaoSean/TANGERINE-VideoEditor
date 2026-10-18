using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private readonly DispatcherTimer _idlePreviewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly MediaElement _cachedPreviewPlayer = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
        Stretch = Stretch.Fill, Volume = 0, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _idlePreviewCancellation;
    private long _lastPreviewInput = Environment.TickCount64;
    private long _idleReadyRevision = -1;
    private long _idleReadyGeneration = -1;
    private long _idleAttemptRevision = -1;
    private long _idleAttemptGeneration = -1;
    private bool _idlePreviewBusy;
    private bool _cachedPreviewActive;
    private string? _idleReadyPath;
    private double _idleReadyDuration;
    private bool _cachedPreviewSeekPending;
    private string? _cachedPreviewLoadedPath;
    private static string IdlePreviewDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TANGERINE-VideoEditor", "cache", "timeline-v2");

    private void InitializeIdlePreview()
    {
        PreviewVisualLayer.Children.Add(_cachedPreviewPlayer);
        _cachedPreviewPlayer.MediaOpened += (_, _) => { _cachedPreviewSeekPending = true; if (_isPlaying) _cachedPreviewPlayer.Play(); };
        _cachedPreviewPlayer.MediaFailed += (_, args) =>
        {
            _idleReadyRevision = -1; StopCachedPreview();
            StatusText.Text = $"后台预览读取失败 · PREVIEW-CACHE-PLAY：{args.ErrorException.Message}";
        };
        // Input only defers/cancels speculative work; it never changes the project.
        PreviewKeyDown += (_, _) => DeferIdlePreview();
        PreviewMouseDown += (_, _) => DeferIdlePreview();
        PreviewMouseWheel += (_, _) => _lastPreviewInput = Environment.TickCount64;
        PreviewMouseMove += (_, args) => { if (args.LeftButton == MouseButtonState.Pressed) DeferIdlePreview(); };
        _idlePreviewTimer.Tick += async (_, _) =>
        {
            if (_idlePreviewBusy && (_isPlaying || _exportInProgress || McpControlBusy)) { _idlePreviewCancellation?.Cancel(); return; }
            if (_exportInProgress || McpControlBusy || _isPlaying || _catalogPreviewActive || _catalogPreviewPending || Environment.TickCount64 - _lastPreviewInput < 2000 || _idlePreviewBusy ||
                _idleAttemptRevision == _projectRevision && _idleAttemptGeneration == _projectGeneration) return;
            await BuildIdlePreviewAsync();
        };
        _idlePreviewTimer.Start();
        Closed += (_, _) => { _idlePreviewTimer.Stop(); _idlePreviewCancellation?.Cancel(); _cachedPreviewPlayer.Close(); };
    }

    private void DeferIdlePreview()
    {
        _lastPreviewInput = Environment.TickCount64;
        if (_idlePreviewBusy) _idlePreviewCancellation?.Cancel();
    }

    private void InvalidateIdlePreview()
    {
        DeferIdlePreview();
        _idleReadyRevision = -1;
        _idleAttemptRevision = -1;
        StopCachedPreview();
    }

    private void StopCachedPreview()
    {
        if (!_cachedPreviewActive) return;
        _cachedPreviewPlayer.Pause(); _cachedPreviewPlayer.Visibility = Visibility.Collapsed;
        _cachedPreviewActive = false; PreviewOverlay.Opacity = 1;
    }

    private bool TryShowCachedPreview(double seconds)
    {
        if (!_isPlaying || _playbackDirection != 1 || _scrubbingPlayhead ||
            _idleReadyRevision != _projectRevision || _idleReadyGeneration != _projectGeneration ||
            _idleReadyPath is null || seconds >= _idleReadyDuration) { StopCachedPreview(); return false; }
        if (!_cachedPreviewActive)
        {
            ClearRenderedPreviewFrame();
            _cachedPreviewPlayer.Width = PreviewVisualLayer.Width; _cachedPreviewPlayer.Height = PreviewVisualLayer.Height;
            if (_cachedPreviewLoadedPath != _idleReadyPath)
            {
                _cachedPreviewPlayer.Source = new Uri(_idleReadyPath); _cachedPreviewLoadedPath = _idleReadyPath;
            }
            _cachedPreviewSeekPending = true;
            _cachedPreviewPlayer.Visibility = Visibility.Visible;
            PreviewImage.Visibility = PreviewMediaElement.Visibility = PreviewPlaceholder.Visibility = Visibility.Collapsed;
            PreviewMediaElement.Pause();
            PreviewOverlay.Opacity = 0; _cachedPreviewActive = true; _cachedPreviewPlayer.Play();
        }
        if (_cachedPreviewSeekPending || Math.Abs(_cachedPreviewPlayer.Position.TotalSeconds - seconds) > .18)
        {
            _cachedPreviewPlayer.Position = TimeSpan.FromSeconds(seconds); _cachedPreviewSeekPending = false;
        }
        // Audio stays on the existing monitor to preserve live mute/solo controls.
        return true;
    }

    private async Task BuildIdlePreviewAsync()
    {
        _idlePreviewBusy = true;
        long revision = _projectRevision, generation = _projectGeneration;
        _idleAttemptRevision = revision; _idleAttemptGeneration = generation;
        _idlePreviewCancellation?.Dispose();
        var cancellation = _idlePreviewCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        string? scratch = null;
        try
        {
            ProjectDocument snapshot = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(_project))!;
            double targetWidth = Math.Max(16, PreviewVisualLayer.Width) * Math.Max(1, PreviewZoomSlider.Value) * PreviewResolutionRatio;
            double targetHeight = targetWidth * snapshot.Sequence.Height / snapshot.Sequence.Width;
            double limit = Math.Min(1, 4096 / Math.Max(targetWidth, targetHeight));
            int width = Math.Max(16, (int)Math.Round(targetWidth * limit / 2) * 2);
            int height = Math.Max(16, (int)Math.Round(targetHeight * limit / 2) * 2);
            var plan = await Task.Run(() => ProjectPreviewCache.Plan(snapshot, width, height), token);
            if (plan.Count == 0) return;
            Directory.CreateDirectory(IdlePreviewDirectory);
            scratch = Path.Combine(IdlePreviewDirectory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
            string finalKey = ProjectPreviewCache.Hash(string.Join("|", plan.Select(s => s.Key)));
            string final = Path.Combine(IdlePreviewDirectory, finalKey + ".mp4");
            if (!File.Exists(final))
            {
                var titles = await RenderTitlesAsync(snapshot, scratch, token);
                var profile = _previewGpuMode == "CPU 软件渲染" ? PreviewEncodingProfile.Software :
                    await PreviewEncodingProfile.SelectAsync(TimelineExporter.ResolveEncoderPath(), token);
                int completed = 0;
                foreach (var segment in plan)
                {
                    token.ThrowIfCancellationRequested();
                    string path = Path.Combine(IdlePreviewDirectory, segment.Key + ".mp4");
                    if (!File.Exists(path))
                    {
                        try
                        {
                            await Task.Run(() => TimelineExporter.ExportMp4Async(snapshot, path, titles, cancellationToken: token,
                                settings: new VideoExportSettings(Width: width, Height: height, StartSeconds: segment.Start,
                                    EndSeconds: segment.End), previewEncoding: profile), token);
                        }
                        catch (ExportException) when (profile.IsHardwareAccelerated)
                        {
                            token.ThrowIfCancellationRequested();
                            profile.MarkUnavailable(); profile = PreviewEncodingProfile.Software;
                            await Task.Run(() => TimelineExporter.ExportMp4Async(snapshot, path, titles, cancellationToken: token,
                                settings: new VideoExportSettings(Width: width, Height: height, StartSeconds: segment.Start,
                                    EndSeconds: segment.End), previewEncoding: PreviewEncodingProfile.Software), token);
                        }
                    }
                    completed++;
                    if (revision == _projectRevision && generation == _projectGeneration)
                        StatusText.Text = $"后台预览缓存 {completed}/{plan.Count} · {profile.DisplayName}";
                }
                string manifest = Path.Combine(scratch, "segments.txt");
                await File.WriteAllLinesAsync(manifest, plan.SelectMany(s => new[]
                {
                    "file '" + Path.Combine(IdlePreviewDirectory, s.Key + ".mp4").Replace("'", "'\\''") + "'",
                    "duration " + (s.End - s.Start).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                }), token);
                string assembled = Path.Combine(scratch, "assembled.mp4");
                var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", manifest, "-map", "0:v:0", "-an", "-c", "copy", "-movflags", "+faststart", assembled }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("无法启动预览拼接器");
                using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                var errors = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                string error = await errors;
                if (process.ExitCode != 0) throw new IOException(error);
                token.ThrowIfCancellationRequested();
                File.Move(assembled, final, true);
            }
            if (revision != _projectRevision || generation != _projectGeneration || token.IsCancellationRequested) return;
            _idleReadyPath = final; _idleReadyRevision = revision; _idleReadyGeneration = generation; _idleReadyDuration = plan[^1].End;
            StatusText.Text = "整个工程的后台预览已就绪 · 编辑后仅重建受影响时间段";
            // Keep the active playlist and reusable intervals; prune old disposable files.
            var keep = plan.Select(s => s.Key + ".mp4").Append(finalKey + ".mp4").ToHashSet();
            await Task.Run(() =>
            {
                var files = new DirectoryInfo(IdlePreviewDirectory).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                long total = 0;
                foreach (var file in files)
                {
                    total += file.Length;
                    if (!keep.Contains(file.Name) && (total > 2L * 1024 * 1024 * 1024 || file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)))
                        try { file.Delete(); } catch (IOException) { }
                }
            }, token);
        }
        catch (OperationCanceledException) { _idleAttemptRevision = -1; }
        catch (Exception exception) { StatusText.Text = $"后台预览失败 · PREVIEW-CACHE-RENDER：{exception.Message}"; }
        finally
        {
            _idlePreviewBusy = false;
            if (scratch is not null) try { await Task.Run(() => Directory.Delete(scratch, true)); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Debug.WriteLine($"PREVIEW-CACHE-CLEANUP: {exception}"); }
        }
    }
}
