using System.Diagnostics;
using System.Globalization;
using System.IO;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor.Editor;

public sealed class StabilizationSettings
{
    public bool Enabled { get; set; }
    public int Smoothing { get; set; } = 30;
    public int Shakiness { get; set; } = 5;
    public double MaxZoomPercent { get; set; } = 10;
    public string? OriginalAssetId { get; set; }
    public double OriginalSourceIn { get; set; }
}

/// <summary>
/// Real vidstab analysis/compensation is baked to a new lossless media asset.
/// This preserves identical pictures in native playback, composite previews,
/// exports, and portable projects without shipping temporary transform files.
/// </summary>
public static class VideoStabilization
{
    public static async Task RenderAsync(TimelineClip clip, MediaAsset asset, StabilizationSettings settings, string output, CancellationToken token)
    {
        if (clip.Kind != ClipKind.Video || settings.Smoothing is < 1 or > 120 || settings.Shakiness is < 1 or > 10 ||
            !double.IsFinite(settings.MaxZoomPercent) || settings.MaxZoomPercent is < 0 or > 50)
            throw new StageException("STABL0001", "防抖参数无效：平滑1–120，抖动1–10，边缘放大0–50%。");
        string work = Path.Combine(Path.GetTempPath(), "tangerine-stabilize-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
        string span = ClipTimeMap.SourceOffset(clip, clip.DurationSeconds).ToString("R", CultureInfo.InvariantCulture);
        string start = clip.SourceInSeconds.ToString("R", CultureInfo.InvariantCulture);
        try
        {
            await Run(["-ss", start, "-t", span, "-i", asset.EffectiveExportPath, "-an", "-vf",
                $"vidstabdetect=shakiness={settings.Shakiness}:accuracy=15:result=motion.trf", "-f", "null", "-"], work, token);
            await Run(["-ss", start, "-t", span, "-i", asset.EffectiveExportPath, "-map", "0:v:0", "-map", "0:a?", "-vf",
                $"vidstabtransform=input=motion.trf:smoothing={settings.Smoothing}:optzoom=0:zoom={settings.MaxZoomPercent.ToString("R", CultureInfo.InvariantCulture)}:interpol=bicubic:crop=black",
                "-c:v", "ffv1", "-level", "3", "-c:a", "pcm_s24le", output], work, token);
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } }
    }
    private static async Task Run(string[] args, string work, CancellationToken token)
    {
        var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { WorkingDirectory = work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new StageException("STABL0002", "无法启动防抖分析");
        using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        string error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        if (process.ExitCode != 0) throw new StageException("STABL0003", "防抖处理失败：" + error);
    }
}
