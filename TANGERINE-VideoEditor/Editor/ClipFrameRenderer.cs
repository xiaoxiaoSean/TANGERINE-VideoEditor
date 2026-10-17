using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum ClipFrameStageCode { Validate, EncoderMissing, Decode, Filter }

public sealed class ClipFrameException(ClipFrameStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ClipFrameStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Renders one local source frame with the same configurable effect filters
/// as export. Calls may be canceled when the playhead moves again. The result
/// is a frozen bitmap suitable for assignment on the WPF dispatcher.
/// </summary>
public static class ClipFrameRenderer
{
    public static async Task<BitmapSource> RenderClipFrameAsync(TimelineClip clip, MediaAsset asset,
        ProjectSequence sequence, double timelineSeconds, int previewWidth, int previewHeight,
        CancellationToken cancellationToken = default, bool includeVisualEffects = true,
        string previewGpuMode = "自动（GPU 优先）")
    {
        if (clip.Kind is not ClipKind.Image and not ClipKind.Video ||
            previewWidth <= 0 || previewHeight <= 0 ||
            previewWidth > 4096 || previewHeight > 4096 ||
            !double.IsFinite(timelineSeconds) || !double.IsFinite(clip.DurationSeconds) ||
            clip.DurationSeconds <= 0 || !double.IsFinite(clip.Speed) || clip.Speed <= 0)
            throw new ClipFrameException(ClipFrameStageCode.Validate, "预览帧请求无效。");
        // Slow path inspection and LUT parsing stay off the dispatcher. The
        // continuation also stays on the worker while FFmpeg is started.
        (string Path, double SourceSeconds, string Filters) prepared;
        try
        {
            prepared = await Task.Run(() =>
            {
                string sourcePath = asset.EffectivePreviewPath;
                if (!File.Exists(sourcePath))
                    throw new ClipFrameException(ClipFrameStageCode.Validate, "找不到要预览的媒体文件。");
                double local = Math.Clamp(timelineSeconds - clip.StartSeconds, 0, clip.DurationSeconds);
                double source = ClipTimeMap.SourceTime(clip, timelineSeconds);
                string chain = $"scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease";
                if (includeVisualEffects)
                {
                    // The clip mask is defined in source-image normalized
                    // coordinates, so keep the fitted picture unpadded here.
                    chain += VisualEffectPipeline.BuildInputColorConversion(asset, sequence) +
                        VisualEffectPipeline.BuildFfmpegFilters(clip, sequence, local) + ",format=rgba";
                    if (clip.Mask is not null) chain += VisualEffectPipeline.BuildMaskAlpha(clip.Mask, clip, local);
                }
                return (Path: sourcePath, SourceSeconds: source, Filters: chain);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ExportException exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.Filter, "预览效果配置无效。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ClipFrameException(ClipFrameStageCode.Validate, "无法读取预览素材或效果文件。", exception);
        }
        var startInfo = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel"); startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-nostdin");
        foreach (string argument in PreviewHardwareAcceleration.Arguments(previewGpuMode))
            startInfo.ArgumentList.Add(argument);
        if (clip.Kind == ClipKind.Video)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(VisualEffectPipeline.Number(prepared.SourceSeconds));
        }
        else
        {
            startInfo.ArgumentList.Add("-loop");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-framerate");
            startInfo.ArgumentList.Add("1");
        }
        startInfo.ArgumentList.Add("-i"); startInfo.ArgumentList.Add(prepared.Path);
        startInfo.ArgumentList.Add("-vf"); startInfo.ArgumentList.Add(prepared.Filters);
        startInfo.ArgumentList.Add("-frames:v"); startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-f"); startInfo.ArgumentList.Add("image2pipe");
        // Single-clip effect previews can carry transparency from masks, so
        // preserve the alpha channel while decoding into the WPF visual.
        startInfo.ArgumentList.Add("-vcodec"); startInfo.ArgumentList.Add("png");
        startInfo.ArgumentList.Add("pipe:1");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new ClipFrameException(ClipFrameStageCode.EncoderMissing, "无法启动本地 FFmpeg。");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.EncoderMissing, "找不到本地 FFmpeg。", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ClipFrameException(ClipFrameStageCode.EncoderMissing, "无法启动本地 FFmpeg。", exception);
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var buffer = new MemoryStream();
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string errorText = await errors;
            if (process.ExitCode != 0 || buffer.Length == 0)
                throw new ClipFrameException(ClipFrameStageCode.Filter,
                    $"视频帧渲染失败：{errorText.Trim()}");
            byte[] png = buffer.ToArray();
            // BitmapImage is a WPF Freezable. Decode on a dedicated STA worker
            // and freeze before handing the result to the UI dispatcher.
            var decoded = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    using var stream = new MemoryStream(png, writable: false);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    decoded.TrySetResult(bitmap);
                }
                catch (Exception exception) { decoded.TrySetException(exception); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            return await decoded.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is not ClipFrameException)
        {
            throw new ClipFrameException(ClipFrameStageCode.Decode, "无法读取预览帧。", exception);
        }
    }
}
