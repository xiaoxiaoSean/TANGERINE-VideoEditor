using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    /// <summary>
    /// One export owns one nonmodal progress view. Finishing the view only
    /// closes its own window; it never cancels another export or MCP job.
    /// Every method is called on the editor dispatcher.
    /// </summary>
    private sealed class ExportProgressPresenter
    {
        private readonly Window _window;
        private readonly TextBlock _message;
        private readonly ProgressBar _bar;
        private readonly Button? _cancelButton;
        private readonly Action? _cancel;
        private bool _finished;
        private bool _cancellationRequested;

        public string? JobId { get; }
        public bool IsOpen => !_finished && _window.IsVisible;

        public ExportProgressPresenter(MainWindow owner, string outputPath, string? jobId = null,
            Action? cancel = null)
        {
            owner.Dispatcher.VerifyAccess();
            JobId = jobId;
            _cancel = cancel;
            bool mcp = jobId is not null;
            _window = new Window
            {
                Title = mcp ? "MCP 正在导出视频" : "正在导出视频",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = Math.Max(1, (owner.ActualWidth > 0 ? owner.ActualWidth : SystemParameters.WorkArea.Width) * .34),
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = !mcp,
                Background = Brush("#191B20"),
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(outputPath), TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#C5CAD4"), Margin = new Thickness(0, 0, 0, 10),
                ToolTip = outputPath
            });
            _message = new TextBlock { Text = "准备渲染…", TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12) };
            _bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0,
                IsIndeterminate = true,
                Height = Math.Max(1, (owner.ActualHeight > 0 ? owner.ActualHeight : SystemParameters.WorkArea.Height) * .018) };
            panel.Children.Add(_message);
            panel.Children.Add(_bar);
            if (cancel is not null)
            {
                _cancelButton = new Button
                {
                    Content = "取消导出", HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 12, 0, 0)
                };
                _cancelButton.Click += (_, _) => RequestCancellation();
                panel.Children.Add(_cancelButton);
            }
            else panel.Children.Add(new TextBlock
            {
                Text = "正在通过 MCP 导出，任务结束后自动关闭",
                TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C5CAD4"),
                Margin = new Thickness(0, 12, 0, 0)
            });
            _window.Content = panel;
            _window.Closing += (_, args) =>
            {
                if (_finished) return;
                args.Cancel = true;
                // A read-only MCP progress window cannot bypass the editor's
                // control lock. Only jobs_cancel cancels the owning MCP job.
                if (_cancel is not null) RequestCancellation();
            };
            _window.Closed += (_, _) => _finished = true;
            _window.Show();
        }

        public void Prepare(string message)
        {
            _window.Dispatcher.VerifyAccess();
            if (!IsOpen || _cancellationRequested) return;
            _bar.IsIndeterminate = true;
            _message.Text = message;
        }

        public void Encode(double ratio)
        {
            _window.Dispatcher.VerifyAccess();
            if (!IsOpen || _cancellationRequested || !double.IsFinite(ratio)) return;
            int percent = (int)Math.Round(Math.Clamp(ratio, 0, 1) * 100);
            _bar.IsIndeterminate = false;
            _bar.Value = percent;
            _message.Text = $"正在编码：{percent}%";
        }

        public void Complete()
        {
            _window.Dispatcher.VerifyAccess();
            if (_finished) return;
            _finished = true;
            _window.Close();
        }

        private void RequestCancellation()
        {
            if (_finished || _cancellationRequested || _cancel is null) return;
            _cancellationRequested = true;
            if (_cancelButton is not null) _cancelButton.IsEnabled = false;
            _message.Text = "正在取消…";
            _cancel();
        }
    }

    private async Task UpdateMcpExportProgressAsync(McpJob job, ExportProgressPresenter view,
        Action<ExportProgressPresenter> update)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!view.IsOpen || view.JobId != job.Id || job.Status != "running" ||
                    job.Cancellation.IsCancellationRequested || _mcpLifetime.IsCancellationRequested ||
                    job.OwnerSessionId != _mcpControlOwnerSessionId ||
                    !_mcpJobs.TryGetValue(job.Id, out McpJob? current) || !ReferenceEquals(current, job)) return;
                update(view);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { System.Diagnostics.Debug.WriteLine($"EXPRT0901: {exception}"); }
    }

    private async Task CloseMcpExportProgressAsync(McpJob job, ExportProgressPresenter view)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            await Dispatcher.InvokeAsync(() =>
            {
                // The captured view belongs to this job even if its control
                // session or project has since changed. Closing it performs
                // no cancellation and cannot touch another job's window.
                if (view.JobId == job.Id) view.Complete();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        { System.Diagnostics.Debug.WriteLine($"EXPRT0901: {exception}"); }
    }
}
