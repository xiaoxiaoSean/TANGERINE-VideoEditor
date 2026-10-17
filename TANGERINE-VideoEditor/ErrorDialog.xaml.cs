using System.Windows;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Displays a formatted diagnostic message with stage code and recovery hints.
/// </summary>
internal sealed partial class ErrorDialogWindow : Window
{
    internal ErrorDialogWindow(Window? owner, string message)
    {
        InitializeComponent();
        Width = SystemParameters.WorkArea.Width * 0.46;
        Height = SystemParameters.WorkArea.Height * 0.48;
        MaxHeight = SystemParameters.WorkArea.Height * 0.85;
        Title = LanguageManager.Get("ErrorTitle");
        diagnosticText.Text = message;
        closeButton.Content = LanguageManager.Get("Confirm");
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = owner is null;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
