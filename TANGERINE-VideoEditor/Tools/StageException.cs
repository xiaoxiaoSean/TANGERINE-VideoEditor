using System.Globalization;

namespace TANGERINE_VideoEditor.Tools;

/// <summary>
/// Carries a stable, language-neutral stage code alongside a localized
/// message. The stage code identifies the failing boundary so a report stays
/// comparable across UI languages and operating systems.
/// Codes use five uppercase letters plus four digits, for example PRJIO0001.
/// </summary>
public sealed class StageException : Exception
{
    public StageException(string stageCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StageCode = StageCodes.Normalize(stageCode);
    }

    /// <summary>Five letters identify the component; four digits the detail.</summary>
    public string StageCode { get; }
}

/// <summary>
/// Projects the editor's existing per-domain stage code enumerations onto the
/// shared five-letter component namespace. Keeping one canonical formatter
/// means a dialog never has to know which subsystem produced a failure.
/// </summary>
public static class StageCodes
{
    /// <summary>Builds a canonical code from a component and a detail number.</summary>
    public static string Of(string? component, int detail)
    {
        bool validComponent = component is { Length: 5 } && component.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        string prefix = validComponent ? component!.ToUpperInvariant() : "UNKWN";
        return prefix + Math.Clamp(detail, 0, 9999).ToString("0000", CultureInfo.InvariantCulture);
    }

    public const string Unknown = "UNKWN0001";

    public static bool IsValid(string? code) => code is { Length: 9 } &&
        code.Take(5).All(character => character is >= 'A' and <= 'Z') &&
        code.Skip(5).All(character => character is >= '0' and <= '9');

    public static string Normalize(string? code) => IsValid(code) ? code! : Unknown;

    // Component identifiers contain five ASCII letters; diagnostic hints
    // group related operations by these language-independent prefixes.
    public const string ProjectFile = "PRJIO";
    public const string ProjectMedia = "PMEDA";
    public const string Exchange = "EXCHG";
    public const string Export = "EXPRT";
    public const string Audio = "AUDIO";
    public const string Graphic = "GRAPH";
    public const string Preview = "PRVEW";
    public const string Timeline = "TMLNE";
    public const string Workspace = "WKSPC";
    public const string Keyframe = "KEYFR";
    public const string Effect = "EFECT";
    public const string Subtitle = "SUBTL";
    public const string ClipFrame = "CLPFR";
    public const string GraphicTemplate = "GTMPL";
    public const string ExportDialog = "EXPDL";
    public const string ImageSequence = "IMGSQ";
    public const string PaperEdit = "PEDIT";
    public const string StillFrame = "STLFR";
    public const string Multicam = "MLCAM";
    public const string Program = "PROGM";
    public const string McpJson = "MCPJS";

    /// <summary>
    /// Resolves the stage code for any exception the editor surfaces. Domain
    /// exceptions keep their own stage identity; everything else falls back to
    /// the boundary's own code so the dialog still names a real stage.
    /// </summary>
    public static string Resolve(Exception exception, string fallbackStageCode)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (Exception current in Enumerate(exception))
        {
            string? known = ResolveKnown(current);
            if (known is not null) return known;
        }
        return Normalize(fallbackStageCode);
    }

    /// <summary>Walks framework and aggregate wrappers without parsing localized messages.</summary>
    internal static IEnumerable<Exception> Enumerate(Exception exception)
    {
        var pending = new Queue<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue(exception);
        while (pending.TryDequeue(out Exception? current))
        {
            if (!visited.Add(current)) continue;
            yield return current;
            if (current is AggregateException aggregate)
                foreach (Exception child in aggregate.InnerExceptions) pending.Enqueue(child);
            else if (current.InnerException is Exception inner) pending.Enqueue(inner);
        }
    }

    // Explicit member mappings keep identifiers stable if enum declarations
    // later insert or reorder values. Unknown members stay within their domain.
    private static string? ResolveKnown(Exception exception) => exception switch
    {
        StageException staged => Normalize(staged.StageCode),
        Project.ProjectFileException project => Of(ProjectFile, project.StageCode switch
        {
            Project.ProjectStageCode.InvalidPath => 1, Project.ProjectStageCode.Validate => 2,
            Project.ProjectStageCode.UnsupportedVersion => 3, Project.ProjectStageCode.Migrate => 4,
            Project.ProjectStageCode.Read => 5, Project.ProjectStageCode.Parse => 6,
            Project.ProjectStageCode.Write => 7, Project.ProjectStageCode.Replace => 8,
            Project.ProjectStageCode.Space => 9, _ => 9999
        }),
        Editor.ProjectExchangeException exchange => Of(Exchange, exchange.StageCode switch
        {
            Editor.ProjectExchangeStageCode.Validate => 1, Editor.ProjectExchangeStageCode.Read => 2,
            Editor.ProjectExchangeStageCode.Parse => 3, Editor.ProjectExchangeStageCode.Reference => 4,
            Editor.ProjectExchangeStageCode.Timecode => 5, Editor.ProjectExchangeStageCode.Write => 6, _ => 9999
        }),
        Editor.ExportException export => Of(Export, export.StageCode switch
        {
            Editor.ExportStageCode.Validate => 1, Editor.ExportStageCode.Prepare => 2,
            Editor.ExportStageCode.EncoderMissing => 3, Editor.ExportStageCode.Encode => 4,
            Editor.ExportStageCode.Finalize => 5, _ => 9999
        }),
        Editor.AudioOperationException audio => Of(Audio, audio.StageCode switch
        {
            Editor.AudioStageCode.Validate => 1, Editor.AudioStageCode.Read => 2,
            Editor.AudioStageCode.Write => 3, Editor.AudioStageCode.Capture => 4,
            Editor.AudioStageCode.Process => 5, _ => 9999
        }),
        Editor.GraphicShapeException graphic => Of(Graphic, graphic.StageCode switch
        {
            Editor.GraphicShapeStageCode.Validate => 1, Editor.GraphicShapeStageCode.Read => 2, _ => 9999
        }),
        Editor.ClipFrameException frame => Of(ClipFrame, frame.StageCode switch
        {
            Editor.ClipFrameStageCode.Validate => 1, Editor.ClipFrameStageCode.EncoderMissing => 2,
            Editor.ClipFrameStageCode.Decode => 3, Editor.ClipFrameStageCode.Filter => 4, _ => 9999
        }),
        Editor.GraphicTemplateException template => Of(GraphicTemplate, template.StageCode switch
        {
            Editor.GraphicTemplateStageCode.Validate => 1, Editor.GraphicTemplateStageCode.Read => 2,
            Editor.GraphicTemplateStageCode.Write => 3, _ => 9999
        }),
        Editor.ExportDialogException dialog => Of(ExportDialog, dialog.StageCode switch
        {
            Editor.ExportDialogStageCode.Validate => 1, Editor.ExportDialogStageCode.PresetRead => 2,
            Editor.ExportDialogStageCode.PresetWrite => 3, _ => 9999
        }),
        Editor.ImageSequenceException sequence => Of(ImageSequence, sequence.StageCode switch
        {
            Editor.ImageSequenceStageCode.Validate => 1, Editor.ImageSequenceStageCode.Encode => 2, _ => 9999
        }),
        Editor.PaperEditException paper => Of(PaperEdit, paper.StageCode switch
        {
            Editor.PaperEditStageCode.Validate => 1, Editor.PaperEditStageCode.Read => 2, _ => 9999
        }),
        Editor.StillFrameException still => Of(StillFrame, still.StageCode switch
        {
            Editor.StillFrameStageCode.Validate => 1, Editor.StillFrameStageCode.Prepare => 2,
            Editor.StillFrameStageCode.EncoderMissing => 3, Editor.StillFrameStageCode.Render => 4,
            Editor.StillFrameStageCode.Write => 5, _ => 9999
        }),
        Editor.SubtitleException subtitle => Of(Subtitle, subtitle.StageCode switch
        {
            Editor.SubtitleStageCode.Validate => 1, Editor.SubtitleStageCode.Parse => 2,
            Editor.SubtitleStageCode.Read => 3, Editor.SubtitleStageCode.Write => 4, _ => 9999
        }),
        MulticamException multicam => Of(Multicam, multicam.StageCode switch
        {
            MulticamStageCode.Validate => 1, MulticamStageCode.Synchronize => 2,
            MulticamStageCode.Source => 3, MulticamStageCode.Edit => 4, _ => 9999
        }),
        global::Tangerine.VideoEditor.Automation.JsonModelException json => Of(McpJson, json.StageCode switch
        {
            global::Tangerine.VideoEditor.Automation.JsonModelStageCode.Validate => 1,
            global::Tangerine.VideoEditor.Automation.JsonModelStageCode.Patch => 2,
            global::Tangerine.VideoEditor.Automation.JsonModelStageCode.Deserialize => 3, _ => 9999
        }),
        _ => null
    };
}
