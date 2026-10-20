using System.Configuration;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private int _reportingGlobalError;

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);
                var window = new MainWindow();
                MainWindow = window;
                if (e.Args.Length > 0)
                {
                    // Explorer supplies the whole quoted path as one argument,
                    // including spaces and Chinese characters. Wait for Loaded
                    // so preview/layout controls are ready before project load.
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
            catch (Exception exception) { ReportGlobalError(exception, "PROGM0001", fatal: true); }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Match TANGERINE-ZIP's final WPF boundary: display the original
            // domain stage, then stop instead of continuing a failed UI handler
            // with partially updated project/selection state.
            e.Handled = true;
            ReportGlobalError(e.Exception, "PROGM0010", fatal: true);
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            Trace.TraceError($"{StageCodes.Resolve(e.Exception, "PROGM0011")}: {e.Exception}");
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            // The finalizer thread must neither create a WPF window nor block
            // waiting for user interaction. Surface the failure on the UI queue.
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => ReportGlobalError(e.Exception, "PROGM0011", fatal: false)));
            }
            catch (InvalidOperationException reportFailure) { Trace.TraceError($"PROGM0013: {reportFailure}"); }
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception ??
                new StageException("PROGM0012", LanguageManager.Get("ErrorCauseGeneral"));
            Environment.ExitCode = 1;
            Trace.TraceError($"{StageCodes.Resolve(exception, "PROGM0012")}: {exception}");
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                if (Dispatcher.CheckAccess()) ReportGlobalError(exception, "PROGM0012", fatal: true);
                else Dispatcher.Invoke(() => ReportGlobalError(exception, "PROGM0012", fatal: true),
                    DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(5));
            }
            catch (Exception reportFailure)
            {
                // A terminating worker can race dispatcher shutdown. Do not
                // throw another exception from the runtime's final error hook.
                Trace.TraceError($"PROGM0013: {reportFailure}");
            }
        }

        private void ReportGlobalError(Exception exception, string fallbackStageCode, bool fatal)
        {
            string stage = StageCodes.Resolve(exception, fallbackStageCode);
            Trace.TraceError($"{stage}: {exception}");
            if (Interlocked.CompareExchange(ref _reportingGlobalError, 1, 0) != 0) return;
            try
            {
                if (fatal) Environment.ExitCode = 1;
                ErrorDialog.Show(MainWindow, stage, exception);
            }
            catch (Exception reportFailure) { Trace.TraceError($"PROGM0013: {reportFailure}"); }
            finally
            {
                Interlocked.Exchange(ref _reportingGlobalError, 0);
                if (fatal) Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            base.OnExit(e);
        }
    }

}
