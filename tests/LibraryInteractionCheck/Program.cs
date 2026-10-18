using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
    [STAThread] static int Main()
    {
        string bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TANGERINE-VideoEditor/bin/Debug/net10.0-windows/ffmpeg"));
        Environment.SetEnvironmentVariable("PATH", bundled + ";" + Environment.GetEnvironmentVariable("PATH"));
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
                var categories = (ListBox)window.FindName("CategoryList"); var list = (ListBox)window.FindName("AssetList");
                var project = Field<ProjectDocument>("_project");
                project.Sequence.Width = 320; project.Sequence.Height = 180;
                project.Tracks[0].Clips.Add(new() { Kind = ClipKind.Color, DurationSeconds = 4, Color = "#00AA66" });
                categories.SelectedIndex = 2;
                await Layout();
                object first = list.Items[0], second = list.Items[1];
                Point At(object item)
                {
                    list.ScrollIntoView(item); window.UpdateLayout();
                    var card = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
                    return card.TranslatePoint(new Point(card.ActualWidth * .5, card.ActualHeight * .5), list);
                }
                Point a = At(first), b = At(second);
                Check(ReferenceEquals(Invoke("LibraryItemAt", a), first) && ReferenceEquals(Invoke("LibraryItemAt", b), second), "按实际卡片位置解析点击对象");
                long generation = Field<long>("_catalogPreviewGeneration");
                Invoke("BeginLibraryGesture", a, 1);
                Check(ReferenceEquals(Field<object>("_libraryPressedItem"), first), "首次按下绑定第一个对象");
                Check(generation == Field<long>("_catalogPreviewGeneration"), "按下仅选择，尚未误启动示例任务");
                Invoke("ShowDropGhost", new Point(300, 120), first);
                Set("_libraryDragInProgress", true);
                Invoke("FinishLibraryDrag");
                Check(Field<object?>("_libraryPressedItem") is null && !Field<bool>("_libraryDragInProgress") && Field<object?>("_dropGhost") is null, "拖动结束统一清理按下对象和轨道幽灵");
                Invoke("BeginLibraryGesture", b, 1);
                Check(ReferenceEquals(Field<object>("_libraryPressedItem"), second) && ReferenceEquals(list.SelectedItem, second), "第二次手势绑定第二个对象，不沿用前一次");
                int clips = project.Tracks.Sum(t => t.Clips.Count);
                Invoke("ShowDropGhost", new Point(330, 130), second);
                var leave = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [new DataObject(), DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, list, new Point()], null)!;
                leave.RoutedEvent = DragDrop.DragLeaveEvent;
                ((Canvas)window.FindName("TimelineCanvas")).RaiseEvent(leave);
                Check(Field<object?>("_dropGhost") is null, "离开轨道立即移除拖动提示");
                Set("_libraryDragInProgress", true); Invoke("FinishLibraryDrag");
                Check(!(bool)Invoke("CompleteLibraryClick", b)! && project.Tracks.Sum(t => t.Clips.Count) == clips, "拖回素材区后松开只取消，不添加对象或误播放");
                Invoke("BeginLibraryGesture", b, 1);
                Check((bool)Invoke("CompleteLibraryClick", b)! && Field<bool>("_catalogPreviewPending"), "一个完整单击启动示例视频生成");
                long started = Field<long>("_catalogPreviewGeneration");
                Check(!(bool)Invoke("CompleteLibraryClick", b)! && Field<long>("_catalogPreviewGeneration") == started, "一次单击只启动一个任务，不被第二次松开重启");
                await Until(() => Field<bool>("_catalogPreviewActive"), TimeSpan.FromSeconds(45));
                Check(File.Exists(Field<string>("_catalogPreviewPath")), "单击后真实示例MP4生成并交给播放器");
                var player = (MediaElement)window.FindName("PreviewMediaElement");
                Check(player.Source is not null && player.Visibility == Visibility.Visible, "单击示例后播放器可见且载入实际文件");
                Invoke("ScheduleRenderedPreviewFrame", new object?[] { null });
                Invoke("SeekPreview", 1.0); Invoke("DrawTextOverlay");
                Check(player.Visibility == Visibility.Visible && !Field<bool>("_renderedPreviewInFlight") && Field<bool>("_catalogPreviewActive"), "异步时间线合成与刷新不会覆盖示例播放");
                Invoke("BeginLibraryGesture", b, 1); Invoke("CompleteLibraryClick", b);
                Check(Field<bool>("_catalogPreviewPending"), "再次单击同一示例也能重新播放");
                Invoke("BeginLibraryGesture", a, 1); Invoke("CompleteLibraryClick", a);
                await Until(() => Field<bool>("_catalogPreviewActive"), TimeSpan.FromSeconds(45));
                Check(ReferenceEquals(list.SelectedItem, first) && File.Exists(Field<string>("_catalogPreviewPath")), "快速切换后只播放最后点击的示例");
                Invoke("StopCatalogPreview");
                Check(!Field<bool>("_catalogPreviewPending") && !Field<bool>("_catalogPreviewActive"), "停止示例清理生成和播放占用状态");
                categories.SelectedIndex = 1; await Layout();
                first = list.Items[0]; second = list.Items[1]; a = At(first); b = At(second);
                Invoke("BeginLibraryGesture", a, 1); Invoke("FinishLibraryDrag");
                Invoke("BeginLibraryGesture", b, 1);
                object current = Field<object>("_libraryPressedItem");
                var data = new DataObject(current.GetType(), current);
                var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [data, DragDropKeyStates.None, DragDropEffects.Copy, window.FindName("TimelineCanvas"), new Point(400, 110)], null)!;
                drop.RoutedEvent = DragDrop.DropEvent;
                int priorCount = project.Tracks.Sum(t => t.Clips.Count);
                Invoke("TimelineCanvas_Drop", window.FindName("TimelineCanvas"), drop);
                Invoke("FinishLibraryDrag");
                string selectedId = (string)current.GetType().GetProperty("Id")!.GetValue(current)!;
                Check(project.Tracks.Sum(t => t.Clips.Count) == priorCount + 1 && project.Tracks.SelectMany(t => t.Clips).Any(c => c.TemplateId == selectedId),
                    "连续拖不同文字后，实际落下的是第二个对象的预设");
                Check(Field<object?>("_dropGhost") is null && Field<object?>("_libraryPressedItem") is null, "成功落下也结束当前手势，无残留可复用对象");
                Console.WriteLine($"PASS {count} library interaction checks");
                async Task Layout() { await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle); }
                async Task Until(Func<bool> condition, TimeSpan timeout)
                {
                    DateTime deadline = DateTime.UtcNow + timeout;
                    while (!condition())
                    {
                        if (DateTime.UtcNow > deadline) throw new Exception("示例播放超时: " + ((TextBlock)window.FindName("StatusText")).Text);
                        await Task.Delay(50);
                    }
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e); exit = 1; }
            finally { Invoke("StopCatalogPreview"); Set("_isProjectDirty", false); window.Close(); Environment.ExitCode = exit; app.Shutdown(exit); }
        };
        app.Run(window); return Environment.ExitCode;
    }
}
