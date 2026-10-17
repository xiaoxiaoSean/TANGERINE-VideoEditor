using System.Globalization;
using System.IO;
using System.Text;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// A changing title is exported as a bounded sequence of transparent PNG
/// states. A concat manifest repeats each state until the next visible text
/// change, so long static portions do not require a PNG for every frame.
/// </summary>
public static class DynamicTitleFrames
{
    private const int MaximumFrames = 1800;

    public static string Render(TimelineClip clip, ProjectSequence sequence,
        string directory, CancellationToken cancellationToken = default)
    {
        if (!DynamicTitlePreset.HasChangingText(clip) ||
            !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
            !double.IsFinite(sequence.FramesPerSecond) || sequence.FramesPerSecond <= 0)
            throw new ExportException(ExportStageCode.Validate, "动态标题时间或帧率无效。");
        double frameSeconds = 1 / sequence.FramesPerSecond;
        int totalFrames = (int)Math.Ceiling(clip.DurationSeconds * sequence.FramesPerSecond);
        if (totalFrames < 1 || totalFrames > MaximumFrames)
            throw new ExportException(ExportStageCode.Validate,
                "逐字或数字标题超过 1800 帧，请缩短片段或使用普通文字。");
        Directory.CreateDirectory(directory);
        var manifest = new StringBuilder("ffconcat version 1.0\n");
        string? lastText = null;
        string? lastPath = null;
        int accumulatedFrames = 0;
        int stateIndex = 0;
        for (int frame = 0; frame < totalFrames; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double localTime = frame * frameSeconds;
            string shown = DynamicTitlePreset.VisibleText(clip, localTime);
            if (shown == lastText)
            {
                accumulatedFrames++;
                continue;
            }
            if (lastPath is not null)
                AppendState(manifest, lastPath, accumulatedFrames * frameSeconds);
            string path = Path.Combine(directory, $"state-{stateIndex++:0000}.png");
            TitleBitmapRenderer.Render(clip, sequence, path, localTime);
            lastPath = path;
            lastText = shown;
            accumulatedFrames = 1;
        }
        if (lastPath is null)
            throw new ExportException(ExportStageCode.Prepare, "无法绘制动态标题帧。");
        AppendState(manifest, lastPath, accumulatedFrames * frameSeconds);
        // The concat demuxer needs one final file entry to apply the last
        // duration; the downstream trim keeps the exact clip length.
        manifest.Append("file '").Append(EscapePath(lastPath)).Append("'\n");
        string manifestPath = Path.Combine(directory, "title.ffconcat");
        File.WriteAllText(manifestPath, manifest.ToString(), new UTF8Encoding(false));
        return manifestPath;
    }

    private static void AppendState(StringBuilder manifest, string path, double duration)
    {
        manifest.Append("file '").Append(EscapePath(path)).Append("'\n")
            .Append("duration ")
            .Append(duration.ToString("0.########", CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static string EscapePath(string path) => path.Replace("'", "'\\''", StringComparison.Ordinal)
        .Replace('\\', '/');
}
