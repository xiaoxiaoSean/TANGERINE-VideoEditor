using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// User-adjustable numbers of a clip transition, kept in
/// <see cref="ClipTransition.Parameters"/>. The still-frame compositor and the
/// video exporter both read them here, so a preview frame and the exported
/// video use the same values. A parameter that was never set means 100%, which
/// is the value the transitions used before they were adjustable.
/// </summary>
public static class TransitionSettings
{
    public const string StrengthKey = "strength";
    public const string TurnsKey = "turns";
    public const double MinimumStrength = 0, MaximumStrength = 2;
    public const double MinimumTurns = .25, MaximumTurns = 10;

    /// <summary>Transitions whose size or intensity scales with the strength setting.</summary>
    public static bool UsesStrength(string presetId) => presetId is
        "transition.zoom-in" or "transition.zoom-out" or "transition.zoom" or "transition.blur" or
        "transition.blur-dissolve" or "transition.flash" or "transition.light-leak" or "transition.page" or
        "transition.glitch" or "transition.film" or "transition.shake";

    public static bool UsesTurns(string presetId) => presetId is "transition.spin" or "transition.rotate";

    /// <summary>What the strength setting changes for this transition, as a field label.</summary>
    public static string StrengthLabel(string presetId) => presetId switch
    {
        "transition.zoom-in" or "transition.zoom-out" or "transition.zoom" => "缩放幅度（%，100 = 默认）",
        "transition.blur" or "transition.blur-dissolve" => "模糊程度（%，100 = 默认）",
        "transition.flash" or "transition.light-leak" => "光效亮度（%，100 = 默认）",
        "transition.page" => "翻页卷曲幅度（%，100 = 默认）",
        "transition.glitch" => "故障抖动幅度（%，100 = 默认）",
        "transition.film" or "transition.shake" => "胶片抖动幅度（%，100 = 默认）",
        _ => "效果强度（%，100 = 默认）"
    };

    public static double Strength(ClipTransition transition) =>
        Read(transition, StrengthKey, 1, MinimumStrength, MaximumStrength);

    /// <summary>Full rotations a spin transition makes.</summary>
    public static double Turns(ClipTransition transition) =>
        Read(transition, TurnsKey, 1, MinimumTurns, MaximumTurns);

    public static void Write(ClipTransition transition, string key, double value) =>
        transition.Parameters[key] = JsonSerializer.SerializeToElement(Math.Round(value, 4));

    private static double Read(ClipTransition transition, string key, double fallback,
        double minimum, double maximum) =>
        transition.Parameters.TryGetValue(key, out JsonElement element) &&
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) &&
        double.IsFinite(value)
            ? Math.Clamp(value, minimum, maximum) : fallback;
}
