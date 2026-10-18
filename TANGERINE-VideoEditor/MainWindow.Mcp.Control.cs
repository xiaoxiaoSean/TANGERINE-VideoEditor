using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private string? _mcpControlOwnerSessionId;
    private string? _mcpControlOwnerLabel;
    private DateTimeOffset _mcpControlLastActivityUtc;
    private int _mcpActiveOperations;
    private bool _mcpControlOriginalHitTestVisible;
    private bool _mcpControlOriginalNativeEnabled;
    private IntPtr _mcpControlWindowHandle;
    private const int McpControlIdleSeconds = 60;

    static MainWindow()
    {
        // Class preview handlers run before the editor's instance shortcuts.
        // Keeping controls enabled allows programmatic MCP edits and repainting
        // while rejecting mouse, keyboard, touch, drop and routed user commands.
        static bool Locked(object? sender) => sender is MainWindow window && window._mcpControlOwnerSessionId is not null;
        KeyEventHandler keys = (sender, args) => { if (Locked(sender)) args.Handled = true; };
        EventManager.RegisterClassHandler(typeof(MainWindow), Keyboard.PreviewKeyDownEvent, keys, true);
        EventManager.RegisterClassHandler(typeof(MainWindow), Keyboard.PreviewKeyUpEvent, keys, true);
        EventManager.RegisterClassHandler(typeof(MainWindow), TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((sender, args) => { if (Locked(sender)) args.Handled = true; }), true);
        foreach (RoutedEvent input in new[] { Mouse.PreviewMouseDownEvent, Mouse.PreviewMouseUpEvent })
            EventManager.RegisterClassHandler(typeof(MainWindow), input,
                new MouseButtonEventHandler((sender, args) => { if (Locked(sender)) args.Handled = true; }), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), Mouse.PreviewMouseMoveEvent,
            new MouseEventHandler((sender, args) => { if (Locked(sender)) args.Handled = true; }), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), Mouse.PreviewMouseWheelEvent,
            new MouseWheelEventHandler((sender, args) =>
            {
                if (Locked(sender) && sender is MainWindow window && !window.IsTimelineWheelTarget(args.OriginalSource as DependencyObject))
                    args.Handled = true;
            }), true);
        foreach (RoutedEvent input in new[] { UIElement.PreviewTouchDownEvent, UIElement.PreviewTouchMoveEvent, UIElement.PreviewTouchUpEvent })
            EventManager.RegisterClassHandler(typeof(MainWindow), input,
                new EventHandler<TouchEventArgs>((sender, args) => { if (Locked(sender)) args.Handled = true; }), true);
        foreach (RoutedEvent input in new[] { DragDrop.PreviewDragEnterEvent, DragDrop.PreviewDragOverEvent, DragDrop.PreviewDropEvent })
            EventManager.RegisterClassHandler(typeof(MainWindow), input,
                new DragEventHandler((sender, args) => { if (Locked(sender)) { args.Effects = DragDropEffects.None; args.Handled = true; } }), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), CommandManager.PreviewCanExecuteEvent,
            new CanExecuteRoutedEventHandler((sender, args) =>
            { if (Locked(sender)) { args.CanExecute = false; args.ContinueRouting = false; args.Handled = true; } }), true);
        EventManager.RegisterClassHandler(typeof(MainWindow), CommandManager.PreviewExecutedEvent,
            new ExecutedRoutedEventHandler((sender, args) => { if (Locked(sender)) args.Handled = true; }), true);
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        // Suppress the ordinary unsaved-project dialog as well as native close
        // requests while the AI owns the window. Idle expiry restores closing.
        if (_mcpControlOwnerSessionId is not null && !_mcpLifetime.IsCancellationRequested)
        { args.Cancel = true; return; }
        base.OnClosing(args);
    }

    private bool McpControlBusy => _mcpActiveOperations > 0 ||
        _mcpJobs.Values.Any(job => job.Status == "running" || !job.Completion.IsCompleted) || _mcpRecorder is not null || _mcpRecordingBusy;

    private object McpControlState(string? sessionId) => new
    {
        locked = _mcpControlOwnerSessionId is not null,
        mode = _mcpControlOwnerSessionId is null ? "user" : "mcp",
        ownedByCaller = sessionId is not null && sessionId == _mcpControlOwnerSessionId,
        ownerLabel = _mcpControlOwnerLabel, idleTimeoutSeconds = McpControlIdleSeconds,
        activeOperations = _mcpActiveOperations,
        activeJobs = _mcpJobs.Values.Count(job => job.Status == "running" || !job.Completion.IsCompleted),
        recording = _mcpRecorder is not null || _mcpRecordingBusy,
        lastActivityUtc = _mcpControlOwnerSessionId is null ? (DateTimeOffset?)null : _mcpControlLastActivityUtc,
        idleExpiresUtc = _mcpControlOwnerSessionId is null || McpControlBusy ? (DateTimeOffset?)null
            : _mcpControlLastActivityUtc.AddSeconds(McpControlIdleSeconds)
    };

    private object ExecuteMcpControl(AutomationRequest request)
    {
        CheckMcpControlIdle();
        if (request.Command == "editor_control_status") return McpControlState(request.SessionId);
        string session = request.SessionId is { Length: > 0 } id ? id
            : throw new StageException("MCPCT0001", "缺少 MCP 会话身份，请通过标准 MCP 桥接程序连接。");
        if (request.Command == "editor_control_acquire")
        {
            if (_mcpControlOwnerSessionId is not null && _mcpControlOwnerSessionId != session)
                throw new StageException("MCPCT0003", "此进程正由另一个 MCP 会话操作，请等待操作权释放。");
            if (_mcpControlOwnerSessionId is null)
            {
                McpEnsureIdle();
                if (OwnedWindows.Cast<Window>().Any(window => window.IsVisible))
                    throw new StageException("MCPCT0004", "此进程仍有打开的对话窗口，请关闭对话窗口后再获取 MCP 操作权。");
                _mcpControlOriginalHitTestVisible = EditorRootGrid.IsHitTestVisible;
                _mcpControlWindowHandle = new WindowInteropHelper(this).Handle;
                if (!IsEnabled || _mcpControlWindowHandle != IntPtr.Zero && !McpIsWindowEnabled(_mcpControlWindowHandle))
                    throw new StageException("MCPCT0004", "应用正在执行界面操作，请等待完成后再获取 MCP 操作权。");
                _mcpControlOriginalNativeEnabled = _mcpControlWindowHandle != IntPtr.Zero && McpIsWindowEnabled(_mcpControlWindowHandle);
                _mcpControlOwnerSessionId = session;
                ApplyMcpControlUi(true);
            }
            _mcpControlOwnerLabel = McpString(request.Arguments ?? new JsonObject(), "ownerLabel", "AI");
            _mcpControlLastActivityUtc = DateTimeOffset.UtcNow;
            StatusText.Text = "正在通过MCP操作本程序";
            return McpControlState(session);
        }
        McpRequireControl(session);
        if (McpControlBusy) throw new StageException("MCPCT0004", "MCP 操作仍在执行，请等待任务结束，或取消任务并等待停止后再解锁。");
        ReleaseMcpControl("MCP 操作已结束，操作权已交还用户");
        return McpControlState(session);
    }

    private void McpRequireControl(string? sessionId)
    {
        CheckMcpControlIdle();
        if (_mcpControlOwnerSessionId is null)
            throw new StageException("MCPCT0002", "请先调用 editor_control_acquire，将此进程锁定为 MCP 操作模式。");
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId != _mcpControlOwnerSessionId)
            throw new StageException("MCPCT0003", "此进程由另一个 MCP 会话锁定，当前会话没有操作权。");
    }

    private void CheckMcpControlIdle()
    {
        if (_mcpControlOwnerSessionId is not null && !McpControlBusy &&
            DateTimeOffset.UtcNow - _mcpControlLastActivityUtc >= TimeSpan.FromSeconds(McpControlIdleSeconds))
            ReleaseMcpControl("MCP 已空闲 1 分钟，操作权已自动交还用户");
    }

    private void ReleaseMcpControl(string message)
    {
        _mcpControlOwnerSessionId = null; _mcpControlOwnerLabel = null;
        ApplyMcpControlUi(false);
        StatusText.Text = message;
        UpdateMcpInfo();
    }

    private void ApplyMcpControlUi(bool locked)
    {
        EditorMenuBar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        McpControlNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        // Keep hit testing and the native window alive for read-only timeline
        // wheel navigation. Class input guards still reject editing gestures.
        EditorRootGrid.IsHitTestVisible = locked || _mcpControlOriginalHitTestVisible;
        if (locked)
        {
            Keyboard.ClearFocus();
            Mouse.Capture(null);
        }
        // A disabled HWND never receives physical wheel messages. Closing is
        // still rejected by OnClosing while MCP owns the editor.
        if (_mcpControlWindowHandle != IntPtr.Zero)
            McpEnableWindow(_mcpControlWindowHandle, locked || _mcpControlOriginalNativeEnabled);
        CommandManager.InvalidateRequerySuggested();
    }

    [DllImport("user32.dll", EntryPoint = "EnableWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool McpEnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool McpIsWindowEnabled(IntPtr window);
}
