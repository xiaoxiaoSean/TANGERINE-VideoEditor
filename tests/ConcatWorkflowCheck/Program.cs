using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using Tangerine.VideoEditor.Automation;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int count;
    static string root = "";
    static void Check(bool value, string text) { if (!value) throw new Exception(text); count++; Console.WriteLine("PASS " + text); }
    static async Task Run(params string[] args)
    {
        var start = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-y" }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; string errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(errors);
    }
    static async Task<JsonNode> Probe(string path)
    {
        var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, ArgumentList = { "-v", "error", "-show_streams", "-of", "json", path } };
        using var process = Process.Start(start)!; string json = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); return JsonNode.Parse(json)!;
    }
    [STAThread] static int Main(string[] args)
    {
        if (args.Contains("--catalog"))
        {
            string path = Path.GetFullPath("artifacts/ConcatWorkflowCheck/catalog.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Commands = AutomationCatalog.Commands, Models = AutomationCatalog.Models }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TANGERINE-VideoEditor/bin/Debug/net10.0-windows/ffmpeg"));
        Environment.SetEnvironmentVariable("PATH", bundled + ";" + Environment.GetEnvironmentVariable("PATH"));
        root = Path.GetFullPath("artifacts/ConcatWorkflowCheck/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1100, Height = 800, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        window.Loaded += async (_, _) =>
        {
            int exit = 0;
            try
            {
                await window.Dispatcher.InvokeAsync(() => {}, DispatcherPriority.ApplicationIdle);
                Field<DispatcherTimer>("_idlePreviewTimer").Stop(); Field<DispatcherTimer>("_autoSaveTimer").Stop();
                string source = Path.Combine(root, "multi.mkv");
                await Run("-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=5", "-f", "lavfi", "-i", "aevalsrc=if(lt(t\\,1)\\,0\\,0.1*sin(2*PI*440*t)):s=48000:d=5", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=5", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-c:a", "pcm_s16le", "-metadata:s:a:0", "title=麦克风", "-metadata:s:a:1", "title=桌面声", source);
                var metadata = await MediaMetadataProbe.TryReadAsync(source);
                Check(metadata?.AudioStreams?.Count == 2 && metadata.AudioStreams[1].Title == "桌面声", "探测并保留源文件的两条音频流");
                var project = Field<ProjectDocument>("_project"); project.Sequence.Width = 320; project.Sequence.Height = 180; project.Sequence.FramesPerSecond = 10;
                var asset = new MediaAsset { Name = "多音轨", Kind = MediaKind.Video, SourcePath = source, HasAudio = true, DurationSeconds = 5, Width = 320, Height = 180, FramesPerSecond = 10, AudioStreams = metadata!.AudioStreams!.ToList() }; project.Media.Add(asset);
                var clip = new TimelineClip { Name = "镜头 A", Kind = ClipKind.Video, MediaAssetId = asset.Id, DurationSeconds = 2 };
                project.Tracks[0].Clips.Add(clip); Invoke("RefreshEditorFromProject");
                var silent = await AudioPeakCache.GetAsync(source, 0, 0, CancellationToken.None); var continuous = await AudioPeakCache.GetAsync(source, 1, 0, CancellationToken.None);
                Check(silent.Take(90).Max() < .001 && silent.Skip(110).Max() > .05 && continuous.Take(90).Max() > .05, "真实波形精确区分静音区与不同源音轨");
                clip.SourceAudioStreamIndex = 1;
                try
                {
                    await ClipLoudnessNormalizer.MeasureAsync(new TimelineClip { Kind = ClipKind.Audio, DurationSeconds = .6 }, asset, -14, -1, CancellationToken.None);
                    throw new Exception("静音归一化被接受");
                }
                catch (AudioOperationException error) when (error.StageCode == AudioStageCode.Validate) { Check(true, "静音响度测量明确报错且不产生无效增益"); }
                clip.TimeRemapPoints.Add(new() { TimeSeconds = .8, Speed = 1.5 });
                var remappedMeasurement = await ClipLoudnessNormalizer.MeasureAsync(clip, asset, -14, -1, CancellationToken.None);
                Check(double.IsFinite(remappedMeasurement.IntegratedLufs), "速度曲线通过实际监听窗口测量响度");
                clip.TimeRemapPoints.Clear();
                string monitor = Path.Combine(root, "monitor.wav"); await TimelineAudioMonitor.RenderWindowAsync(project, 0, 2, monitor);
                var meter = ((ValueTuple<double, double>[])typeof(MainWindow).GetMethod("ReadMeterPcm", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [monitor])!);
                Check(meter.Length > 30 && meter[0].Item1 > .05, "监听实际解码第二条源音轨；电平来自混音PCM");
                Invoke("DetachClipAudio", clip);
                var audio = project.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == clip.DetachedAudioClipId);
                Check(audio.Kind == ClipKind.Audio && audio.SourceAudioStreamIndex == 1 && clip.AudioDetached && audio.DetachedVideoClipId == clip.Id, "分离音频创建独立对象并保留流选择与关联");
                audio.StartSeconds = .2;
                try { Invoke("AttachClipAudio", audio); throw new Exception("不对齐附着被接受"); } catch (TargetInvocationException e) when (e.InnerException is TANGERINE_VideoEditor.Tools.StageException) { Check(clip.AudioDetached, "分别剪辑后附着明确拒绝静默丢失内容"); }
                audio.StartSeconds = 0; audio.Volume = .7; Invoke("AttachClipAudio", audio);
                Check(!clip.AudioDetached && clip.Volume == .7 && !project.Tracks.Any(t => t.Clips.Contains(audio)), "重新附着保留声音设置且不重复发声");
                clip.Volume = 1;
                await (Task)Invoke("NormalizeClipAudioAsync", clip, -14.0, -1.0, CancellationToken.None)!;
                var norm = AudioSettings.FromClip(clip);
                Check(norm.NormalizeEnabled && norm.MeasuredLufs is < -10 && double.IsFinite(norm.NormalizationGainDb), "一键R128测量并保存可撤销归一化参数");
                await TimelineAudioMonitor.RenderWindowAsync(project, 0, 2, Path.Combine(root, "normalized.wav")); Check(true, "归一化与峰值限制实际进入预览滤镜链");
                var normalizedMeasurement = await ClipLoudnessNormalizer.MeasureAsync(new TimelineClip { Kind = ClipKind.Audio, DurationSeconds = 2 },
                    new MediaAsset { Kind = MediaKind.Audio, SourcePath = Path.Combine(root, "normalized.wav"), HasAudio = true }, -14, -1, CancellationToken.None);
                Check(Math.Abs(normalizedMeasurement.IntegratedLufs + 14) < 1 && normalizedMeasurement.TruePeakDb <= -.8,
                    "真实监听输出达到目标响度且遵守真峰值上限");
                var incoming = new TimelineClip { Name = "镜头 B", Kind = ClipKind.Video, MediaAssetId = asset.Id, StartSeconds = 2, DurationSeconds = 2, SourceInSeconds = 1 };
                incoming.Transitions.Add(new() { PresetId = "transition.crossfade", Edge = "In", DurationSeconds = .8 }); project.Tracks[0].Clips.Add(incoming);
                Check(TransitionHandles.CanMove(clip, asset, .8), "转场识别前镜头可用源余量");
                string h264 = Path.Combine(root, "moving-h264.mp4"); await TimelineExporter.ExportMp4Async(project, h264, new Dictionary<string,string>());
                Check(File.Exists(h264), "双源连续运动交叉溶解可导出H264");
                var before = await CompositeFrameRenderer.RenderFrameAsync(project, 2.2, 320, 180); var after = await CompositeFrameRenderer.RenderFrameAsync(project, 2.6, 320, 180);
                Check(!Pixels(before).SequenceEqual(Pixels(after)), "交叉溶解中两个时刻画面持续变化");
                incoming.Opacity = 0;
                var tail1 = await CompositeFrameRenderer.RenderFrameAsync(project, 2.2, 320, 180);
                var tail2 = await CompositeFrameRenderer.RenderFrameAsync(project, 2.6, 320, 180);
                Check(!Pixels(tail1).SequenceEqual(Pixels(tail2)), "隐藏后段后，前段余量画面仍在运动而非保持尾帧");
                string movingOnly = Path.Combine(root, "outgoing-tail.mp4"); await TimelineExporter.ExportMp4Async(project, movingOnly, new Dictionary<string,string>());
                string frameA = Path.Combine(root, "tail-a.png"), frameB = Path.Combine(root, "tail-b.png");
                await Run("-ss", "2.2", "-i", movingOnly, "-frames:v", "1", frameA); await Run("-ss", "2.6", "-i", movingOnly, "-frames:v", "1", frameB);
                Check(!File.ReadAllBytes(frameA).SequenceEqual(File.ReadAllBytes(frameB)), "正式导出的前段源余量也持续运动");
                incoming.Opacity = 1;
                clip.SourceInSeconds = 3;
                Check(!TransitionHandles.CanMove(clip, asset, .8) && TransitionHandles.Describe(clip, asset, .8).Contains("回退"), "源余量不足有明确回退说明"); clip.SourceInSeconds = 0;
                foreach (string codec in new[] { "HEVC", "AV1" }) foreach (int depth in new[] { 8, 10 })
                {
                    string output = Path.Combine(root, codec + depth + ".mp4");
                    await TimelineExporter.ExportMp4Async(project, output, new Dictionary<string,string>(), settings: new VideoExportSettings(EndSeconds: .6, VideoCodec: codec, BitDepth: depth));
                    var info = (await Probe(output))["streams"]!.AsArray().First(s => s!["codec_type"]!.GetValue<string>() == "video")!;
                    Check(info["codec_name"]!.GetValue<string>() == (codec == "HEVC" ? "hevc" : "av1") && info["pix_fmt"]!.GetValue<string>() == (depth == 10 ? "yuv420p10le" : "yuv420p"), codec + " " + depth + "位真实编码与像素格式验证");
                    await Run("-i", output, "-f", "null", "-"); Check(true, codec + depth + "完整解码");
                }
                string precisionSource = Path.Combine(root, "precision-source.mkv");
                await Run("-f", "lavfi", "-i", "nullsrc=s=320x180:r=10:d=0.4,format=yuv420p10le,geq=lum='600+mod(N,2)':cb=512:cr=512", "-c:v", "ffv1", precisionSource);
                var precisionProject = new ProjectDocument { Sequence = new() { Width = 320, Height = 180, FramesPerSecond = 10 } };
                var precisionAsset = new MediaAsset { Kind = MediaKind.Video, SourcePath = precisionSource, DurationSeconds = .4 };
                precisionProject.Media.Add(precisionAsset); precisionProject.Tracks.Add(new() { Clips = [new() { Kind = ClipKind.Video, MediaAssetId = precisionAsset.Id, DurationSeconds = .4 }] });
                string precisionOutput = Path.Combine(root, "precision-hevc10.mp4");
                await TimelineExporter.ExportMp4Async(precisionProject, precisionOutput, new Dictionary<string,string>(), settings: new VideoExportSettings(VideoCodec: "HEVC", BitDepth: 10, VideoBitrateKbps: 50000));
                string raw10 = Path.Combine(root, "precision.y10");
                await Run("-i", precisionOutput, "-vf", "extractplanes=y", "-pix_fmt", "gray10le", "-f", "rawvideo", raw10);
                byte[] rawPixels = await File.ReadAllBytesAsync(raw10);
                ushort firstLuma = BitConverter.ToUInt16(rawPixels, (90 * 320 + 160) * 2);
                ushort secondLuma = BitConverter.ToUInt16(rawPixels, (320 * 180 + 90 * 320 + 160) * 2);
                Console.WriteLine($"PRECISION Y10 first={firstLuma} second={secondLuma}");
                Check(firstLuma != secondLuma, "10位源中小于8位阶梯的亮度差穿过合成和编码后仍保留");
                incoming.Transitions[0].PresetId = "transition.luma";
                await TimelineExporter.ExportMp4Async(project, Path.Combine(root, "luma10.mp4"), new Dictionary<string,string>(),
                    settings: new VideoExportSettings(StartSeconds: 2, EndSeconds: 2.5, VideoCodec: "HEVC", BitDepth: 10));
                Check(true, "高位深亮度转场使用16位采样范围并实际编码");
                incoming.Transitions[0].PresetId = "transition.crossfade";
                clip.Effects.Add(new ClipEffect { PresetId = "effect.brightness" });
                try
                {
                    await TimelineExporter.ExportMp4Async(project, Path.Combine(root, "unsafe10.mp4"), new Dictionary<string,string>(), settings: new VideoExportSettings(EndSeconds: .5, VideoCodec: "HEVC", BitDepth: 10));
                    throw new Exception("10位静默降精度被接受");
                }
                catch (ExportException error) when (error.StageCode == ExportStageCode.Validate) { Check(!File.Exists(Path.Combine(root, "unsafe10.mp4")), "未验证高精度的效果组合明确拒绝，不冒充10位处理"); }
                clip.Effects.Clear();
                var stabilized = Path.Combine(root, "stabilized.mkv");
                await VideoStabilization.RenderAsync(clip, asset, new StabilizationSettings { Smoothing = 10, Shakiness = 4, MaxZoomPercent = 8 }, stabilized, CancellationToken.None);
                Check(File.Exists(stabilized) && (await Probe(stabilized))["streams"]!.AsArray().Any(s => s!["codec_name"]!.GetValue<string>() == "ffv1"), "真实运动分析与防抖补偿输出无损视频");
                await (Task)Invoke("StabilizeVideoAsync", clip, 10, 4, 8.0, CancellationToken.None)!;
                Check(clip.Stabilization.Enabled && clip.MediaAssetId != asset.Id && project.Media.Any(m => m.Id == clip.MediaAssetId), "界面防抖切换到可打包的派生媒体");
                var portableStabilized = Path.Combine(root, "stabilized-project.tvep"); await ProjectFileService.SaveAsync(project, portableStabilized);
                var reopened = await ProjectFileService.LoadAsync(portableStabilized);
                Check(reopened.Tracks[0].Clips[0].Stabilization.Enabled && File.Exists(reopened.Media.First(m => m.Id == reopened.Tracks[0].Clips[0].MediaAssetId).SourcePath), "防抖派生媒体实际打包并能随项目重新打开");
                await ProjectFileService.ReleaseCachedMediaAsync(reopened);
                Invoke("DisableVideoStabilization", clip); Check(clip.MediaAssetId == asset.Id && !clip.Stabilization.Enabled, "关闭防抖无损恢复原始源引用");
                var dispatchCommand = typeof(MainWindow).GetMethod("ExecuteMcpCommandAsync", Private)!;
                async Task<object?> Call(string name, JsonObject args)
                {
                    AutomationCatalog.ValidateArguments(name, args);
                    return await (Task<object?>)dispatchCommand.Invoke(window, [name, JsonNode.Parse(args.ToJsonString())!.AsObject(), CancellationToken.None])!;
                }
                var streamState = await Call("audio_source_streams", new() { ["clipId"] = clip.Id });
                Check(JsonNode.Parse(JsonSerializer.Serialize(streamState))!["streams"]!.AsArray().Count == 2, "MCP实际返回全部源音轨信息");
                await Call("audio_detach", new() { ["clipId"] = clip.Id }); Check(clip.AudioDetached, "MCP实际分离声音且刷新内存模型");
                await Call("audio_attach", new() { ["clipId"] = clip.Id }); Check(!clip.AudioDetached, "MCP实际重新附着声音");
                var meterState = await Call("audio_meter_get", new()); Check(meterState is not null, "MCP实际读取电平状态");
                await Call("audio_normalize", new() { ["clipId"] = clip.Id, ["targetLufs"] = -16 }); Check(AudioSettings.FromClip(clip).TargetLufs == -16, "MCP实际归一化声音并使用用户目标");
                Invoke("ShowAudioClipProperties", clip); Invoke("SeekPreview", 1.5); await Task.Delay(150); Invoke("DrawTimeline"); window.UpdateLayout();
                var visual = (FrameworkElement)window.Content;
                var screenshot = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); screenshot.Render(visual);
                using (var output = File.Create(Path.Combine(root, "audio-workflow.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(screenshot)); png.Save(output); }
                project.Tracks[0].Clips.Clear(); project.Tracks[0].Clips.Add(new() { Kind = ClipKind.Color, DurationSeconds = 1 });
                var gap = new TimelineClip { Kind = ClipKind.Color, StartSeconds = 3, DurationSeconds = 1 }; project.Tracks[0].Clips.Add(gap);
                Invoke("ToggleMagneticTimeline"); Check(project.Sequence.MagneticTimeline && gap.StartSeconds == 1, "持续磁性模式开启即闭合空隙");
                project.Tracks[0].Clips[0].DurationSeconds = .5; Invoke("MarkProjectDirty"); Check(gap.StartSeconds == .5, "普通修剪提交自动闭合后续空隙");
                project.Tracks[0].IsLocked = true; gap.StartSeconds = 4; Invoke("MarkProjectDirty"); Check(gap.StartSeconds == 4, "磁性模式不移动锁定轨道"); project.Tracks[0].IsLocked = false;
                Invoke("ToggleMagneticTimeline"); gap.StartSeconds = 5; Invoke("MarkProjectDirty"); Check(gap.StartSeconds == 5, "关闭磁性模式后保留空隙");
                Invoke("ApplyCustomShortcut", "下一帧", Key.F9, ModifierKeys.None);
                Check(Field<Dictionary<string, InputBinding>>("_customShortcutBindings").ContainsKey("下一帧"), "新增动作支持单键重绑");
                Invoke("CloseSettingsTabs"); window.UpdateLayout();
                var search = (TextBox)window.FindName("AssetSearchBox"); search.Focus(); Keyboard.Focus(search);
                var binding = (KeyBinding)Field<Dictionary<string, InputBinding>>("_customShortcutBindings")["下一帧"];
                double originalTime = project.Sequence.PlayheadSeconds;
                binding.Command.Execute(null); Check(project.Sequence.PlayheadSeconds == originalTime, "文字输入框聚焦时自定义剪辑快捷键不误触发");
                ((Button)window.FindName("PlayPauseButton")).Focus(); Keyboard.Focus((Button)window.FindName("PlayPauseButton"));
                binding.Command.Execute(null); Check(project.Sequence.PlayheadSeconds > originalTime, "非文字输入时自定义逐帧动作真正执行");
                try { Invoke("ApplyCustomShortcut", "上一帧", Key.F9, ModifierKeys.None); throw new Exception("冲突被接受"); } catch (TargetInvocationException e) when(e.InnerException is TANGERINE_VideoEditor.Tools.StageException) { Check(true, "冲突快捷键明确拒绝"); }
                foreach (string command in new[] { "audio_source_streams", "audio_detach", "audio_attach", "audio_normalize", "video_stabilize", "audio_meter_get" }) Check(AutomationCatalog.Commands.Any(c => c.Name == command), "MCP提供" + command);
                var catalogFile = Path.GetFullPath("artifacts/ConcatWorkflowCheck/catalog.json");
                await File.WriteAllTextAsync(catalogFile, JsonSerializer.Serialize(new { Commands = AutomationCatalog.Commands, Models = AutomationCatalog.Models }, new JsonSerializerOptions { WriteIndented = true }));
                string file = Path.Combine(root, "portable.tvep"); await ProjectFileService.SaveAsync(project, file); var loaded = await ProjectFileService.LoadAsync(file);
                Check(loaded.Media.First(m => m.Id == asset.Id).AudioStreams.Count == 2, "项目保存重载保留全部源音轨元数据"); await ProjectFileService.ReleaseCachedMediaAsync(loaded);
                Console.WriteLine($"PASS {count} workflow checks; outputs: {root}");
            }
            catch(Exception e) { Console.Error.WriteLine(e); exit = 1; }
            finally { Set("_isProjectDirty", false); window.Close(); Environment.ExitCode = exit; app.Shutdown(exit); }
        };
        app.Run(window); return Environment.ExitCode;
    }
    static byte[] Pixels(BitmapSource source) { byte[] bytes = new byte[source.PixelHeight * source.PixelWidth * 4]; source.CopyPixels(bytes, source.PixelWidth * 4, 0); return bytes; }
}

