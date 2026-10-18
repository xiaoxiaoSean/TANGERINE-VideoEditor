using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Defines the complete numeric contract of each transition. Saved projects keep
/// these values in ClipTransition.Parameters; both rendering paths and the editor
/// consume the same schema, including defaults, bounds, labels and units.
/// </summary>
public static class TransitionSettings
{
    public sealed record Parameter(string Key, string Label, double Default, double Minimum,
        double Maximum, bool Integer = false);

    public const string StrengthKey = "strength", TurnsKey = "turns";
    public const double MinimumStrength = 0, MaximumStrength = 2;
    public const double MinimumTurns = .25, MaximumTurns = 10;

    public static IReadOnlyList<Parameter> Describe(string id)
    {
        var fields = new List<Parameter>
        {
            new("easing", "动画曲线（0 线性 / 1 平滑 / 2 加速 / 3 减速）", id == "transition.fade" ? 0 : 1, 0, 3, true)
        };
        if (UsesStrength(id)) fields.Add(new(StrengthKey, StrengthLabel(id), 1, 0, 2));
        if (UsesTurns(id)) fields.Add(new(TurnsKey, "完整旋转圈数", 1, .25, 10));
        if (id == "transition.rotate") fields.Add(new("angle", "开场旋转角度（度）", 90, -360, 360));
        if (id is "transition.spin" or "transition.radial")
            fields.Add(new("direction", "旋转方向（1 顺时针 / -1 逆时针）", 1, -1, 1, true));
        if (id is "transition.circle" or "transition.circle-open" or "transition.diamond" or
            "transition.radial" or "transition.zoom-in" or "transition.zoom-out" or "transition.zoom" or
            "transition.spin" or "transition.rotate")
        {
            fields.Add(new("centerX", "中心 X（画幅比例）", .5, 0, 1));
            fields.Add(new("centerY", "中心 Y（画幅比例）", .5, 0, 1));
        }
        if (id is "transition.wipe-left" or "transition.wipe-right" or "transition.wipe-up" or
            "transition.wipe-down" or "transition.circle" or "transition.circle-open" or "transition.diamond" or
            "transition.split" or "transition.diagonal" or "transition.luma")
            fields.Add(new("feather", "边缘柔化（画幅比例）", id == "transition.circle" ? .12 : .02, 0, .5));
        if (id is "transition.wipe-left" or "transition.wipe-right" or "transition.wipe-up" or
            "transition.wipe-down" or "transition.circle" or "transition.circle-open" or "transition.diamond" or
            "transition.split" or "transition.diagonal" or "transition.luma" or "transition.blinds" or "transition.checker")
            fields.Add(new("reverse", "反向揭示（0 关闭 / 1 开启）", 0, 0, 1, true));
        if (id is "transition.blur" or "transition.blur-dissolve")
            fields.Add(new("radius", "高斯模糊半径（画幅宽度比例）", id == "transition.blur" ? .025 : .05, .001, .15));
        if (id is "transition.shake" or "transition.film" or "transition.glitch")
            fields.Add(new("frequency", "抖动 / 闪烁频率（次/秒）", id == "transition.shake" ? 12 : 8, 1, 40));
        if (id is "transition.blinds" or "transition.checker" or "transition.glitch")
            fields.Add(new("count", id == "transition.checker" ? "棋盘格数" : "条带数", id == "transition.glitch" ? 14 : 8, 2, 40, true));
        if (id == "transition.pixelize") fields.Add(new("blockSize", "最大像素块（画幅宽度比例）", .08, .005, .3));
        if (id == "transition.diagonal") fields.Add(new("slope", "斜向比例", 1, .1, 4));
        if (id == "transition.page") fields.Add(new("fold", "二维折页阴影宽度（画幅比例）", .12, .01, .4));
        if (id == "transition.light-leak")
        {
            fields.Add(new("warmth", "暖色程度", .8, 0, 1));
            fields.Add(new("spread", "漏光光斑宽度（画幅比例）", .35, .05, 1));
        }
        if (id == "transition.radial") fields.Add(new("startAngle", "扫描起始角度（度）", -90, -360, 360));
        return fields;
    }

    public static bool UsesStrength(string id) => id is "transition.zoom-in" or "transition.zoom-out" or
        "transition.zoom" or "transition.blur" or "transition.blur-dissolve" or "transition.flash" or "transition.light-leak" or "transition.page" or
        "transition.glitch" or "transition.film" or "transition.shake";
    public static bool UsesTurns(string id) => id == "transition.spin";
    public static string StrengthLabel(string id) => id switch
    {
        "transition.zoom-in" or "transition.zoom-out" or "transition.zoom" => "缩放幅度（1 = 默认）",
        "transition.blur" or "transition.blur-dissolve" => "模糊强度（1 = 默认）",
        "transition.flash" or "transition.light-leak" => "光效亮度（1 = 默认）",
        "transition.page" => "折页弯曲幅度（1 = 默认）",
        "transition.glitch" => "故障位移幅度（1 = 默认）",
        "transition.film" => "胶片滚动幅度（1 = 默认）",
        "transition.shake" => "画面位移幅度（1 = 默认）",
        _ => "强度（1 = 默认）"
    };
    public static double Strength(ClipTransition transition) => Value(transition, StrengthKey);
    public static double Turns(ClipTransition transition) => Value(transition, TurnsKey);

    /// <summary>Invalid or missing serialized values use their schema default.</summary>
    public static double Value(ClipTransition transition, string key)
    {
        Parameter? definition = Describe(transition.PresetId).FirstOrDefault(item => item.Key == key);
        if (definition is null) return key is StrengthKey or TurnsKey ? 1 : 0;
        double number = transition.Parameters.TryGetValue(key, out JsonElement element) &&
            element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double value) &&
            double.IsFinite(value) ? Math.Clamp(value, definition.Minimum, definition.Maximum) : definition.Default;
        return definition.Integer ? Math.Round(number) : number;
    }
    public static void Write(ClipTransition transition, string key, double value) =>
        transition.Parameters[key] = JsonSerializer.SerializeToElement(Math.Round(value, 4));
}
