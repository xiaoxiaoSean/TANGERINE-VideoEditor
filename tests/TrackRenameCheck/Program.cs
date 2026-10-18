using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

internal static class Program
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    static int checks;
    [STAThread] static void Main()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var window=new MainWindow{WindowState=WindowState.Normal,ShowActivated=false,ShowInTaskbar=false,Opacity=0};
        window.Loaded+=async(_,_)=>
        {
            int exit=0;
            try
            {
                ProjectDocument Project()=>(ProjectDocument)typeof(MainWindow).GetField("_project",Private)!.GetValue(window)!;
                var track=Project().Tracks[0];
                var canvas=(Canvas)window.FindName("TimelineCanvas");
                Border Header()=>canvas.Children.OfType<Border>().Single(border=>ReferenceEquals(border.Tag,Project().Tracks[0]));
                var menu=Header().ContextMenu;
                Check(menu is not null,"轨道名称区有专属右键菜单");
                var rename=menu!.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()=="重命名轨道…");
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};
                timer.Tick+=(_,_)=>
                {
                    Window? dialog=window.OwnedWindows.Cast<Window>().FirstOrDefault(w=>w.Title=="重命名轨道");
                    if(dialog is null)return;
                    timer.Stop();
                    Descendants(dialog).OfType<TextBox>().Single().Text="  主画面 · A  ";
                    Descendants(dialog).OfType<Button>().Single(b=>b.Content?.ToString()=="确定").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                timer.Start();rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(track.Name=="主画面 · A","实际右键命令可输入并提交轨道名称");
                Check(((TextBlock)Header().Child).Text.Contains("主画面 · A"),"重命名即时刷新轨道名称");
                var undo=(Stack<string>)typeof(MainWindow).GetField("_undoStates",Private)!.GetValue(window)!;
                Check(undo.Count==1,"重命名只有一个撤销步骤");
                var method=typeof(MainWindow).GetMethod("RenameTimelineTrack",Private)!;
                try{method.Invoke(window,[track," \n "]);throw new Exception("empty name accepted");}
                catch(TargetInvocationException e)when(e.InnerException is StageException){Check(track.Name=="主画面 · A"&&undo.Count==1,"空名称被拒绝且不污染撤销历史");}
                track.IsLocked=true;method.Invoke(window,[track,"锁定轨道名称"]);
                Check(track.Name=="锁定轨道名称","锁定内容不阻止修改轨道显示名称");
                string file=Path.Combine(Path.GetTempPath(),"tangerine-track-name-"+Guid.NewGuid().ToString("N")+".tvep");
                try{await ProjectFileService.SaveAsync(Project(),file);var loaded=await ProjectFileService.LoadAsync(file);Check(loaded.Tracks[0].Name=="锁定轨道名称","名称保存至项目并可重新加载");await ProjectFileService.ReleaseCachedMediaAsync(loaded);}finally{File.Delete(file);}
                await(Task)typeof(MainWindow).GetMethod("RestoreHistoryAsync",Private)!.Invoke(window,[undo,(Stack<string>)typeof(MainWindow).GetField("_redoStates",Private)!.GetValue(window)!,"撤销重命名"] )!;
                Check(Project().Tracks[0].Name=="主画面 · A","撤销恢复此前轨道名称");
                Check(((MenuItem)window.FindName("TimelineRenameTrackItem")).Header.ToString()!.Contains("重命名"),"空白轨道区域右键菜单也有重命名入口");
                for(int i=0;i<30;i++)Project().Tracks.Add(new(){Name="滚动测试 "+i});
                Project().Tracks[0].Clips.Add(new(){Kind=ClipKind.Color,DurationSeconds=300});
                typeof(MainWindow).GetMethod("RefreshTimeline",Private)!.Invoke(window,null);
                window.UpdateLayout();
                var scroll=(ScrollViewer)window.FindName("TimelineScrollViewer");
                async Task Layout(){await window.Dispatcher.InvokeAsync(()=>window.UpdateLayout(),DispatcherPriority.Background);}
                await Layout();
                Check(scroll.ScrollableHeight>120&&scroll.ScrollableWidth>120,"滚动测试具有横向和纵向可滚动内容");
                canvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent});
                await Layout();Check(scroll.VerticalOffset>0&&scroll.HorizontalOffset==0,"普通纵向滚轮向下滚动轨道");
                canvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,120){RoutedEvent=Mouse.PreviewMouseWheelEvent});
                await Layout();Check(scroll.VerticalOffset==0,"普通纵向滚轮向上滚动轨道");
                var center=scroll.PointToScreen(new Point(scroll.ActualWidth/2,scroll.ActualHeight/2));
                IntPtr position=new(unchecked((int)(((uint)(ushort)(int)center.Y<<16)|(ushort)(int)center.X)));
                SendMessage(new WindowInteropHelper(window).Handle,0x020E,new IntPtr(120<<16),position);
                await Layout();Check(scroll.HorizontalOffset>0&&scroll.VerticalOffset==0,"原生横向滚轮向右滚动时间轴");
                SendMessage(new WindowInteropHelper(window).Handle,0x020E,new IntPtr(-120<<16),position);
                await Layout();Check(scroll.HorizontalOffset==0,"原生横向滚轮向左滚动时间轴");
                var wheel=typeof(MainWindow).GetMethod("ScrollTimelineWheel",Private)!;
                wheel.Invoke(window,[-120,ModifierKeys.Shift,false]);await Layout();
                Check(scroll.HorizontalOffset>0&&scroll.VerticalOffset==0,"Shift 加纵向滚轮保留横向滚动");
                var owner=typeof(MainWindow).GetField("_mcpControlOwnerSessionId",Private)!;owner.SetValue(window,"wheel-test");
                double offset=scroll.HorizontalOffset;
                try
                {
                    SendMessage(new WindowInteropHelper(window).Handle,0x020E,new IntPtr(120<<16),position);await Layout();
                    Check(scroll.HorizontalOffset>offset,"MCP 独占期间允许原生横向滚轮浏览");
                    canvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent});await Layout();
                    Check(scroll.VerticalOffset>0,"MCP 独占期间允许纵向滚轮浏览");
                    double zoom=((Slider)window.FindName("TimelineZoomSlider")).Value;
                    wheel.Invoke(window,[120,ModifierKeys.Control,false]);
                    Check(((Slider)window.FindName("TimelineZoomSlider")).Value==zoom,"MCP 浏览例外不允许 Ctrl 滚轮缩放");
                }
                finally{owner.SetValue(window,null);}
                Console.WriteLine($"PASS {checks} track rename checks");
            }
            catch(Exception e){Console.Error.WriteLine(e);exit=1;}
            finally{typeof(MainWindow).GetField("_isProjectDirty",Private)!.SetValue(window,false);window.Close();app.Shutdown(exit);}
        };app.Run(window);
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var c in Descendants(VisualTreeHelper.GetChild(root,i)))yield return c;}
    static void Check(bool value,string text){if(!value)throw new Exception(text);checks++;Console.WriteLine("PASS "+text);}
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
}
