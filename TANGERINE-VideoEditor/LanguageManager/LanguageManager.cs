using System.Globalization;
using System.Reflection;
using System.Resources;

/// <summary>
/// Resolves localized UI strings from embedded resource files. The neutral
/// resource holds Simplified Chinese; English serves as final fallback.
/// </summary>
public static class LanguageManager
{
    private static readonly ResourceManager rm =
        new ResourceManager(
            "TANGERINE_VideoEditor.LanguageManager.UiStrings",
            Assembly.GetExecutingAssembly());

    public static string Get(string key)
    {
        return rm.GetString(key, CultureInfo.CurrentUICulture)
            ?? rm.GetString(key, CultureInfo.GetCultureInfo("en-US"))
            ?? key;
    }
}
