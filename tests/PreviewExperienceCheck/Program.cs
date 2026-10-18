using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    [STAThread] static int Main()
    {
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TANGERINE-VideoEditor/bin/Debug/net10.0-windows/ffmpeg"));
        if (Directory.Exists(bundled)) Environment.SetEnvironmentVariable("PATH", bundled + ";" + Environment.GetEnvironmentVariable("PATH"));
        var app = new Application();
        string root = Path.Combine(Path.GetTempPath(), "tangerine-preview-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var window = new MainWindow { WindowState = WindowState.Normal, Width = 1100, Height = 800, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
        window.Loaded += async (_, _) =>
        {
            int exit = 0;
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Field<DispatcherTimer>("_idlePreviewTimer").Stop(); Field<DispatcherTimer>("_autoSaveTimer").Stop();
                var project = Field<ProjectDocument>("_project");
                project.Sequence.Width = 640; project.Sequence.Height = 360; project.Sequence.FramesPerSecond = 10;
                string source = Path.Combine(root, "stripes.png");
                byte[] pixels = new byte[640 * 360 * 4];
                for (int y = 0; y < 360; y++) for (int x = 0; x < 640; x++)
                { int i = (y * 640 + x) * 4; byte value = (byte)(x % 2 == 0 ? 255 : 0); pixels[i] = pixels[i + 1] = pixels[i + 2] = value; pixels[i + 3] = 255; }
                var image = BitmapSource.Create(640, 360, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
                using (var file = File.Create(source)) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); png.Save(file); }
                var asset = new MediaAsset { Name = "测试图片", SourcePath = source, Kind = MediaKind.Image, Width = 640, Height = 360 };
                project.Media.Add(asset);
                var first = new TimelineClip { Kind = ClipKind.Image, Name = "第一段", MediaAssetId = asset.Id, DurationSeconds = 3 };
                var second = new TimelineClip { Kind = ClipKind.Image, Name = "第二段", MediaAssetId = asset.Id, StartSeconds = 3, DurationSeconds = 3 };
                project.Tracks[0].Clips.AddRange([first, second]);
                Invoke("RefreshEditorFromProject"); window.UpdateLayout();
                var upper = (Grid)window.FindName("UpperWorkspaceGrid");
                Check(upper.ColumnDefinitions.Count == 3 && upper.ColumnDefinitions[1].ActualWidth > 1, "左右分隔条有实际命中区域");
                var editor = (Grid)window.FindName("EditorAreaGrid");
                Check(editor.RowDefinitions[1].ActualHeight > 1, "上下分隔条有实际命中区域");
                Invoke("ApplyWorkspacePreset", .6, .4, 0); window.UpdateLayout();
                Check(upper.ColumnDefinitions[1].ActualWidth / upper.ActualWidth < .03, "恢复布局后分隔条仍保持窄比例");
                var zoom = (Slider)window.FindName("PreviewZoomSlider");
                string before = JsonSerializer.Serialize(project);
                zoom.Value = 2;
                Check(((ScaleTransform)((Grid)window.FindName("PreviewStage")).RenderTransform).ScaleX == 2, "预览缩放应用于整个交互视图");
                Check(JsonSerializer.Serialize(project) == before, "视图缩放不修改素材或工程");
                ((Button)window.FindName("PreviewZoomResetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(zoom.Value == 1, "一键回位恢复100%");
                var views = (ComboBox)window.FindName("MediaViewBox");
                views.SelectedIndex = 0; var list = (ListBox)window.FindName("AssetList"); var template = list.ItemTemplate;
                views.SelectedIndex = 2; Check(list.ItemTemplate != template, "列表与大缩略图使用不同视图");
                var bitmap = await MediaThumbnailCache.GetAsync(asset, 0, CancellationToken.None);
                Check(bitmap.IsFrozen && bitmap.PixelWidth > 0, "图片缩略图可解码且可跨线程复用");
                Check(ReferenceEquals(bitmap, await MediaThumbnailCache.GetAsync(asset, 0, CancellationToken.None)), "媒体区与时间线复用缩略图缓存");
                string video = Path.Combine(root, "colors.mp4");
                using (var encode = Process.Start(new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                    ArgumentList = { "-v", "error", "-y", "-f", "lavfi", "-i", "color=red:s=320x180:r=10:d=1", "-f", "lavfi", "-i", "color=blue:s=320x180:r=10:d=1", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]", "-map", "[v]", "-c:v", "libx264", "-pix_fmt", "yuv420p", video } })!)
                { string errors = await encode.StandardError.ReadToEndAsync(); await encode.WaitForExitAsync(); Check(encode.ExitCode == 0, "生成视频缩略图时间采样测试源 " + errors); }
                var videoAsset = new MediaAsset { Kind = MediaKind.Video, SourcePath = video };
                var red = new FormatConvertedBitmap(await MediaThumbnailCache.GetAsync(videoAsset, .2, CancellationToken.None), PixelFormats.Bgra32, null, 0);
                var blue = new FormatConvertedBitmap(await MediaThumbnailCache.GetAsync(videoAsset, 1.2, CancellationToken.None), PixelFormats.Bgra32, null, 0);
                byte[] redPixel = new byte[4], bluePixel = new byte[4]; red.CopyPixels(new Int32Rect(20, 20, 1, 1), redPixel, 4, 0); blue.CopyPixels(new Int32Rect(20, 20, 1, 1), bluePixel, 4, 0);
                Check(redPixel[2] > 200 && bluePixel[0] > 200, "视频缩略图按片段源时间采样不同画面");
                await Task.Delay(300); Invoke("DrawTimeline");
                var canvas = (Canvas)window.FindName("TimelineCanvas");
                Check(Descendants(canvas).OfType<Image>().Any(), "时间线素材包含缩略图条");
                var categories = (ListBox)window.FindName("CategoryList");
                Check(categories.Items.Cast<string>().Contains("效果") && !categories.Items.Cast<string>().Contains("滤镜") && !categories.Items.Cast<string>().Contains("特效"), "滤镜与特效合并为效果");
                categories.SelectedIndex = 2;
                Check(list.Items.Count == BuiltinCatalog.Effects.Count + BuiltinCatalog.Filters.Count, "效果分类保留全部滤镜与特效预设");
                categories.SelectedIndex = 0;
                var plan = ProjectPreviewCache.Plan(project, 320, 180);
                Check(plan.Count == 2 && plan[0].End == plan[1].Start, "整个工程划分为连续帧对齐段");
                project.Sequence.PlayheadSeconds = 1;
                Check(plan.Select(s => s.Key).SequenceEqual(ProjectPreviewCache.Plan(project, 320, 180).Select(s => s.Key)), "移动播放头不使缓存失效");
                first.Transform.PositionX = 20;
                var changed = ProjectPreviewCache.Plan(project, 320, 180);
                Check(plan[0].Key != changed[0].Key && plan[1].Key == changed[1].Key, "局部编辑只使相交时间段失效");
                first.Transform.PositionX = 0;
                first.Transform.ScaleX = first.Transform.ScaleY = 2;
                var overlay = new TimelineClip { Kind = ClipKind.Image, MediaAssetId = asset.Id, DurationSeconds = 3, Opacity = 0 };
                project.Tracks.Add(new TimelineTrack { Name = "透明叠加", Order = -1, Clips = [overlay] });
                var frame = await CompositeFrameRenderer.RenderFrameAsync(project, 1, 320, 180);
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); byte[] row = new byte[320 * 4]; rgba.CopyPixels(new Int32Rect(0, 90, 320, 1), row, row.Length, 0);
                var values = Enumerable.Range(100, 120).Select(x => (int)row[x * 4]).ToArray();
                Console.WriteLine($"DETAIL range={values.Max()-values.Min()} min={values.Min()} max={values.Max()}"); Check(values.Max() - values.Min() > 110, "图片重叠且放大时仍保留高频细节");
                project.Tracks.RemoveAt(project.Tracks.Count - 1); first.Transform.ScaleX = first.Transform.ScaleY = 1;
                // Test the actual background exporter, concat pass, and editor playback handoff.
                Invoke("MarkProjectDirty");
                await (Task)Invoke("BuildIdlePreviewAsync")!;
                string ready = Field<string>("_idleReadyPath");
                Check(File.Exists(ready), "后台渲染并拼接完整工程MP4");
                using (var process = Process.Start(new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, ArgumentList = { "-v", "error", "-i", ready, "-f", "null", "-" } })!)
                { string errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Check(process.ExitCode == 0, "拼接预览完整解码通过：" + errors); }
                using (var probe = Process.Start(new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    ArgumentList = { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames,duration", "-of", "json", ready } })!)
                {
                    string json = await probe.StandardOutput.ReadToEndAsync(); await probe.WaitForExitAsync();
                    var stream = JsonNode.Parse(json)!["streams"]![0]!;
                    Check(stream["nb_read_frames"]!.GetValue<string>() == "60" && Math.Abs(double.Parse(stream["duration"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) - 6) < .02,
                        "拼接缓存严格保持6秒和60帧，无AAC间隔累积");
                }
                var directory = Path.GetDirectoryName(ready)!;
                var actualPlan = ProjectPreviewCache.Plan(project,
                    Math.Clamp((int)Math.Round(Math.Max(16, ((Canvas)window.FindName("PreviewVisualLayer")).Width) / 2) * 2, 16, 4096),
                    Math.Clamp((int)Math.Round(Math.Clamp((int)Math.Round(Math.Max(16, ((Canvas)window.FindName("PreviewVisualLayer")).Width) / 2) * 2, 16, 4096) * 360.0 / 640 / 2) * 2, 16, 4096));
                string retained = Path.Combine(directory, actualPlan[1].Key + ".mp4"); DateTime timestamp = File.GetLastWriteTimeUtc(retained);
                first.Transform.PositionX = 12; Invoke("MarkProjectDirty"); await (Task)Invoke("BuildIdlePreviewAsync")!;
                Check(File.Exists(retained) && File.GetLastWriteTimeUtc(retained) == timestamp, "真实局部编辑后未改动段不重新编码");
                Set("_isPlaying", true); Check((bool)Invoke("TryShowCachedPreview", 1.0)!, "播放接入完整缓存视频");
                Set("_isPlaying", false); Check(!(bool)Invoke("TryShowCachedPreview", 1.0)! && !Field<bool>("_cachedPreviewActive"), "暂停及时交还交互预览");
                Invoke("MarkProjectDirty");
                Task interrupted = (Task)Invoke("BuildIdlePreviewAsync")!;
                Invoke("MarkProjectDirty"); await interrupted;
                Check(!Field<bool>("_idlePreviewBusy") && Field<long>("_idleReadyRevision") == -1, "后台渲染期间编辑会取消任务且不发布过期结果");
                project.Sequence.PlayheadSeconds = 1.5; Set("_selectedClip", null); Set("_selectedTrack", null);
                ((Button)window.FindName("SplitButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(project.Tracks[0].Clips.Count == 3, "未预先选择素材时单击分割也立即执行");
                Check(Field<long>("_idleReadyRevision") == -1, "编辑立即废弃旧拼接缓存");
                Invoke("SeekPreview", 1.0); window.UpdateLayout();
                string artifacts = Path.GetFullPath("artifacts/PreviewExperience"); Directory.CreateDirectory(artifacts);
                var visual = (FrameworkElement)window.Content;
                var screenshot = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                screenshot.Render(visual);
                using (var file = File.Create(Path.Combine(artifacts, "workspace.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(screenshot)); png.Save(file); }
                Console.WriteLine($"PASS {count} checks");
            }
            catch (Exception e) { Console.Error.WriteLine(e); exit = 1; }
            finally { Set("_isProjectDirty", false); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; window.Close(); Environment.ExitCode = exit; app.Shutdown(exit); try { Directory.Delete(root, true); } catch { } }
        };
        app.Run(window); return Environment.ExitCode;
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
}




