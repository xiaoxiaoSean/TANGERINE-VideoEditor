using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TANGERINE_VideoEditor.Tools
{
    /// <summary>
    /// Builds localized error messages from stage codes and exception details.
    /// Stage codes follow AAAAA0000 format (five letters, four digits).
    /// </summary>
    public static partial class MessageTipGenerator
    {
        private const string UnknownStageCode = "UNKWN0001";

        [GeneratedRegex("^[A-Z]{5}[0-9]{4}$", RegexOptions.CultureInvariant)]
        private static partial Regex StageCodePattern();

        public static string GenerateTip(string stageCode, string exceptionMessage)
            => GenerateTip(stageCode, exceptionMessage, null);

        /// <summary>
        /// Builds a complete, localized diagnostic for a dialog. The stage code
        /// identifies the failing boundary; the optional exception selects a
        /// more useful recovery hint without changing that stable identifier.
        /// </summary>
        public static string GenerateTip(string stageCode, string exceptionMessage, Exception? exception)
        {
            string code = stageCode is not null && StageCodePattern().IsMatch(stageCode)
                ? stageCode : UnknownStageCode;
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
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is UnauthorizedAccessException or System.Security.SecurityException) return "Permission";
                if (current is FileNotFoundException or DirectoryNotFoundException) return "Missing";
                if (current is OutOfMemoryException) return "Memory";
                if (current is System.IO.IOException) return "Io";
            }

            return stageCode[..5] switch
            {
                "PRJIO" or "PMEDA" or "EXCHG" or "EXPRT" => "Io",
                "AUDIO" or "GRAPH" or "PRVEW" or "EFECT" => "Render",
                "TMLNE" or "KEYFR" or "SUBTL" => "Timeline",
                _ => "General"
            };
        }
    }
}
