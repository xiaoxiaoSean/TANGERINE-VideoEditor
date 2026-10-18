using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

// This executable deliberately inspects pixels as well as encoder exit codes.
// Asymmetric quadrants, readable labels and high-frequency grids make geometric
// and blur regressions observable; a pair of uniform color cards cannot do that.
internal static class Program
{
    private const int Width = 320, Height = 180, Fps = 20;
    private static readonly double[] SampleTimes = [1.05, 1.15, 1.30, 1.40];
    private static readonly string[] ExportPresets = ["transition.fade", "transition.rotate", "transition.spin",
        "transition.shake", "transition.film", "transition.blur-dissolve", "transition.push-up", "transition.radial"];
    private static readonly List<CheckResult> Results = [];
    private static readonly Dictionary<string, List<BitmapSource>> Frames = [];
    private static readonly Dictionary<string, string> Videos = [];
    private static string Output = string.Empty, SourceA = string.Empty, SourceB = string.Empty;
    private static int Checks;

    private enum VisualStage { Prepare, Render, Encode, Decode, Identity, Geometry, Blur, Parity, Artifact }
    private sealed class VisualCheckException(VisualStage stageCode, string message, Exception? inner = null)
        : Exception(message, inner) { public VisualStage StageCode { get; } = stageCode; }
    private sealed record CheckResult(string Name, bool Passed, string Detail, string? StageCode = null);
    private sealed record Pixels(byte[] Data, int Width, int Height)
    {
        public double Channel(int x, int y, int channel) => Data[(y * Width + x) * 4 + channel];
    }

    private static async Task<int> Main(string[] args)
    {
        Output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("tests", "TransitionVisualCheck", "artifacts", "latest"));
        Directory.CreateDirectory(Output);
        SourceA = Path.Combine(Output, "source-a.png");
        SourceB = Path.Combine(Output, "source-b.png");
        try
        {
            await Sta(() => { DrawSource(SourceA, false); DrawSource(SourceB, true); });
            foreach (BuiltinPreset preset in BuiltinCatalog.Transitions)
                await Check("pixels/" + preset.Id, VisualStage.Render, async () =>
                {
                    var frames = new List<BitmapSource>();
                    foreach (double time in SampleTimes)
                    {
                        BitmapSource frame = await Render(preset.Id, time);
                        frames.Add(frame);
                        Save(frame, Path.Combine(Output, preset.Id + "-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".png"));
                    }
                    Frames[preset.Id] = frames;
                    return "4 asymmetric frames rendered";
                });

            await Check("identity/all-preset-pairs", VisualStage.Identity, () =>
            {
                var ids = Frames.Keys.ToArray();
                var collisions = new List<string>();
                double smallest = double.MaxValue; string closest = string.Empty;
                for (int a = 0; a < ids.Length; a++)
                for (int b = a + 1; b < ids.Length; b++)
                {
                    double distance = Enumerable.Range(0, SampleTimes.Length)
                        .Average(i => MeanDifference(Read(Frames[ids[a]][i]), Read(Frames[ids[b]][i])));
                    if (distance < smallest) { smallest = distance; closest = ids[a] + " / " + ids[b]; }
                    // Sub-byte differences cannot support a claim of a distinct
                    // visual preset. Compare several phases, not only midpoint.
                    if (distance < .6) collisions.Add(ids[a] + " = " + ids[b] + $" ({distance:0.000})");
                }
                Require(collisions.Count == 0, VisualStage.Identity, string.Join("; ", collisions));
                return Task.FromResult($"{ids.Length * (ids.Length - 1) / 2} pairs distinct; closest {closest}: MAE {smallest:0.00}");
            });

            await Check("geometry/rotate-has-diagonal-edges", VisualStage.Geometry, async () =>
            {
                Pixels fade = Read(await Render("transition.fade", 1.25));
                Pixels rotation = Read(await Render("transition.rotate", 1.25));
                int plain = DiagonalEdges(fade), rotated = DiagonalEdges(rotation);
                Require(rotated > plain * 1.15 + 20, VisualStage.Geometry,
                    $"Rotation did not create diagonal grid/corner edges: fade={plain}, rotate={rotated}");
                return $"diagonal edge pixels: fade {plain}, rotate {rotated}";
            });
            await Check("geometry/shake-displaces-picture", VisualStage.Geometry, async () =>
            {
                Pixels prior = Read(await Render(null, .5));
                Pixels incoming = Read(await Render(null, 1.75));
                double bestImprovement = 0; string bestDetail = string.Empty;
                foreach (double time in new[] { 1.15, 1.35 })
                {
                    Pixels shake = Read(await Render("transition.shake", time));
                    double centered = MixtureError(prior, incoming, shake, 0, 0);
                    double best = centered; int bestX = 0, bestY = 0;
                    for (int dy = -6; dy <= 6; dy++)
                    for (int dx = -10; dx <= 10; dx++)
                    {
                        double error = MixtureError(prior, incoming, shake, dx, dy);
                        if (error < best) { best = error; bestX = dx; bestY = dy; }
                    }
                    double improvement = 1 - best / Math.Max(centered, .00001);
                    if (improvement > bestImprovement)
                    {
                        bestImprovement = improvement;
                        bestDetail = $"t={time:0.00}, shift=({bestX},{bestY}), centered error={centered:0.00}, shifted={best:0.00}";
                    }
                }
                Require(bestImprovement > .12, VisualStage.Geometry, "No nonzero displacement fits shake better: " + bestDetail);
                return bestDetail;
            });
            await Check("blur/softens-high-frequency-grid", VisualStage.Blur, async () =>
            {
                Pixels fade = Read(await Render("transition.fade", 1.25));
                Pixels blur = Read(await Render("transition.blur-dissolve", 1.25));
                double sharpEnergy = GridEnergy(fade), blurredEnergy = GridEnergy(blur);
                Require(blurredEnergy < sharpEnergy * .65, VisualStage.Blur,
                    $"High-frequency detail was not blurred: plain={sharpEnergy:0.00}, blur={blurredEnergy:0.00}");
                return $"grid gradient energy: plain {sharpEnergy:0.00}, blur {blurredEnergy:0.00}";
            });

            foreach (string id in ExportPresets)
                await Check("export-parity/" + id, VisualStage.Parity, async () =>
                {
                    string video = Path.Combine(Output, id + ".mp4");
                    await TimelineExporter.ExportMp4Async(Project(id), video, new Dictionary<string, string>());
                    Videos[id] = video;
                    double maximumMae = 0, maximumP95 = 0;
                    foreach (double time in new[] { 1.05, 1.15, 1.30, 1.40, 1.75 })
                    {
                        Pixels preview = Read(await Render(id, time));
                        string decoded = Path.Combine(Output, id + "-export-" + time.ToString("0.00", CultureInfo.InvariantCulture) + ".png");
                        // Selecting by frame index avoids keyframe seeking and
                        // rounding ambiguity at exact 20-fps timeline samples.
                        await Ffmpeg(["-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf",
                            $"select=eq(n\\,{(int)Math.Round(time * Fps)})", "-frames:v", "1", decoded], VisualStage.Decode);
                        Pixels exported = Read(Load(decoded));
                        double mae = MeanDifference(preview, exported), p95 = DifferencePercentile(preview, exported, .95);
                        maximumMae = Math.Max(maximumMae, mae); maximumP95 = Math.Max(maximumP95, p95);
                        Require(mae <= 9 && p95 <= 35, VisualStage.Parity,
                            $"{id} at {time:0.00}s differs: MAE={mae:0.00}, P95={p95:0.00}");
                    }
                    return $"5 frames agree; max MAE {maximumMae:0.00}/255, P95 {maximumP95:0.00}/255";
                });
            await Check("artifacts/contact-sheet", VisualStage.Artifact, async () =>
            {
                await Sta(DrawContactSheet);
                return "contact-sheet.png";
            });
            await Check("artifacts/montage", VisualStage.Artifact, async () =>
            {
                // Export one short, labelled chapter for each core motion type;
                // concatenating equal-format outputs requires no extra encode.
                var list = new List<string>();
                foreach ((string id, string video) in Videos)
                {
                    string labelled = Path.Combine(Output, id + "-label.mp4");
                    string caption = Path.Combine(Output, id + "-caption.png");
                    await Sta(() => DrawCaption(id, caption));
                    await Ffmpeg(["-hide_banner", "-loglevel", "error", "-y", "-i", video, "-i", caption,
                        "-filter_complex", "[0:v][1:v]overlay=0:0", "-an", "-c:v", "libx264", "-preset", "ultrafast",
                        "-crf", "18", "-pix_fmt", "yuv420p", "-threads", "2", labelled], VisualStage.Artifact);
                    list.Add("file '" + labelled.Replace("\\", "/").Replace("'", "'\\''") + "'");
                }
                Require(list.Count > 0, VisualStage.Artifact, "No exports available for montage");
                string concat = Path.Combine(Output, "montage-list.txt");
                await File.WriteAllLinesAsync(concat, list);
                await Ffmpeg(["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", concat,
                    "-c", "copy", Path.Combine(Output, "transition-montage.mp4")], VisualStage.Artifact);
                return $"transition-montage.mp4, {list.Count} labelled chapters";
            });
        }
        catch (Exception exception)
        {
            Results.Add(new("fatal", false, exception.ToString(), VisualStage.Prepare.ToString()));
        }
        string report = JsonSerializer.Serialize(new { Checks, Failures = Results.Count(r => !r.Passed), Output, Results },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(Output, "report.json"), report);
        Console.WriteLine($"Checks: {Checks}; Failures: {Results.Count(r => !r.Passed)}; Outputs: {Output}");
        return Results.All(r => r.Passed) ? 0 : 1;
    }

    private static ProjectDocument Project(string? id)
    {
        ProjectDocument project = ProjectDocument.CreateDefault();
        project.Sequence.Width = Width; project.Sequence.Height = Height; project.Sequence.FramesPerSecond = Fps;
        project.Sequence.BackgroundColor = "#101820";
        foreach ((string path, double start) in new[] { (SourceA, 0d), (SourceB, 1d) })
        {
            var asset = new MediaAsset { Name = start == 0 ? "A" : "B", SourcePath = path, Kind = MediaKind.Image, Width = Width, Height = Height };
            project.Media.Add(asset);
            var clip = new TimelineClip { Kind = ClipKind.Image, MediaAssetId = asset.Id, Name = asset.Name, StartSeconds = start, DurationSeconds = 1 };
            if (start > 0 && id is not null) clip.Transitions.Add(new ClipTransition { PresetId = id, DurationSeconds = .5, Edge = "In" });
            project.Tracks[0].Clips.Add(clip);
        }
        return project;
    }
    private static Task<BitmapSource> Render(string? id, double time) =>
        CompositeFrameRenderer.RenderFrameAsync(Project(id), time, Width, Height, previewGpuMode: "CPU 软件渲染");

    private static async Task Check(string name, VisualStage stage, Func<Task<string>> action)
    {
        Checks++;
        try { string detail = await action().WaitAsync(TimeSpan.FromMinutes(3)); Results.Add(new(name, true, detail)); Console.WriteLine("PASS " + name + ": " + detail); }
        catch (Exception exception)
        {
            string stageCode = exception is VisualCheckException visual ? visual.StageCode.ToString() : stage.ToString();
            Results.Add(new(name, false, exception.ToString(), stageCode));
            Console.WriteLine("FAIL [VIS-" + stageCode.ToUpperInvariant() + "] " + name + ": " + exception.Message);
        }
    }
    private static void Require(bool condition, VisualStage stage, string message)
    { if (!condition) throw new VisualCheckException(stage, message); }
    private static async Task Ffmpeg(string[] arguments, VisualStage stage)
    {
        var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string value in arguments) start.ArgumentList.Add(value);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            Task<string> stderr = process.StandardError.ReadToEndAsync(), stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            await stdout;
            Require(process.ExitCode == 0, stage, await stderr);
        }
        catch (Exception exception) when (exception is not VisualCheckException)
        { if (!process.HasExited) process.Kill(entireProcessTree: true); throw new VisualCheckException(stage, "FFmpeg invocation failed", exception); }
    }
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception e) { completion.SetException(e); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private static BitmapSource Load(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0]; frame.Freeze(); return frame;
    }
    private static void Save(BitmapSource frame, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); using var stream = File.Create(path); encoder.Save(stream); }
    private static Pixels Read(BitmapSource frame)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[frame.PixelWidth * frame.PixelHeight * 4]; converted.CopyPixels(bytes, frame.PixelWidth * 4, 0);
        return new(bytes, frame.PixelWidth, frame.PixelHeight);
    }
    private static double MeanDifference(Pixels a, Pixels b)
    {
        double sum = 0;
        for (int i = 0; i < a.Data.Length; i += 4) for (int c = 0; c < 3; c++) sum += Math.Abs(a.Data[i + c] - b.Data[i + c]);
        return sum / (a.Width * a.Height * 3);
    }
    private static double DifferencePercentile(Pixels a, Pixels b, double percentile)
    {
        int[] histogram = new int[256];
        for (int i = 0; i < a.Data.Length; i += 4) for (int c = 0; c < 3; c++) histogram[Math.Abs(a.Data[i + c] - b.Data[i + c])]++;
        double threshold = a.Width * a.Height * 3 * percentile; int count = 0;
        for (int value = 0; value < 256; value++) { count += histogram[value]; if (count >= threshold) return value; }
        return 255;
    }
    private static double Luma(Pixels p, int x, int y) => .2126 * p.Channel(x, y, 2) + .7152 * p.Channel(x, y, 1) + .0722 * p.Channel(x, y, 0);
    private static int DiagonalEdges(Pixels p)
    {
        int count = 0;
        for (int y = 3; y < Height - 3; y++) for (int x = 3; x < Width - 3; x++)
        {
            double dx = Math.Abs(Luma(p, x + 1, y) - Luma(p, x - 1, y));
            double dy = Math.Abs(Luma(p, x, y + 1) - Luma(p, x, y - 1));
            if (dx > 25 && dy > 25 && dx / dy is > .4 and < 2.5) count++;
        }
        return count;
    }
    private static double GridEnergy(Pixels p)
    {
        double sum = 0; int count = 0;
        for (int y = 98; y < 156; y++) for (int x = 198; x < 302; x++)
        { sum += Math.Abs(Luma(p, x + 1, y) - Luma(p, x, y)) + Math.Abs(Luma(p, x, y + 1) - Luma(p, x, y)); count += 2; }
        return sum / count;
    }
    private static double MixtureError(Pixels a, Pixels b, Pixels output, int dx, int dy)
    {
        // Fit an arbitrary scalar opacity to an independently translated source.
        // This is an image-registration assertion, not a copy of the animation
        // expression: it proves the image actually moved rather than only faded.
        double numerator = 0, denominator = 0;
        for (int y = 12; y < Height - 12; y += 3) for (int x = 12; x < Width - 12; x += 3) for (int c = 0; c < 3; c++)
        {
            double delta = b.Channel(x + dx, y + dy, c) - a.Channel(x, y, c);
            numerator += delta * (output.Channel(x, y, c) - a.Channel(x, y, c)); denominator += delta * delta;
        }
        double alpha = Math.Clamp(numerator / Math.Max(denominator, 1), 0, 1), sum = 0; int count = 0;
        for (int y = 12; y < Height - 12; y += 3) for (int x = 12; x < Width - 12; x += 3) for (int c = 0; c < 3; c++)
        { double expected = (1 - alpha) * a.Channel(x, y, c) + alpha * b.Channel(x + dx, y + dy, c); sum += Math.Pow(output.Channel(x, y, c) - expected, 2); count++; }
        return sum / count;
    }

    private static void DrawSource(string path, bool incoming)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Color[] colors = incoming ? [Color.FromRgb(30, 160, 110), Color.FromRgb(140, 40, 175), Color.FromRgb(225, 155, 30), Color.FromRgb(30, 45, 70)]
                : [Color.FromRgb(25, 65, 150), Color.FromRgb(185, 50, 45), Color.FromRgb(50, 105, 125), Color.FromRgb(110, 55, 65)];
            for (int i = 0; i < 4; i++) dc.DrawRectangle(new SolidColorBrush(colors[i]), null, new Rect(i % 2 * Width / 2, i / 2 * Height / 2, Width / 2, Height / 2));
            var thin = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
            for (int x = 0; x < Width; x += 20) dc.DrawLine(thin, new Point(x, 0), new Point(x, Height));
            for (int y = 0; y < Height; y += 20) dc.DrawLine(thin, new Point(0, y), new Point(Width, y));
            if (incoming)
                for (int y = 92; y < 164; y += 4) for (int x = 192; x < 308; x += 4)
                    dc.DrawRectangle((x / 4 + y / 4) % 2 == 0 ? Brushes.White : Brushes.Black, null, new Rect(x, y, 4, 4));
            else dc.DrawRectangle(new SolidColorBrush(colors[3]), null, new Rect(190, 90, 120, 76));
            Text(dc, incoming ? "B" : "A", 35, 23, 64, Brushes.White);
            Text(dc, incoming ? "INCOMING" : "OUTGOING", 97, 50, 17, Brushes.White);
            for (int i = 0; i < 4; i++)
            {
                double x = i % 2 == 0 ? 5 : Width - 30, y = i / 2 == 0 ? 4 : Height - 30;
                dc.DrawRectangle(incoming ? Brushes.Magenta : Brushes.Cyan, null, new Rect(x, y, 24, 24));
                Text(dc, (i + 1).ToString(), x + 6, y + 2, 16, Brushes.Black);
            }
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); Save(bitmap, path);
    }
    private static void DrawContactSheet()
    {
        const int labelWidth = 210, labelHeight = 28;
        int width = labelWidth + Width * SampleTimes.Length, height = labelHeight + Height * Frames.Count;
        var visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 24, 32)), null, new Rect(0, 0, width, height));
            for (int i = 0; i < SampleTimes.Length; i++) Text(dc, SampleTimes[i].ToString("0.00s", CultureInfo.InvariantCulture), labelWidth + i * Width + 10, 3, 16, Brushes.White);
            int row = 0;
            foreach ((string id, List<BitmapSource> frames) in Frames)
            {
                string label = id.Replace("transition.", string.Empty);
                Text(dc, label, 9, labelHeight + row * Height + 45, 17, Brushes.White);
                Text(dc, BuiltinCatalog.Transitions.First(p => p.Id == id).Name, 9, labelHeight + row * Height + 72, 15, Brushes.LightGray);
                for (int i = 0; i < frames.Count; i++) dc.DrawImage(frames[i], new Rect(labelWidth + i * Width, labelHeight + row * Height, Width, Height));
                row++;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); Save(bitmap, Path.Combine(Output, "contact-sheet.png"));
    }
    private static void DrawCaption(string id, string path)
    {
        var visual = new DrawingVisual(); using (DrawingContext dc = visual.RenderOpen())
        { dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), null, new Rect(0, 0, Width, 23)); Text(dc, id.Replace("transition.", string.Empty), 8, 1, 16, Brushes.White); }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); Save(bitmap, path);
    }
    private static void Text(DrawingContext dc, string content, double x, double y, double size, Brush brush) =>
        dc.DrawText(new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1), new Point(x, y));
}
