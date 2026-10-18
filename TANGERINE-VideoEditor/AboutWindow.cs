using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_VideoEditor;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "关于 TANGERINE-VideoEditor";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        ShowInTaskbar = false;

        // Keep the dialog and its content proportional to the current desktop work area.
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width * 0.4;
        Height = workArea.Height * 0.28;
        MinWidth = workArea.Width * 0.25;
        MinHeight = workArea.Height * 0.18;
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(94, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7, GridUnitType.Star) });

        // A read-only TextBox preserves selection, Ctrl+C and the standard copy context menu.
        var details = new TextBox
        {
            Text = string.Join(Environment.NewLine,
                "TANGERINE-VideoEditor",
                "TANGERINE 视频剪辑软件",
                $"版本：{AppVersion.DisplayName}"),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Black,
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            FontSize = 16
        };
        Grid.SetColumn(details, 1);
        Grid.SetRow(details, 1);
        layout.Children.Add(details);
        Content = layout;
    }
}
