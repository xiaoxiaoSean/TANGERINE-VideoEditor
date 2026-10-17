using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_VideoEditor;

/// <summary>Simple localized modal inputs that stretch with their host window.</summary>
public partial class MainWindow
{
    private string? PromptText(string title, string label, string initial)
    {
        var dialog = NewProjectMediaDialog(title);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9) });
        var input = new TextBox { Text = initial };
        content.Children.Add(input);
        content.Children.Add(DialogButtons(dialog));
        dialog.Content = content;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private ProjectPickerItem? PromptChoice(string title, string label,
        IReadOnlyList<ProjectPickerItem> choices, string? selectedId = null)
    {
        if (choices.Count == 0) return null;
        var dialog = NewProjectMediaDialog(title);
        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9) });
        var selector = new ComboBox { ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(item => item.Id == selectedId) ?? choices[0] };
        content.Children.Add(selector);
        content.Children.Add(DialogButtons(dialog));
        dialog.Content = content;
        return dialog.ShowDialog() == true ? selector.SelectedItem as ProjectPickerItem : null;
    }

    private Window NewProjectMediaDialog(string title) => new()
    {
        Title = title,
        Owner = this,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        SizeToContent = SizeToContent.WidthAndHeight,
        MinWidth = Math.Max(ActualWidth * 0.25, 1),
        Background = new SolidColorBrush(Color.FromRgb(25, 27, 32)),
        Foreground = Brushes.White,
        FontFamily = FontFamily
    };

    private static UIElement DialogButtons(Window dialog)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(NewToolButton("取消", (_, _) => dialog.DialogResult = false));
        buttons.Children.Add(NewToolButton("确定", (_, _) => dialog.DialogResult = true));
        return buttons;
    }
}
