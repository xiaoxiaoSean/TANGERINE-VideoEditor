using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    private const double SampleTime = 2.5;
    private const int Width = 960;
    private const int Height = 540;
    private const int ManifestVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(Argument(args, "--output=") ??
                Path.Combine("TANGERINE-VideoEditor", "Resources", "CatalogThumbnails"));
            string? only = Argument(args, "--id=");
            bool verifyOnly = args.Contains("--verify-only", StringComparer.Ordinal);
            bool force = args.Contains("--force", StringComparer.Ordinal);
            List<CatalogItem> items = CatalogItems();
            if (only is not null) items = items.Where(item => item.Preset.Id == only).ToList();
            if (items.Count == 0) throw new GenerationException("THMBG0001", "没有找到指定的内置预设。");
            string ffmpeg = TimelineExporter.ResolveEncoderPath();
            string rendererHash = await Hash(typeof(CatalogPreviewService).Assembly.Location);
            string encoderHash = await Hash(ffmpeg);
            if (verifyOnly)
            {
                await Verify(output, CatalogItems(), rendererHash, encoderHash);
                return 0;
            }

            Directory.CreateDirectory(output);
            Manifest? old = ReadManifest(output);
            bool compatible = old is { Version: ManifestVersion } && old.RendererSha256 == rendererHash &&
                old.EncoderSha256 == encoderHash;
            Dictionary<string, Entry> entries = compatible
                ? old!.Entries.ToDictionary(entry => entry.PresetId, StringComparer.Ordinal)
                : [];
            var watch = Stopwatch.StartNew();
            int complete = 0;
            foreach (CatalogItem item in items)
            {
                string path = Path.Combine(output, item.Preset.Id + ".jpg");
                if (!force && compatible &&
                    entries.TryGetValue(item.Preset.Id, out Entry? existing) && File.Exists(path) &&
                    await Hash(path) == existing.Sha256)
                {
                    ValidateImage(path);
                    Console.WriteLine($"[{++complete}/{items.Count}] 已验证 {item.Preset.Id}");
                    continue;
                }

                await Generate(item, ffmpeg, path);
                ValidateImage(path);
                entries[item.Preset.Id] = new Entry(item.Preset.Id, item.Kind, item.Preset.Name,
                    Path.GetFileName(path), Width, Height, item.Kind == "effect" ? 0 : SampleTime,
                    item.Kind == "effect" ? 100 : null,
                    item.Kind == "effect" ? VisualEffectPipeline.ParametersFor(item.Preset.Id)
                        .ToDictionary(parameter => parameter.Id, parameter => parameter.Default) : null,
                    await Hash(path));
                var manifest = new Manifest(ManifestVersion, rendererHash, encoderHash, "example",
                    entries.Values.OrderBy(entry => entry.PresetId, StringComparer.Ordinal).ToList());
                await WriteManifest(output, manifest);
                Console.WriteLine($"[{++complete}/{items.Count}] 已生成 {item.Preset.Id} ({watch.Elapsed.TotalSeconds:F1}s)");
            }

            // An individual regeneration preserves entries belonging to all
            // other presets. A full run additionally proves complete coverage.
            if (only is null) await Verify(output, CatalogItems(), rendererHash, encoderHash);
            Console.WriteLine($"离线缩略图完成：{output}");
            return 0;
        }
        catch (Exception exception)
        {
            string code = exception is GenerationException typed ? typed.StageCode : "THMBG0005";
            Console.Error.WriteLine($"{code}：{exception.Message}");
            return 1;
        }
        finally { CatalogPreviewService.StopPreview(); }
    }

    private static string? Argument(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

    private static List<CatalogItem> CatalogItems() =>
    [
        .. BuiltinCatalog.Titles.Select(preset => new CatalogItem(preset, "title")),
        .. BuiltinCatalog.Transitions.Select(preset => new CatalogItem(preset, "transition")),
        .. BuiltinCatalog.Effects.Concat(BuiltinCatalog.Filters).DistinctBy(preset => preset.Id)
            .Select(preset => new CatalogItem(preset, "effect"))
    ];

    private static async Task Generate(CatalogItem item, string ffmpeg, string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string temporary = output + ".pending.jpg";
        string? video = null;
        try
        {
            if (item.Kind == "effect")
            {
                // Strength is the existing global percentage, not the maximum
                // of every independent parameter. Keep each authored default
                // so contrast, saturation, masks, etc. retain their real meaning.
                var effect = new ClipEffect { PresetId = item.Preset.Id, Name = item.Preset.Name };
                effect.Parameters["intensity"] = JsonSerializer.SerializeToElement(100);
                foreach (VisualEffectPipeline.Parameter parameter in VisualEffectPipeline.ParametersFor(item.Preset.Id))
                    VisualEffectPipeline.SetNumber(effect, parameter, parameter.Default);
                var clip = new TimelineClip { DurationSeconds = 5, Effects = [effect] };
                string filters = VisualEffectPipeline.BuildFfmpegFilters(clip, new ProjectSequence()).TrimStart(',');
                if (filters.Length == 0) filters = "null";
                filters += $",scale={Width}:{Height}:flags=lanczos";

                // The sample service owns the landscape fixture. Read that
                // fixture rather than maintaining a second synthetic picture.
                Type service = typeof(CatalogPreviewService);
                MethodInfo ensure = service.GetMethod("EnsureSampleImagesExist", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new GenerationException("THMBG0002", "找不到示例图生成器。");
                ensure.Invoke(null, [ffmpeg]);
                string sample = (string)(service.GetField("Image1Path", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                    ?? throw new GenerationException("THMBG0002", "找不到内置示例图。"));

                // Evaluate temporal filters at the same midpoint as the video
                // demonstration. Scaling happens only after the real effect.
                List<string> input = item.Preset.Id == "effect.motion-blur"
                    ? ["-loop", "1", "-framerate", "30", "-i", sample]
                    : ["-i", sample];
                string[] frameTime = item.Preset.Id == "effect.motion-blur"
                    ? ["-ss", SampleTime.ToString(CultureInfo.InvariantCulture)] : [];
                await Execute(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    .. input, "-vf", filters, .. frameTime, "-frames:v", "1",
                    "-q:v", "2", "-threads", "2", temporary], timeout.Token);
            }
            else
            {
                void Delivered(string path) => video = path;
                if (item.Kind == "transition")
                    await CatalogPreviewService.PlayTransitionPreviewAsync(item.Preset.Id, ffmpeg, Delivered, timeout.Token);
                else
                    await CatalogPreviewService.PlayTextPreviewAsync(item.Preset.Id, "example", ffmpeg,
                        TitleStyle, Delivered, timeout.Token);
                if (video is null || !File.Exists(video))
                    throw new GenerationException("THMBG0003", $"示例视频未生成：{item.Preset.Id}");

                // Input-side seeking decodes the requested frame accurately;
                // the transition occupies [2, 3), so 2.5 is its true midpoint.
                await Execute(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                    "-ss", SampleTime.ToString(CultureInfo.InvariantCulture), "-i", video,
                    "-frames:v", "1", "-q:v", "2", "-threads", "2", temporary], timeout.Token);
            }
            ValidateImage(temporary);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (video is not null) File.Delete(video);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ClipText TitleStyle(string preset)
    {
        // This is the same internal factory used by timeline insertion and the
        // MCP preview tool. Reflection is confined to this developer utility;
        // the shipped editor does not reflect or render catalog thumbnails.
        MethodInfo factory = typeof(MainWindow).GetMethod("CreateTitleStyle", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new GenerationException("THMBG0002", "找不到内置文字样式工厂。");
        return (ClipText)(factory.Invoke(null, [preset])
            ?? throw new GenerationException("THMBG0002", "内置文字样式为空。"));
    }

    private static async Task Execute(string executable, IEnumerable<string> arguments, CancellationToken cancellation)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardError = true,
                RedirectStandardOutput = true, CreateNoWindow = true
            }
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using CancellationTokenRegistration registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> standard = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(error, standard);
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new GenerationException("THMBG0003", "FFmpeg 生成缩略图失败：" + await error);
    }

    private static void ValidateImage(string path)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1 || decoder.Frames[0].PixelWidth != Width || decoder.Frames[0].PixelHeight != Height)
            throw new GenerationException("THMBG0004", "缩略图尺寸或帧数错误：" + path);
    }

    private static async Task Verify(string output, List<CatalogItem> items,
        string rendererHash, string encoderHash)
    {
        Manifest manifest = ReadManifest(output) ?? throw new GenerationException("THMBG0004", "缺少缩略图清单。");
        if (manifest.Version != ManifestVersion || manifest.RendererSha256 != rendererHash ||
            manifest.EncoderSha256 != encoderHash ||
            manifest.Entries.Count != items.Count ||
            manifest.Entries.Select(entry => entry.PresetId).Distinct().Count() != manifest.Entries.Count)
            throw new GenerationException("THMBG0004", "缩略图清单版本或 ID 存在错误。");
        foreach (CatalogItem item in items)
        {
            Entry entry = manifest.Entries.SingleOrDefault(candidate => candidate.PresetId == item.Preset.Id)
                ?? throw new GenerationException("THMBG0004", "缺少内置预设缩略图：" + item.Preset.Id);
            string path = Path.Combine(output, entry.FileName);
            ValidateImage(path);
            bool effectParametersMatch = item.Kind != "effect" ||
                (entry.AuthoredParameters is not null &&
                 entry.AuthoredParameters.Count == VisualEffectPipeline.ParametersFor(item.Preset.Id).Count &&
                 VisualEffectPipeline.ParametersFor(item.Preset.Id).All(parameter =>
                     entry.AuthoredParameters.TryGetValue(parameter.Id, out double value) && value == parameter.Default));
            if (entry.FileName != item.Preset.Id + ".jpg" || await Hash(path) != entry.Sha256 ||
                entry.Kind != item.Kind || entry.SampleTimeSeconds != (item.Kind == "effect" ? 0 : SampleTime) ||
                (item.Kind == "effect" && (entry.IntensityPercent != 100 || !effectParametersMatch)) ||
                (item.Kind != "effect" && (entry.IntensityPercent is not null || entry.AuthoredParameters is not null)))
                throw new GenerationException("THMBG0004", "缩略图清单或校验值不匹配：" + item.Preset.Id);
        }
        Console.WriteLine($"验证通过：{items.Count} 个预设，全部 960×540，渲染器与 SHA-256 一致，效果强度 100%。");
    }

    private static Manifest? ReadManifest(string output)
    {
        string path = Path.Combine(output, "manifest.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions) : null;
    }

    private static async Task WriteManifest(string output, Manifest manifest)
    {
        string path = Path.Combine(output, "manifest.json");
        string temporary = path + ".pending";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<string> Hash(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private sealed record CatalogItem(BuiltinPreset Preset, string Kind);
    private sealed record Entry(string PresetId, string Kind, string Name, string FileName,
        int Width, int Height, double SampleTimeSeconds, int? IntensityPercent,
        Dictionary<string, double>? AuthoredParameters, string Sha256);
    private sealed record Manifest(int Version, string RendererSha256, string EncoderSha256,
        string SampleText, List<Entry> Entries);
    private sealed class GenerationException(string stageCode, string message) : Exception(message)
    {
        public string StageCode { get; } = stageCode;
    }
}
