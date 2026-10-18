using System.Configuration;
using System.Data;
using System.Windows;

namespace TANGERINE_VideoEditor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow();
            MainWindow = window;
            if (e.Args.Length > 0)
            {
                // Explorer supplies the whole quoted path as one argument,
                // including spaces and Chinese characters. Wait for Loaded so
                // the preview/layout controls are ready before project load.
                string projectPath = e.Args[0];
                RoutedEventHandler? open = null;
                open = async (_, _) =>
                {
                    window.Loaded -= open;
                    await window.OpenProjectPathAsync(projectPath);
                };
                window.Loaded += open;
            }
            window.Show();
        }
    }

}
