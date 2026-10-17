using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum ProjectExchangeStageCode { Validate, Read, Parse, Reference, Timecode, Write }

public sealed class ProjectExchangeException(ProjectExchangeStageCode stageCode, string message,
    Exception? inner = null) : Exception(message, inner)
{
    public ProjectExchangeStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Exchanges basic edit decisions without an external service. The XML format
/// preserves all Tangerine clip instructions, while CMX3600 EDL deliberately
/// carries only straight video cuts and source references.
/// </summary>
public static class ProjectExchangeService
{
    public static async Task ExportXmlAsync(ProjectDocument project, string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path, ".xml");
        try
        {
            ValidateImported(project);
            var root = new XElement("tangerineProjectExchange", new XAttribute("version", "1"),
                new XAttribute("name", project.Name));
        // Keep a complete copy for native round trips while leaving the
            // clip/track XML readable by simpler local interchange tools.
            // Encode the native data so arbitrary user text cannot break XML.
            var embedded = new XElement("nativeProject", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(project))));
            root.Add(embedded);
            var media = new XElement("media");
            foreach (MediaAsset asset in project.Media)
                media.Add(new XElement("asset", new XAttribute("id", asset.Id),
                    new XAttribute("name", asset.Name), new XAttribute("kind", asset.Kind),
                    new XAttribute("path", asset.SourcePath)));
            root.Add(media);
            var sequences = new XElement("sequences");
            foreach (ProjectSequenceWorkspace workspace in AllSequences(project))
            {
                ProjectSequence sequence = workspace.Sequence;
                var element = new XElement("sequence", new XAttribute("id", sequence.Id),
                    new XAttribute("name", sequence.Name), new XAttribute("width", sequence.Width),
                    new XAttribute("height", sequence.Height),
                    new XAttribute("fps", Number(sequence.FramesPerSecond)),
                    new XAttribute("sampleRate", sequence.AudioSampleRate));
                foreach (TimelineTrack track in workspace.Tracks.OrderBy(track => track.Order))
                {
                    var trackElement = new XElement("track", new XAttribute("id", track.Id),
                        new XAttribute("name", track.Name), new XAttribute("kind", track.Kind),
                        new XAttribute("order", track.Order), new XAttribute("hidden", track.IsHidden),
                        new XAttribute("muted", track.IsMuted));
                    foreach (TimelineClip clip in track.Clips.OrderBy(clip => clip.StartSeconds))
                    {
                        var clipElement = new XElement("clip", new XAttribute("id", clip.Id),
                            new XAttribute("name", clip.Name), new XAttribute("kind", clip.Kind),
                            new XAttribute("start", Number(clip.StartSeconds)),
                            new XAttribute("duration", Number(clip.DurationSeconds)),
                            new XAttribute("sourceIn", Number(clip.SourceInSeconds)),
                            new XAttribute("speed", Number(clip.Speed)),
                            new XAttribute("reversed", clip.IsReversed),
                            new XAttribute("frameInterpolation", clip.FrameInterpolation));
                        if (!string.IsNullOrWhiteSpace(clip.MediaAssetId))
                            clipElement.Add(new XAttribute("assetId", clip.MediaAssetId));
                        if (!string.IsNullOrWhiteSpace(clip.NestedSequenceId))
                            clipElement.Add(new XAttribute("nestedSequenceId", clip.NestedSequenceId));
                        if (!string.IsNullOrWhiteSpace(clip.MulticamGroupId))
                        {
                            clipElement.Add(new XAttribute("multicamGroupId", clip.MulticamGroupId));
                            clipElement.Add(new XAttribute("multicamAngleId", clip.MulticamAngleId ?? string.Empty));
                            clipElement.Add(new XAttribute("multicamLocalStart",
                                Number(clip.MulticamLocalStartSeconds)));
                        }
                        foreach (ClipTimeRemapPoint point in clip.TimeRemapPoints)
                            clipElement.Add(new XElement("speedPoint", new XAttribute("time", Number(point.TimeSeconds)),
                                new XAttribute("speed", Number(point.Speed))));
                        trackElement.Add(clipElement);
                    }
                    element.Add(trackElement);
                }
                sequences.Add(element);
            }
            root.Add(sequences);
            var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
            await WriteAtomicAsync(path, document.ToString(), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (ProjectExchangeException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "项目内容无法写入 XML 交换文件。", exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Write, "写入 XML 交换文件失败。", exception); }
    }

    public static async Task<ProjectDocument> ImportXmlAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path, ".xml");
        string xml;
        try { xml = await File.ReadAllTextAsync(path, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Read, "无法读取 XML 交换文件。", exception); }
        try
        {
            XDocument file = XDocument.Parse(xml);
            XElement root = file.Root ?? throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "缺少根节点。");
            if (root.Name != "tangerineProjectExchange" || (string?)root.Attribute("version") != "1")
                throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "XML 交换格式或版本不受支持。");
            // The human-readable edit list supports exchange with simpler
            // systems; a native payload additionally preserves effects, text,
            // keyframes and masks on a Tangerine-to-Tangerine round trip.
            string? native = (string?)root.Element("nativeProject");
            if (!string.IsNullOrWhiteSpace(native))
            {
                ProjectDocument complete = System.Text.Json.JsonSerializer.Deserialize<ProjectDocument>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(native)))
                    ?? throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "原生项目内容为空。");
                ValidateImported(complete);
                return complete;
            }
            var project = ProjectDocument.CreateDefault();
            project.Name = (string?)root.Attribute("name") ?? "导入项目";
            project.Media = root.Element("media")?.Elements("asset").Select(asset => new MediaAsset
            {
                Id = Required(asset, "id"), Name = Required(asset, "name"),
                Kind = Enum.Parse<MediaKind>(Required(asset, "kind")),
                SourcePath = Required(asset, "path")
            }).ToList() ?? [];
            var workspaces = new List<ProjectSequenceWorkspace>();
            foreach (XElement element in root.Element("sequences")?.Elements("sequence") ?? [])
            {
                var sequence = new ProjectSequence
                {
                    Id = Required(element, "id"), Name = Required(element, "name"),
                    Width = PositiveInt(element, "width"), Height = PositiveInt(element, "height"),
                    FramesPerSecond = PositiveDouble(element, "fps"),
                    AudioSampleRate = PositiveInt(element, "sampleRate")
                };
                var workspace = new ProjectSequenceWorkspace { Sequence = sequence };
                foreach (XElement trackElement in element.Elements("track"))
                {
                    var track = new TimelineTrack
                    {
                        Id = Required(trackElement, "id"), Name = Required(trackElement, "name"),
                        Kind = Enum.Parse<TrackKind>(Required(trackElement, "kind")),
                        Order = NonnegativeInt(trackElement, "order"),
                        IsHidden = Boolean(trackElement, "hidden"), IsMuted = Boolean(trackElement, "muted")
                    };
                    foreach (XElement clipElement in trackElement.Elements("clip"))
                    {
                        var clip = new TimelineClip
                        {
                            Id = Required(clipElement, "id"), Name = Required(clipElement, "name"),
                            Kind = Enum.Parse<ClipKind>(Required(clipElement, "kind")),
                            StartSeconds = NonnegativeDouble(clipElement, "start"),
                            DurationSeconds = PositiveDouble(clipElement, "duration"),
                            SourceInSeconds = NonnegativeDouble(clipElement, "sourceIn"),
                            Speed = PositiveDouble(clipElement, "speed"),
                            IsReversed = Boolean(clipElement, "reversed"),
                            FrameInterpolation = (string?)clipElement.Attribute("frameInterpolation") ?? "Sampling",
                            MediaAssetId = (string?)clipElement.Attribute("assetId"),
                            NestedSequenceId = (string?)clipElement.Attribute("nestedSequenceId")
                        };
                        clip.TimeRemapPoints = clipElement.Elements("speedPoint").Select(point =>
                            new ClipTimeRemapPoint { TimeSeconds = NonnegativeDouble(point, "time"),
                                Speed = PositiveDouble(point, "speed") }).ToList();
                        track.Clips.Add(clip);
                    }
                    workspace.Tracks.Add(track);
                }
                workspaces.Add(workspace);
            }
            if (workspaces.Count == 0)
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "交换文件没有序列。");
            project.Sequence = workspaces[0].Sequence;
            project.Tracks = workspaces[0].Tracks;
            project.Sequences = workspaces.Skip(1).ToList();
            ValidateImported(project);
            return project;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or FormatException or
            InvalidOperationException or OverflowException or ArgumentException or System.Text.Json.JsonException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "XML 交换文件内容无效。", exception); }
    }

    public static async Task ExportEdlAsync(ProjectDocument project, string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path, ".edl");
        if (!double.IsFinite(project.Sequence.FramesPerSecond) || project.Sequence.FramesPerSecond < 1 ||
            project.Sequence.FramesPerSecond > 120)
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "序列帧率无效，无法写入 EDL。");
        var lines = new List<string> { $"TITLE: {project.Sequence.Name}", "FCM: NON-DROP FRAME" };
        int fps = Math.Clamp((int)Math.Round(project.Sequence.FramesPerSecond), 1, 120);
        lines.Add($"* TANGERINE FPS: {fps}");
        if (project.Tracks.Count(track => track.Kind != TrackKind.Audio && !track.IsHidden &&
                track.Clips.Any(clip => clip.Kind == ClipKind.Video)) > 1 ||
            project.Tracks.Any(track => track.Kind == TrackKind.Audio && track.Clips.Count > 0))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                "CMX3600 EDL 只支持单视频轨直切序列；多轨或带音频的项目请使用 XML。");
        if (project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips).Any(clip => clip.Kind == ClipKind.Video &&
                (clip.IsReversed || clip.TimeRemapPoints.Count > 0 || Math.Abs(clip.Speed - 1) > .000001)))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                "CMX3600 EDL 无法准确表示倒放或变速片段；请使用 XML 交换文件。");
        if (Math.Abs(project.Sequence.FramesPerSecond - fps) > .000001)
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                "CMX3600 非丢帧 EDL 无法准确表示当前分数帧率；请使用 XML 交换文件。");
        if (project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips).Any(clip => clip.Kind != ClipKind.Video))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                "CMX3600 EDL 无法准确保存标题、图片、颜色片段或调整图层；请使用 XML 交换文件。");
        if (project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips).Any(clip => clip.Effects.Count > 0 ||
                clip.Transitions.Count > 0 || clip.Keyframes.Count > 0 || clip.Mask is not null ||
                clip.Opacity < .999 || clip.BlendMode != "Normal" ||
                clip.Transform.PositionX != 0 || clip.Transform.PositionY != 0 ||
                clip.Transform.ScaleX != 1 || clip.Transform.ScaleY != 1 ||
                clip.Transform.RotationDegrees != 0 || clip.Transform.CropLeft != 0 ||
                clip.Transform.CropRight != 0 || clip.Transform.CropTop != 0 ||
                clip.Transform.CropBottom != 0))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                "CMX3600 EDL 无法准确保存效果、转场或动画；请使用 XML 交换文件。");
        var videoClips = project.Tracks.Where(track => track.Kind != TrackKind.Audio && !track.IsHidden)
            .SelectMany(track => track.Clips).Where(clip => clip.Kind == ClipKind.Video)
            .OrderBy(clip => clip.StartSeconds).ToArray();
        if (videoClips.Length == 0)
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "序列没有可导出的 EDL 视频片段。");
        if (videoClips.Any(clip => !double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
            !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
            !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "EDL 视频片段的时间无效。");
        for (int clipIndex = 1; clipIndex < videoClips.Length; clipIndex++)
            if (videoClips[clipIndex].StartSeconds <
                videoClips[clipIndex - 1].StartSeconds + videoClips[clipIndex - 1].DurationSeconds - .000001)
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                    "CMX3600 EDL 无法准确保存重叠片段；请使用 XML 交换文件。");
        int index = 1;
        foreach (TimelineClip clip in videoClips)
        {
            MediaAsset? asset = project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
            if (asset is null)
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                    $"片段“{clip.Name}”缺少源素材，无法写入 EDL。");
            string reel = new string(Path.GetFileNameWithoutExtension(asset.Name).Where(character =>
                    (character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9'))
                .Take(8).ToArray()).ToUpperInvariant();
            if (reel.Length == 0) reel = "AX";
            double sourceEnd = clip.SourceInSeconds + ClipTimeMap.SourceOffset(clip, clip.DurationSeconds);
            lines.Add($"{index++:000}  {reel,-8} V     C        " +
                $"{Timecode(clip.SourceInSeconds, fps)} {Timecode(sourceEnd, fps)} " +
                $"{Timecode(clip.StartSeconds, fps)} {Timecode(clip.StartSeconds + clip.DurationSeconds, fps)}");
            lines.Add($"* FROM CLIP NAME: {asset.Name}");
            lines.Add($"* SOURCE FILE: {asset.SourcePath}");
        }
        try { await WriteAtomicAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine,
            cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Write, "写入 EDL 交换文件失败。", exception); }
    }

    public static async Task<ProjectDocument> ImportEdlAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path, ".edl");
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Read, "无法读取 EDL 文件。", exception); }
        try
        {
            var project = ProjectDocument.CreateDefault();
            project.Name = Path.GetFileNameWithoutExtension(path);
            string title = lines.FirstOrDefault(line => line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)) ?? "";
            project.Sequence.Name = title.Length > 6 ? title[6..].Trim() : project.Name;
            int fps = 30;
            string? fpsLine = lines.FirstOrDefault(line => line.StartsWith("* TANGERINE FPS:", StringComparison.Ordinal));
            if (fpsLine is not null && (!int.TryParse(fpsLine["* TANGERINE FPS:".Length..].Trim(),
                    out fps) || fps is < 1 or > 120))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "EDL 帧率无效。");
            project.Sequence.FramesPerSecond = fps;
            TimelineTrack track = project.Tracks.First(item => item.Kind == TrackKind.Video);
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string[] words = lines[lineIndex].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length < 7 || !int.TryParse(words[0], out _) || words[2] != "V") continue;
                double sourceIn = ParseTimecode(words[^4], fps);
                double sourceOut = ParseTimecode(words[^3], fps);
                double recordIn = ParseTimecode(words[^2], fps);
                double recordOut = ParseTimecode(words[^1], fps);
                if (sourceOut <= sourceIn || recordOut <= recordIn)
                    throw new ProjectExchangeException(ProjectExchangeStageCode.Timecode, "EDL 时间码区间无效。");
                string sourcePath = string.Empty;
                int annotation = lineIndex + 1;
                while (annotation < lines.Length && lines[annotation].StartsWith("*", StringComparison.Ordinal))
                {
                    if (lines[annotation].StartsWith("* SOURCE FILE:", StringComparison.Ordinal))
                        sourcePath = lines[annotation]["* SOURCE FILE:".Length..].Trim();
                    annotation++;
                }
                if (sourcePath.Length == 0) continue;
                var asset = new MediaAsset { Name = Path.GetFileName(sourcePath), SourcePath = sourcePath,
                    Kind = MediaKind.Video };
                project.Media.Add(asset);
                track.Clips.Add(new TimelineClip { Name = asset.Name, Kind = ClipKind.Video,
                    MediaAssetId = asset.Id, StartSeconds = recordIn, DurationSeconds = recordOut - recordIn,
                    SourceInSeconds = sourceIn, Speed = (sourceOut - sourceIn) / (recordOut - recordIn) });
            }
            ValidateImported(project);
            return project;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, "EDL 文件内容无效。", exception); }
    }

    private static IEnumerable<ProjectSequenceWorkspace> AllSequences(ProjectDocument project) =>
        new[] { new ProjectSequenceWorkspace { Sequence = project.Sequence, Tracks = project.Tracks,
            Markers = project.Markers } }.Concat(project.Sequences);

    private static async Task WriteAtomicAsync(string path, string contents, CancellationToken cancellationToken)
    {
        await Task.Run(async () =>
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "交换文件目录不存在。");
            string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, contents, new UTF8Encoding(false), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, fullPath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, cancellationToken);
    }

    private static void ValidateImported(ProjectDocument project)
    {
        var assetIds = project.Media.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        var sequenceIds = AllSequences(project).Select(workspace => workspace.Sequence.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (assetIds.Count != project.Media.Count || sequenceIds.Count != project.Sequences.Count + 1)
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "交换文件存在重复标识。");
        foreach (ProjectSequenceWorkspace workspace in AllSequences(project))
            if (workspace.Sequence.Width < 16 || workspace.Sequence.Height < 16 ||
                !double.IsFinite(workspace.Sequence.FramesPerSecond) ||
                workspace.Sequence.FramesPerSecond < 1 || workspace.Sequence.FramesPerSecond > 120 ||
                workspace.Sequence.AudioSampleRate is < 8000 or > 192000)
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "交换文件中的序列规格无效。");
        foreach (TimelineClip clip in AllSequences(project).SelectMany(workspace => workspace.Tracks)
            .SelectMany(track => track.Clips))
        {
            if (clip.MediaAssetId is { Length: > 0 } id && !assetIds.Contains(id))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Reference, "片段引用了不存在的素材。");
            if (clip.NestedSequenceId is { Length: > 0 } nested && !sequenceIds.Contains(nested))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Reference, "片段引用了不存在的嵌套序列。");
            if (clip.Speed is < .05 or > 32 || !double.IsFinite(clip.Speed) ||
                !double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
                !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
                !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
                clip.TimeRemapPoints.Any(point => point.Speed is < .05 or > 32 ||
                    !double.IsFinite(point.Speed) || !double.IsFinite(point.TimeSeconds) ||
                    point.TimeSeconds < 0 || point.TimeSeconds >= clip.DurationSeconds))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "片段速度或速度节点无效。");
            if (clip.TimeRemapPoints.GroupBy(point => point.TimeSeconds)
                .Any(group => group.Count() > 1))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "片段包含重复速度节点。");
            if (clip.Kind is (ClipKind.Video or ClipKind.Audio or ClipKind.Image) &&
                string.IsNullOrWhiteSpace(clip.MediaAssetId))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Reference, "媒体片段缺少源素材引用。");
            if (clip.FrameInterpolation is not ("Sampling" or "Blend" or "OpticalFlow"))
                throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "片段帧插值模式无效。");
        }
    }

    private static void ValidatePath(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.GetExtension(path).Equals(extension,
            StringComparison.OrdinalIgnoreCase))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Validate,
                $"请选择 {extension} 交换文件。");
        try { _ = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ProjectExchangeException(ProjectExchangeStageCode.Validate, "交换文件路径无效。", exception); }
    }

    private static string Required(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value ? value :
        throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"缺少 {name}。");

    private static int PositiveInt(XElement element, string name) =>
        int.TryParse(Required(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value : throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"{name} 无效。");

    private static int NonnegativeInt(XElement element, string name) =>
        int.TryParse(Required(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value : throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"{name} 无效。");

    private static double PositiveDouble(XElement element, string name) =>
        double.TryParse(Required(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) && value > 0 ? value
            : throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"{name} 无效。");

    private static double NonnegativeDouble(XElement element, string name) =>
        double.TryParse(Required(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) && value >= 0 ? value
            : throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"{name} 无效。");

    private static bool Boolean(XElement element, string name) =>
        bool.TryParse(Required(element, name), out bool value) ? value
            : throw new ProjectExchangeException(ProjectExchangeStageCode.Parse, $"{name} 无效。");

    private static string Timecode(double seconds, int fps)
    {
        long frames = Math.Max(0, (long)Math.Round(seconds * fps));
        long hours = frames / (fps * 3600L);
        return $"{hours:00}:{frames / (fps * 60L) % 60:00}:{frames / fps % 60:00}:{frames % fps:00}";
    }

    private static double ParseTimecode(string text, int fps)
    {
        string[] parts = text.Split(':');
        if (parts.Length != 4 || parts.Any(part => !int.TryParse(part, out _)))
            throw new ProjectExchangeException(ProjectExchangeStageCode.Timecode, "时间码无效。");
        int[] values = parts.Select(int.Parse).ToArray();
        if (values[0] < 0 || values[1] is < 0 or > 59 || values[2] is < 0 or > 59 ||
            values[3] < 0 || values[3] >= fps)
            throw new ProjectExchangeException(ProjectExchangeStageCode.Timecode, "时间码超出范围。");
        return values[0] * 3600 + values[1] * 60 + values[2] + (double)values[3] / fps;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
