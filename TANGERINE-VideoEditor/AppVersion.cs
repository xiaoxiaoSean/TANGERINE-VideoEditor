using System.Globalization;

namespace TANGERINE_VideoEditor;

internal static class AppVersion
{
    // The SDK generates this assembly version from the project Version property.
    // Read our own assembly so test hosts and launchers cannot change the displayed version.
    public static string Number { get; } = GetVersionNumber();

    public static string DisplayName => $"v{Number}";

    private static string GetVersionNumber()
    {
        var version = typeof(AppVersion).Assembly.GetName().Version;
        return version is null
            ? "未知"
            : string.Create(CultureInfo.InvariantCulture,
                $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}");
    }
}
