using System;
using System.Diagnostics;
using System.IO;
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
    public static async Task PlayEffectPreviewAsync(
        string effectId,
        string ffmpegPath,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        StopPreview();
        _currentPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            EnsureSampleImagesExist(ffmpegPath);

            string outputPath = Path.Combine(Path.GetTempPath(), $"effect_preview_{Guid.NewGuid():N}.mp4");

            // Convert image to 5-second video with effect applied
            string effectFilter = GetEffectFilter(effectId);
            string arguments = $"-loop 1 -i \"{Image1Path}\" -t 5 {effectFilter} " +
                             $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -r 30 -y \"{outputPath}\"";

            await RunFfmpegAsync(ffmpegPath, arguments, _currentPreviewCancellation.Token, progressCallback, 0, 100, 5);

            if (!_currentPreviewCancellation.Token.IsCancellationRequested && File.Exists(outputPath))
            {
                playVideoCallback(outputPath);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Plays a transition preview: Image1 -> Transition -> Image2 (5 seconds total).
    /// Structure: 2s Image1 + 1s Transition + 2s Image2
    /// </summary>
    public static async Task PlayTransitionPreviewAsync(
        string transitionId,
        string ffmpegPath,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        StopPreview();
        _currentPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            EnsureSampleImagesExist(ffmpegPath);

            string outputPath = Path.Combine(Path.GetTempPath(), $"transition_preview_{Guid.NewGuid():N}.mp4");

            // Convert images to video streams
            string video1 = Path.Combine(Path.GetTempPath(), $"temp_vid1_{Guid.NewGuid():N}.mp4");
            string video2 = Path.Combine(Path.GetTempPath(), $"temp_vid2_{Guid.NewGuid():N}.mp4");

            // Create 3-second video from each image
            await RunFfmpegAsync(ffmpegPath,
                $"-loop 1 -i \"{Image1Path}\" -t 3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -r 30 -y \"{video1}\"",
                _currentPreviewCancellation.Token, progressCallback, 0, 33, 3);

            await RunFfmpegAsync(ffmpegPath,
                $"-loop 1 -i \"{Image2Path}\" -t 3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -r 30 -y \"{video2}\"",
                _currentPreviewCancellation.Token, progressCallback, 33, 33, 3);

            // Apply transition between the two videos
            string transitionFilter = GetTransitionFilter(transitionId);
            string arguments = $"-i \"{video1}\" -i \"{video2}\" " +
                             $"-filter_complex \"{transitionFilter}\" " +
                             $"-map \"[out]\" -an -c:v libx264 -preset ultrafast -pix_fmt yuv420p -r 30 -t 5 -y \"{outputPath}\"";

            await RunFfmpegAsync(ffmpegPath, arguments, _currentPreviewCancellation.Token, progressCallback, 66, 34, 5);

            // Cleanup temp files
            try { File.Delete(video1); } catch { }
            try { File.Delete(video2); } catch { }

            if (!_currentPreviewCancellation.Token.IsCancellationRequested && File.Exists(outputPath))
            {
                playVideoCallback(outputPath);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Plays a text preset preview: Image with text animation for 5 seconds.
    /// </summary>
    public static async Task PlayTextPreviewAsync(
        string presetId,
        string sampleText,
        string ffmpegPath,
        Func<string, ClipText> styleProvider,
        Action<string> playVideoCallback,
        CancellationToken cancellationToken = default,
        Action<double>? progressCallback = null)
    {
        StopPreview();
        _currentPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            string outputPath = Path.Combine(Path.GetTempPath(), $"text_preview_{Guid.NewGuid():N}.mp4");
            // The sample goes through a file rather than the command line, so
            // no character in it can break the filter graph.
            string textFile = Path.Combine(Path.GetTempPath(), $"text_preview_{Guid.NewGuid():N}.txt");
            try
            {
                await File.WriteAllTextAsync(textFile, sampleText,
                    new System.Text.UTF8Encoding(false), _currentPreviewCancellation.Token);

                // The style is the preset's real one, so the demonstration shows
                // what dropping it on the timeline will produce.
                ClipText style = styleProvider(presetId);
                string textFilter = GetTextFilter(presetId, style, textFile);
                // Black stage by design: the text, not a background, is the subject.
                string arguments = $"-f lavfi -i color=c=black:s=1920x1080:r=30 -t 5 {textFilter} " +
                                 $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -r 30 -y \"{outputPath}\"";

                await RunFfmpegAsync(ffmpegPath, arguments, _currentPreviewCancellation.Token, progressCallback, 0, 100, 5);
            }
            finally
            {
                try { if (File.Exists(textFile)) File.Delete(textFile); } catch (IOException) { }
            }

            if (!_currentPreviewCancellation.Token.IsCancellationRequested && File.Exists(outputPath))
            {
                playVideoCallback(outputPath);
            }
        }
        catch (OperationCanceledException) { }
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
            catch (InvalidOperationException) { }
            _currentPreviewProcess = null;
        }
    }

    private static void EnsureSampleImagesExist(string ffmpegPath)
    {
        Directory.CreateDirectory(SamplesDirectory);

        // These are generated locally from pixels, so every project gets two
        // original landscape images without downloading or embedding stock
        // photography. The marker also upgrades images created by older builds.
        if (!File.Exists(SampleVersionPath) || !File.Exists(Image1Path) || !File.Exists(Image2Path))
        {
            GenerateSampleImage(ffmpegPath, Image1Path, sunset: false);
            GenerateSampleImage(ffmpegPath, Image2Path, sunset: true);
            try { File.WriteAllText(SampleVersionPath, "2"); } catch (IOException) { }
        }
    }

    private static void GenerateSampleImage(string ffmpegPath, string outputPath, bool sunset)
    {
        if (!File.Exists(ffmpegPath)) return;

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

    private static async Task RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken cancellationToken,
        Action<double>? progressCallback, double progressStart, double progressSpan, double expectedDurationSeconds)
    {
        arguments = AddProgressReporting(arguments);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                    CreateNoWindow = true,
                    // FFmpeg can emit more diagnostics than a redirected
                    // pipe can hold. Leaving the streams unredirected keeps
                    // the background preview task from deadlocking while it
                    // waits for the encoder to finish.
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
            }
        };
        lock (PreviewProcessGate) _currentPreviewProcess = process;
        try
        {
            process.Start();
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task readProgressTask = ReadProgressAsync(process.StandardOutput, cancellationToken,
                progressCallback, progressStart, progressSpan, expectedDurationSeconds);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(errorTask, readProgressTask);
            if (process.ExitCode != 0)
            {
                string error = await errorTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "示例视频生成失败" : error.Trim());
            }
            progressCallback?.Invoke(progressStart + progressSpan);
        }
        finally
        {
            lock (PreviewProcessGate)
                if (ReferenceEquals(_currentPreviewProcess, process)) _currentPreviewProcess = null;
        }
    }

    private static string AddProgressReporting(string arguments)
    {
        const string marker = " -y \"";
        return arguments.Contains(marker, StringComparison.Ordinal)
            ? arguments.Replace(marker, " -progress pipe:1 -nostats -y \"", StringComparison.Ordinal)
            : arguments + " -progress pipe:1 -nostats";
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

    private static string GetEffectFilter(string effectId)
    {
        return effectId switch
        {
            "effect.brightness" => "-vf \"eq=brightness=0.2\"",
            "effect.contrast" => "-vf \"eq=contrast=1.5\"",
            "effect.saturation" => "-vf \"eq=saturation=1.8\"",
            "effect.blur" => "-vf \"gblur=sigma=8\"",
            "effect.sharpen" => "-vf \"unsharp=5:5:1.5\"",
            "effect.sepia" => "-vf \"colorchannelmixer=.393:.769:.189:.349:.686:.168:.272:.534:.131\"",
            "effect.black-white" => "-vf \"hue=s=0\"",
            "effect.invert" => "-vf \"negate\"",
            "effect.vignette" => "-vf \"vignette=PI/3\"",
            "effect.grain" => "-vf \"noise=alls=30:allf=t\"",
            _ => "-vf \"eq=brightness=0.1\""
        };
    }

    private static string GetTransitionFilter(string transitionId)
    {
        return transitionId switch
        {
            "transition.fade" or "transition.crossfade" =>
                "[0:v][1:v]xfade=transition=fade:duration=1:offset=2[out]",

            "transition.wipe-left" =>
                "[0:v][1:v]xfade=transition=wipeleft:duration=1:offset=2[out]",

            "transition.wipe-right" =>
                "[0:v][1:v]xfade=transition=wiperight:duration=1:offset=2[out]",

            "transition.wipe-up" =>
                "[0:v][1:v]xfade=transition=wipeup:duration=1:offset=2[out]",

            "transition.wipe-down" =>
                "[0:v][1:v]xfade=transition=wipedown:duration=1:offset=2[out]",

            "transition.slide-left" =>
                "[0:v][1:v]xfade=transition=slideleft:duration=1:offset=2[out]",

            "transition.slide-right" =>
                "[0:v][1:v]xfade=transition=slideright:duration=1:offset=2[out]",

            "transition.dissolve" =>
                "[0:v][1:v]xfade=transition=dissolve:duration=1:offset=2[out]",
            "transition.circle-open" =>
                "[0:v][1:v]xfade=transition=circleopen:duration=1:offset=2[out]",
            "transition.zoom" =>
                "[0:v][1:v]xfade=transition=zoomin:duration=1:offset=2[out]",
            "transition.rotate" =>
                "[0:v][1:v]xfade=transition=rotateleft:duration=1:offset=2[out]",
            "transition.shake" =>
                "[0:v][1:v]xfade=transition=distance:duration=1:offset=2[out]",
            "transition.blur-dissolve" =>
                "[0:v][1:v]xfade=transition=smoothleft:duration=1:offset=2[out]",

            "transition.dip-black" =>
                "[0:v][1:v]xfade=transition=fadeblack:duration=1:offset=2[out]",
            "transition.dip-white" =>
                "[0:v][1:v]xfade=transition=fadewhite:duration=1:offset=2[out]",
            "transition.push-up" =>
                "[0:v][1:v]xfade=transition=slideup:duration=1:offset=2[out]",
            "transition.push-down" =>
                "[0:v][1:v]xfade=transition=slidedown:duration=1:offset=2[out]",
            "transition.zoom-in" =>
                "[0:v][1:v]xfade=transition=zoomin:duration=1:offset=2[out]",
            "transition.zoom-out" =>
                "[0:v][1:v]xfade=transition=fade:duration=1:offset=2[out]",
            "transition.spin" =>
                "[0:v][1:v]xfade=transition=circleopen:duration=1:offset=2[out]",
            "transition.blur" =>
                "[0:v][1:v]xfade=transition=smoothleft:duration=1:offset=2[out]",
            "transition.flash" or "transition.light-leak" =>
                "[0:v][1:v]xfade=transition=fadefast:duration=1:offset=2[out]",
            "transition.circle" =>
                "[0:v][1:v]xfade=transition=circleopen:duration=1:offset=2[out]",
            "transition.diamond" =>
                "[0:v][1:v]xfade=transition=vertopen:duration=1:offset=2[out]",
            "transition.split" =>
                "[0:v][1:v]xfade=transition=horzopen:duration=1:offset=2[out]",
            "transition.page" or "transition.film" =>
                "[0:v][1:v]xfade=transition=coverleft:duration=1:offset=2[out]",
            "transition.glitch" =>
                "[0:v][1:v]xfade=transition=distance:duration=1:offset=2[out]",

            _ => "[0:v][1:v]xfade=transition=fade:duration=1:offset=2[out]"
        };
    }

    /// <summary>
    /// Builds the drawtext filter for a title preview from the preset's real
    /// style. The style is the same one used when the preset is dropped on the
    /// timeline, so the demonstration shows what the user will get. drawtext
    /// cannot blur a shadow or underline text, so those are approximated.
    /// </summary>
    private static string GetTextFilter(string presetId, ClipText style, string textFilePath)
    {
        // Preview frames are 1920x1080 against a project canvas of the same
        // nominal height, but a preset's font size targets the project canvas.
        // Clamp so a 128 pt title and a 54 pt caption both stay in frame.
        int fontSize = (int)Math.Clamp(Math.Round(style.FontSize * 1.1), 36, 150);
        var options = new List<string>
        {
            // textfile keeps the sample out of the command line, so no quote,
            // colon, comma or backslash in it can break the filter graph.
            // expansion=none stops a percent sign being read as a macro.
            $"textfile='{EscapeFilterPath(textFilePath)}'",
            "expansion=none",
            $"fontsize={fontSize}",
            $"fontcolor={FfmpegColor(style.ForegroundColor, 1)}"
        };
        string fontFile = ResolveFontFile(style.Bold, style.Italic);
        if (fontFile.Length > 0) options.Insert(0, $"fontfile='{fontFile}'");

        if (style.OutlineEnabled && style.OutlineThickness > 0)
        {
            options.Add($"borderw={Math.Clamp((int)Math.Round(style.OutlineThickness * 1.5), 1, 12)}");
            options.Add($"bordercolor={FfmpegColor(style.OutlineColor, 1)}");
        }
        if (style.ShadowEnabled)
        {
            // drawtext only has a hard offset shadow; the blur is dropped.
            options.Add($"shadowx={Math.Clamp((int)Math.Round(style.ShadowOffsetX), -20, 20)}");
            options.Add($"shadowy={Math.Clamp((int)Math.Round(style.ShadowOffsetY), -20, 20)}");
            options.Add($"shadowcolor={FfmpegColor(style.ShadowColor, Math.Clamp(style.ShadowOpacity, 0, 1))}");
        }
        if (!string.IsNullOrWhiteSpace(style.BackgroundColor))
        {
            options.Add("box=1");
            options.Add($"boxcolor={FfmpegColor(style.BackgroundColor, 1)}");
            options.Add("boxborderw=18");
        }

        // Horizontal and vertical placement follow the style's alignment.
        string x = style.HorizontalAlignment switch
        {
            "Left" => "w*0.08", "Right" => "w-text_w-w*0.08", _ => "(w-text_w)/2"
        };
        string y = style.VerticalAlignment switch
        {
            "Top" => "h*0.1", "Bottom" => "h-text_h-h*0.12", _ => "(h-text_h)/2"
        };

        // Motion is the preset's own character. Commas inside an expression
        // are escaped because the whole filter sits in a comma-separated chain.
        switch (presetId)
        {
            case "title.fade":
                options.Add("alpha='if(lt(t\\,1)\\,t\\,if(gt(t\\,4)\\,5-t\\,1))'");
                break;
            case "title.slide":
                // Slide in from below over the first second, then hold.
                y = $"if(lt(t\\,1)\\,{y}+(1-t)*h*0.5\\,{y})";
                break;
            case "title.pop":
            case "title.kinetic":
                // Grow in by size: handled with a short alpha ramp, since
                // drawtext cannot animate its own font size per frame.
                options.Add("alpha='min(1\\,t*4)'");
                break;
        }
        options.Add($"x={x}");
        options.Add($"y={y}");
        return $"-vf \"drawtext={string.Join(":", options)}\"";
    }

    /// <summary>
    /// ffmpeg wants 0xRRGGBB@alpha. Accepts #RRGGBB and #AARRGGBB, and falls
    /// back to white on anything unparsable rather than failing the preview.
    /// </summary>
    private static string FfmpegColor(string? hex, double opacity)
    {
        string text = (hex ?? string.Empty).Trim().TrimStart('#');
        double alpha = Math.Clamp(opacity, 0, 1);
        if (text.Length == 8 && int.TryParse(text[..2], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int a))
        {
            alpha *= a / 255d;
            text = text[2..];
        }
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            text = "FFFFFF";
        return $"0x{text}@{alpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Escapes a Windows path for use inside a quoted filter option: forward
    /// slashes, and the drive colon escaped because colon separates options.
    /// </summary>
    private static string EscapeFilterPath(string path) =>
        path.Replace("\\", "/").Replace(":", "\\:");

    /// <summary>
    /// The most dependable font for a demonstration: Arial ships with every
    /// Windows install and its bold and italic faces sit next to it. An empty
    /// result tells the caller to omit fontfile and let ffmpeg use its default.
    /// The sample word is Latin, so a CJK-capable face is not needed here.
    /// </summary>
    private static string ResolveFontFile(bool bold, bool italic)
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string face = (bold, italic) switch
        {
            (true, true) => "arialbi.ttf",
            (true, false) => "arialbd.ttf",
            (false, true) => "ariali.ttf",
            _ => "arial.ttf"
        };
        string path = Path.Combine(fonts, face);
        if (!File.Exists(path)) path = Path.Combine(fonts, "arial.ttf");
        return File.Exists(path) ? EscapeFilterPath(path) : string.Empty;
    }
}
