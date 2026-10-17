using System.IO;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum GraphicTemplateStageCode
{
    Validate,
    Read,
    Write
}

public sealed class GraphicTemplateException(GraphicTemplateStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public GraphicTemplateStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// A portable, local template stores editable title properties and animation
/// without referencing another application. Clip IDs and timeline placement
/// are intentionally excluded so every insertion is an independent instance.
/// </summary>
public sealed class GraphicTemplateDocument
{
    public const string Extension = ".tgt";
    private const long MaximumTemplateBytes = 32L * 1024 * 1024;
    public string Format { get; set; } = "TANGERINE-GraphicTemplate";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "图形模板";
    public ClipText Text { get; set; } = new();
    public ClipTransform Transform { get; set; } = new();
    public double DurationSeconds { get; set; } = 5;
    public double Opacity { get; set; } = 1;
    public List<ClipKeyframe> Keyframes { get; set; } = [];
    public List<ClipEffect> Effects { get; set; } = [];
    public List<ClipTransition> Transitions { get; set; } = [];
    public GraphicShape? Shape { get; set; }

    public static GraphicTemplateDocument FromClip(TimelineClip clip) => new()
    {
        Name = clip.Name,
        Text = DeepCopy(clip.Text ?? new ClipText { Content = clip.Name }),
        Transform = DeepCopy(clip.Transform),
        DurationSeconds = clip.DurationSeconds,
        Opacity = clip.Opacity,
        Keyframes = DeepCopy(clip.Keyframes),
        Effects = DeepCopy(clip.Effects),
        Transitions = DeepCopy(clip.Transitions),
        Shape = GraphicShape.FromClip(clip)
    };

    public TimelineClip Instantiate(double startSeconds)
    {
        Validate();
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            startSeconds + DurationSeconds > 24 * 3600)
            throw new GraphicTemplateException(GraphicTemplateStageCode.Validate,
                "图形模板插入位置无效。");
        var clip = new TimelineClip
        {
            Name = Name,
            Kind = ClipKind.Text,
            StartSeconds = startSeconds,
            DurationSeconds = DurationSeconds,
            Text = DeepCopy(Text),
            Transform = DeepCopy(Transform),
            Opacity = Opacity,
            Keyframes = DeepCopy(Keyframes).Select(key => { key.Id = Guid.NewGuid().ToString("N"); return key; }).ToList(),
            Effects = DeepCopy(Effects),
            Transitions = DeepCopy(Transitions).Select(transition => { transition.Id = Guid.NewGuid().ToString("N"); return transition; }).ToList()
        };
        // Effect keyframe properties encode their owning effect ID. Remap
        // those property prefixes together with IDs so animation survives
        // every insertion of this template into a new sequence.
        for (int index = 0; index < clip.Effects.Count; index++)
        {
            string formerId = clip.Effects[index].Id;
            string replacementId = Guid.NewGuid().ToString("N");
            clip.Effects[index].Id = replacementId;
            foreach (ClipKeyframe key in clip.Keyframes.Where(key =>
                key.Property.StartsWith($"Effect:{formerId}:", StringComparison.Ordinal)))
                key.Property = $"Effect:{replacementId}:" + key.Property[("Effect:".Length + formerId.Length + 1)..];
        }
        try { Shape?.Save(clip); }
        catch (GraphicShapeException exception)
        {
            throw new GraphicTemplateException(GraphicTemplateStageCode.Validate, "图形模板中的形状无效。", exception);
        }
        return clip;
    }

    public void Validate()
    {
        if (Format != "TANGERINE-GraphicTemplate" || Version != 1 ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 512 ||
            Text is null || Transform is null ||
            !double.IsFinite(DurationSeconds) || DurationSeconds <= 0 || DurationSeconds > 24 * 3600 ||
            !double.IsFinite(Opacity) || Opacity < 0 || Opacity > 1 ||
            Keyframes is null || Keyframes.Count > 10000 ||
            Effects is null || Effects.Count > 256 ||
            Transitions is null || Transitions.Count > 256 ||
            string.IsNullOrWhiteSpace(Text.FontFamily) || Text.FontFamily.Length > 256 ||
            Text.Content is null || Text.Content.Length > 100000 ||
            !double.IsFinite(Text.FontSize) || Text.FontSize is < 1 or > 1000 ||
            !double.IsFinite(Text.LetterSpacing) || Text.LetterSpacing is < -100 or > 300 ||
            !double.IsFinite(Text.LineHeight) || Text.LineHeight is < .5 or > 5 ||
            Keyframes.Any(key => key is null || string.IsNullOrWhiteSpace(key.Property) ||
                !double.IsFinite(key.TimeSeconds) || key.TimeSeconds < 0 ||
                key.TimeSeconds > DurationSeconds || !double.IsFinite(key.Value)))
            throw new GraphicTemplateException(GraphicTemplateStageCode.Validate, "图形模板数据无效或版本不受支持。");
        if (Shape is not null)
        {
            try { Shape.Validate(); }
            catch (GraphicShapeException exception)
            {
                throw new GraphicTemplateException(GraphicTemplateStageCode.Validate, "图形模板中的形状无效。", exception);
            }
        }
    }

    public static async Task<GraphicTemplateDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumTemplateBytes)
                    throw new GraphicTemplateException(GraphicTemplateStageCode.Validate,
                        "图形模板超过 32 MB，无法导入。");
                GraphicTemplateDocument document =
                    await JsonSerializer.DeserializeAsync<GraphicTemplateDocument>(stream,
                        cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new GraphicTemplateException(GraphicTemplateStageCode.Validate,
                        "图形模板为空。");
                document.Validate();
                return document;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphicTemplateException) { throw; }
        catch (JsonException exception)
        {
            throw new GraphicTemplateException(GraphicTemplateStageCode.Validate, "图形模板格式无效。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new GraphicTemplateException(GraphicTemplateStageCode.Read, "无法读取图形模板。", exception);
        }
    }

    public async Task WriteAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await Task.Run(async () =>
            {
                Validate();
                string json = JsonSerializer.Serialize(this);
                if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumTemplateBytes)
                    throw new GraphicTemplateException(GraphicTemplateStageCode.Validate,
                        "图形模板超过 32 MB，无法保存。");
                await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new GraphicTemplateException(GraphicTemplateStageCode.Write, "无法保存图形模板。", exception);
        }
    }

    private static T DeepCopy<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;
}
