using System;

namespace TANGERINE_VideoEditor.Tools
{
    /// <summary>
    /// Builds localized error messages from stage codes and exception details.
    /// Stage codes follow AAAAA0000 format (five letters, four digits).
    /// </summary>
    public static class MessageTipGenerator
    {
        public static string GenerateTip(string stageCode, string exceptionMessage)
            => GenerateTip(stageCode, exceptionMessage, null);

        /// <summary>
        /// Builds a complete, localized diagnostic for a dialog. The stage code
        /// identifies the failing boundary; the optional exception selects a
        /// more useful recovery hint without changing that stable identifier.
        /// </summary>
        public static string GenerateTip(string stageCode, string exceptionMessage, Exception? exception)
        {
            string code = exception is null ? StageCodes.Normalize(stageCode) : StageCodes.Resolve(exception, stageCode);
            string detail = string.IsNullOrWhiteSpace(exceptionMessage)
                ? LanguageManager.Get("ErrorTitle") : exceptionMessage.Trim();
            string category = GetCategory(code, exception);
            return LanguageManager.Get("StageCodeTip1") + code + Environment.NewLine + Environment.NewLine +
                LanguageManager.Get("StageCodeTip2") + detail + Environment.NewLine + Environment.NewLine +
                LanguageManager.Get("ErrorPossibleCause") + Environment.NewLine +
                LanguageManager.Get("ErrorCause" + category) + Environment.NewLine + Environment.NewLine +
                LanguageManager.Get("ErrorSuggestedAction") + Environment.NewLine +
                LanguageManager.Get("ErrorAction" + category);
        }

        private static string GetCategory(string stageCode, Exception? exception)
        {
            // Inspect types across all aggregate branches before choosing a
            // broad I/O hint: an outer IOException can wrap a permission or
            // missing-file failure with a more specific recovery action.
            Exception[] failures = exception is null ? [] : StageCodes.Enumerate(exception).ToArray();
            if (failures.Any(current => current is UnauthorizedAccessException or System.Security.SecurityException)) return "Permission";
            if (failures.Any(current => current is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)) return "Missing";
            if (failures.Any(current => current is OutOfMemoryException)) return "Memory";
            if (failures.Any(current => current is OperationCanceledException)) return "Cancelled";
            if (failures.Any(current => current is TimeoutException)) return "Timeout";
            if (failures.Any(current => current is System.Text.Json.JsonException or System.IO.InvalidDataException)) return "Format";
            if (failures.Any(current => current is System.ComponentModel.Win32Exception)) return "Tool";
            if (failures.Any(current => current is System.IO.IOException)) return "Io";
            if (failures.Any(current => current is ArgumentException or FormatException or OverflowException)) return "Validation";
            if (failures.Any(current => current is InvalidOperationException)) return "State";

            // A few stages have precise recovery guidance even when their
            // domain exception intentionally hides the implementation detail.
            if (stageCode is "EXPRT0003" or "CLPFR0002" or "STLFR0003") return "Tool";
            return stageCode[..5] switch
            {
                "PRJIO" or "PMEDA" or "EXCHG" => "Io",
                "AUDIO" or "GRAPH" or "PRVEW" or "EFECT" or "CLPFR" or "STLFR" or "EXPRT" or "IMGSQ" or
                    "CATPV" or "AUDDT" or "AUDNM" or "STABL" or "VISTR" or "GNRTR" or "THUMB" or "WAVFM" or "MUSIC" => "Render",
                "TMLNE" or "KEYFR" or "SUBTL" or "PEDIT" or "MLCAM" or "SEQNC" => "Timeline",
                "GTMPL" or "EXPDL" or "WKSPC" or "TITLE" => "Settings",
                "PROPC" or "HISTY" or "MCPCT" => "State",
                "MCPJS" or "MCPFT" => "Validation",
                "SFTAS" or "DEFAS" or "UNRAS" or "LUCAS" => "Permission",
                "MCPRT" or "MCPJB" => "General",
                _ => "General"
            };
        }
    }
}
