using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum StillFrameStageCode { Validate, Prepare, EncoderMissing, Render, Write }

public sealed class StillFrameException(StillFrameStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public StillFrameStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Exports source stills and a complete sequence frame through the local
/// encoder. The sequence path renders only the requested playhead frame,
/// including rasterized title layers, rather than encoding an entire movie.
/// </summary>
public static class StillFrameExporter
{
    public static async Task ExportSourceFrameAsync(TimelineClip clip, MediaAsset asset,
        ProjectSequence sequence, double timelineSeconds, string outputPath,
        IReadOnlyCollection<string>? protectedPaths = null,
        CancellationToken cancellationToken = default)
    {
        if (clip.Kind != ClipKind.Video ||
            !double.IsFinite(timelineSeconds) || !IsPngPath(outputPath))
            throw new StillFrameException(StillFrameStageCode.Validate, "冻结帧请求无效。");
        // Render through the same compositing graph as the sequence preview.
        // This keeps crop, arbitrary rotation, animated placement, opacity,
        // masks, and effects in the frozen image. Proxy use is disabled because
        // a freeze frame is rendered from the original media.
        ProjectDocument isolated;
        try
        {
            isolated = await Task.Run(() =>
                JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(new ProjectDocument
                {
                    Sequence = sequence,
                    Media = [asset],
                    Tracks = [new TimelineTrack { Kind = TrackKind.Video, Clips = [clip] }]
                })) ?? throw new JsonException("Empty freeze-frame snapshot."), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        { throw new StillFrameException(StillFrameStageCode.Prepare, "无法复制冻结帧素材。", exception); }
        isolated.Media[0].UseProxy = false;
        if (!await Task.Run(() => File.Exists(isolated.Media[0].SourcePath), cancellationToken)
            .ConfigureAwait(false))
            throw new StillFrameException(StillFrameStageCode.Validate, "冻结帧的源媒体文件不存在。");
        TimelineClip frozenClip = isolated.Tracks[0].Clips[0];
        double lastFrame = frozenClip.StartSeconds + Math.Max(0,
            frozenClip.DurationSeconds - 1 / Math.Max(1, isolated.Sequence.FramesPerSecond));
        double sampleTime = Math.Clamp(timelineSeconds, frozenClip.StartSeconds, lastFrame);
        await ExportCompositedFrameAsync(isolated, sampleTime, outputPath, cancellationToken,
            protectedPaths)
            .ConfigureAwait(false);
    }

    public static async Task ExportCompositedFrameAsync(ProjectDocument project, double timelineSeconds,
        string outputPath, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? protectedPaths = null)
    {
        if (!IsPngPath(outputPath) || !double.IsFinite(timelineSeconds) || timelineSeconds < 0)
            throw new StillFrameException(StillFrameStageCode.Validate, "导出帧请求无效。");
        string destination;
        try { destination = await Task.Run(() => Path.GetFullPath(outputPath), cancellationToken)
            .ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new StillFrameException(StillFrameStageCode.Validate, "静帧输出路径无效。", exception); }
        bool targetsSource = await Task.Run(() => project.Media.Any(asset =>
            IsSamePath(asset.SourcePath, destination) || IsSamePath(asset.ProxyPath, destination)) ||
            protectedPaths?.Any(path => IsSamePath(path, destination)) == true,
            cancellationToken).ConfigureAwait(false);
        if (targetsSource)
            throw new StillFrameException(StillFrameStageCode.Validate,
                "导出帧不能覆盖工程中的源媒体或代理文件。");
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"tangerine-frame-{Guid.NewGuid():N}");
        try
        {
            await Task.Run(() => Directory.CreateDirectory(tempDirectory), cancellationToken)
                .ConfigureAwait(false);
            ProjectDocument snapshot = await Task.Run(() =>
                SequenceFlattener.Flatten(project), cancellationToken).ConfigureAwait(false);
            var titlePaths = await RenderTitlesAsync(snapshot, tempDirectory,
                timelineSeconds, cancellationToken).ConfigureAwait(false);
            BitmapSource frame = await CompositeFrameRenderer.RenderFrameAsync(snapshot, timelineSeconds,
                snapshot.Sequence.Width, snapshot.Sequence.Height, cancellationToken, titlePaths)
                .ConfigureAwait(false);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? parent = Path.GetDirectoryName(destination);
                if (parent is null || !Directory.Exists(parent))
                    throw new StillFrameException(StillFrameStageCode.Validate, "静帧导出目录不存在。");
                string temporary = Path.Combine(parent, $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.png");
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frame));
                    using (var stream = File.Create(temporary)) encoder.Save(stream);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException exception) { Debug.WriteLine($"FRAME-CLEANUP: {exception}"); }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectFileException exception)
        { throw new StillFrameException(StillFrameStageCode.Prepare, "展开序列失败。", exception); }
        catch (OperationCanceledException) { throw; }
        catch (ExportException exception)
        { throw new StillFrameException(StillFrameStageCode.Render, "合成视频帧失败。", exception); }
        catch (ClipFrameException exception)
        { throw new StillFrameException(StillFrameStageCode.Render, "合成视频帧失败。", exception); }
        catch (StillFrameException) { throw; }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        { throw new StillFrameException(StillFrameStageCode.Write, "无法编码视频静帧。", exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw new StillFrameException(StillFrameStageCode.Write, "写入视频帧失败。", exception); }
        finally
        {
            await Task.Run(() =>
            {
                try { if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Debug.WriteLine($"FRAME-CLEANUP: {exception}"); }
            }).ConfigureAwait(false);
        }
    }

    private static Task<Dictionary<string, string>> RenderTitlesAsync(ProjectDocument snapshot,
        string directory, double timelineSeconds, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                var paths = new Dictionary<string, string>();
                foreach (TimelineClip clip in snapshot.Tracks
                    .Where(track => !track.IsHidden && track.Kind != TrackKind.Audio)
                    .SelectMany(track => track.Clips)
                    .Where(clip => clip.Kind == ClipKind.Text &&
                        timelineSeconds >= clip.StartSeconds &&
                        timelineSeconds < clip.StartSeconds + clip.DurationSeconds))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string path = Path.Combine(directory, $"{Guid.NewGuid():N}.png");
                    TitleBitmapRenderer.Render(clip, snapshot.Sequence, path,
                        timelineSeconds - clip.StartSeconds);
                    paths[clip.Id] = path;
                }
                completion.TrySetResult(paths);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static bool IsPngPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    private static bool IsSamePath(string? candidate, string destination)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try { return string.Equals(Path.GetFullPath(candidate), destination,
            StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

}
