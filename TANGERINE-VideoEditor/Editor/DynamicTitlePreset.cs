using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Evaluates built-in offline title motion on the clip-local clock. Preview
/// and export both call these functions, so seeking to the same frame yields
/// the same visible string and animation progress.
/// <para>
/// Every timing and distance comes from the clip's <see cref="TitleMotion"/>
/// when set. A value left unset falls back to the number the presets used
/// before they were configurable, so older projects render exactly as before.
/// </para>
/// </summary>
public static partial class DynamicTitlePreset
{
    // Built-in defaults, public so the settings page can show what "unset" means.
    public const double DefaultFadeSeconds = .45;
    public const double DefaultSlideSeconds = .65;
    public const double DefaultSlideDistance = .35;
    public const string DefaultSlideFrom = "Left";
    public const double DefaultPopSeconds = .55;
    public const double DefaultPopStartScale = .55;
    public const double DefaultPopOvershoot = .12;
    /// <summary>The fixed time a whole typed line used to take, whatever its length.</summary>
    public const double LegacyTypeSeconds = 1.8;
    /// <summary>Speed given to a newly created typewriter title.</summary>
    public const double DefaultSecondsPerCharacter = .1;

    /// <summary>
    /// The motion settings a freshly created title of this preset starts with.
    /// A typewriter gets an explicit per-character speed, because the old
    /// "whole line in 1.8 s" rule makes a long sentence type absurdly fast.
    /// Other presets start unset, which already means their built-in defaults.
    /// </summary>
    public static TitleMotion? DefaultMotionFor(string? presetId) => presetId switch
    {
        "title.typewriter" => new TitleMotion { SecondsPerCharacter = DefaultSecondsPerCharacter },
        "title.kinetic" => new TitleMotion { PopSeconds = .45, PopStartScale = .45, PopOvershoot = .16,
            SlideSeconds = .55, SlideDistance = .32, SlideFrom = "Bottom" },
        "title.clean-lower-third" => new TitleMotion { SlideSeconds = .7, SlideDistance = .45, SlideFrom = "Left", Easing = "EaseOut" },
        "title.box-reveal" => new TitleMotion { FadeInSeconds = .65, FadeOutSeconds = .35 },
        "title.outline-pop" => new TitleMotion { PopSeconds = .65, PopStartScale = .2, PopOvershoot = .22 },
        "title.crawl" => new TitleMotion { ScrollDistance = 1.6, ScrollDirection = "Left", Easing = "Linear" },
        "title.zoom" => new TitleMotion { ZoomStartScale = .65, ZoomEndScale = 1.3, Easing = "Smooth" },
        _ => null
    };

    [GeneratedRegex(@"(?<first>-?\d+(?:\.\d+)?)\s*(?:→|->)\s*(?<last>-?\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CounterRangeRegex();

    public static bool IsDynamic(TimelineClip clip) => clip.TemplateId is
        "title.typewriter" or "title.counter" or "title.fade" or "title.slide" or "title.pop" or "title.kinetic" or "title.credits" or
        "title.clean-lower-third" or "title.box-reveal" or "title.outline-pop" or "title.crawl" or "title.zoom";

    public static bool HasChangingText(TimelineClip clip) => clip.TemplateId is
        "title.typewriter" or "title.counter";

    // ---- Parameter access -------------------------------------------------

    private static TitleMotion? Motion(TimelineClip clip) => clip.Text?.Motion;

    /// <summary>The stored value limited to a range, or the fallback when unset or not a number.</summary>
    private static double Setting(double? stored, double fallback, double minimum, double maximum) =>
        stored is double value && double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    /// <summary>Wait before the motion starts, kept inside the clip.</summary>
    public static double DelaySeconds(TimelineClip clip) =>
        Math.Min(Setting(Motion(clip)?.DelaySeconds, 0, 0, 3600), Math.Max(0, clip.DurationSeconds - .05));

    /// <summary>Length of the fade-in and fade-out. Together they never exceed the clip.</summary>
    public static (double In, double Out) FadeSeconds(TimelineClip clip)
    {
        double duration = Math.Max(0, clip.DurationSeconds);
        double standard = Math.Min(DefaultFadeSeconds, duration / 2);
        double fadeIn = Setting(Motion(clip)?.FadeInSeconds, standard, 0, duration);
        double fadeOut = Setting(Motion(clip)?.FadeOutSeconds, standard, 0, duration);
        double total = fadeIn + fadeOut;
        if (total > duration && total > 0)
        {
            // Overlapping ramps would make preview (a minimum) and export (two
            // chained fade filters) disagree, so shrink them to fit instead.
            double fit = duration / total;
            fadeIn *= fit;
            fadeOut *= fit;
        }
        return (fadeIn, fadeOut);
    }

    /// <summary>Time one character takes to appear.</summary>
    public static double TypewriterSecondsPerCharacter(TimelineClip clip, int characterCount)
    {
        if (Motion(clip)?.SecondsPerCharacter is double value && double.IsFinite(value) && value > 0)
            return Math.Clamp(value, .005, 60);
        // Unset: the original behaviour, a whole line typed in a fixed time.
        double span = Math.Min(clip.DurationSeconds - DelaySeconds(clip), LegacyTypeSeconds);
        return Math.Max(span, .001) / Math.Max(1, characterCount);
    }

    /// <summary>Seconds the whole line needs to finish typing, counting the start delay.</summary>
    public static double TypewriterTotalSeconds(TimelineClip clip)
    {
        int count = TextElements(clip.Text?.Content ?? clip.Name).Count;
        return DelaySeconds(clip) + count * TypewriterSecondsPerCharacter(clip, count);
    }

    public static double SlideSeconds(TimelineClip clip) =>
        Setting(Motion(clip)?.SlideSeconds, DefaultSlideSeconds, .05, 60);

    public static double SlideDistance(TimelineClip clip) =>
        Setting(Motion(clip)?.SlideDistance, DefaultSlideDistance, 0, 2);

    public static string SlideFrom(TimelineClip clip) =>
        Motion(clip)?.SlideFrom is "Left" or "Right" or "Top" or "Bottom"
            ? Motion(clip)!.SlideFrom! : DefaultSlideFrom;

    public static double PopSeconds(TimelineClip clip) =>
        Setting(Motion(clip)?.PopSeconds, DefaultPopSeconds, .05, 60);

    public static double PopStartScale(TimelineClip clip) =>
        Setting(Motion(clip)?.PopStartScale, DefaultPopStartScale, .05, 1);

    public static double PopOvershoot(TimelineClip clip) =>
        Setting(Motion(clip)?.PopOvershoot, DefaultPopOvershoot, 0, 1);

    public static bool HasSlide(TimelineClip clip) => clip.TemplateId is "title.slide" or "title.kinetic" or "title.clean-lower-third";
    public static bool HasPop(TimelineClip clip) => clip.TemplateId is "title.pop" or "title.kinetic" or "title.outline-pop";
    public static bool HasFade(TimelineClip clip) => clip.TemplateId is "title.fade" or "title.box-reveal";
    public static string Easing(TimelineClip clip) => Motion(clip)?.Easing is "Linear" or "EaseIn" or "EaseOut" or "Smooth"
        ? Motion(clip)!.Easing! : clip.TemplateId == "title.crawl" ? "Linear" : "Smooth";
    public static double ScrollSeconds(TimelineClip clip) =>
        Setting(Motion(clip)?.ScrollSeconds, Math.Max(.05, clip.DurationSeconds - DelaySeconds(clip)), .05, 3600);
    public static double ScrollDistance(TimelineClip clip) => Setting(Motion(clip)?.ScrollDistance, 1.6, 0, 8);
    public static string ScrollDirection(TimelineClip clip) => Motion(clip)?.ScrollDirection == "Right" ? "Right" : "Left";
    public static double ZoomSeconds(TimelineClip clip) =>
        Setting(Motion(clip)?.ZoomSeconds, Math.Max(.05, clip.DurationSeconds - DelaySeconds(clip)), .05, 3600);
    public static double ZoomStartScale(TimelineClip clip) => Setting(Motion(clip)?.ZoomStartScale, .65, .05, 8);
    public static double ZoomEndScale(TimelineClip clip) => Setting(Motion(clip)?.ZoomEndScale, 1.3, .05, 8);

    // ---- Text that changes over time --------------------------------------

    public static ClipText RenderStyle(TimelineClip clip, double localSeconds)
    {
        ClipText original = clip.Text ?? new ClipText { Content = clip.Name };
        if (!HasChangingText(clip)) return original;
        ClipText copy;
        try
        {
            copy = JsonSerializer.Deserialize<ClipText>(JsonSerializer.Serialize(original)) ??
                throw new ExportException(ExportStageCode.Prepare, "无法复制动态标题样式。");
        }
        catch (JsonException exception)
        {
            throw new ExportException(ExportStageCode.Prepare,
                "无法复制动态标题样式。", exception);
        }
        copy.Content = VisibleText(clip, localSeconds);
        return copy;
    }

    public static string VisibleText(TimelineClip clip, double localSeconds)
    {
        string content = clip.Text?.Content ?? clip.Name;
        if (clip.TemplateId == "title.typewriter")
        {
            List<string> elements = TextElements(content);
            return string.Concat(elements.Take(TypedCount(clip, localSeconds, elements.Count)));
        }
        if (clip.TemplateId == "title.counter")
        {
            Match match = CounterRangeRegex().Match(content);
            if (!match.Success) return content;
            if (!double.TryParse(match.Groups["first"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double first) ||
                !double.TryParse(match.Groups["last"].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double last)) return content;
            double delay = DelaySeconds(clip);
            double available = Math.Max(clip.DurationSeconds - delay, .001);
            double span = Motion(clip)?.CountSeconds is double requested && double.IsFinite(requested) && requested > 0
                ? Math.Min(requested, available) : available;
            double progress = double.IsFinite(localSeconds)
                ? Math.Clamp((localSeconds - delay) / span, 0, 1) : 1;
            double current = first + (last - first) * progress;
            int decimals = Motion(clip)?.Decimals is int chosen
                ? Math.Clamp(chosen, 0, 6)
                : Math.Max(DecimalPlaces(match.Groups["first"].Value),
                    DecimalPlaces(match.Groups["last"].Value));
            string shown = Math.Round(current, decimals, MidpointRounding.AwayFromZero)
                .ToString($"F{decimals}", CultureInfo.InvariantCulture);
            return content[..match.Index] + shown + content[(match.Index + match.Length)..];
        }
        return content;
    }

    /// <summary>How many characters of a typewriter title are showing at this time.</summary>
    private static int TypedCount(TimelineClip clip, double localSeconds, int total)
    {
        if (total <= 0) return 0;
        double elapsed = localSeconds - DelaySeconds(clip);
        if (double.IsNaN(elapsed) || elapsed <= 0) return 0;
        double perCharacter = TypewriterSecondsPerCharacter(clip, total);
        // The small addend keeps a frame that lands exactly on a character
        // boundary from rounding down to the previous character.
        double typed = Math.Floor(elapsed / perCharacter + .000001);
        return typed >= total ? total : (int)typed;
    }

    /// <summary>Grapheme clusters, so an emoji or accented letter counts as one character.</summary>
    private static List<string> TextElements(string content)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(content);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        return elements;
    }

    // ---- Preview motion -----------------------------------------------------

    public static double OpacityMultiplier(TimelineClip clip, double localSeconds)
    {
        if (!HasFade(clip)) return 1;
        (double fadeIn, double fadeOut) = FadeSeconds(clip);
        double rampIn = fadeIn > .000001 ? Math.Clamp(localSeconds / fadeIn, 0, 1) : 1;
        double rampOut = fadeOut > .000001
            ? Math.Clamp((clip.DurationSeconds - localSeconds) / fadeOut, 0, 1) : 1;
        return Math.Min(rampIn, rampOut);
    }

    /// <summary>Eased progress of the slide, 0 before it starts and 1 once it has arrived.</summary>
    private static double SlideProgress(TimelineClip clip, double localSeconds)
    {
        double delay = DelaySeconds(clip);
        return Ease(clip, Progress(localSeconds - delay, clip.DurationSeconds - delay, SlideSeconds(clip)));
    }

    public static double HorizontalOffset(TimelineClip clip, double localSeconds, double canvasWidth)
    {
        if (clip.TemplateId == "title.crawl")
        {
            double delay = DelaySeconds(clip);
            double progress = Ease(clip, Progress(localSeconds - delay, clip.DurationSeconds - delay, ScrollSeconds(clip)));
            return (ScrollDirection(clip) == "Left" ? 1 : -1) * canvasWidth * ScrollDistance(clip) * (.5 - progress);
        }
        if (!HasSlide(clip)) return 0;
        double travel = SlideDistance(clip) * canvasWidth * (1 - SlideProgress(clip, localSeconds));
        return SlideFrom(clip) switch { "Left" => -travel, "Right" => travel, _ => 0 };
    }

    public static double VerticalOffset(TimelineClip clip, double localSeconds, double canvasHeight)
    {
        if (!HasSlide(clip)) return 0;
        double travel = SlideDistance(clip) * canvasHeight * (1 - SlideProgress(clip, localSeconds));
        return SlideFrom(clip) switch { "Top" => -travel, "Bottom" => travel, _ => 0 };
    }

    public static double ScaleMultiplier(TimelineClip clip, double localSeconds)
    {
        double delay = DelaySeconds(clip);
        if (clip.TemplateId == "title.zoom")
        {
            double zoomProgress = Ease(clip, Progress(localSeconds - delay, clip.DurationSeconds - delay, ZoomSeconds(clip)));
            return ZoomStartScale(clip) + (ZoomEndScale(clip) - ZoomStartScale(clip)) * zoomProgress;
        }
        if (!HasPop(clip)) return 1;
        double progress = Progress(localSeconds - delay, clip.DurationSeconds - delay, PopSeconds(clip));
        if (progress >= 1) return 1;
        double start = PopStartScale(clip);
        // The overshoot gives the title a clear entrance. The start scale is
        // kept above zero, so this never produces a nonpositive scale or moves
        // the sequence-center pivot.
        return start + (1 - start) * Ease(clip, progress) + PopOvershoot(clip) * Math.Sin(Math.PI * progress);
    }

    public static double CreditsOffset(TimelineClip clip, double localSeconds, double canvasHeight)
    {
        if (clip.TemplateId != "title.credits") return 0;
        double speed = clip.Text?.Motion?.CreditsSpeed is double value && double.IsFinite(value)
            ? Math.Clamp(value, .05, 4) : .45;
        double progress = Math.Clamp(localSeconds / Math.Max(.05, clip.DurationSeconds), 0, 1);
        // Credits are already bottom-aligned. A whole extra canvas height
        // kept the default title below the visible frame for the entire clip.
        return canvasHeight * (.1 - progress * speed);
    }

    public static string FfmpegCreditsOffset(TimelineClip clip, int canvasHeight, string clock)
    {
        if (clip.TemplateId != "title.credits") return string.Empty;
        double speed = clip.Text?.Motion?.CreditsSpeed is double value && double.IsFinite(value)
            ? Math.Clamp(value, .05, 4) : .45;
        return $"{Number(canvasHeight * .1)}-{Number(canvasHeight * speed)}*clip(({clock})/{Number(Math.Max(.05, clip.DurationSeconds))},0,1)";
    }

    // ---- Export motion (the same curves as FFmpeg expressions) -------------

    public static string FfmpegFade(TimelineClip clip)
    {
        if (!HasFade(clip)) return string.Empty;
        (double fadeIn, double fadeOut) = FadeSeconds(clip);
        var filters = new StringBuilder();
        if (fadeIn > .000001)
            filters.Append($",fade=t=in:st=0:d={Number(fadeIn)}:alpha=1");
        if (fadeOut > .000001)
            filters.Append($",fade=t=out:st={Number(clip.DurationSeconds - fadeOut)}:d={Number(fadeOut)}:alpha=1");
        return filters.ToString();
    }

    public static string FfmpegHorizontalOffset(TimelineClip clip, int canvasWidth, string clock)
    {
        string from = SlideFrom(clip);
        if (clip.TemplateId == "title.crawl")
        {
            double delay = DelaySeconds(clip);
            string progress = FfmpegEase(clip, Fraction(clock, delay, Math.Min(ScrollSeconds(clip), clip.DurationSeconds - delay)));
            return $"{Number((ScrollDirection(clip) == "Left" ? 1 : -1) * canvasWidth * ScrollDistance(clip))}*(0.5-{progress})";
        }
        if (!HasSlide(clip) || from is not ("Left" or "Right")) return string.Empty;
        return $"{(from == "Left" ? "-" : string.Empty)}{Number(SlideDistance(clip) * canvasWidth)}*(1-{FfmpegSlideEase(clip, clock)})";
    }

    public static string FfmpegVerticalOffset(TimelineClip clip, int canvasHeight, string clock)
    {
        string from = SlideFrom(clip);
        if (!HasSlide(clip) || from is not ("Top" or "Bottom")) return string.Empty;
        return $"{(from == "Top" ? "-" : string.Empty)}{Number(SlideDistance(clip) * canvasHeight)}*(1-{FfmpegSlideEase(clip, clock)})";
    }

    private static string FfmpegSlideEase(TimelineClip clip, string clock)
    {
        double delay = DelaySeconds(clip);
        double span = Math.Max(.001, Math.Min(SlideSeconds(clip), clip.DurationSeconds - delay));
        return FfmpegEase(clip, Fraction(clock, delay, span));
    }

    public static string FfmpegScale(TimelineClip clip, string clock)
    {
        double delay = DelaySeconds(clip);
        if (clip.TemplateId == "title.zoom")
        {
            string zoomProgress = FfmpegEase(clip, Fraction(clock, delay, Math.Min(ZoomSeconds(clip), clip.DurationSeconds - delay)));
            return $"({Number(ZoomStartScale(clip))}+{Number(ZoomEndScale(clip) - ZoomStartScale(clip))}*{zoomProgress})";
        }
        if (!HasPop(clip)) return string.Empty;
        double span = Math.Max(.001, Math.Min(PopSeconds(clip), clip.DurationSeconds - delay));
        string elapsed = delay > .000001 ? $"(({clock})-{Number(delay)})" : $"({clock})";
        string progress = $"clip({elapsed}/{Number(span)},0,1)";
        string eased = FfmpegEase(clip, progress);
        double start = PopStartScale(clip);
        return $"({Number(start)}+{Number(1 - start)}*{eased}+{Number(PopOvershoot(clip))}*sin(PI*{progress}))";
    }

    private static string Fraction(string clock, double delay, double span) =>
        $"clip((({clock})-{Number(delay)})/{Number(Math.Max(.001, span))},0,1)";

    private static double Ease(TimelineClip clip, double progress) => Easing(clip) switch
    {
        "Linear" => progress,
        "EaseIn" => progress * progress,
        "EaseOut" => 1 - (1 - progress) * (1 - progress),
        _ => Smooth(progress)
    };

    private static string FfmpegEase(TimelineClip clip, string fraction) => Easing(clip) switch
    {
        "Linear" => $"({fraction})",
        "EaseIn" => $"({fraction}*{fraction})",
        "EaseOut" => $"(1-(1-{fraction})*(1-{fraction}))",
        _ => $"({fraction}*{fraction}*(3-2*{fraction}))"
    };

    private static double Progress(double localSeconds, double availableSeconds, double actionSeconds)
    {
        double span = Math.Min(actionSeconds, availableSeconds);
        return !double.IsFinite(span) || span <= 0 || !double.IsFinite(localSeconds)
            ? 1 : Math.Clamp(localSeconds / span, 0, 1);
    }

    private static double Smooth(double value) => value * value * (3 - 2 * value);

    private static int DecimalPlaces(string value)
    {
        int period = value.IndexOf('.');
        return period < 0 ? 0 : Math.Clamp(value.Length - period - 1, 0, 3);
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
