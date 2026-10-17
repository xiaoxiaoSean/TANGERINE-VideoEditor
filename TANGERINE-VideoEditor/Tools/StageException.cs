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
        StageCode = stageCode;
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
    public static string Of(string component, int detail) =>
        component.Length == 5 ? $"{component.ToUpperInvariant()}{Math.Clamp(detail, 0, 9999):0000}"
            : $"UNKWN{Math.Clamp(detail, 0, 9999):0000}";

    // Component identifiers. Five letters each, so the regex in
    // MessageTipGenerator accepts them and the category map can group them.
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

    /// <summary>
    /// Resolves the stage code for any exception the editor surfaces. Domain
    /// exceptions keep their own stage identity; everything else falls back to
    /// the boundary's own code so the dialog still names a real stage.
    /// </summary>
    public static string Resolve(Exception exception, string fallbackStageCode) => exception switch
    {
        StageException staged => staged.StageCode,
        Project.ProjectFileException project => Of(ProjectFile, (int)project.StageCode + 1),
        Editor.ProjectExchangeException exchange => Of(Exchange, (int)exchange.StageCode + 1),
        Editor.ExportException export => Of(Export, (int)export.StageCode + 1),
        Editor.AudioOperationException audio => Of(Audio, (int)audio.StageCode + 1),
        Editor.GraphicShapeException graphic => Of(Graphic, (int)graphic.StageCode + 1),
        _ => fallbackStageCode
    };
}
