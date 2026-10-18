using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Project;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Owner = "mcp-control-check-" + Guid.NewGuid().ToString("N");
    private static readonly string Other = "mcp-other-check-" + Guid.NewGuid().ToString("N");
    private static int _checks;
    private static TaskCompletionSource<object?>? _jobCompletion;

    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { WindowState = WindowState.Normal, ShowActivated = false,
            ShowInTaskbar = false, Opacity = 0 };
        application.MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            int exit = 0;
            try
            {
                await RunAsync(window);
                Console.WriteLine($"PASS {_checks} MCP control and real input checks");
            }
            catch (Exception exception) { exit = 1; Console.Error.WriteLine(exception); }
            finally
            {
                _jobCompletion?.TrySetResult(new { completed = true });
                try { await Request(window, "editor_control_release", session: Owner); } catch { }
                Set(window, "_isProjectDirty", false);
                Set(window, "_mcpControlOwnerSessionId", null);
                window.Close();
                application.Shutdown(exit);
            }
        };
        application.Run(window);
    }

    private static async Task RunAsync(MainWindow window)
    {
        // Register a harmless real routed command. Executing it while locked
        // must not reach a user action even if Execute bypasses CanExecute.
        int executions = 0;
        var command = new RoutedCommand("ControlCheck", typeof(Program));
        var binding = new CommandBinding(command, (_, args) => { executions++; args.Handled = true; },
            (_, args) => { args.CanExecute = true; args.Handled = true; });
        window.CommandBindings.Add(binding);
        Check(command.CanExecute(null, window), "User commands work before AI control");
        var initialProject = Project(window);
        int originalTracks = initialProject.Tracks.Count;
        long initialRevision = Revision(window);
        await Fails(window, "track_create", new() { ["name"] = "Without control" }, Owner);
        await Fails(window, "editor_get_state", session: Owner);
        Check(Project(window).Tracks.Count == originalTracks && Revision(window) == initialRevision,
            "Commands without acquired control leave the live project untouched");
        await Fails(window, "editor_control_acquire", session: null);
        await Succeeds(window, "editor_control_status", session: Other);

        await Succeeds(window, "editor_control_acquire", session: Owner);
        Check(ControlledBy(window) == Owner, "The acquiring session owns this application process");
        var menu = (Menu)window.FindName("EditorMenuBar");
        var notice = (TextBlock)window.FindName("McpControlNotice");
        Check(menu.Visibility == Visibility.Collapsed && notice.Visibility == Visibility.Visible &&
            notice.Text == "正在通过MCP操作本程序", "AI control replaces the top menu with the requested notice");
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Check(handle != IntPtr.Zero && IsWindowEnabled(handle) &&
            ((Grid)window.FindName("EditorRootGrid")).IsHitTestVisible,
            "AI control retains native input and hit testing for timeline wheel browsing");
        Check(!command.CanExecute(null, window), "PreviewCanExecute blocks real routed user commands");
        command.Execute(null, window);
        Check(executions == 0, "PreviewExecuted blocks direct routed-command execution");
        Check(KeyboardHandled(window), "Preview keyboard input is blocked during AI control");
        Check(MouseHandled(window), "Preview mouse input is blocked during AI control");
        Check(WheelHandled(window), "Mouse-wheel input outside the timeline is blocked during AI control");
        Check(ClosingCancelled(window), "Native window closing is blocked while an AI owns control");
        var scroll = (ScrollViewer)window.FindName("TimelineScrollViewer");
        var canvas = (Canvas)window.FindName("TimelineCanvas");
        var extraTracks = Enumerable.Range(0, 24).Select(index => new TimelineTrack { Name = "Browse " + index }).ToList();
        var longClip = new TimelineClip { Kind = ClipKind.Color, DurationSeconds = 300 };
        initialProject.Tracks.AddRange(extraTracks); initialProject.Tracks[0].Clips.Add(longClip);
        typeof(MainWindow).GetMethod("RefreshTimeline", Private)!.Invoke(window, null);
        window.UpdateLayout();
        DateTimeOffset beforeBrowse = (DateTimeOffset)typeof(MainWindow).GetField("_mcpControlLastActivityUtc", Private)!.GetValue(window)!;
        canvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Background);
        Check(scroll.VerticalOffset > 0, "Acquired MCP control permits user vertical timeline wheel browsing");
        var point = scroll.PointToScreen(new Point(scroll.ActualWidth / 2, scroll.ActualHeight / 2));
        IntPtr position = new(unchecked((int)(((uint)(ushort)(int)point.Y << 16) | (ushort)(int)point.X)));
        SendMessage(handle, 0x020E, new IntPtr(120 << 16), position);
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.Background);
        Check(scroll.HorizontalOffset > 0, "Acquired MCP control permits native horizontal timeline wheel browsing");
        Check(Revision(window) == initialRevision && beforeBrowse == (DateTimeOffset)typeof(MainWindow).GetField("_mcpControlLastActivityUtc", Private)!.GetValue(window)!,
            "User browsing neither edits the project nor renews the MCP idle timeout");
        initialProject.Tracks.RemoveAll(track => extraTracks.Contains(track)); initialProject.Tracks[0].Clips.Remove(longClip);
        typeof(MainWindow).GetMethod("RefreshTimeline", Private)!.Invoke(window, null);

        await Fails(window, "editor_control_acquire", session: Other);
        await Fails(window, "track_create", new() { ["name"] = "Other session" }, Other);
        await Fails(window, "editor_control_release", session: Other);
        Check(ControlledBy(window) == Owner, "Another session cannot edit or release this owner's control");
        await Succeeds(window, "track_create", new() { ["name"] = "Owned MCP track", ["kind"] = "Video" }, Owner);
        Check(Project(window).Tracks.Any(track => track.Name == "Owned MCP track") && Revision(window) == initialRevision + 1,
            "The owning AI still edits the live application while user input is blocked");

        DateTimeOffset oldActivity = DateTimeOffset.UtcNow.AddSeconds(-30);
        Set(window, "_mcpControlLastActivityUtc", oldActivity);
        await Succeeds(window, "editor_control_status", session: Other);
        Check(Activity(window) == oldActivity, "Status polling does not extend the control timeout");
        await Succeeds(window, "editor_control_acquire", session: Owner);
        Check(Activity(window) > oldActivity && ControlledBy(window) == Owner,
            "The owner can renew its current control session");

        Set(window, "_mcpActiveOperations", 1);
        Set(window, "_mcpControlLastActivityUtc", DateTimeOffset.UtcNow.AddMinutes(-2));
        CheckIdle(window);
        Check(ControlledBy(window) == Owner, "A running command prevents idle control release");
        Set(window, "_mcpActiveOperations", 0);
        Set(window, "_mcpControlLastActivityUtc", DateTimeOffset.UtcNow);

        bool cancelled = false;
        _jobCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<IProgress<double>, CancellationToken, Task<object?>> work = async (progress, token) =>
        {
            using var registration = token.Register(() => cancelled = true);
            jobStarted.TrySetResult();
            progress.Report(.25);
            return await _jobCompletion.Task.WaitAsync(token);
        };
        object descriptor = typeof(MainWindow).GetMethod("McpStartJob", Private)!.Invoke(window, ["控制锁长任务", work])!;
        string jobId = JsonModelTools.ToNode(descriptor)!["jobId"]!.GetValue<string>();
        await jobStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Set(window, "_mcpControlLastActivityUtc", DateTimeOffset.UtcNow.AddMinutes(-2));
        CheckIdle(window);
        Check(ControlledBy(window) == Owner && !cancelled, "A running job keeps AI control without being cancelled");
        await Fails(window, "editor_control_release", session: Owner);
        Check(!cancelled && ControlledBy(window) == Owner, "Release rejects unfinished owned jobs instead of cancelling them");

        _jobCompletion.TrySetResult(new { completed = true });
        // Keep the dispatcher on this stack until the worker publishes its
        // result. Its final UI callback is then still pending, reproducing
        // the completion/idle race deterministically without a timed delay.
        object jobs = Get(window, "_mcpJobs")!;
        object job = jobs.GetType().GetProperty("Item")!.GetValue(jobs, [jobId])!;
        PropertyInfo jobStatus = job.GetType().GetProperty("Status")!;
        if (!SpinWait.SpinUntil(() => (string?)jobStatus.GetValue(job) == "completed", TimeSpan.FromSeconds(3)))
            throw new TimeoutException("Synthetic background work did not publish its result.");
        CheckIdle(window);
        Check(ControlledBy(window) == Owner && !cancelled,
            "Pending job UI completion remains busy after its worker publishes a result");
        JsonNode? completed = null;
        AutomationResponse? lastJobResponse = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            AutomationResponse state = await Request(window, "jobs_status", new() { ["jobId"] = jobId }, Owner);
            lastJobResponse = state;
            if (state.Success && state.Data?["status"]?.GetValue<string>() == "completed") { completed = state.Data; break; }
            await Task.Delay(20);
        }
        Check(completed?["result"]?["completed"]?.GetValue<bool>() == true && !cancelled,
            "A job completes normally while its control lock is retained: " + lastJobResponse?.Error?.Message);
        Set(window, "_mcpControlLastActivityUtc", DateTimeOffset.UtcNow.AddMinutes(-2));
        CheckIdle(window);
        Check(ControlledBy(window) is null, "Idle control is released after completed work exceeds sixty seconds");
        Check(menu.Visibility == Visibility.Visible && notice.Visibility == Visibility.Collapsed,
            "Timeout restores the ordinary application menu");
        Check(IsWindowEnabled(handle) && ((Grid)window.FindName("EditorRootGrid")).IsHitTestVisible,
            "Timeout restores native input and WPF hit testing");
        Check(command.CanExecute(null, window) && !KeyboardHandled(window) && !MouseHandled(window) && !WheelHandled(window),
            "Timeout restores keyboard, mouse, wheel and routed-command access");
        Check(!cancelled, "Idle expiration does not cancel already completed background work");

        await Succeeds(window, "editor_control_acquire", session: Other);
        Check(ControlledBy(window) == Other, "Another AI can acquire the released application");
        await Succeeds(window, "editor_control_release", session: Other);
        Check(ControlledBy(window) is null && menu.Visibility == Visibility.Visible && notice.Visibility == Visibility.Collapsed,
            "Explicit release restores user control immediately");
        command.Execute(null, window);
        Check(executions == 1, "Real routed commands execute again after explicit release");
        Check(!ClosingCancelled(window), "Ordinary window closing works after AI release");
        window.CommandBindings.Remove(binding);
    }

    private static bool KeyboardHandled(MainWindow window)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.F24)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
        return args.Handled;
    }

    private static bool MouseHandled(MainWindow window)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        window.RaiseEvent(args);
        return args.Handled;
    }

    private static bool WheelHandled(MainWindow window)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        window.RaiseEvent(args);
        return args.Handled;
    }

    private static bool ClosingCancelled(MainWindow window)
    {
        // Raise the real Window.Closing event without closing this test host.
        Set(window, "_isProjectDirty", false);
        var args = new CancelEventArgs();
        typeof(MainWindow).GetMethod("OnClosing", Private)!.Invoke(window, [args]);
        return args.Cancel;
    }

    private static async Task Succeeds(MainWindow window, string command, JsonObject? arguments = null, string? session = null)
    {
        AutomationResponse result = await Request(window, command, arguments, session);
        Check(result.Success, command + " succeeds: " + result.Error?.Message);
    }

    private static async Task Fails(MainWindow window, string command, JsonObject? arguments = null, string? session = null)
    {
        AutomationResponse result = await Request(window, command, arguments, session);
        Check(!result.Success && result.Error?.StageCode is not null, command + " rejects unauthorized or busy control");
    }

    private static Task<AutomationResponse> Request(MainWindow window, string command, JsonObject? arguments = null, string? session = null) =>
        (Task<AutomationResponse>)typeof(MainWindow).GetMethod("HandleMcpRequestAsync", Private)!
            .Invoke(window, [new AutomationRequest(Guid.NewGuid().ToString("N"), command,
                arguments is null ? null : JsonNode.Parse(arguments.ToJsonString())!.AsObject(), SessionId: session), CancellationToken.None])!;

    private static string? ControlledBy(MainWindow window) => (string?)Get(window, "_mcpControlOwnerSessionId");
    private static DateTimeOffset Activity(MainWindow window) => (DateTimeOffset)Get(window, "_mcpControlLastActivityUtc")!;
    private static ProjectDocument Project(MainWindow window) => (ProjectDocument)Get(window, "_project")!;
    private static long Revision(MainWindow window) => (long)Get(window, "_projectRevision")!;
    private static object? Get(MainWindow window, string field) => typeof(MainWindow).GetField(field, Private)!.GetValue(window);
    private static void Set(MainWindow window, string field, object? value) => typeof(MainWindow).GetField(field, Private)!.SetValue(window, value);
    private static void CheckIdle(MainWindow window) => typeof(MainWindow).GetMethod("CheckMcpControlIdle", Private)!.Invoke(window, null);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("MCP-CONTROL-CHECK: " + description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}
