using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TANGERINE_VideoEditor.Editor;

public enum SubtitleStageCode
{
    Validate,
    Parse,
    Read,
    Write
}

public sealed class SubtitleException(SubtitleStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SubtitleStageCode StageCode { get; } = stageCode;
}

public sealed record SubtitleCue(double StartSeconds, double EndSeconds, string Text);

/// <summary>
/// Reads and writes standard SubRip cues without a network dependency. Time
/// values are kept as seconds in memory and rounded to milliseconds only when
/// serializing a sidecar file. Malformed blocks are rejected with a stage code
/// so the editor never silently drops a subtitle during import.
/// </summary>
public static partial class SubtitleService
{
    // Large sidecar files must not allocate an unbounded string or keep the
    // dispatcher busy while parsing thousands of cues.
    private const long MaximumSrtBytes = 32L * 1024 * 1024;
    [GeneratedRegex(@"^(?<start>\d{2,}):(?<sm>[0-5]\d):(?<ss>[0-5]\d)[,.](?<sms>\d{1,3})\s*-->\s*(?<end>\d{2,}):(?<em>[0-5]\d):(?<es>[0-5]\d)[,.](?<ems>\d{1,3})(?:\s+.*)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TimeRangeRegex();

    public static IReadOnlyList<SubtitleCue> ParseSrt(string content)
    {
        if (content is null)
            throw new SubtitleException(SubtitleStageCode.Validate, "字幕内容为空。");
        try
        {
            string normalized = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var cues = new List<SubtitleCue>();
            foreach (string block in Regex.Split(normalized.Trim(), @"\n[\t ]*\n",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            {
                if (string.IsNullOrWhiteSpace(block)) continue;
                string[] lines = block.Split('\n');
                int rangeIndex = lines.Length > 0 && int.TryParse(lines[0].Trim(), CultureInfo.InvariantCulture, out _) ? 1 : 0;
                if (rangeIndex >= lines.Length)
                    throw new SubtitleException(SubtitleStageCode.Parse, $"第 {cues.Count + 1} 条字幕缺少时间范围。");
                Match match = TimeRangeRegex().Match(lines[rangeIndex].Trim());
                if (!match.Success)
                    throw new SubtitleException(SubtitleStageCode.Parse, $"第 {cues.Count + 1} 条字幕的时间格式无效。");
                double start;
                double end;
                try
                {
                    start = Time(match, "start", "sm", "ss", "sms");
                    end = Time(match, "end", "em", "es", "ems");
                }
                catch (OverflowException exception)
                {
                    throw new SubtitleException(SubtitleStageCode.Parse, $"第 {cues.Count + 1} 条字幕的时间值过大。", exception);
                }
                string caption = string.Join("\n", lines.Skip(rangeIndex + 1)).Trim();
                if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 ||
                end > 24 * 3600 || end <= start || caption.Length == 0 || caption.Length > 100000)
                    throw new SubtitleException(SubtitleStageCode.Validate, $"第 {cues.Count + 1} 条字幕缺少文字或结束时间不晚于开始时间。");
                cues.Add(new SubtitleCue(start, end, caption));
            }
            return cues;
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new SubtitleException(SubtitleStageCode.Parse, "字幕分段过于复杂，无法安全解析。", exception);
        }
    }

    public static string FormatSrt(IEnumerable<SubtitleCue> cues)
    {
        var builder = new StringBuilder();
        int number = 0;
        foreach (SubtitleCue cue in cues.OrderBy(cue => cue.StartSeconds))
        {
            if (!double.IsFinite(cue.StartSeconds) || !double.IsFinite(cue.EndSeconds) ||
                cue.StartSeconds < 0 || cue.EndSeconds > 24 * 3600 ||
                cue.EndSeconds <= cue.StartSeconds || string.IsNullOrWhiteSpace(cue.Text) ||
                cue.Text.Length > 100000)
                throw new SubtitleException(SubtitleStageCode.Validate, "字幕包含无效时间或空文字。");
            builder.Append(++number).Append("\r\n")
                .Append(FormatTime(cue.StartSeconds)).Append(" --> ").Append(FormatTime(cue.EndSeconds)).Append("\r\n")
                .Append(cue.Text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal))
                .Append("\r\n\r\n");
        }
        return builder.ToString();
    }

    public static async Task<IReadOnlyList<SubtitleCue>> ReadSrtAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            string text = await Task.Run(async () =>
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumSrtBytes)
                    throw new SubtitleException(SubtitleStageCode.Validate,
                        "SRT 文件超过 32 MB，无法导入。");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => ParseSrt(text), cancellationToken).ConfigureAwait(false);
        }
        catch (SubtitleException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            throw new SubtitleException(SubtitleStageCode.Read, "无法读取字幕文件。", exception);
        }
    }

    public static async Task WriteSrtAsync(string path, IEnumerable<SubtitleCue> cues, CancellationToken cancellationToken = default)
    {
        // Materialize on the caller thread while its project snapshot is
        // stable. Format the detached values on a worker before writing.
        SubtitleCue[] snapshot = cues.ToArray();
        string content = await Task.Run(() => FormatSrt(snapshot), cancellationToken)
            .ConfigureAwait(false);
        try { await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new SubtitleException(SubtitleStageCode.Write, "无法写入字幕文件。", exception);
        }
    }

    private static double Time(Match match, string hours, string minutes, string seconds, string milliseconds) =>
        long.Parse(match.Groups[hours].Value, CultureInfo.InvariantCulture) * 3600d +
        int.Parse(match.Groups[minutes].Value, CultureInfo.InvariantCulture) * 60d +
        int.Parse(match.Groups[seconds].Value, CultureInfo.InvariantCulture) +
        int.Parse(match.Groups[milliseconds].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000d;

    private static string FormatTime(double seconds)
    {
        long total = checked((long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero));
        return $"{total / 3_600_000:00}:{total / 60_000 % 60:00}:{total / 1000 % 60:00},{total % 1000:000}";
    }
}
