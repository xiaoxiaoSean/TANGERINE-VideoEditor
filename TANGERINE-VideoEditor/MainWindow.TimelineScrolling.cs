using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private HwndSource? _timelineWheelSource;
    private const int TimelineHorizontalWheelMessage = 0x020E;

    private void InitializeTimelineHorizontalWheel()
    {
        // WPF exposes the vertical wheel as a routed event, but does not
        // translate WM_MOUSEHWHEEL. Handle native horizontal-wheel messages
        // only over the timeline, leaving other panels to their own handlers.
        SourceInitialized += (_, _) =>
        {
            _timelineWheelSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _timelineWheelSource?.AddHook(TimelineWheelWindowHook);
        };
        Closed += (_, _) =>
        {
            _timelineWheelSource?.RemoveHook(TimelineWheelWindowHook);
            _timelineWheelSource = null;
        };
    }

    private IntPtr TimelineWheelWindowHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != TimelineHorizontalWheelMessage || !TimelineScrollViewer.IsVisible) return IntPtr.Zero;
        // GET_X/Y_LPARAM must sign extend: monitors left of or above the
        // primary display have negative screen coordinates. PointFromScreen
        // also converts device pixels to WPF units on high-DPI displays.
        long position = lParam.ToInt64();
        var point = TimelineScrollViewer.PointFromScreen(new Point(unchecked((short)position), unchecked((short)(position >> 16))));
        if (!new Rect(TimelineScrollViewer.RenderSize).Contains(point)) return IntPtr.Zero;
        handled = true;
        int delta = unchecked((short)(wParam.ToInt64() >> 16));
        ScrollTimelineWheel(delta, Keyboard.Modifiers, horizontal: true);
        return IntPtr.Zero;
    }

    private void ScrollTimelineWheel(int delta, ModifierKeys modifiers, bool horizontal)
    {
        // Timeline panning is read-only and remains available to the user
        // during MCP ownership. Ctrl-wheel zoom is not part of that exception.
        if (delta == 0 || _mcpControlOwnerSessionId is not null && (modifiers & ModifierKeys.Control) != 0) return;
        if (horizontal)
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset + delta);
        else if ((modifiers & ModifierKeys.Control) != 0)
            TimelineZoomSlider.Value = Math.Clamp(TimelineZoomSlider.Value * (delta > 0 ? 1.1 : .9), TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
        else if ((modifiers & ModifierKeys.Shift) != 0)
            TimelineScrollViewer.ScrollToHorizontalOffset(TimelineScrollViewer.HorizontalOffset - delta);
        else
            TimelineScrollViewer.ScrollToVerticalOffset(TimelineScrollViewer.VerticalOffset - delta);
    }

    private bool IsTimelineWheelTarget(DependencyObject? target)
    {
        while (target is not null)
        {
            if (ReferenceEquals(target, TimelineScrollViewer)) return true;
            target = target is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
}
