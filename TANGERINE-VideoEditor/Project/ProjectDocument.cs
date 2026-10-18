using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TANGERINE_VideoEditor.Project;

/// <summary>
/// Describes an editable Tangerine project. Times are measured in seconds.
/// </summary>
public sealed class ProjectDocument
{
    public const string FileExtension = ".tvep";
    public const string FormatIdentifier = "TANGERINE-VideoEditor";
    public const int CurrentVersion = 2;

    public string Format { get; set; } = FormatIdentifier;
    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "未命名项目";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ProjectSequence Sequence { get; set; } = new();
    /// <summary>Stores every sequence other than the one currently open in the editor.</summary>
    public List<ProjectSequenceWorkspace> Sequences { get; set; } = [];
    /// <summary>Hierarchical project bins do not change any source file.</summary>
    public List<ProjectBin> Bins { get; set; } = [];
    public List<MediaAsset> Media { get; set; } = [];
    /// <summary>Reusable source ranges saved inside the project file.</summary>
    public List<ProjectSubclip> Subclips { get; set; } = [];
    /// <summary>Locally synchronized camera sources used by editable angle cuts.</summary>
    public List<MulticamGroup> MulticamGroups { get; set; } = [];
    public List<TimelineTrack> Tracks { get; set; } = [];
    public List<TimelineMarker> Markers { get; set; } = [];

    /// <summary>Finds a sequence regardless of which sequence is currently open.</summary>
    public ProjectSequenceWorkspace? FindSequence(string id) =>
        Sequence.Id == id ? new ProjectSequenceWorkspace { Sequence = Sequence, Tracks = Tracks, Markers = Markers } :
        Sequences.FirstOrDefault(item => item.Sequence.Id == id);

    /// <summary>
    /// Preserves fields added by later versions of the project format.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public static ProjectDocument CreateDefault() => new()
    {
        Tracks =
        [
            new TimelineTrack { Name = "视频 1", Kind = TrackKind.Video, Order = 0 },
            new TimelineTrack { Name = "音频 1", Kind = TrackKind.Audio, Order = 1 }
        ]
    };
}

/// <summary>A reusable source range. It never changes the parent media file.</summary>
public sealed class ProjectSubclip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "子片段";
    public string ParentAssetId { get; set; } = string.Empty;
    public double InSeconds { get; set; }
    public double OutSeconds { get; set; }
    public string Notes { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Defines the sequence canvas and timebase.
/// </summary>
public sealed class ProjectSequence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "主序列";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FramesPerSecond { get; set; } = 30;
    public int AudioSampleRate { get; set; } = 48000;
    public string WorkingColorSpace { get; set; } = "Rec709";
    public string OutputColorSpace { get; set; } = "Rec709";
    public string BackgroundColor { get; set; } = "#000000";
    public double PlayheadSeconds { get; set; }
    public double WorkAreaStartSeconds { get; set; }
    public double? WorkAreaEndSeconds { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// Keeps inactive sequence edits together while the active sequence remains in
/// the legacy top-level fields for compatibility with existing projects.
/// </summary>
public sealed class ProjectSequenceWorkspace
{
    public ProjectSequence Sequence { get; set; } = new();
    public List<TimelineTrack> Tracks { get; set; } = [];
    public List<TimelineMarker> Markers { get; set; } = [];
}

/// <summary>Project-only media grouping; parent IDs form an acyclic tree.</summary>
public sealed class ProjectBin
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新建素材箱";
    public string? ParentId { get; set; }
}

/// <summary>
/// Camera sources share a clip-local clock. A resulting timeline cut is an
/// ordinary video clip with MulticamGroupId and MulticamAngleId, so preview,
/// audio monitoring, and export all read the same selected source.
/// </summary>
public sealed class MulticamGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "多机位";
    public string SyncMethod { get; set; } = "Timeline";
    public double DurationSeconds { get; set; }
    public List<MulticamAngle> Angles { get; set; } = [];
}

/// <summary>One angle trimmed to the group's shared zero and duration.</summary>
public sealed class MulticamAngle
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public TimelineClip SourceClip { get; set; } = new();
}

public enum MediaKind
{
    Video,
    Audio,
    Image
}

/// <summary>
/// Source paths may be absolute or relative to the .tvep file.
/// Missing media is retained so an editor can offer relinking.
/// </summary>
public sealed class MediaAsset
{
    private readonly object _previewPathGate = new();
    private string? _checkedProxyPath;
    private bool _checkedProxyExists;
    private long _nextProxyCheckTicks;
    private long _proxyCheckGeneration;
    private bool _proxyCheckInFlight;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public MediaKind Kind { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? FramesPerSecond { get; set; }
    public bool HasAudio { get; set; }
    public string? BinId { get; set; }
    public List<string> Tags { get; set; } = [];
    public string LabelColor { get; set; } = "#75AEEB";
    public string Description { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = [];
    public double SourceInSeconds { get; set; }
    public double? SourceOutSeconds { get; set; }
    public List<TimelineMarker> Markers { get; set; } = [];
    public string? ProxyPath { get; set; }
    public bool UseProxy { get; set; }
    public string InputColorSpace { get; set; } = "Auto";
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTimeOffset? FileModifiedUtc { get; set; }
    /// <summary>
    /// Preview can use a proxy while export keeps the source. The first proxy
    /// check and subsequent refreshes happen on a worker so playback and
    /// scrubbing never wait for a slow filesystem or network share.
    /// </summary>
    [JsonIgnore]
    public string EffectivePreviewPath
    {
        get
        {
            string? proxy = ProxyPath;
            if (!UseProxy || string.IsNullOrWhiteSpace(proxy)) return SourcePath;
            lock (_previewPathGate)
            {
                long now = Environment.TickCount64;
                if (_checkedProxyPath != proxy)
                {
                    _checkedProxyPath = proxy;
                    _checkedProxyExists = false;
                    _nextProxyCheckTicks = 0;
                    _proxyCheckGeneration++;
                    _proxyCheckInFlight = false;
                }
                if (!_proxyCheckInFlight && now >= _nextProxyCheckTicks)
                {
                    _nextProxyCheckTicks = now + 3000;
                    _proxyCheckInFlight = true;
                    string requestedPath = proxy;
                    long requestedGeneration = _proxyCheckGeneration;
                    _ = Task.Run(() =>
                    {
                        bool available = File.Exists(requestedPath);
                        lock (_previewPathGate)
                        {
                            if (_checkedProxyPath == requestedPath &&
                                _proxyCheckGeneration == requestedGeneration)
                            {
                                _checkedProxyExists = available;
                                _nextProxyCheckTicks = Environment.TickCount64 + 3000;
                                _proxyCheckInFlight = false;
                            }
                        }
                    });
                }
                return _checkedProxyExists ? proxy : SourcePath;
            }
        }
    }
    [JsonIgnore]
    public string EffectiveExportPath => SourcePath;

    /// <summary>Refresh proxy availability after an explicit attach or relink.</summary>
    internal void SetProxyAvailability(bool available)
    {
        lock (_previewPathGate)
        {
            _checkedProxyPath = ProxyPath;
            _checkedProxyExists = available;
            _nextProxyCheckTicks = Environment.TickCount64 + 3000;
            _proxyCheckGeneration++;
            _proxyCheckInFlight = false;
        }
    }

    /// <summary>Read the last filesystem snapshot without probing a disk.</summary>
    [JsonIgnore]
    internal bool CachedProxyAvailable
    {
        get
        {
            lock (_previewPathGate)
                return _checkedProxyPath == ProxyPath && _checkedProxyExists;
        }
    }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// The editor has two kinds of track, video and audio. Text, subtitles and
/// shapes live on video tracks. Overlay and Caption remain only so older
/// project files still deserialize; loading turns both into Video.
/// </summary>
public enum TrackKind
{
    Video,
    Audio,
    Overlay,
    Caption
}

public sealed class TimelineTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public TrackKind Kind { get; set; } = TrackKind.Video;
    public int Order { get; set; }
    public bool IsLocked { get; set; }
    public bool IsHidden { get; set; }
    public bool IsMuted { get; set; }
    public bool IsSyncLocked { get; set; } = true;
    /// <summary>When enabled, clips on this track close gaps and snap during edits.</summary>
    public bool IsSnapping { get; set; } = true;
    public List<TimelineClip> Clips { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public enum ClipKind
{
    Video,
    Audio,
    Image,
    Text,
    Color,
    Adjustment,
    Sequence
}

/// <summary>
/// A clip is positioned on a track and can hold media, generated content,
/// effects, keyframes, and transitions independently.
/// </summary>
public sealed class TimelineClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ClipKind Kind { get; set; }
    public string? TemplateId { get; set; }
    public string? MediaAssetId { get; set; }
    public string? NestedSequenceId { get; set; }
    public string? MulticamGroupId { get; set; }
    public string? MulticamAngleId { get; set; }
    public double MulticamLocalStartSeconds { get; set; }
    // Optional associations keep older .tvep projects readable: clips without
    // these values remain independent, while newer projects can move linked
    // audio and grouped clips together.
    public string? GroupId { get; set; }
    public string? LinkGroupId { get; set; }
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; } = 5;
    public double SourceInSeconds { get; set; }
    public double Speed { get; set; } = 1;
    /// <summary>Records that speed settings were explicitly applied so the
    /// timeline can keep a direct shortcut visible even at 1x.</summary>
    public bool SpeedAdjustmentApplied { get; set; }
    public double Volume { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public string BlendMode { get; set; } = "Normal";
    public ClipMask? Mask { get; set; }
    public bool IsMuted { get; set; }
    public bool IsReversed { get; set; }
    /// <summary>Clip-relative speed knots. The local interval uses the left knot speed.</summary>
    public List<ClipTimeRemapPoint> TimeRemapPoints { get; set; } = [];
    /// <summary>Sampling, Blend, or OpticalFlow for retimed video.</summary>
    public string FrameInterpolation { get; set; } = "Sampling";
    public string? Color { get; set; }
    public ClipTransform Transform { get; set; } = new();
    public ClipText? Text { get; set; }
    public List<ClipEffect> Effects { get; set; } = [];
    public List<ClipTransition> Transitions { get; set; } = [];
    public List<ClipKeyframe> Keyframes { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class ClipTimeRemapPoint
{
    public double TimeSeconds { get; set; }
    public double Speed { get; set; } = 1;
}

/// <summary>Normalized mask geometry for non-destructive visual effects.</summary>
public sealed class ClipMask
{
    public string Shape { get; set; } = "Rectangle";
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public double Width { get; set; } = 0.5;
    public double Height { get; set; } = 0.5;
    public double Feather { get; set; }
    public bool Invert { get; set; }
    /// <summary>Closed pen path in source-image normalized coordinates.</summary>
    public List<MaskPoint> Points { get; set; } = [];
}

/// <summary>One editable vertex of a clip mask's closed freeform path.</summary>
public sealed class MaskPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ClipTransform
{
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double ScaleX { get; set; } = 1;
    public double ScaleY { get; set; } = 1;
    public double RotationDegrees { get; set; }
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class ClipText
{
    public string Content { get; set; } = "在预览区双击编辑文字";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 72;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string ForegroundColor { get; set; } = "#FFFFFF";
    public string? BackgroundColor { get; set; }
    /// <summary>Sequence-pixel padding around the glyph bounds; independent from alignment.</summary>
    public double BackgroundPaddingX { get; set; }
    public double BackgroundPaddingY { get; set; }
    public double BackgroundCornerRadius { get; set; }
    public double BackgroundOpacity { get; set; } = 1;
    /// <summary>Safe-area inset as a fraction of the sequence, applied to edge-aligned titles.</summary>
    public double HorizontalInset { get; set; }
    public double VerticalInset { get; set; }
    public bool OutlineEnabled { get; set; }
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 2;
    public bool ShadowEnabled { get; set; }
    public string ShadowColor { get; set; } = "#000000";
    public double ShadowBlur { get; set; } = 8;
    public double ShadowOpacity { get; set; } = 0.75;
    public double ShadowOffsetX { get; set; } = 4;
    public double ShadowOffsetY { get; set; } = 4;
    public string GlowColor { get; set; } = "#53C5EC";
    public double GlowRadius { get; set; } = 24;
    public double GlowOpacity { get; set; } = .85;
    public double LetterSpacing { get; set; }
    public double LineHeight { get; set; } = 1.2;
    public string HorizontalAlignment { get; set; } = "Center";
    public string VerticalAlignment { get; set; } = "Center";
    /// <summary>
    /// Tuning for an animated title preset (typewriter, fade, slide, pop,
    /// counter). Null means every value keeps its built-in default.
    /// </summary>
    public TitleMotion? Motion { get; set; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// User-adjustable parameters of the animated title presets. Every value is
/// nullable on purpose: unset means "use the built-in default", which is also
/// the value projects saved before these settings existed were rendered with,
/// so opening an older project changes nothing. Seconds are clip-local.
/// </summary>
public sealed class TitleMotion
{
    /// <summary>Wait before the motion starts (typewriter, counter, slide, pop).</summary>
    public double? DelaySeconds { get; set; }

    /// <summary>Typewriter: time each character takes to appear.</summary>
    public double? SecondsPerCharacter { get; set; }

    /// <summary>Counter: time to count from the first number to the last.</summary>
    public double? CountSeconds { get; set; }
    /// <summary>Counter: digits after the decimal point; unset follows the typed numbers.</summary>
    public int? Decimals { get; set; }

    /// <summary>Fade: length of the fade-in and the fade-out.</summary>
    public double? FadeInSeconds { get; set; }
    public double? FadeOutSeconds { get; set; }

    /// <summary>Slide: how long it takes, how far it travels, and where from.</summary>
    public double? SlideSeconds { get; set; }
    /// <summary>Fraction of the canvas width (left/right) or height (top/bottom).</summary>
    public double? SlideDistance { get; set; }
    /// <summary>Left, Right, Top or Bottom.</summary>
    public string? SlideFrom { get; set; }

    /// <summary>Pop: duration, the scale it starts from, and how far it overshoots.</summary>
    public double? PopSeconds { get; set; }
    public double? PopStartScale { get; set; }
    public double? PopOvershoot { get; set; }

    /// <summary>Named interpolation shared by numeric preview and FFmpeg expressions.</summary>
    public string? Easing { get; set; }
    /// <summary>Continuous horizontal crawl: duration, canvas-width travel and direction.</summary>
    public double? ScrollSeconds { get; set; }
    public double? ScrollDistance { get; set; }
    public string? ScrollDirection { get; set; }
    /// <summary>Continuous zoom: duration and independent start/end scale factors.</summary>
    public double? ZoomSeconds { get; set; }
    public double? ZoomStartScale { get; set; }
    public double? ZoomEndScale { get; set; }

    /// <summary>Credits: normalized scroll speed (screen heights per clip).</summary>
    public double? CreditsSpeed { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class ClipEffect
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PresetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class ClipTransition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PresetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Edge { get; set; } = "In";
    public double DurationSeconds { get; set; } = 0.5;
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class ClipKeyframe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Property { get; set; } = string.Empty;
    public double TimeSeconds { get; set; }
    public double Value { get; set; }
    public string Interpolation { get; set; } = "Linear";
    /// <summary>Outgoing Bezier control: normalized time and value fractions.</summary>
    public double BezierOutTime { get; set; } = 1d / 3;
    public double BezierOutValue { get; set; } = 0;
    /// <summary>Incoming Bezier control for the span ending at this keyframe.</summary>
    public double BezierInTime { get; set; } = 2d / 3;
    public double BezierInValue { get; set; } = 1;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class TimelineMarker
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "标记";
    public string Notes { get; set; } = string.Empty;
    public double TimeSeconds { get; set; }
    public string Color { get; set; } = "#FFB347";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
