using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Subclip management for creating reusable segments from long source media.
/// Useful for organizing interview takes, B-roll selections, and alternate shots.
/// </summary>
public static class SubclipService
{
    public sealed class Subclip
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "子片段";
        public string ParentAssetId { get; set; } = string.Empty;
        public double InSeconds { get; set; }
        public double OutSeconds { get; set; }
        public string Notes { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        // Stored in UTC: a local DateTime is ambiguous around daylight-saving
        // changes, and the project file keeps this as a UTC DateTimeOffset.
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates a subclip from source media with In/Out points.
    /// </summary>
    public static Subclip CreateSubclip(MediaAsset parentAsset, double inSeconds, double outSeconds, string name = "")
    {
        if (string.IsNullOrWhiteSpace(name))
            name = $"{parentAsset.Name} - 子片段";

        return new Subclip
        {
            Name = name,
            ParentAssetId = parentAsset.Id,
            InSeconds = inSeconds,
            OutSeconds = outSeconds
        };
    }

    /// <summary>
    /// Converts a subclip to a timeline clip.
    /// </summary>
    public static TimelineClip SubclipToTimelineClip(Subclip subclip, MediaAsset parentAsset, double startSeconds)
    {
        return new TimelineClip
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = subclip.Name,
            // The clip kind follows the parent media. A hard-coded Video kind
            // would put an audio source on an audio track as a video clip.
            Kind = parentAsset.Kind switch
            {
                MediaKind.Audio => ClipKind.Audio,
                MediaKind.Image => ClipKind.Image,
                _ => ClipKind.Video
            },
            MediaAssetId = parentAsset.Id,
            StartSeconds = startSeconds,
            DurationSeconds = subclip.OutSeconds - subclip.InSeconds,
            SourceInSeconds = subclip.InSeconds
        };
    }

    /// <summary>
    /// Gets all subclips for a media asset.
    /// </summary>
    public static List<Subclip> GetSubclipsForAsset(string assetId, ProjectDocument project)
    {
        return project.Subclips.Where(item => item.ParentAssetId == assetId)
            .Select(item => new Subclip
            {
                Id = item.Id, Name = item.Name, ParentAssetId = item.ParentAssetId,
                InSeconds = item.InSeconds, OutSeconds = item.OutSeconds,
                Notes = item.Notes, Tags = [.. item.Tags], CreatedTime = item.CreatedUtc.UtcDateTime
            }).ToList();
    }

    public static ProjectSubclip ToProjectSubclip(Subclip subclip) => new()
    {
        Id = subclip.Id, Name = subclip.Name, ParentAssetId = subclip.ParentAssetId,
        InSeconds = subclip.InSeconds, OutSeconds = subclip.OutSeconds,
        Notes = subclip.Notes, Tags = [.. subclip.Tags],
        // Force UTC. A DateTime of unspecified kind would be read as local time
        // by the DateTimeOffset constructor and shift the stored instant.
        CreatedUtc = new DateTimeOffset(DateTime.SpecifyKind(subclip.CreatedTime, DateTimeKind.Utc))
    };
}

/// <summary>
/// Media relinking service for reconnecting offline media files.
/// </summary>
public static class MediaRelinkService
{
    public enum RelinkStrategy
    {
        Manual,          // User manually selects each file
        AutomaticPath,   // Search in specified directories
        AutomaticName    // Match by filename only
    }

    public sealed class RelinkResult
    {
        public int TotalAssets { get; set; }
        public int RelinkedCount { get; set; }
        public int StillOfflineCount { get; set; }
        public List<string> FailedAssets { get; set; } = new();
    }

    /// <summary>
    /// Checks which media assets are offline.
    /// </summary>
    public static List<MediaAsset> GetOfflineAssets(ProjectDocument project)
    {
        return project.Media
            .Where(asset => !string.IsNullOrEmpty(asset.SourcePath) && !File.Exists(asset.SourcePath))
            .ToList();
    }

    /// <summary>
    /// Attempts to relink offline media automatically by searching directories.
    /// </summary>
    public static RelinkResult AutoRelinkMedia(ProjectDocument project, List<string> searchDirectories)
    {
        var result = new RelinkResult();
        var offline = GetOfflineAssets(project);
        result.TotalAssets = offline.Count;

        foreach (var asset in offline)
        {
            string? originalFilename = Path.GetFileName(asset.SourcePath);
            if (string.IsNullOrEmpty(originalFilename))
                continue;

            // Search in specified directories
            foreach (var directory in searchDirectories)
            {
                if (!Directory.Exists(directory))
                    continue;

                var foundFiles = Directory.GetFiles(directory, originalFilename, SearchOption.AllDirectories);
                if (foundFiles.Length > 0)
                {
                    // Found a match - relink to first occurrence
                    asset.SourcePath = foundFiles[0];
                    result.RelinkedCount++;
                    break;
                }
            }

            if (!File.Exists(asset.SourcePath))
            {
                result.FailedAssets.Add(asset.Name);
            }
        }

        result.StillOfflineCount = result.TotalAssets - result.RelinkedCount;
        return result;
    }

    /// <summary>
    /// Manually relinks a specific asset to a new file path.
    /// </summary>
    public static bool RelinkAsset(MediaAsset asset, string newPath)
    {
        if (!File.Exists(newPath))
            return false;

        asset.SourcePath = newPath;

        // Also update proxy path if it was relative
        if (!string.IsNullOrEmpty(asset.ProxyPath) && !Path.IsPathRooted(asset.ProxyPath))
        {
            string? directory = Path.GetDirectoryName(newPath);
            if (directory != null)
            {
                string proxyFilename = Path.GetFileName(asset.ProxyPath);
                string newProxyPath = Path.Combine(directory, proxyFilename);
                if (File.Exists(newProxyPath))
                    asset.ProxyPath = newProxyPath;
            }
        }

        return true;
    }

    /// <summary>
    /// Converts absolute paths to relative paths based on project location.
    /// </summary>
    public static void MakePathsRelative(ProjectDocument project, string projectDirectory)
    {
        foreach (var asset in project.Media)
        {
            if (!string.IsNullOrEmpty(asset.SourcePath) && Path.IsPathRooted(asset.SourcePath))
            {
                asset.SourcePath = GetRelativePath(projectDirectory, asset.SourcePath);
            }

            if (!string.IsNullOrEmpty(asset.ProxyPath) && Path.IsPathRooted(asset.ProxyPath))
            {
                asset.ProxyPath = GetRelativePath(projectDirectory, asset.ProxyPath);
            }
        }
    }

    /// <summary>
    /// Converts relative paths to absolute paths based on project location.
    /// </summary>
    public static void MakePathsAbsolute(ProjectDocument project, string projectDirectory)
    {
        foreach (var asset in project.Media)
        {
            if (!string.IsNullOrEmpty(asset.SourcePath) && !Path.IsPathRooted(asset.SourcePath))
            {
                asset.SourcePath = Path.GetFullPath(Path.Combine(projectDirectory, asset.SourcePath));
            }

            if (!string.IsNullOrEmpty(asset.ProxyPath) && !Path.IsPathRooted(asset.ProxyPath))
            {
                asset.ProxyPath = Path.GetFullPath(Path.Combine(projectDirectory, asset.ProxyPath));
            }
        }
    }

    private static string GetRelativePath(string fromPath, string toPath)
    {
        Uri fromUri = new Uri(AppendDirectorySeparator(fromPath));
        Uri toUri = new Uri(toPath);

        if (fromUri.Scheme != toUri.Scheme)
            return toPath; // Path can't be made relative

        Uri relativeUri = fromUri.MakeRelativeUri(toUri);
        string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            return path + Path.DirectorySeparatorChar;
        return path;
    }
}
