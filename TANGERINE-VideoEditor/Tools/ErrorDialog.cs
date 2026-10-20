using System;
using System.Diagnostics;
using System.Windows;

namespace TANGERINE_VideoEditor.Tools;

/// <summary>
/// Single static entry point for diagnostic dialogs. Callers supply the
/// boundary's fallback stage code; StageException keeps its original code.
/// </summary>
internal static class ErrorDialog
{
    internal static void Show(Window? owner, string fallbackStageCode, Exception exception)
    {
        string stageCode = StageCodes.Resolve(exception, fallbackStageCode);
        Show(owner, stageCode, exception.Message, exception);
    }

    internal static void Show(Window? owner, string stageCode, string detail, Exception? exception = null) =>
        ShowFormatted(owner, MessageTipGenerator.GenerateTip(stageCode, detail, exception));

    internal static void ShowFormatted(Window? owner, string message)
    {
        var dispatcher = owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            { Trace.TraceError(message); return; }
            try { dispatcher.Invoke(() => ShowFormatted(owner, message)); }
            catch (InvalidOperationException reportFailure) { Trace.TraceError($"PROGM0013: {reportFailure}\n{message}"); }
            return;
        }
        try
        {
            // Startup, a closing source monitor, or a completed MCP command
            // can leave an owner reference whose native window no longer exists.
            Window? activeOwner = owner is { IsLoaded: true, IsVisible: true } ? owner : null;
            new ErrorDialogWindow(activeOwner, message).ShowDialog();
        }
        catch (Exception dialogFailure)
        {
            Trace.TraceError($"PROGM0013: {dialogFailure}\n{message}");
            // A broken resource/theme must not recursively reopen the same
            // custom dialog through the dispatcher's unhandled-error hook.
            MessageBox.Show(message, LanguageManager.Get("ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
