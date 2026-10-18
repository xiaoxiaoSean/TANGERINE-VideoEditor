using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    private static readonly string Ffmpeg = TimelineExporter.ResolveEncoderPath();
    private static readonly List<object> Measurements = [];
    private static readonly List<string> Failures = [];
    private static int Checks;

    private static async Task Main(string[] args)
    {
        bool baseline = args.Contains("--baseline", StringComparer.Ordinal);
        bool cold = args.Contains("--cold", StringComparer.Ordinal);
        string report = args.FirstOrDefault(value => value.StartsWith("--report=", StringComparison.Ordinal))?[9..]
            ?? Path.Combine(AppContext.BaseDirectory, baseline ? "baseline.json" : "preview-check.json");
        string nonce = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        try
        {
            if (!baseline) await Check("gpu/profile-and-real-encode", CheckEncodingProfile);
            var examples = baseline
                ? new[] { ("transition", "transition.rotate"), ("transition", "transition.blur-dissolve"), ("title", "title.counter") }
                : new[]
                {
                    ("effect", "effect.vintage"), ("effect", "effect.blur"),
                    ("transition", "transition.rotate"), ("transition", "transition.shake"),
                    ("transition", "transition.blur-dissolve"),
                    ("title", "title.typewriter"), ("title", "title.counter"),
                    ("title", "title.clean-lower-third"), ("title", "title.outline-pop")
                };
            foreach (var (kind, id) in examples)
                await Check($"{kind}/{id}", async () =>
                {
                    // A unique title string gives the text cache a genuinely new key.
                    // Transition/effect timings are labelled first-call because an
                    // existing persistent cache entry may predate this test process.
                    string sample = $"Preview {nonce}";
                    if (cold && !baseline) RemoveSpecificCacheEntry(kind, id, sample);
                    var first = await Render(kind, id, sample);
                    try
                    {
                        await VerifyVideo(first.Path);
                        if (baseline)
                        {
                            Measurements.Add(new { Kind = kind, Id = id, FirstCallMilliseconds = first.Milliseconds });
                            Console.WriteLine($"TIME {id}: {first.Milliseconds:F1} ms");
                            return;
                        }
                        var cached = await Render(kind, id, sample);
                        try
                        {
                            Require(!StringComparer.OrdinalIgnoreCase.Equals(first.Path, cached.Path), "Cache reused a playback lease path");
                            byte[] firstHash = SHA256.HashData(await File.ReadAllBytesAsync(first.Path));
                            byte[] cachedHash = SHA256.HashData(await File.ReadAllBytesAsync(cached.Path));
                            Require(firstHash.SequenceEqual(cachedHash), "Cached bytes differ from original rendering");
                            Require(cached.Milliseconds < 1000, $"Cached playback took {cached.Milliseconds:F1} ms");
                            File.Delete(first.Path);
                            Require(File.Exists(cached.Path), "Deleting one playback lease removed another lease");
                            await VerifyVideo(cached.Path);
                            Measurements.Add(new { Kind = kind, Id = id, Cold = cold, FirstCallMilliseconds = first.Milliseconds,
                                CachedMilliseconds = cached.Milliseconds });
                            Console.WriteLine($"TIME {id}: first {first.Milliseconds:F1} ms, cached {cached.Milliseconds:F1} ms");
                        }
                        finally { File.Delete(cached.Path); }
                    }
                    finally { File.Delete(first.Path); }
                });
            if (!baseline)
            {
                await Check("catalog/pre-cancelled-cache-hit", async () =>
                {
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    string? delivered = null;
                    try
                    {
                        await CatalogPreviewService.PlayEffectPreviewAsync("effect.vintage", Ffmpeg,
                            path => delivered = path, cancelled.Token);
                    }
                    catch (OperationCanceledException) { }
                    try { Require(delivered is null, "A pre-cancelled cache hit delivered a playback callback"); }
                    finally { if (delivered is not null) File.Delete(delivered); }
                });
                await Check("catalog/superseded-cold-title", async () =>
                {
                    string? staleDelivered = null;
                    string? latest = null;
                    var stale = CatalogPreviewService.PlayTextPreviewAsync("title.typewriter", "Superseded " + Guid.NewGuid(),
                        Ffmpeg, Style, path => staleDelivered = path);
                    var current = CatalogPreviewService.PlayEffectPreviewAsync("effect.vintage", Ffmpeg, path => latest = path);
                    try
                    {
                        await Task.WhenAll(stale, current);
                        Require(staleDelivered is null, "Superseded title delivered its playback callback");
                        Require(latest is not null && File.Exists(latest), "New preview was cancelled by superseded preview cleanup");
                    }
                    finally
                    {
                        if (latest is not null) File.Delete(latest);
                        if (staleDelivered is not null) File.Delete(staleDelivered);
                    }
                });
            }
        }
        finally
        {
            CatalogPreviewService.StopPreview();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                Baseline = baseline, Cold = cold, Assembly = typeof(CatalogPreviewService).Assembly.Location,
                CreatedUtc = DateTime.UtcNow, Checks, Failures, Measurements
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Checks: {Checks}; Failures: {Failures.Count}; Report: {Path.GetFullPath(report)}");
            foreach (string failure in Failures) Console.WriteLine(failure);
            Environment.ExitCode = Failures.Count == 0 ? 0 : 1;
        }
    }

    private static async Task CheckEncodingProfile()
    {
        // Reflection keeps the same benchmark executable compatible with the
        // unmodified baseline assembly, which has no encoding-profile type.
        Type type = typeof(CatalogPreviewService).Assembly.GetType("TANGERINE_VideoEditor.Editor.PreviewEncodingProfile")
            ?? throw new InvalidOperationException("PreviewEncodingProfile is missing");
        var watch = Stopwatch.StartNew();
        Task pending = (Task)type.GetMethod("SelectAsync")!.Invoke(null, [Ffmpeg, CancellationToken.None])!;
        await pending;
        object profile = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
        string encoder = (string)type.GetProperty("EncoderName")!.GetValue(profile)!;
        bool hardware = (bool)type.GetProperty("IsHardwareAccelerated")!.GetValue(profile)!;
        string[] encoding = ((IEnumerable<string>)type.GetProperty("VideoArguments")!.GetValue(profile)!).ToArray();
        Measurements.Add(new { EncodingProfile = encoder, HardwareAccelerated = hardware, ProbeMilliseconds = watch.Elapsed.TotalMilliseconds });
        Console.WriteLine($"ENCODER {encoder}; hardware {hardware}; probe {watch.Elapsed.TotalMilliseconds:F1} ms");
        string output = Path.Combine(Path.GetTempPath(), $"tangerine-profile-test-{Guid.NewGuid():N}.mp4");
        try
        {
            await Execute(Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi", "-i",
                "testsrc2=s=160x90:r=30:d=0.2", "-an", .. encoding, "-pix_fmt", "yuv420p", output]);
            await Execute(Ffmpeg, ["-v", "error", "-i", output, "-f", "null", "-"]);
        }
        finally { File.Delete(output); }
    }

    private static async Task<(string Path, double Milliseconds)> Render(string kind, string id, string sample)
    {
        string? output = null;
        var watch = Stopwatch.StartNew();
        Action<string> delivered = path => output = path;
        await (kind switch
        {
            "effect" => CatalogPreviewService.PlayEffectPreviewAsync(id, Ffmpeg, delivered),
            "transition" => CatalogPreviewService.PlayTransitionPreviewAsync(id, Ffmpeg, delivered),
            _ => CatalogPreviewService.PlayTextPreviewAsync(id, sample, Ffmpeg, Style, delivered)
        });
        Require(output is not null && File.Exists(output), "No playback file was delivered");
        return (output!, watch.Elapsed.TotalMilliseconds);
    }

    private static ClipText Style(string preset) => (ClipText)typeof(MainWindow)
        .GetMethod("CreateTitleStyle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [preset])!;

    private static void RemoveSpecificCacheEntry(string kind, string id, string sample)
    {
        string content = kind + "|" + id;
        if (kind == "title")
        {
            ClipText style = Style(id);
            style.Content = id switch
            {
                "title.counter" => "0 → 100",
                "title.credits" => $"{sample}\nDIRECTOR\nCAMERA\nEDITOR\nTHANK YOU",
                _ => sample
            };
            content = "text|" + id + "|" + JsonSerializer.Serialize(style);
        }
        Type type = typeof(CatalogPreviewService).Assembly.GetType("TANGERINE_VideoEditor.Editor.CatalogPreviewCache")
            ?? throw new InvalidOperationException("CatalogPreviewCache is missing");
        string directory = (string)type.GetField("DirectoryPath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        string key = (string)type.GetMethod("Key", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [Ffmpeg, content])!;
        // Clear exactly the cache entry this test is about to measure. Never
        // purge the cache directory or unrelated previews from an active app.
        string path = Path.Combine(directory, key + ".mp4");
        if (File.Exists(path)) File.Delete(path);
    }

    private static async Task VerifyVideo(string path)
    {
        string probe = Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe.exe");
        string json = await Execute(probe, ["-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=width,height,avg_frame_rate,codec_name:format=duration", "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        JsonElement stream = document.RootElement.GetProperty("streams")[0];
        Require(stream.GetProperty("width").GetInt32() == 960 && stream.GetProperty("height").GetInt32() == 540,
            "Preview dimensions changed from 960x540");
        string[] rate = stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        double fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
        Require(Math.Abs(fps - 30) < .01, $"Unexpected preview frame rate: {fps}");
        double duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        Require(Math.Abs(duration - 5) < .1, $"Unexpected preview duration: {duration}");
        Require(stream.GetProperty("codec_name").GetString() == "h264", "Preview codec is not H.264");
        // Decode every output frame to catch MP4 files that were finalized but
        // contain truncated or otherwise unusable encoded video packets.
        await Execute(Ffmpeg, ["-v", "error", "-xerror", "-i", path, "-f", "null", "-"]);
    }

    private static async Task<string> Execute(string executable, IEnumerable<string> arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        string text = await stdout;
        string error = await stderr;
        Require(process.ExitCode == 0, $"{Path.GetFileName(executable)} failed: {error}");
        return text;
    }

    private static async Task Check(string name, Func<Task> action)
    {
        Checks++;
        try { await action().WaitAsync(TimeSpan.FromSeconds(180)); Console.WriteLine("PASS " + name); }
        catch (Exception exception) { Failures.Add(name + ": " + exception); Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
