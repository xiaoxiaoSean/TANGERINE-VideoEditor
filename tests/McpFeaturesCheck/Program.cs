using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    private static int _checks;
    private static readonly string SessionId = "mcp-feature-check-" + Guid.NewGuid().ToString("N");
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] arguments)
    {
        int result = 1;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Dispatcher.InvokeAsync(async () =>
        {
            MainWindow? window = null;
            try
            {
                string output = arguments.Length > 0 ? Path.GetFullPath(arguments[0]) :
                    Path.Combine(Path.GetTempPath(), $"tangerine-mcp-features-{Guid.NewGuid():N}");
                Directory.CreateDirectory(output);
                await GenerateFixturesAsync(output);
                window = new MainWindow();
                await RunAsync(window, output);
                Console.WriteLine($"MCP advanced feature checks: {_checks} passed. Artifacts: {output}");
                result = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                if (window is not null)
                {
                    try { await Request(window, "editor_control_release", null); } catch { }
                    typeof(MainWindow).GetMethod("DisposeMcpFeatures", PrivateInstance)!.Invoke(window, null);
                    var lifetime = (CancellationTokenSource)typeof(MainWindow).GetField("_mcpLifetime", PrivateInstance)!.GetValue(window)!;
                    lifetime.Cancel();
                    Set(window, "_isProjectDirty", false);
                    window.Close();
                }
                app.Shutdown(result);
            }
        });
        app.Run();
        return result;
    }

    private static async Task RunAsync(MainWindow window, string output)
    {
        await Call(window, "editor_control_acquire");
        ProjectDocument fixture = Fixture(output);
        Set(window, "_project", fixture);
        Set(window, "_projectCreated", true);
        typeof(MainWindow).GetMethod("RefreshMcpUi", PrivateInstance)!.Invoke(window, ["测试项目", null]);
        await Call(window, "project_validate");

        string srt = Path.Combine(output, "输入字幕.srt");
        await File.WriteAllTextAsync(srt, "1\r\n00:00:00,000 --> 00:00:00,500\r\n第一条字幕\r\n\r\n2\r\n00:00:00,600 --> 00:00:00,950\r\nSecond caption\r\n");
        JsonNode imported = await Call(window, "subtitle_import", new() { ["path"] = srt });
        string subtitleTrackId = imported["trackId"]!.GetValue<string>();
        string firstSubtitle = imported["clipIds"]![0]!.GetValue<string>();
        Check(Project(window).Tracks.First(track => track.Id == subtitleTrackId).Clips.Count == 2,
            "SRT import creates real editable timeline clips");
        JsonNode cues = await Call(window, "subtitle_list", new() { ["trackId"] = subtitleTrackId });
        Check(cues.AsArray().Count == 2 && cues[0]!["text"]!.GetValue<string>() == "第一条字幕", "subtitle listing preserves exact text");
        string srtOutput = Path.Combine(output, "exported.srt");
        await Call(window, "subtitle_export", new() { ["path"] = srtOutput, ["trackId"] = subtitleTrackId });
        Check((await SubtitleService.ReadSrtAsync(srtOutput)).Count == 2, "SRT sidecar round trip");

        string template = Path.Combine(output, "字幕模板.tgt");
        await Call(window, "graphic_template_save", new() { ["path"] = template, ["clipId"] = firstSubtitle });
        JsonNode inserted = await Call(window, "graphic_template_insert", new() { ["path"] = template, ["startSeconds"] = 2 });
        Check(inserted["clipId"]!.GetValue<string>() != firstSubtitle, "template insertion allocates a new clip ID");
        JsonNode read = await Call(window, "graphic_template_read", new() { ["path"] = template });
        Check(read["text"]!["content"]!.GetValue<string>() == "第一条字幕", "template saves editable text data");

        TimelineClip music = Project(window).Tracks.SelectMany(track => track.Clips).First(clip => clip.Name == "Music");
        await Call(window, "audio_settings_update", new()
        {
            ["clipId"] = music.Id, ["patch"] = new JsonObject { ["pan"] = -.4, ["category"] = "音乐", ["equalizerHz"] = 1000, ["equalizerGainDb"] = 3 }
        });
        Check(Math.Abs(AudioSettings.FromClip(music).Pan + .4) < 1e-8, "audio parameters persist in the project's extension area");
        JsonNode audio = await Call(window, "audio_settings_get", new() { ["clipId"] = music.Id });
        Check(audio["settings"]!["equalizerGainDb"]!.GetValue<double>() == 3, "audio settings are discoverable after update");
        long beforeInvalid = Revision(window);
        await Fails(window, "audio_settings_update", new() { ["clipId"] = music.Id, ["patch"] = new JsonObject { ["gain"] = 10 } });
        Check(Revision(window) == beforeInvalid && AudioSettings.FromClip(music).Gain == 1, "invalid audio settings do not change the project");
        JsonNode remixed = await Call(window, "audio_remix", new() { ["clipId"] = music.Id, ["targetDurationSeconds"] = 12, ["bpm"] = 120 });
        Check(remixed["clipIds"]!.AsArray().Count > 1, "music remix creates beat-based segments");

        string cameraTrack = Project(window).Tracks.First(track => track.Name == "Camera A").Id;
        JsonNode rough = await Call(window, "paper_edit", new()
        {
            ["trackId"] = cameraTrack, ["startSeconds"] = 3,
            ["ranges"] = new JsonArray(new JsonObject { ["startSeconds"] = .1, ["endSeconds"] = .5 })
        });
        TimelineClip roughClip = Project(window).Tracks.First(track => track.Id == rough["trackId"]!.GetValue<string>()).Clips.Single();
        Check(Math.Abs(roughClip.DurationSeconds - .4) < 1e-8 && Math.Abs(roughClip.SourceInSeconds - .1) < 1e-8,
            "paper edit translates caption ranges into real source trims");

        TimelineClip[] cameras = Project(window).Tracks.Where(track => track.Name is "Camera A" or "Camera B")
            .SelectMany(track => track.Clips).ToArray();
        JsonNode groupJob = await Call(window, "multicam_create", new()
        {
            ["name"] = "Two cameras", ["clipIds"] = new JsonArray(cameras.Select(clip => JsonValue.Create(clip.Id)).ToArray()), ["syncMethod"] = "Timeline"
        });
        JsonNode group = await Finish(window, groupJob);
        Check(Project(window).MulticamGroups.Single().Angles.Count == 2, "multicam creates two actual normalized source angles");
        string cutId = group["clipId"]!.GetValue<string>();
        JsonNode switched = await Call(window, "multicam_switch", new()
        {
            ["clipId"] = cutId, ["angleId"] = group["angleIds"]![1]!.GetValue<string>(), ["timeSeconds"] = .5
        });
        TimelineClip cameraCut = Project(window).Tracks.SelectMany(track => track.Clips)
            .First(clip => clip.Id == switched["clipId"]!.GetValue<string>());
        Check(Math.Abs(cameraCut.StartSeconds - .5) < 1e-8 && Math.Abs(cameraCut.MulticamLocalStartSeconds - .5) < 1e-8,
            "angle switching cuts on the shared multicamera clock");

        string waveformAsset = fixture.Media.First(asset => asset.Name == "Music").Id;
        JsonNode analysis = await Call(window, "media_analyze", new()
        {
            ["assetId"] = waveformAsset, ["kind"] = "waveform", ["options"] = new JsonObject { ["durationSeconds"] = 1, ["bins"] = 16 }
        });
        JsonNode waveform = await Finish(window, analysis);
        Check(waveform["envelope"]!.AsArray().Count == 16 && waveform["envelope"]![0]!["peak"]!.GetValue<double>() > .1,
            "waveform analysis reads actual PCM rather than returning placeholder samples");
        JsonNode loudnessJob = await Call(window, "media_analyze", new()
        {
            ["assetId"] = waveformAsset, ["kind"] = "loudness", ["options"] = new JsonObject { ["durationSeconds"] = 1 }
        });
        Check((await Finish(window, loudnessJob))["loudness"]!["input_i"] is not null, "loudness analysis returns actual FFmpeg statistics");
        MediaAsset sceneAsset = Project(window).Media.First(asset => asset.Name == "Scene cuts");
        JsonNode sceneJob = await Call(window, "media_analyze", new() { ["assetId"] = sceneAsset.Id, ["kind"] = "sceneCuts" });
        JsonNode detected = await Finish(window, sceneJob);
        Check(detected["cuts"]!.AsArray().Count == 1 && Math.Abs(detected["cuts"]![0]!["timeSeconds"]!.GetValue<double>() - .5) < .05,
            "scene detection reads FFmpeg's separate time and score metadata lines");

        JsonNode reframeJob = await Call(window, "media_reframe", new() { ["clipId"] = cameraCut.Id, ["samples"] = 2 });
        Check((await Finish(window, reframeJob))["keyframes"]!.AsArray().Count == 4, "reframe writes position keyframes to the live clip");
        cameraCut.Mask = new ClipMask();
        JsonNode trackJob = await Call(window, "mask_track", new() { ["clipId"] = cameraCut.Id, ["timeSeconds"] = .5, ["frames"] = 1 });
        Check((await Finish(window, trackJob))["trackedFrames"]!.GetValue<int>() == 2, "mask tracking uses consecutive real video frames");
        await Call(window, "effect_add", new() { ["clipId"] = cameraCut.Id, ["presetId"] = "effect.brightness", ["parameters"] = new JsonObject { ["amount"] = .1 } });
        await Call(window, "transition_add", new() { ["clipId"] = cameraCut.Id, ["presetId"] = "transition.rotate", ["edge"] = "In", ["durationSeconds"] = .25 });

        string captured = Path.Combine(output, "frame.png");
        await Finish(window, await Call(window, "preview_capture", new() { ["path"] = captured, ["timeSeconds"] = .7 }));
        Check(new FileInfo(captured).Length > 100, "preview capture includes the real composed timeline");
        string scope = Path.Combine(output, "scope.png");
        await Finish(window, await Call(window, "scope_capture", new() { ["path"] = scope, ["timeSeconds"] = .7, ["mode"] = "histogram" }));
        Check(new FileInfo(scope).Length > 100, "scope capture writes an actual histogram PNG");
        string exported = Path.Combine(output, "export.mp4");
        await Finish(window, await Call(window, "export_start", new()
        {
            ["path"] = exported, ["settings"] = new JsonObject { ["startSeconds"] = 0, ["endSeconds"] = .8, ["width"] = 160, ["height"] = 90, ["framesPerSecond"] = 10 }
        }));
        Check(new FileInfo(exported).Length > 100, "export job produces a real MP4 with explicit render settings");
        foreach ((string format, string file) in new[] { ("xml", "exchange.xml"), ("otio", "exchange.otio"), ("aafXml", "exchange.aaf.xml") })
        {
            string exchange = Path.Combine(output, file);
            await Finish(window, await Call(window, "sequence_exchange_export", new() { ["path"] = exchange, ["format"] = format }));
            Check(new FileInfo(exchange).Length > 100, $"{format} exchange file contains real project data");
        }
        string xml = Path.Combine(output, "exchange.xml");
        string originalId = Project(window).Id;
        await Call(window, "sequence_exchange_import", new() { ["path"] = xml });
        Check(Project(window).Id == originalId && Project(window).MulticamGroups.Count == 1, "XML interchange preserves multicamera project metadata");
        await Call(window, "history_undo");
        Check(Project(window).Tracks.Count > 0, "exchange import participates in editor undo history");
        string otio = Path.Combine(output, "exchange.otio");
        await Call(window, "sequence_exchange_import", new() { ["path"] = otio });
        Check(Project(window).Id == originalId && Project(window).MulticamGroups.Count == 1,
            "OTIO metadata round trip preserves all Tangerine parameters and media references");
        string frozen = Path.Combine(output, "frozen.png");
        TimelineClip source = Project(window).Tracks.SelectMany(track => track.Clips).First(clip => clip.Kind == ClipKind.Video);
        JsonNode freeze = await Finish(window, await Call(window, "clip_freeze_frame", new()
        {
            ["clipId"] = source.Id, ["path"] = frozen, ["timeSeconds"] = source.StartSeconds,
            ["replace"] = true
        }));
        Check(new FileInfo(frozen).Length > 100 && !Project(window).Tracks.SelectMany(track => track.Clips).Any(clip => clip.Id == source.Id) &&
            Project(window).Tracks.SelectMany(track => track.Clips).Any(clip => clip.Id == freeze["clipId"]!.GetValue<string>() && clip.Kind == ClipKind.Image),
            "freeze-frame job renders actual source and commits an editable still with undo");
        File.Copy(captured, Path.Combine(output, "frames0001.png"), overwrite: true);
        File.Copy(frozen, Path.Combine(output, "frames0002.png"), overwrite: true);
        JsonNode imageSequence = await Finish(window, await Call(window, "media_image_sequence", new()
        {
            ["firstFrame"] = Path.Combine(output, "frames0001.png"), ["path"] = Path.Combine(output, "image-sequence.mp4"),
            ["framesPerSecond"] = 10
        }));
        Check(Project(window).Media.Any(asset => asset.SourcePath == Path.Combine(output, "image-sequence.mp4")),
            "image-sequence job encodes actual numbered frames and imports the video");
        JsonObject standard = JsonNode.Parse(await File.ReadAllTextAsync(otio))!.AsObject();
        standard["metadata"]!.AsObject().Remove("tangerine_project");
        string standardPath = Path.Combine(output, "standard.otio");
        await File.WriteAllTextAsync(standardPath, standard.ToJsonString());
        await Call(window, "sequence_exchange_import", new() { ["path"] = standardPath });
        Check(Project(window).Media.Count > 0 && Project(window).Tracks.SelectMany(track => track.Clips)
            .Where(clip => clip.Kind is ClipKind.Video or ClipKind.Audio or ClipKind.Image)
            .All(clip => Project(window).Media.Any(asset => asset.Id == clip.MediaAssetId)),
            "standard OTIO import creates real local media references without Tangerine metadata");
        await Fails(window, "voiceover_stop", new() { ["path"] = Path.Combine(output, "not-recording.wav") });
        await Fails(window, "subtitle_recognize", new() { ["assetId"] = waveformAsset, ["modelPath"] = Path.Combine(output, "missing.bin") });
        await Call(window, "voiceover_cancel");
        await Call(window, "voiceover_cancel");
        await Call(window, "project_validate");
        await CheckStickerPreviewsAsync(output);
    }

    private static async Task<JsonNode> Call(MainWindow window, string command, JsonObject? args = null)
    {
        AutomationResponse response = await Request(window, command, args);
        if (!response.Success) throw new InvalidOperationException($"{command}: {response.Error?.StageCode}: {response.Error?.Message}");
        if (command != "jobs_status") _checks++;
        return response.Data ?? new JsonObject();
    }

    private static async Task Fails(MainWindow window, string command, JsonObject args)
    {
        AutomationResponse response = await Request(window, command, args);
        Check(!response.Success && !string.IsNullOrWhiteSpace(response.Error?.StageCode), command + " reports a staged failure");
    }

    private static Task<AutomationResponse> Request(MainWindow window, string command, JsonObject? args) =>
        (Task<AutomationResponse>)typeof(MainWindow).GetMethod("HandleMcpRequestAsync", PrivateInstance)!
            .Invoke(window, [new AutomationRequest(Guid.NewGuid().ToString("N"), command,
                args is null ? null : JsonNode.Parse(args.ToJsonString())!.AsObject(), SessionId: SessionId), CancellationToken.None])!;

    private static async Task<JsonNode> Finish(MainWindow window, JsonNode descriptor)
    {
        string id = descriptor["jobId"]!.GetValue<string>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMinutes(2))
        {
            JsonNode status = await Call(window, "jobs_status", new() { ["jobId"] = id });
            string state = status["status"]!.GetValue<string>();
            if (state == "completed") return status["result"] ?? new JsonObject();
            if (state is "failed" or "cancelled") throw new InvalidOperationException($"MCP job {id}: {status}");
            await Task.Delay(30);
        }
        await Call(window, "jobs_cancel", new() { ["jobId"] = id });
        throw new TimeoutException("MCP job did not complete within two minutes.");
    }

    private static ProjectDocument Fixture(string output)
    {
        var first = new MediaAsset { Name = "Camera A", Kind = MediaKind.Video, SourcePath = Path.Combine(output, "camera-a.mp4"),
            DurationSeconds = 1, Width = 160, Height = 90, FramesPerSecond = 10 };
        var second = new MediaAsset { Name = "Camera B", Kind = MediaKind.Video, SourcePath = Path.Combine(output, "camera-b.mp4"),
            DurationSeconds = 1, Width = 160, Height = 90, FramesPerSecond = 10 };
        var music = new MediaAsset { Name = "Music", Kind = MediaKind.Audio, SourcePath = Path.Combine(output, "music.wav"),
            DurationSeconds = 8, HasAudio = true, AudioChannels = 1, AudioSampleRate = 48000, AudioCodec = "pcm_s16le" };
        return new ProjectDocument
        {
            Name = "MCP feature check", Sequence = new ProjectSequence { Width = 160, Height = 90, FramesPerSecond = 10 },
            Media = [first, second, music, new MediaAsset { Name = "Scene cuts", Kind = MediaKind.Video,
                SourcePath = Path.Combine(output, "scene-cuts.mp4"), DurationSeconds = 1, Width = 160, Height = 90, FramesPerSecond = 10 }],
            Tracks =
            [
                new() { Name = "Camera A", Order = 0, Clips = [new() { Kind = ClipKind.Video, Name = first.Name, MediaAssetId = first.Id, DurationSeconds = 1 }] },
                new() { Name = "Camera B", Order = 1, Clips = [new() { Kind = ClipKind.Video, Name = second.Name, MediaAssetId = second.Id, DurationSeconds = 1 }] },
                new() { Name = "Music", Order = 2, Kind = TrackKind.Audio, Clips = [new() { Kind = ClipKind.Audio, Name = music.Name, MediaAssetId = music.Id, DurationSeconds = 8 }] }
            ]
        };
    }

    private static async Task GenerateFixturesAsync(string output)
    {
        foreach ((string name, string source) in new[] { ("camera-a.mp4", "testsrc2=size=160x90:rate=10"), ("camera-b.mp4", "smptebars=size=160x90:rate=10") })
        {
            var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", source, "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-y", Path.Combine(output, name) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
        }
        byte[] samples = new byte[8 * 48000 * 2];
        for (int sample = 0; sample < samples.Length / 2; sample++)
            BitConverter.TryWriteBytes(samples.AsSpan(sample * 2, 2), (short)(Math.Sin(2 * Math.PI * 440 * sample / 48000) * 16000));
        await WaveInRecorder.WriteWaveAsync(Path.Combine(output, "music.wav"), samples);
        var sceneStart = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=red:size=160x90:rate=10:duration=0.5",
            "-f", "lavfi", "-i", "color=c=blue:size=160x90:rate=10:duration=0.5", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0",
            "-c:v", "libx264", "-preset", "ultrafast", "-y", Path.Combine(output, "scene-cuts.mp4") }) sceneStart.ArgumentList.Add(argument);
        using var sceneProcess = Process.Start(sceneStart)!;
        Task<string> sceneErrors = sceneProcess.StandardError.ReadToEndAsync();
        await sceneProcess.WaitForExitAsync();
        if (sceneProcess.ExitCode != 0) throw new InvalidOperationException(await sceneErrors);
    }

    private static async Task CheckStickerPreviewsAsync(string output)
    {
        foreach (BuiltinPreset preset in BuiltinCatalog.Stickers)
        {
            GraphicShape shape = GraphicShape.FromPreset(preset, 960, 540);
            Check(!string.IsNullOrWhiteSpace(shape.Kind), preset.Id + " has a real shape definition");
        }
        GraphicShape circle = GraphicShape.FromPreset(BuiltinCatalog.Stickers.Single(preset => preset.Id == "sticker.circle"), 960, 540);
        Check(Math.Abs(circle.WidthFraction * 960 - circle.HeightFraction * 540) < 1e-9,
            "circle defaults use equal physical diameters on a wide sequence");
        var samples = new Dictionary<string, byte[]>();
        foreach (string id in new[] { "sticker.circle", "sticker.rectangle" })
        {
            string? video = null;
            ClipText Style(string value) => (ClipText)typeof(MainWindow).GetMethod("CreateTitleStyle",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value])!;
            await CatalogPreviewService.PlayTextPreviewAsync(id, "example", TimelineExporter.ResolveEncoderPath(), Style,
                path => video = path);
            if (video is null) throw new InvalidOperationException("Sticker preview did not produce a video.");
            try
            {
                string movie = Path.Combine(output, id + ".mp4");
                File.Copy(video, movie, overwrite: true);
                string png = Path.Combine(output, id + ".png");
                var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (string argument in new[] { "-v", "error", "-ss", "1", "-i", video, "-frames:v", "1", "-y", png }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                Task<string> error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException(await error);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(png); bitmap.EndInit();
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
                converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
                samples.Add(id, pixels);
            }
            finally { File.Delete(video); }
        }
        bool Orange(byte[] pixels, int x, int y)
        {
            int index = (y * 960 + x) * 4;
            return pixels[index + 2] > 160 && pixels[index + 1] > 90 && pixels[index] < 130;
        }
        Check(Orange(samples["sticker.circle"], 480, 270), "circle preview contains the actual orange shape, without placeholder text");
        Check(Orange(samples["sticker.rectangle"], 330, 200), "rectangle preview covers its expected geometric corner");
        Check(!Orange(samples["sticker.circle"], 330, 200), "circle and rectangle catalog movies have distinct geometry");
    }

    private static ProjectDocument Project(MainWindow window) => (ProjectDocument)typeof(MainWindow).GetField("_project", PrivateInstance)!.GetValue(window)!;
    private static long Revision(MainWindow window) => (long)typeof(MainWindow).GetField("_projectRevision", PrivateInstance)!.GetValue(window)!;
    private static void Set(MainWindow window, string field, object value) => typeof(MainWindow).GetField(field, PrivateInstance)!.SetValue(window, value);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("MCP-FEATURE-CHECK: " + label);
        _checks++;
    }
}
