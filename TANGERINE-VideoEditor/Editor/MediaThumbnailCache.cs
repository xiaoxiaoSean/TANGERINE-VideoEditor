using System.IO;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>Bounded, coalesced source decoding shared by media cards and visible timeline strips.</summary>
public static class MediaThumbnailCache
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly Dictionary<string, Task<BitmapSource>> Frames = [];
    private static readonly Queue<string> Order = [];
    public static Task<BitmapSource> GetAsync(MediaAsset asset, double seconds, CancellationToken token)
    {
        string path = asset.EffectivePreviewPath;
        var file = new FileInfo(path);
        double sample = asset.Kind == MediaKind.Image ? 0 : Math.Floor(Math.Max(0, seconds) * 2) / 2;
        string key = $"{path}:{file.Length}:{file.LastWriteTimeUtc.Ticks}:{sample:R}";
        lock (Frames)
        {
            if (Frames.TryGetValue(key, out var frame) && !frame.IsCanceled && !frame.IsFaulted) return frame.WaitAsync(token);
            // Never evict in-flight work: redraws must join the existing decoder.
            while (Order.Count >= 512)
            {
                string oldest = Order.Peek();
                if (Frames.TryGetValue(oldest, out var pending) && !pending.IsCompleted) break;
                Order.Dequeue(); Frames.Remove(oldest);
            }
            frame = DecodeAsync(asset, sample, key, token);
            Frames[key] = frame; Order.Enqueue(key); return frame;
        }
    }
    private static async Task<BitmapSource> DecodeAsync(MediaAsset asset, double sample, string key, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var clip = new TimelineClip { Kind = asset.Kind == MediaKind.Image ? ClipKind.Image : ClipKind.Video,
                DurationSeconds = Math.Max(1, sample + 1), SourceInSeconds = sample };
            return await ClipFrameRenderer.RenderClipFrameAsync(clip, asset, new ProjectSequence(), 0,
                256, 144, token, includeVisualEffects: false).ConfigureAwait(false);
        }
        catch { lock (Frames) Frames.Remove(key); throw; }
        finally { Gate.Release(); }
    }
}
