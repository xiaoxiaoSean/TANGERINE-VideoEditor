using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Exposes the existing bounded project snapshots as a navigable history.
/// Entries remain chronological even though undo and redo use LIFO stacks.
/// </summary>
public partial class MainWindow
{
    private void InitializeHistoryPanelCommand()
    {
        var menu = UndoMenuItem.Parent as MenuItem;
        if (menu is null) return;
        var history = new MenuItem { Header = "编辑历史…" };
        history.Click += (_, _) => ShowHistoryPanel();
        menu.Items.Insert(menu.Items.IndexOf(RedoMenuItem) + 1, history);
    }

    private void ShowHistoryPanel()
    {
        var window = new Window
        {
            Title = "编辑历史", Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * .3),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * .46),
            Background = Brush("#191B20"), Foreground = Brushes.White
        };
        var layout = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(12, 6, 12, 6) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        var jump = new Button { Content = "转到所选记录", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 10, 0, 8) };
        DockPanel.SetDock(jump, Dock.Bottom);
        layout.Children.Add(jump);
        var description = new TextBlock
        {
            Text = "选择记录后点击“转到”。列表最多保留最近 30 次编辑。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(description, Dock.Top);
        layout.Children.Add(description);
        var items = new ListBox();
        layout.Children.Add(items);

        void Refresh()
        {
            items.Items.Clear();
            int undoCount = _undoStates.Count;
            for (int index = 0; index < undoCount; index++)
                items.Items.Add($"{index + 1:00} · 编辑快照（撤销 {undoCount - index} 步）");
            items.Items.Add($"{undoCount + 1:00} · 当前工程");
            for (int index = 0; index < _redoStates.Count; index++)
                items.Items.Add($"{undoCount + index + 2:00} · 重做快照（前进 {index + 1} 步）");
            items.SelectedIndex = undoCount;
        }
        jump.Click += async (_, _) =>
        {
            int selected = items.SelectedIndex;
            if (selected < 0) return;
            int offset = selected - _undoStates.Count;
            if (offset == 0) return;
            if (_historyOperationInProgress || _projectIoInProgress || _exportInProgress ||
                _mediaImportGate.CurrentCount == 0 ||
                _movingClip || _trimmingStart || _trimmingEnd || _scrubbingPlayhead ||
                _rubberBandDrag is not null || _bezierHandleDrag is not null ||
                _draggingTitleClip is not null)
            {
                StatusText.Text = "当前操作尚未完成 · HIST-NAVIGATE-BUSY";
                return;
            }
            jump.IsEnabled = false;
            try
            {
                for (int index = 0; index < Math.Abs(offset); index++)
                {
                    int before = _undoStates.Count;
                    await RestoreHistoryAsync(offset < 0 ? _undoStates : _redoStates,
                        offset < 0 ? _redoStates : _undoStates, "已转到编辑历史记录");
                    if (_undoStates.Count == before) break;
                }
                Refresh();
            }
            finally { jump.IsEnabled = true; }
        };
        window.Content = layout;
        Refresh();
        window.ShowDialog();
    }
}
