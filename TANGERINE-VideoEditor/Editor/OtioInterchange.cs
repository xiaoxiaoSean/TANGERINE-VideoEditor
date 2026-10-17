using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// OpenTimelineIO (OTIO) interchange format support for cross-platform editing workflows.
/// OTIO is an open-source format supported by DaVinci Resolve, Premiere, Avid, Houdini, and more.
/// Specification: https://opentimelineio.readthedocs.io/
/// </summary>
public static class OtioInterchange
{
    private const string OtioSchemaVersion = "0.15.0";

    /// <summary>
    /// Exports project to OTIO format (.otio JSON file).
    /// </summary>
    public static async Task ExportOtioAsync(ProjectDocument project, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var timeline = BuildOtioTimeline(project);
        var json = JsonSerializer.Serialize(timeline, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        await File.WriteAllTextAsync(outputPath, json, cancellationToken);
    }

    /// <summary>
    /// Imports OTIO timeline into project.
    /// </summary>
    public static async Task<ProjectDocument> ImportOtioAsync(string otioPath,
        CancellationToken cancellationToken = default)
    {
        string json = await File.ReadAllTextAsync(otioPath, cancellationToken);
        var otioDoc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        if (otioDoc == null)
            throw new FormatException("Invalid OTIO file");

        var project = ProjectDocument.CreateDefault();
        project.Name = Path.GetFileNameWithoutExtension(otioPath);

        ParseOtioTimeline(otioDoc, project);

        return project;
    }

    private static Dictionary<string, object> BuildOtioTimeline(ProjectDocument project)
    {
        var sequence = project.Sequence;

        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "Timeline.1",
            ["metadata"] = new Dictionary<string, object>
            {
                ["tangerine_version"] = 2,
                ["project_name"] = project.Name
            },
            ["name"] = sequence.Name,
            ["global_start_time"] = BuildRationalTime(0, sequence.FramesPerSecond),
            ["tracks"] = BuildOtioTracks(project)
        };
    }

    private static Dictionary<string, object> BuildRationalTime(double seconds, double fps)
    {
        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "RationalTime.1",
            ["rate"] = fps,
            ["value"] = Math.Round(seconds * fps)
        };
    }

    private static Dictionary<string, object> BuildTimeRange(double startSeconds, double durationSeconds, double fps)
    {
        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "TimeRange.1",
            ["start_time"] = BuildRationalTime(startSeconds, fps),
            ["duration"] = BuildRationalTime(durationSeconds, fps)
        };
    }

    private static object BuildOtioTracks(ProjectDocument project)
    {
        var tracks = new List<Dictionary<string, object>>();

        // Group tracks by kind
        var videoTracks = project.Tracks.Where(t => t.Kind == TrackKind.Video).OrderBy(t => t.Order);
        var audioTracks = project.Tracks.Where(t => t.Kind == TrackKind.Audio).OrderBy(t => t.Order);

        // Video stack (V1, V2, V3...)
        foreach (var track in videoTracks)
        {
            if (track.IsHidden) continue;

            tracks.Add(new Dictionary<string, object>
            {
                ["OTIO_SCHEMA"] = "Track.1",
                ["name"] = track.Name,
                ["kind"] = "Video",
                ["source_range"] = null!,
                ["children"] = BuildOtioClips(track, project)
            });
        }

        // Audio stack (A1, A2, A3...)
        foreach (var track in audioTracks)
        {
            if (track.IsHidden) continue;

            tracks.Add(new Dictionary<string, object>
            {
                ["OTIO_SCHEMA"] = "Track.1",
                ["name"] = track.Name,
                ["kind"] = "Audio",
                ["source_range"] = null!,
                ["children"] = BuildOtioClips(track, project)
            });
        }

        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "Stack.1",
            ["name"] = "tracks",
            ["source_range"] = null!,
            ["children"] = tracks
        };
    }

    private static List<Dictionary<string, object>> BuildOtioClips(TimelineTrack track, ProjectDocument project)
    {
        var clips = new List<Dictionary<string, object>>();
        double fps = project.Sequence.FramesPerSecond;

        var sortedClips = track.Clips.OrderBy(c => c.StartSeconds).ToList();

        double lastEnd = 0;
        foreach (var clip in sortedClips)
        {
            // Add gap if there's a space before this clip
            if (clip.StartSeconds > lastEnd + 0.001)
            {
                clips.Add(new Dictionary<string, object>
                {
                    ["OTIO_SCHEMA"] = "Gap.1",
                    ["name"] = "Gap",
                    ["source_range"] = BuildTimeRange(0, clip.StartSeconds - lastEnd, fps)
                });
            }

            // Add the clip
            var asset = project.Media.FirstOrDefault(m => m.Id == clip.MediaAssetId);

            if (clip.Kind is ClipKind.Video or ClipKind.Audio)
            {
                clips.Add(new Dictionary<string, object>
                {
                    ["OTIO_SCHEMA"] = "Clip.1",
                    ["name"] = clip.Name,
                    ["source_range"] = BuildTimeRange(clip.SourceInSeconds,
                        clip.DurationSeconds, fps),
                    ["media_reference"] = asset != null
                        ? BuildMediaReference(asset)
                        : BuildMissingReference(clip.Name),
                    ["effects"] = BuildOtioEffects(clip),
                    ["markers"] = new List<object>(),
                    ["metadata"] = new Dictionary<string, object>
                    {
                        ["tangerine_clip_id"] = clip.Id,
                        ["tangerine_speed"] = clip.Speed,
                        ["tangerine_opacity"] = clip.Opacity
                    }
                });
            }
            else if (clip.Kind == ClipKind.Text)
            {
                // Text clips as generators
                clips.Add(new Dictionary<string, object>
                {
                    ["OTIO_SCHEMA"] = "Clip.1",
                    ["name"] = clip.Name,
                    ["source_range"] = BuildTimeRange(0, clip.DurationSeconds, fps),
                    ["media_reference"] = new Dictionary<string, object>
                    {
                        ["OTIO_SCHEMA"] = "GeneratorReference.1",
                        ["name"] = "Text",
                        ["generator_kind"] = "Text",
                        ["parameters"] = new Dictionary<string, object>
                        {
                            ["text"] = clip.Text?.Content ?? "",
                            ["font_family"] = clip.Text?.FontFamily ?? "Arial",
                            ["font_size"] = clip.Text?.FontSize ?? 32
                        }
                    },
                    ["metadata"] = new Dictionary<string, object>
                    {
                        ["tangerine_clip_id"] = clip.Id,
                        ["tangerine_text"] = clip.Text?.Content ?? string.Empty
                    }
                });
            }

            lastEnd = clip.StartSeconds + clip.DurationSeconds;
        }

        return clips;
    }

    private static Dictionary<string, object> BuildMediaReference(MediaAsset asset)
    {
        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "ExternalReference.1",
            ["name"] = asset.Name,
            ["target_url"] = Uri.TryCreate(asset.SourcePath, UriKind.Absolute, out var uri)
                ? uri.AbsoluteUri
                : new Uri(Path.GetFullPath(asset.SourcePath ?? "")).AbsoluteUri,
            ["available_range"] = null!,
            ["metadata"] = new Dictionary<string, object>
            {
                ["tangerine_asset_id"] = asset.Id
            }
        };
    }

    private static Dictionary<string, object> BuildMissingReference(string name)
    {
        return new Dictionary<string, object>
        {
            ["OTIO_SCHEMA"] = "MissingReference.1",
            ["name"] = name
        };
    }

    private static List<Dictionary<string, object>> BuildOtioEffects(TimelineClip clip)
    {
        var effects = new List<Dictionary<string, object>>();

        foreach (var effect in clip.Effects)
        {
            effects.Add(new Dictionary<string, object>
            {
                ["OTIO_SCHEMA"] = "Effect.1",
                ["name"] = effect.Name,
                ["effect_name"] = effect.PresetId ?? "unknown",
                ["metadata"] = new Dictionary<string, object>
                {
                    ["tangerine_effect_id"] = effect.Id,
                    ["tangerine_parameters"] = effect.Parameters
                }
            });
        }

        // Add transform as effect
        if (Math.Abs(clip.Transform.ScaleX - 1.0) > 0.001 ||
            Math.Abs(clip.Transform.ScaleY - 1.0) > 0.001 ||
            Math.Abs(clip.Transform.RotationDegrees) > 0.001)
        {
            effects.Add(new Dictionary<string, object>
            {
                ["OTIO_SCHEMA"] = "Effect.1",
                ["name"] = "Transform",
                ["effect_name"] = "transform",
                ["metadata"] = new Dictionary<string, object>
                {
                    ["scale_x"] = clip.Transform.ScaleX,
                    ["scale_y"] = clip.Transform.ScaleY,
                    ["rotation"] = clip.Transform.RotationDegrees,
                    ["position_x"] = clip.Transform.PositionX,
                    ["position_y"] = clip.Transform.PositionY
                }
            });
        }

        return effects;
    }

    private static void ParseOtioTimeline(Dictionary<string, JsonElement> otioDoc, ProjectDocument project)
    {
        if (otioDoc.TryGetValue("name", out var name))
            project.Sequence.Name = name.GetString() ?? "Imported Timeline";

        if (otioDoc.TryGetValue("global_start_time", out var startTime) &&
            startTime.TryGetProperty("rate", out var rate))
        {
            project.Sequence.FramesPerSecond = rate.GetDouble();
        }

        if (otioDoc.TryGetValue("tracks", out var tracksElement) &&
            tracksElement.TryGetProperty("children", out var tracks))
        {
            int trackOrder = 0;
            foreach (var trackElement in tracks.EnumerateArray())
            {
                ParseOtioTrack(trackElement, project, trackOrder++);
            }
        }
    }

    private static void ParseOtioTrack(JsonElement trackElement, ProjectDocument project, int order)
    {
        string trackName = trackElement.TryGetProperty("name", out var nameEl) ?
            nameEl.GetString() ?? $"Track {order + 1}" : $"Track {order + 1}";

        string kindStr = trackElement.TryGetProperty("kind", out var kindEl) ?
            kindEl.GetString() ?? "Video" : "Video";

        TrackKind kind = kindStr.Equals("Audio", StringComparison.OrdinalIgnoreCase) ?
            TrackKind.Audio : TrackKind.Video;

        var track = new TimelineTrack
        {
            Name = trackName,
            Kind = kind,
            Order = order
        };

        project.Tracks.Add(track);

        if (trackElement.TryGetProperty("children", out var children))
        {
            double currentTime = 0;
            foreach (var child in children.EnumerateArray())
            {
                if (child.TryGetProperty("OTIO_SCHEMA", out var schema))
                {
                    string schemaStr = schema.GetString() ?? "";
                    if (schemaStr.StartsWith("Clip"))
                    {
                        var clip = ParseOtioClip(child, project, currentTime);
                        if (clip != null)
                        {
                            track.Clips.Add(clip);
                            currentTime += clip.DurationSeconds;
                        }
                    }
                    else if (schemaStr.StartsWith("Gap"))
                    {
                        double gapDuration = ParseDuration(child);
                        currentTime += gapDuration;
                    }
                }
            }
        }
    }

    private static TimelineClip? ParseOtioClip(JsonElement clipElement, ProjectDocument project, double startTime)
    {
        string clipName = clipElement.TryGetProperty("name", out var nameEl) ?
            nameEl.GetString() ?? "Clip" : "Clip";

        double duration = ParseDuration(clipElement);
        if (duration <= 0) return null;

        var clip = new TimelineClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = clipName,
            Kind = ClipKind.Video,
            StartSeconds = startTime,
            DurationSeconds = duration
        };

        // Parse source range for media clips
        if (clipElement.TryGetProperty("source_range", out var sourceRange) &&
            sourceRange.ValueKind != JsonValueKind.Null)
        {
            if (sourceRange.TryGetProperty("start_time", out var startTimeEl) &&
                startTimeEl.TryGetProperty("value", out var startValue) &&
                startTimeEl.TryGetProperty("rate", out var startRate))
            {
                clip.SourceInSeconds = startValue.GetDouble() / startRate.GetDouble();
            }
        }

        return clip;
    }

    private static double ParseDuration(JsonElement element)
    {
        if (element.TryGetProperty("source_range", out var sourceRange) &&
            sourceRange.ValueKind != JsonValueKind.Null &&
            sourceRange.TryGetProperty("duration", out var durationEl) &&
            durationEl.TryGetProperty("value", out var value) &&
            durationEl.TryGetProperty("rate", out var rate))
        {
            return value.GetDouble() / rate.GetDouble();
        }
        return 0;
    }
}
