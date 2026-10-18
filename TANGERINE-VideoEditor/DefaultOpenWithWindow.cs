using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_VideoEditor.SystemIntegration;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Presents the ZIP application's current-user association workflow for the
/// editor's single project format. Registry work runs off the dispatcher and
/// completion is reported from the effective Shell choice, not registration.
/// </summary>
internal sealed class DefaultOpenWithWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(Color.FromRgb(36, 38, 44)), Foreground = Brushes.White,
        Padding = new Thickness(10), Margin = new Thickness(0, 12, 0, 12)
    };
    private readonly List<Button> _actions = [];
    private bool _busy;

    public DefaultOpenWithWindow()
    {
        Title = "默认打开方式设置";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = SystemParameters.WorkArea.Width * .46;
        Height = SystemParameters.WorkArea.Height * .55;
        Background = new SolidColorBrush(Color.FromRgb(25, 27, 32));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "TANGERINE 项目文件 (.tvep)", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        heading.Children.Add(_status);
        layout.Children.Add(heading);
        Grid.SetRow(_log, 1);
        layout.Children.Add(_log);

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddAction(buttons, "使用本应用", async () =>
        {
            await Task.Run(() => DefaultAppAssociationService.SetThisAppDefault(
                DefaultAppAssociationService.ProjectExtension, DefaultAppAssociationService.GetExecutablePath()));
            Log("已验证：.tvep 默认使用 TANGERINE 视频剪辑器打开。");
        });
        AddAction(buttons, "仅注册打开方式", async () =>
        {
            await Task.Run(() => DefaultAppAssociationService.RegisterHandler(
                DefaultAppAssociationService.ProjectExtension, DefaultAppAssociationService.GetExecutablePath()));
            Log("已注册 .tvep 打开方式，项目文件使用 Windows 默认白色文件图标。");
        });
        AddAction(buttons, "打开 Windows 默认应用设置", async () =>
        {
            DefaultSettingsLaunch launch = await Task.Run(() =>
            {
                DefaultAppAssociationService.RegisterHandler(DefaultAppAssociationService.ProjectExtension,
                    DefaultAppAssociationService.GetExecutablePath());
                return DefaultAppAssociationService.OpenWindowsSettings(useThisApp: true);
            });
            foreach (StageException warning in launch.Warnings) Log($"{warning.Message} · {warning.StageCode}");
            Log("已打开 Windows 默认应用设置。选择 .tvep 的打开程序后返回此窗口刷新状态。");
        });
        AddAction(buttons, "取消设置并删除注册", async () =>
        {
            IProgress<string> progress = new Progress<string>(Log);
            await Task.Run(() => DefaultAppUnregistrationService.Unregister(progress.Report));
            Log("已取消本应用的 .tvep 默认打开设置并删除注册。");
        });
        AddAction(buttons, "刷新状态", () => Task.CompletedTask);
        var close = new Button { Content = "关闭", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;

        Loaded += async (_, _) => await RunAsync(() => Task.CompletedTask);
        Activated += async (_, _) =>
        {
            if (!_busy && IsLoaded) await RunAsync(() => Task.CompletedTask);
        };
        Closing += (_, args) =>
        {
            // A write must finish or roll back before this dialog disappears.
            // Closing while idle never modifies or unregisters associations.
            if (!_busy) return;
            args.Cancel = true;
            _status.Text = "正在更新打开方式，请等待完成。";
        };
    }

    private void AddAction(Panel host, string label, Func<Task> action)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(4)
        };
        button.Click += async (_, _) => await RunAsync(action);
        _actions.Add(button);
        host.Children.Add(button);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        foreach (Button button in _actions) button.IsEnabled = false;
        _status.Text = "正在检查 .tvep 打开方式…";
        try
        {
            await action();
            await RefreshStatusAsync();
        }
        catch (Exception exception)
        {
            string code = StageCodes.Resolve(exception, "DAPWN0001");
            Log($"{exception.Message} · {code}");
            _status.Text = "操作未完成。";
            ErrorDialog.Show(this, code, exception.Message, exception);
        }
        finally
        {
            _busy = false;
            foreach (Button button in _actions) button.IsEnabled = true;
        }
    }

    private async Task RefreshStatusAsync()
    {
        string? current = await Task.Run(() => DefaultAppAssociationService.QueryCurrentProgId(
            DefaultAppAssociationService.ProjectExtension));
        _status.Text = string.Equals(current, DefaultAppAssociationService.ProjectProgId, StringComparison.OrdinalIgnoreCase)
            ? "当前默认：TANGERINE 视频剪辑器"
            : string.IsNullOrWhiteSpace(current) ? "当前默认：尚未设置"
            : "当前默认：其他应用（" + current + "）";
    }

    private void Log(string message)
    {
        _log.AppendText(message + Environment.NewLine);
        _log.ScrollToEnd();
    }
}
