using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TANGERINE_VideoEditor.Editor;

public enum ImageSequenceStageCode { Validate, Encode }

public sealed class ImageSequenceException(ImageSequenceStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ImageSequenceStageCode StageCode { get; } = stageCode;
}

/// <summary>Encodes consecutive still files to an embedded project video.</summary>
public static class ImageSequenceEncoder
{
    public static async Task EncodeAsync(IReadOnlyList<string> frames, string output, double fps,
        CancellationToken cancellationToken = default)
    {
        if (frames.Count < 2 || !double.IsFinite(fps) || fps is < 1 or > 120)
            throw new ImageSequenceException(ImageSequenceStageCode.Validate, "图像序列或帧率无效。");
        string first = frames[0];
        string name = Path.GetFileNameWithoutExtension(first);
        int digitStart = name.Length;
        while (digitStart > 0 && char.IsAsciiDigit(name[digitStart - 1])) digitStart--;
        if (digitStart == name.Length)
            throw new ImageSequenceException(ImageSequenceStageCode.Validate, "文件名必须以连续数字结尾。");
        string pattern = Path.Combine(Path.GetDirectoryName(first)!,
            name[..digitStart] + $"%0{name.Length - digitStart}d" + Path.GetExtension(first));
        string startNumber = name[digitStart..];
        if (!long.TryParse(startNumber, NumberStyles.None, CultureInfo.InvariantCulture, out long firstIndex))
            throw new ImageSequenceException(ImageSequenceStageCode.Validate, "起始帧编号超出支持范围。");
        var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-framerate", fps.ToString("0.######", CultureInfo.InvariantCulture),
            "-start_number", firstIndex.ToString(CultureInfo.InvariantCulture),
            "-i", pattern, "-frames:v", frames.Count.ToString(CultureInfo.InvariantCulture),
            "-vf", "scale=ceil(iw/2)*2:ceil(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast",
            "-crf", "16", "-pix_fmt", "yuv420p", output }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new ImageSequenceException(ImageSequenceStageCode.Encode, "无法启动图像序列编码器。");
            using var registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> outputText = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await outputText;
            string details = await errors;
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new ImageSequenceException(ImageSequenceStageCode.Encode,
                    $"图像序列编码失败：{details[^Math.Min(500, details.Length)..]}");
        }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new ImageSequenceException(ImageSequenceStageCode.Encode, "找不到本地 FFmpeg。", exception); }
        catch (ImageSequenceException)
        {
            DeleteIncompleteOutput(output);
            throw;
        }
        catch (OperationCanceledException)
        {
            DeleteIncompleteOutput(output);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            DeleteIncompleteOutput(output);
            throw new ImageSequenceException(ImageSequenceStageCode.Encode,
                "图像序列编码时无法读取源图片或写入输出文件。", exception);
        }
    }

    private static void DeleteIncompleteOutput(string output)
    {
        try { if (File.Exists(output)) File.Delete(output); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
