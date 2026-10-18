using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
class Program
{
    static readonly string Ffmpeg = TimelineExporter.ResolveEncoderPath();
    static readonly string Work = Path.Combine(Path.GetTempPath(), "tangerine-render-audit-" + Guid.NewGuid().ToString("N"));
    static readonly List<string> Failures = [];
    static int Checks;
    static string? Filter;
    static async Task Main(string[] args)
    {
        Filter = args.FirstOrDefault();
        Directory.CreateDirectory(Work);
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(Ffmpeg) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        foreach (var preset in BuiltinCatalog.Effects)
            await Check(preset.Id, async () => { var (p, c) = Project(); c.Effects.Add(new ClipEffect { PresetId = preset.Id }); await Render(p); });
        foreach (var preset in BuiltinCatalog.Transitions)
            foreach (var edge in new[] { "In", "Out" })
                await Check(preset.Id + "/" + edge, async () => { var (p, c) = Project(); c.Transitions.Add(new ClipTransition { PresetId = preset.Id, Edge = edge, DurationSeconds = .5 }); await Render(p); });
        foreach (var preset in BuiltinCatalog.Transitions)
            await Check(preset.Id + "/boundary", async () =>
            {
                var (p, prior) = Project();
                var incoming = new TimelineClip { Kind = ClipKind.Color, Name = "Incoming", Color = "#CC8844", StartSeconds = 1, DurationSeconds = 1 };
                incoming.Transitions.Add(new ClipTransition { PresetId = preset.Id, DurationSeconds = .5 });
                p.Tracks[0].Clips.Add(incoming);
                await Render(p, 1.25);
            });
        foreach (var preset in BuiltinCatalog.Effects)
            foreach (var parameter in VisualEffectPipeline.ParametersFor(preset.Id).Where(x => x.Animatable))
                await Check(preset.Id + "/keyframe", async () => { var (p, c) = Project(); var e = new ClipEffect { PresetId = preset.Id }; c.Effects.Add(e); var prop = VisualEffectPipeline.ParameterProperty(e, parameter); c.Keyframes.Add(new ClipKeyframe { Property = prop, TimeSeconds = 0, Value = parameter.Minimum }); c.Keyframes.Add(new ClipKeyframe { Property = prop, TimeSeconds = 1, Value = parameter.Default }); await Render(p); });
        await Check("vintage/zero", async () => { var (p, c) = Project(); var e = new ClipEffect { PresetId = "effect.vintage" }; c.Effects.Add(e); VisualEffectPipeline.SetNumber(e, VisualEffectPipeline.ParametersFor(e.PresetId)[0], 0); await Render(p); });
        await Check("vintage/fr-FR", async () => { var culture = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); var (p, c) = Project(); c.Effects.Add(new ClipEffect { PresetId = "effect.vintage" }); await Render(p); } finally { CultureInfo.CurrentCulture = culture; } });
        await Check("key/intensity200", async () => { var (p, c) = Project(); var e = new ClipEffect { PresetId = "effect.key" }; e.Parameters["intensity"] = JsonSerializer.SerializeToElement(200); VisualEffectPipeline.SetNumber(e, VisualEffectPipeline.ParametersFor(e.PresetId)[0], 1); c.Effects.Add(e); await Render(p); });
        await Check("hsl/configured", async () => { var (p, c) = Project(); var e = new ClipEffect { PresetId = "effect.hsl" }; e.Parameters["hue"] = JsonSerializer.SerializeToElement(60); c.Effects.Add(e); await Render(p); });
        await Check("lut/configured", async () =>
        {
            string lut = Path.Combine(Work, "identity.cube");
            await File.WriteAllTextAsync(lut, "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
            var (p, c) = Project(); var e = new ClipEffect { PresetId = "effect.lut" }; VisualEffectPipeline.SetString(e, "path", lut); e.Parameters["strength"] = JsonSerializer.SerializeToElement(.5); c.Effects.Add(e); await Render(p);
        });
        foreach (var preset in BuiltinCatalog.Titles)
            await Check(preset.Id, async () =>
            {
                var (p, c) = Project(); c.Kind = ClipKind.Text; c.TemplateId = preset.Id;
                c.Text = (ClipText)typeof(MainWindow).GetMethod("CreateTitleStyle", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [preset.Id])!;
                c.Text.Content = preset.Id == "title.counter" ? "0 -> 100" : "example"; c.Text.FontSize = 18;
                var preview = Path.Combine(Work, c.Id + "-preview.png"); var export = Path.Combine(Work, c.Id + ".png");
                await Sta(() => TitleBitmapRenderer.Render(c, p.Sequence, preview, .5));
                string exportSource = export;
                if (DynamicTitlePreset.HasChangingText(c)) await Sta(() => exportSource = DynamicTitleFrames.Render(c, p.Sequence, Path.Combine(Work, c.Id)));
                else await Sta(() => TitleBitmapRenderer.Render(c, p.Sequence, export));
                var bitmap = await CompositeFrameRenderer.RenderFrameAsync(p, .5, 160, 90, titleImagePaths: new Dictionary<string, string> { { c.Id, preview } }, previewGpuMode: "CPU 软件渲染");
                await TimelineExporter.ExportMp4Async(p, Path.Combine(Work, c.Id + ".mp4"), new Dictionary<string, string> { { c.Id, exportSource } });
            });
        await Check("catalog/effect", async () => { string? file = null; await CatalogPreviewService.PlayEffectPreviewAsync("effect.vintage", Ffmpeg, x => file = x); if (file == null || !File.Exists(file)) throw new Exception("No preview output"); File.Delete(file); });
        await Check("catalog/transition", async () => { string? file = null; await CatalogPreviewService.PlayTransitionPreviewAsync("transition.page", Ffmpeg, x => file = x); if (file == null || !File.Exists(file)) throw new Exception("No preview output"); File.Delete(file); });
        await Check("catalog/cancel", async () => { using var cts = new CancellationTokenSource(); var task = CatalogPreviewService.PlayEffectPreviewAsync("effect.blur", Ffmpeg, _ => throw new Exception("Cancelled output delivered"), cts.Token); cts.CancelAfter(50); await task.WaitAsync(TimeSpan.FromSeconds(20)); CatalogPreviewService.StopPreview(); });
        await Check("catalog/switch", async () =>
        {
            string? generated = null;
            var old = CatalogPreviewService.PlayEffectPreviewAsync("effect.blur", Ffmpeg, _ => throw new Exception("Superseded output delivered"));
            var current = CatalogPreviewService.PlayTransitionPreviewAsync("transition.fade", Ffmpeg, x => generated = x);
            await Task.WhenAll(old, current);
            if (generated is null || !File.Exists(generated)) throw new Exception("New preview was cancelled by old preview");
            File.Delete(generated);
        });
        Console.WriteLine($"Checks: {Checks}; Failures: {Failures.Count}; Outputs: {Work}");
        foreach (var failure in Failures) Console.WriteLine(failure);
        Environment.ExitCode = Failures.Count == 0 ? 0 : 1;
    }
    static (ProjectDocument, TimelineClip) Project()
    {
        var p = ProjectDocument.CreateDefault(); p.Sequence.Width = 160; p.Sequence.Height = 90; p.Sequence.FramesPerSecond = 8;
        var c = new TimelineClip { Kind = ClipKind.Color, Name = "Audit", DurationSeconds = 1, Color = "#4488CC" }; p.Tracks[0].Clips.Add(c); return (p, c);
    }
    static async Task Render(ProjectDocument p, double time = .25)
    {
        var b = await CompositeFrameRenderer.RenderFrameAsync(p, time, 160, 90, previewGpuMode: "CPU 软件渲染");
        if (b.PixelWidth != 160) throw new Exception("Invalid preview frame");
        await TimelineExporter.ExportMp4Async(p, Path.Combine(Work, Guid.NewGuid() + ".mp4"), new Dictionary<string, string>());
    }
    static async Task Check(string name, Func<Task> action)
    {
        if (Filter is not null && !name.StartsWith(Filter, StringComparison.Ordinal)) return;
        Checks++; try { await action().WaitAsync(TimeSpan.FromSeconds(40)); Console.WriteLine("PASS " + name); } catch (Exception e) { Failures.Add(name + ": " + e); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static Task Sta(Action action)
    {
        var done = new TaskCompletionSource(); var thread = new Thread(() => { try { action(); done.SetResult(); } catch (Exception e) { done.SetException(e); } }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
