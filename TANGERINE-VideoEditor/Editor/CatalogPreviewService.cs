using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Preview generator for effects, transitions, and text presets.
/// Uses two built-in images to create quick 5-second demonstration videos.
/// User clicks an item -> immediately see what it looks like with sample images.
/// </summary>
public static class CatalogPreviewService
{
    private static readonly object PreviewProcessGate = new();
    private static readonly object SampleImagesGate = new();
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static CancellationTokenSource? _currentPreviewCancellation;
    private static Process? _currentPreviewProcess;

    // Built-in sample images (will be created if don't exist)
    private static readonly string SamplesDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "samples");

    private static readonly string Image1Path = Path.Combine(SamplesDirectory, "image1.jpg");
    private static readonly string Image2Path = Path.Combine(SamplesDirectory, "image2.jpg");
    private static readonly string SampleVersionPath = Path.Combine(SamplesDirectory, ".landscape-v2");

    /// <summary>
    /// Plays an effect preview: shows one image with the effect applied for 5 seconds.
    /// </summary>
    public static Task PlayEffectPreviewAsync(
        string effectId,
        string ffmpegPath,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        return PlayCachedPreviewAsync("effect|" + effectId, ffmpegPath, async (outputPath, token) =>
        {
            EnsureSampleImagesExist(ffmpegPath);
            PreviewEncodingProfile profile = await PreviewEncodingProfile.SelectAsync(ffmpegPath, token)
                .ConfigureAwait(false);
            string[] input = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-loop", "1",
                "-framerate", "30", "-i", Image1Path, "-t", "5", "-vf", GetEffectFilters(effectId),
                "-an", "-pix_fmt", "yuv420p", "-r", "30", "-movflags", "+faststart"];
            try
            {
                await RunFfmpegAsync(ffmpegPath, [.. input, .. profile.VideoArguments, outputPath],
                    token, progressCallback, 0, 100, 5).ConfigureAwait(false);
            }
            catch (ExportException exception) when (exception.StageCode == ExportStageCode.Encode &&
                profile.IsHardwareAccelerated && !token.IsCancellationRequested)
            {
                await RunFfmpegAsync(ffmpegPath, [.. input, .. PreviewEncodingProfile.Software.VideoArguments, outputPath],
                    token, progressCallback, 0, 100, 5).ConfigureAwait(false);
                profile.MarkUnavailable();
            }
        }, playVideoCallback, cancellationToken, progressCallback);
    }

    /// <summary>
    /// Plays a transition preview: Image1 -> Transition -> Image2 (5 seconds total).
    /// Structure: 2s Image1 + 1s Transition + 2s Image2
    /// </summary>
    public static Task PlayTransitionPreviewAsync(
        string transitionId,
        string ffmpegPath,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        return PlayCachedPreviewAsync("transition|" + transitionId, ffmpegPath, async (outputPath, token) =>
        {
            EnsureSampleImagesExist(ffmpegPath);
            // A catalog demonstration is an ordinary five-second project.
            // It uses the exact transition engine and default parameters used
            // when this preset is applied to imported media. There is no
            // preview-only xfade mapping or approximate substitute effect.
            ProjectDocument project = CreateDemoProject();
            MediaAsset first = DemoImage(Image1Path);
            MediaAsset second = DemoImage(Image2Path);
            project.Media.AddRange([first, second]);
            project.Tracks[0].Clips.Add(new TimelineClip
            {
                Kind = ClipKind.Image, Name = "画面 A", MediaAssetId = first.Id,
                DurationSeconds = 2
            });
            project.Tracks[0].Clips.Add(new TimelineClip
            {
                Kind = ClipKind.Image, Name = "画面 B", MediaAssetId = second.Id,
                StartSeconds = 2, DurationSeconds = 3,
                Transitions = [new ClipTransition { PresetId = transitionId, Name = transitionId, DurationSeconds = 1 }]
            });
            PreviewEncodingProfile profile = await PreviewEncodingProfile.SelectAsync(ffmpegPath, token)
                .ConfigureAwait(false);
            await TimelineExporter.ExportMp4Async(project, outputPath, new Dictionary<string, string>(),
                new DemoProgress(progressCallback), token, previewEncoding: profile, encoderPath: ffmpegPath)
                .ConfigureAwait(false);
        }, playVideoCallback, cancellationToken, progressCallback);
    }

    /// <summary>
    /// Plays a text preset preview: Image with text animation for 5 seconds.
    /// </summary>
    public static Task PlayTextPreviewAsync(
        string presetId,
        string sampleText,
        string ffmpegPath,
        Func<string, ClipText> styleProvider,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        bool isSticker = presetId.StartsWith("sticker.", StringComparison.Ordinal);
        ClipText style = styleProvider(isSticker ? "sticker" : presetId);
        style.Content = isSticker ? string.Empty : presetId switch
        {
            "title.counter" => "0 → 100",
            "title.credits" => $"{sampleText}\nDIRECTOR\nCAMERA\nEDITOR\nTHANK YOU",
            _ => sampleText
        };
        if (isSticker)
        {
            BuiltinPreset preset = BuiltinCatalog.Stickers.FirstOrDefault(item => item.Id == presetId)
                ?? throw new GraphicShapeException(GraphicShapeStageCode.Validate, "内置形状预设不存在。");
            var sticker = new TimelineClip { Kind = ClipKind.Text, Text = style };
            GraphicShape.FromPreset(preset, 960, 540).Save(sticker);
        }
        string key = "text|" + presetId + "|" + JsonSerializer.Serialize(style);
        return PlayCachedPreviewAsync(key, ffmpegPath, async (outputPath, token) =>
        {
            string directory = Path.Combine(Path.GetTempPath(), $"tangerine-title-demo-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(directory);
                ProjectDocument project = CreateDemoProject();
                // Presets are authored at 1080p. Reduce every pixel-based field
                // together to retain their proportions in the 540p demonstration.
                ScaleDemoStyle(style, .5);
                var clip = new TimelineClip
                {
                    Kind = ClipKind.Text, Name = sampleText, TemplateId = presetId,
                    DurationSeconds = 5, Text = style
                };
                project.Tracks[0].Clips.Add(clip);
                string titlePath = Path.Combine(directory, "title.png");
                await RenderTitleOnStaAsync(() =>
                {
                    if (DynamicTitlePreset.HasChangingText(clip))
                        titlePath = DynamicTitleFrames.Render(clip, project.Sequence, directory, token);
                    else TitleBitmapRenderer.Render(clip, project.Sequence, titlePath);
                }, token).ConfigureAwait(false);
                PreviewEncodingProfile profile = await PreviewEncodingProfile.SelectAsync(ffmpegPath, token)
                    .ConfigureAwait(false);
                await TimelineExporter.ExportMp4Async(project, outputPath,
                    new Dictionary<string, string> { [clip.Id] = titlePath },
                    new DemoProgress(progressCallback), token, previewEncoding: profile, encoderPath: ffmpegPath)
                    .ConfigureAwait(false);
            }
            finally
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }, playVideoCallback, cancellationToken, progressCallback);
    }

    private static async Task PlayCachedPreviewAsync(string content, string ffmpegPath,
        Func<string, CancellationToken, Task> render, Action<string> playVideoCallback,
        CancellationToken cancellationToken, Action<double>? progressCallback)
    {
        using var previewCancellation = BeginPreview(cancellationToken);
        CancellationToken token = previewCancellation.Token;
        string outputPath = Path.Combine(Path.GetTempPath(), $"tangerine-catalog-{Guid.NewGuid():N}.mp4");
        string key = CatalogPreviewCache.Key(ffmpegPath, content);
        bool keepOutput = false;
        bool entered = false;
        try
        {
            // A cancelled render drains its FFmpeg process before the next
            // expensive graph starts. Fast preset switching cannot accumulate
            // CPU workers or competing GPU sessions in the background.
            await RenderGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (!CatalogPreviewCache.TryCopy(key, outputPath))
            {
                await render(outputPath, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                CatalogPreviewCache.Store(key, outputPath, token);
            }
            token.ThrowIfCancellationRequested();
            progressCallback?.Invoke(100);
            playVideoCallback(outputPath);
            keepOutput = true;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!keepOutput) DeleteTemporaryFile(outputPath);
            if (entered) RenderGate.Release();
            ClearPreviewCancellation(previewCancellation);
        }
    }

    private static ProjectDocument CreateDemoProject()
    {
        ProjectDocument project = ProjectDocument.CreateDefault();
        project.Sequence.Width = 960;
        project.Sequence.Height = 540;
        project.Sequence.FramesPerSecond = 30;
        return project;
    }

    private static MediaAsset DemoImage(string path) => new()
    {
        Kind = MediaKind.Image, SourcePath = path, Width = 960, Height = 540
    };

    private sealed class DemoProgress(Action<double>? callback) : IProgress<double>
    {
        public void Report(double value) => callback?.Invoke(Math.Clamp(value * 100, 0, 100));
    }

    private static Task RenderTitleOnStaAsync(Action render, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                render();
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(new ExportException(
                ExportStageCode.Prepare, "绘制文字示例失败。", exception)); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void ScaleDemoStyle(ClipText style, double factor)
    {
        style.FontSize *= factor;
        style.OutlineThickness *= factor;
        style.ShadowBlur *= factor;
        style.ShadowOffsetX *= factor;
        style.ShadowOffsetY *= factor;
        style.GlowRadius *= factor;
        style.LetterSpacing *= factor;
        style.BackgroundPaddingX *= factor;
        style.BackgroundPaddingY *= factor;
        style.BackgroundCornerRadius *= factor;
    }

    /// <summary>
    /// Stops any currently playing preview.
    /// </summary>
    public static void StopPreview()
    {
        lock (PreviewProcessGate)
        {
            _currentPreviewCancellation?.Cancel();
            try
            {
                if (_currentPreviewProcess is { HasExited: false }) _currentPreviewProcess.Kill(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException) { }
            _currentPreviewProcess = null;
        }
    }

    private static CancellationTokenSource BeginPreview(CancellationToken cancellationToken)
    {
        lock (PreviewProcessGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StopPreview();
            _currentPreviewCancellation = source;
            return source;
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void ClearPreviewCancellation(CancellationTokenSource source)
    {
        lock (PreviewProcessGate)
            if (ReferenceEquals(_currentPreviewCancellation, source)) _currentPreviewCancellation = null;
    }

    private static void EnsureSampleImagesExist(string ffmpegPath)
    {
        // Rapid preset changes may have two workers reaching the cache before
        // the previous render has observed cancellation. Never overwrite an
        // image while another generator is still encoding it.
        lock (SampleImagesGate)
        {
            Directory.CreateDirectory(SamplesDirectory);
            if (!File.Exists(SampleVersionPath) || !File.Exists(Image1Path) || !File.Exists(Image2Path))
            {
                GenerateSampleImage(ffmpegPath, Image1Path, sunset: false);
                GenerateSampleImage(ffmpegPath, Image2Path, sunset: true);
                try { File.WriteAllText(SampleVersionPath, "2"); } catch (IOException) { }
            }
        }
    }

    private static void GenerateSampleImage(string ffmpegPath, string outputPath, bool sunset)
    {
        string ppmPath = Path.Combine(SamplesDirectory, $"landscape_{Guid.NewGuid():N}.ppm");
        try
        {
            const int width = 960;
            const int height = 540;
            using (var stream = File.Create(ppmPath))
            {
                using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                writer.Write($"P6\n{width} {height}\n255\n");
                writer.Flush();
                byte[] pixel = new byte[width * height * 3];
                for (int y = 0; y < height; y++)
                {
                    double t = y / (double)(height - 1);
                    byte skyR = (byte)(sunset ? 35 + 185 * t : 70 + 110 * t);
                    byte skyG = (byte)(sunset ? 70 + 75 * t : 150 + 80 * t);
                    byte skyB = (byte)(sunset ? 135 + 35 * t : 210 + 35 * t);
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 3;
                        pixel[i] = skyR; pixel[i + 1] = skyG; pixel[i + 2] = skyB;
                        // Distant mountain range and foreground ridge.
                        double ridge = height * (0.60 + 0.08 * Math.Sin(x / 87d + (sunset ? 0.8 : 0)));
                        double foreground = height * (0.72 + 0.05 * Math.Sin(x / 61d + 1.4));
                        if (y > ridge) { pixel[i] = sunset ? (byte)30 : (byte)42; pixel[i + 1] = sunset ? (byte)55 : (byte)86; pixel[i + 2] = sunset ? (byte)73 : (byte)92; }
                        if (y > foreground) { pixel[i] = sunset ? (byte)18 : (byte)24; pixel[i + 1] = sunset ? (byte)34 : (byte)56; pixel[i + 2] = sunset ? (byte)45 : (byte)65; }
                    }
                }
                // A bright sun and its reflection make the two frames clearly
                // distinguishable during a transition.
                int cx = sunset ? 720 : 240, cy = sunset ? 180 : 145, radius = 58;
                for (int y = Math.Max(0, cy - radius); y < Math.Min(height, cy + radius); y++)
                    for (int x = Math.Max(0, cx - radius); x < Math.Min(width, cx + radius); x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
                        { int i = (y * width + x) * 3; pixel[i] = 255; pixel[i + 1] = sunset ? (byte)180 : (byte)235; pixel[i + 2] = sunset ? (byte)92 : (byte)160; }
                stream.Write(pixel);
            }
            // Runs on a worker thread (see the async callers), so waiting here
            // does not freeze the window. The runner drains both streams and a
            // timeout kills the process instead of leaving it running.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ProcessResult run = ExternalProcessRunner.RunAsync(ffmpegPath,
                $"-f image2 -i \"{ppmPath}\" -frames:v 1 -q:v 2 -y \"{outputPath}\"", timeout.Token)
                .GetAwaiter().GetResult();
            if (!run.Succeeded)
                throw new InvalidOperationException("示例图片生成失败：" + run.ErrorTail());
        }
        finally { try { if (File.Exists(ppmPath)) File.Delete(ppmPath); } catch (IOException) { } }
    }

    private static async Task RunFfmpegAsync(string ffmpegPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        Action<double>? progressCallback, double progressStart, double progressSpan, double expectedDurationSeconds)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                    CreateNoWindow = true,
                    // Both redirected streams are drained while encoding.
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
            }
        };
        foreach (string argument in arguments.Take(arguments.Count - 1)) process.StartInfo.ArgumentList.Add(argument);
        foreach (string argument in new[] { "-progress", "pipe:1", "-nostats", arguments[^1] })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            lock (PreviewProcessGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
                _currentPreviewProcess = process;
            }
            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException) { }
            });
            // Drain pipes after cancellation too. The registration kills the
            // encoder and waiting for EOF lets temporary-file cleanup succeed.
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            Task readProgressTask = ReadProgressAsync(process.StandardOutput, CancellationToken.None,
                progressCallback, progressStart, progressSpan, expectedDurationSeconds);
            await process.WaitForExitAsync();
            await Task.WhenAll(errorTask, readProgressTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                string error = await errorTask;
                throw new ExportException(ExportStageCode.Encode,
                    string.IsNullOrWhiteSpace(error) ? "示例视频生成失败。" : error.Trim());
            }
            progressCallback?.Invoke(progressStart + progressSpan);
        }
        finally
        {
            lock (PreviewProcessGate)
                if (ReferenceEquals(_currentPreviewProcess, process)) _currentPreviewProcess = null;
        }
    }

    private static async Task ReadProgressAsync(StreamReader reader, CancellationToken cancellationToken,
        Action<double>? callback, double start, double span, double durationSeconds)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal) ||
                !long.TryParse(line["out_time_ms=".Length..], out long microseconds)) continue;
            double fraction = durationSeconds <= 0 ? 0 : Math.Clamp(microseconds / 1_000_000d / durationSeconds, 0, 1);
            callback?.Invoke(start + span * fraction);
        }
    }

    private static string GetEffectFilters(string effectId)
    {
        // Use the same effect implementation as timeline preview and export.
        // A private preview-only table made most newer catalog effects silently
        // fall back to brightness instead of demonstrating their own effect.
        var clip = new TimelineClip
        {
            DurationSeconds = 5,
            Effects = [new ClipEffect { PresetId = effectId, Name = effectId }]
        };
        string filters = VisualEffectPipeline.BuildFfmpegFilters(clip, new ProjectSequence())
            .TrimStart(',');
        return filters.Length == 0 ? "null" : filters;
    }

}
