using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Content-addressed, frame-aligned interval planning. Timeline/UI revision numbers
/// are intentionally absent: unrelated edits must preserve completed intervals.
/// </summary>
public static class ProjectPreviewCache
{
    public sealed record Segment(double Start, double End, string Key);
    public static IReadOnlyList<Segment> Plan(ProjectDocument project, int width, int height)
    {
        double fps = project.Sequence.FramesPerSecond;
        if (!double.IsFinite(fps) || fps <= 0) return [];
        double duration = project.Tracks.SelectMany(t => t.Clips).Select(c => c.StartSeconds + c.DurationSeconds).DefaultIfEmpty().Max();
        if (!double.IsFinite(duration) || duration <= 0 || duration > 86400) return [];
        double step = Math.Ceiling(fps * 3) / fps;
        var trackSources = project.Tracks.Select(t => (Track: t, Padding: t.Clips.SelectMany(c => c.Transitions)
            .Select(t => t.DurationSeconds).DefaultIfEmpty().Max())).ToArray();
        var result = new List<Segment>();
        for (double start = 0; start < duration - 1e-7; start += step)
        {
            double end = Math.Min(duration, start + step);
            var tracks = trackSources.Select(source => new
            {
                source.Track.Id, source.Track.Kind, source.Track.Order, source.Track.IsHidden, source.Track.IsMuted,
                // Include the preceding boundary partner for held-frame transitions.
                Clips = source.Track.Clips.Where(c => c.StartSeconds < end &&
                    c.StartSeconds + c.DurationSeconds + source.Padding > start + 1e-7).ToArray()
            }).ToArray();
            var clips = tracks.SelectMany(t => t.Clips).ToArray();
            bool nested = clips.Any(c => c.Kind == ClipKind.Sequence);
            var dependencies = nested ? clips.Concat(project.Sequences.SelectMany(s => s.Tracks).SelectMany(t => t.Clips)).ToArray() : clips;
            var ids = dependencies.Select(c => c.MediaAssetId).ToHashSet();
            // LUTs and other effect file parameters are dependencies just like source media.
            var effectFiles = dependencies.SelectMany(c => c.Effects).SelectMany(e => e.Parameters.Values)
                .Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())
                .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                .Distinct().Select(p => new { Path = p, Stamp = Stamp(p) });
            var media = project.Media.Where(m => ids.Contains(m.Id)).Select(m => new
            { Asset = m, Source = Stamp(m.SourcePath), Proxy = Stamp(m.ProxyPath) });
            var seq = project.Sequence;
            string json = JsonSerializer.Serialize(new { Version = "interval-preview-4", start, end, PreviewWidth = width, PreviewHeight = height,
                seq.Width, seq.Height, seq.FramesPerSecond, seq.AudioSampleRate, seq.BackgroundColor,
                seq.WorkingColorSpace, seq.OutputColorSpace, tracks, media, effectFiles,
                Multicam = dependencies.Any(c => c.MulticamGroupId is not null) ? project.MulticamGroups : null,
                Nested = nested ? project.Sequences : null });
            result.Add(new(start, end, Hash(json)));
        }
        return result;
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Stamp(string? path)
    {
        try { var file = new FileInfo(path ?? ""); return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unavailable"; }
    }
}

