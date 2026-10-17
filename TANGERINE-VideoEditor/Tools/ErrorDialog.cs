using System;
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
        string stageCode = exception is StageException staged ? staged.StageCode : fallbackStageCode;
        Show(owner, stageCode, exception.Message, exception);
    }

    internal static void Show(Window? owner, string stageCode, string detail, Exception? exception = null) =>
        ShowFormatted(owner, MessageTipGenerator.GenerateTip(stageCode, detail, exception));

    internal static void ShowFormatted(Window? owner, string message) =>
        new ErrorDialogWindow(owner, message).ShowDialog();
}
