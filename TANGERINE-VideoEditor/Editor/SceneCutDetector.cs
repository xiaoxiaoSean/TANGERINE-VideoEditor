using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Detects scene changes in video footage using FFmpeg scene detection filter.
/// Automatically splits long takes into individual shots for easier editing.
/// </summary>
public static class SceneCutDetector
{
    public sealed record SceneCut(
        double TimeSeconds,
        int FrameNumber,
        double Score
    );

    public sealed record DetectionResult(
        List<SceneCut> Cuts,
        double TotalDuration,
        int TotalFrames
    );

    /// <summary>
    /// Detects scene cuts in a video file.
    /// </summary>
    /// <param name="videoPath">Path to video file</param>
    /// <param name="threshold">Detection threshold (0-1, lower = more sensitive, default 0.3)</param>
    /// <param name="progress">Progress callback</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task<DetectionResult> DetectSceneCutsAsync(
        string videoPath,
        double threshold = 0.3,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        double startSeconds = 0,
        double? durationSeconds = null)
    {
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found", videoPath);

        string ffmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg not found", ffmpeg);

        threshold = Math.Clamp(threshold, 0.01, 1.0);

        var cuts = new List<SceneCut>();
        double duration = 0;
        int totalFrames = 0;

        // Numbers inside the filter graph must use the invariant culture. A
        // comma decimal separator (German, French and others) would produce
        // "scene,0,300", and in a filter expression the comma is a separator,
        // so detection would fail outright on those systems.
        string thresholdText = threshold.ToString("0.000", CultureInfo.InvariantCulture);
        // Seeking before -i is fast, but it restarts the output clock at zero;
        // the start offset is added back when the cut times are read below.
        double seekSeconds = Math.Max(0, double.IsFinite(startSeconds) ? startSeconds : 0);
        string range = seekSeconds > 0
            ? $"-ss {seekSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " : string.Empty;
        if (durationSeconds is double span && double.IsFinite(span) && span > 0)
            range += $"-t {span.ToString("0.###", CultureInfo.InvariantCulture)} ";
        var detect = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"{range}-i \"{videoPath}\" -vf \"select='gt(scene,{thresholdText})',metadata=print:file=-\" -an -f null -",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(detect);
        if (process == null)
            throw new InvalidOperationException("Failed to start scene detection");

        string output = "";
        string errorOutput = "";

        var outputTask = Task.Run(async () =>
        {
            output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        }, cancellationToken);

        var errorTask = Task.Run(async () =>
        {
            errorOutput = await process.StandardError.ReadToEndAsync(cancellationToken);
        }, cancellationToken);

        try
        {
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Disposing a Process does not stop it. Without this the encoder
            // keeps scanning the whole file in the background after the user
            // cancelled.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        // An empty cut list from a failed run would read as "this video has no
        // scene changes". Report the failure instead.
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"场景检测进程异常退出（代码 {process.ExitCode}），请确认视频文件可以正常读取。");

        // metadata=print emits the frame header and scene score on separate
        // lines. Keep their association; requiring both on one line silently
        // reported "no cuts" even when FFmpeg detected every actual boundary.
        cuts.AddRange(ParseMetadata(output, seekSeconds));

        // Extract duration from stderr
        // Format: Duration: 00:01:23.45
        foreach (var line in errorOutput.Split('\n'))
        {
            if (line.Contains("Duration:"))
            {
                int start = line.IndexOf("Duration:") + 9;
                int end = line.IndexOf(',', start);
                if (end > start)
                {
                    string durationStr = line.Substring(start, end - start).Trim();
                    duration = ParseDuration(durationStr);
                }
            }
            else if (line.Contains("frame="))
            {
                int? frame = ExtractIntValue(line, "frame=");
                if (frame.HasValue)
                    totalFrames = Math.Max(totalFrames, frame.Value);
            }
        }

        return new DetectionResult(cuts.OrderBy(c => c.TimeSeconds).ToList(), duration, totalFrames);
    }

    internal static List<SceneCut> ParseMetadata(string output, double seekSeconds = 0)
    {
        var cuts = new List<SceneCut>();
        double? time = null;
        int? frame = null;
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains("pts_time:", StringComparison.Ordinal))
            {
                time = ExtractValue(line, "pts_time:");
                frame = ExtractIntValue(line, "frame:");
            }
            double? score = ExtractValue(line, "lavfi.scene_score=");
            if (time is double seconds && frame is int number && score is double strength &&
                double.IsFinite(seconds) && seconds >= 0 && double.IsFinite(strength) && strength >= 0)
            {
                cuts.Add(new SceneCut(seconds + seekSeconds, number, strength));
                time = null;
                frame = null;
            }
        }
        return cuts.OrderBy(cut => cut.TimeSeconds).ToList();
    }

    /// <summary>
    /// Suggests cut points for splitting a clip based on detected scenes.
    /// </summary>
    public static List<double> SuggestCutPoints(DetectionResult result, double minSegmentDuration = 1.0)
    {
        var cutPoints = new List<double>();

        double lastCut = 0;
        foreach (var cut in result.Cuts)
        {
            // Only suggest cuts that create segments longer than minimum duration
            if (cut.TimeSeconds - lastCut >= minSegmentDuration)
            {
                cutPoints.Add(cut.TimeSeconds);
                lastCut = cut.TimeSeconds;
            }
        }

        return cutPoints;
    }

    /// <summary>
    /// Generates a summary showing detected scene boundaries.
    /// </summary>
    public static string GenerateSummary(DetectionResult result)
    {
        if (result.Cuts.Count == 0)
            return $"未检测到场景切换（总时长 {result.TotalDuration:F1} 秒）";

        var lines = new List<string>
        {
            $"检测到 {result.Cuts.Count} 个场景切换",
            $"总时长：{result.TotalDuration:F1} 秒",
            $"平均场景长度：{result.TotalDuration / (result.Cuts.Count + 1):F1} 秒",
            "",
            "场景边界："
        };

        double lastTime = 0;
        int sceneNumber = 1;

        foreach (var cut in result.Cuts.Take(20)) // Show first 20
        {
            double sceneDuration = cut.TimeSeconds - lastTime;
            lines.Add($"  场景 {sceneNumber}: {FormatTime(lastTime)} - {FormatTime(cut.TimeSeconds)} " +
                     $"({sceneDuration:F1}s, 得分 {cut.Score:F3})");
            lastTime = cut.TimeSeconds;
            sceneNumber++;
        }

        if (result.Cuts.Count > 20)
            lines.Add($"  ... 还有 {result.Cuts.Count - 20} 个场景");

        // Last scene
        double finalDuration = result.TotalDuration - lastTime;
        if (finalDuration > 0.5)
        {
            lines.Add($"  场景 {sceneNumber}: {FormatTime(lastTime)} - {FormatTime(result.TotalDuration)} " +
                     $"({finalDuration:F1}s)");
        }

        return string.Join("\n", lines);
    }

    private static double? ExtractValue(string line, string prefix)
    {
        int start = line.IndexOf(prefix);
        if (start < 0) return null;
        start += prefix.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

        int end = start;
        while (end < line.Length && (char.IsDigit(line[end]) || line[end] is '.' or '-' or '+' or 'e' or 'E'))
            end++;

        if (end > start && double.TryParse(line.Substring(start, end - start),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            return value;

        return null;
    }

    private static int? ExtractIntValue(string line, string prefix)
    {
        int start = line.IndexOf(prefix);
        if (start < 0) return null;
        start += prefix.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start])) start++;

        int end = start;
        while (end < line.Length && char.IsDigit(line[end]))
            end++;

        if (end > start && int.TryParse(line.Substring(start, end - start),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return value;

        return null;
    }

    private static double ParseDuration(string duration)
    {
        // Format: HH:MM:SS.mmm
        var parts = duration.Split(':');
        if (parts.Length != 3) return 0;

        if (int.TryParse(parts[0], out int hours) &&
            int.TryParse(parts[1], out int minutes) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return hours * 3600 + minutes * 60 + seconds;
        }

        return 0;
    }

    private static string FormatTime(double seconds)
    {
        int hours = (int)(seconds / 3600);
        int mins = (int)((seconds % 3600) / 60);
        double secs = seconds % 60;

        if (hours > 0)
            return $"{hours:D2}:{mins:D2}:{secs:05.2f}";
        else
            return $"{mins:D2}:{secs:05.2f}";
    }
}
