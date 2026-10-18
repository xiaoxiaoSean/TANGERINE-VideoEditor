using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace TANGERINE_VideoEditor.Project;

/// <summary>
/// Identifies the stage that failed so the UI can show a useful localized error.
/// </summary>
public enum ProjectStageCode
{
    InvalidPath,
    Validate,
    UnsupportedVersion,
    Migrate,
    Read,
    Parse,
    Write,
    Replace,
    Space
}

public sealed class ProjectFileException : Exception
{
    public ProjectStageCode StageCode { get; }

    public ProjectFileException(ProjectStageCode stageCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        StageCode = stageCode;
    }
}

/// <summary>
/// Reads legacy JSON projects and writes a portable, versioned .tvep archive.
/// </summary>
public static class ProjectFileService
{
    // A migration is deliberately registered per source version. New releases
    // add a migration before increasing CurrentVersion, then load older files
    // through every intermediate step. This keeps old projects upgradeable.
    private static readonly SortedDictionary<int, Func<JsonObject, JsonObject>> Migrations = new();
    private static readonly object MigrationGate = new();
    private const string ManifestEntryName = "manifest.json";
    private const string EmbeddedMediaPrefix = "tvep-media:";
    private const long ArchiveSafetyBytes = 16L * 1024 * 1024;
    private static readonly string SessionCacheRoot = Path.Combine(Path.GetTempPath(),
        "TANGERINE-VideoEditor", "project-cache", Guid.NewGuid().ToString("N"));
    private static readonly ConditionalWeakTable<ProjectDocument, CacheLease> CacheLeases = new();
    private static readonly object CacheGate = new();
    // Autosave uses a fresh filename each time. One asynchronous save gate
    // prevents unbounded per-path semaphores while serializing archive writes.
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private const int MaximumArchiveEntries = 100000;
    private const long MaximumManifestBytes = 256L * 1024 * 1024;
    private sealed class CacheLease(string path)
    {
        public string Path { get; } = path;
        public bool Releasing { get; set; }
    }

    static ProjectFileService()
    {
        // The first archive release changes the envelope, not the document's
        // editing instructions. The explicit step preserves version sequencing.
        Migrations.Add(1, root => root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(SessionCacheRoot)) Directory.Delete(SessionCacheRoot, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Files can still be held by a decoder during shutdown.
            }
        };
    }

    public static void RegisterMigration(int fromVersion, Func<JsonObject, JsonObject> migration)
    {
        if (fromVersion < 1 || fromVersion >= ProjectDocument.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(fromVersion));
        ArgumentNullException.ThrowIfNull(migration);
        lock (MigrationGate)
        {
            if (!Migrations.TryAdd(fromVersion, migration))
                throw new InvalidOperationException($"Version {fromVersion} already has a migration.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        // Archive inspection, JSON parsing, and extraction stay on a worker.
        return Task.Run(() => LoadCore(NormalizePath(path), cancellationToken), cancellationToken);
    }

    private static ProjectDocument LoadCore(string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            using FileStream input = new(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[4];
            if (input.Read(signature) != signature.Length)
                throw new ProjectFileException(ProjectStageCode.Parse, "项目文件为空或已损坏。");
            input.Position = 0;
            if (!IsZipSignature(signature))
            {
                // Version 1 projects were plain JSON. Keep them readable and
                // resolve their original relative references before returning.
                if (input.Length > MaximumManifestBytes)
                    throw new ProjectFileException(ProjectStageCode.Parse, "项目清单超过支持的大小。");
                cancellationToken.ThrowIfCancellationRequested();
                using var reader = new StreamReader(input, new UTF8Encoding(false, true));
                ProjectDocument legacy = ParseDocument(reader.ReadToEnd(), cancellationToken);
                foreach (MediaAsset asset in legacy.Media)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    asset.SourcePath = ResolveMediaPath(projectPath, asset.SourcePath);
                    if (!string.IsNullOrWhiteSpace(asset.ProxyPath))
                    {
                        asset.ProxyPath = ResolveMediaPath(projectPath, asset.ProxyPath);
                        asset.SetProxyAvailability(File.Exists(asset.ProxyPath));
                    }
                }
                return legacy;
            }

            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumArchiveEntries)
                throw new ProjectFileException(ProjectStageCode.Parse, "项目容器条目数量超过支持范围。");
            var archiveEntries = archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            if (!archiveEntries.TryGetValue(ManifestEntryName, out ZipArchiveEntry[]? manifests) ||
                manifests.Length != 1)
                throw new ProjectFileException(ProjectStageCode.Parse, "项目容器的清单数量无效。");
            ZipArchiveEntry manifest = manifests[0];
            if (manifest.Length > MaximumManifestBytes)
                throw new ProjectFileException(ProjectStageCode.Parse, "项目清单超过支持的大小。");
            cancellationToken.ThrowIfCancellationRequested();
            ProjectDocument document = ParseDocument(ReadManifest(manifest, cancellationToken), cancellationToken);
            var embedded = document.Media.SelectMany(asset => new[] { asset.SourcePath, asset.ProxyPath })
                .Where(value => value?.StartsWith(EmbeddedMediaPrefix, StringComparison.Ordinal) == true)
                .Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            int mediaReferenceCount = document.Media.Count +
                document.Media.Count(asset => !string.IsNullOrWhiteSpace(asset.ProxyPath));
            if (document.Media.Any(asset => !asset.SourcePath.StartsWith(EmbeddedMediaPrefix, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(asset.ProxyPath) &&
                !asset.ProxyPath.StartsWith(EmbeddedMediaPrefix, StringComparison.Ordinal)))
                throw new ProjectFileException(ProjectStageCode.Parse, "项目容器包含外部媒体引用，无法保证工程完整性。");
            if (embedded.Length != mediaReferenceCount)
                throw new ProjectFileException(ProjectStageCode.Parse,
                    "项目容器的多个媒体引用指向同一条目。");
            if (archive.Entries.Count != embedded.Length + 1)
                throw new ProjectFileException(ProjectStageCode.Parse,
                    "项目容器包含未引用或重复的条目。");
            long extractionBytes = 0;
            foreach (string reference in embedded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = GetEmbeddedEntry(archiveEntries, reference);
                extractionBytes = checked(extractionBytes + entry.Length);
            }
            string cacheRoot = Path.Combine(SessionCacheRoot, Guid.NewGuid().ToString("N"));
            if (embedded.Length > 0)
            {
                EnsureAvailableSpace(cacheRoot, extractionBytes, ProjectStageCode.Space);
                Directory.CreateDirectory(cacheRoot);
            }
            var extracted = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (string reference in embedded)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = GetEmbeddedEntry(archiveEntries, reference);
                    string extension = SafeExtension(entry.Name);
                    string cachedPath = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N") + extension);
                    using Stream fromArchive = entry.Open();
                    using FileStream output = new(cachedPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 128 * 1024, FileOptions.SequentialScan);
                    CopyWithCancellation(fromArchive, output, cancellationToken, entry.Length);
                    extracted.Add(reference, cachedPath);
                }
                foreach (MediaAsset asset in document.Media)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    asset.SourcePath = ResolveLoadedReference(projectPath, asset.SourcePath, extracted);
                    if (!string.IsNullOrWhiteSpace(asset.ProxyPath))
                    {
                        asset.ProxyPath = ResolveLoadedReference(projectPath, asset.ProxyPath, extracted);
                        asset.SetProxyAvailability(File.Exists(asset.ProxyPath));
                    }
                }
                if (embedded.Length > 0)
                    lock (CacheGate) CacheLeases.Add(document, new CacheLease(cacheRoot));
                return document;
            }
            catch
            {
                try { if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // The original extraction failure is more actionable.
                }
                throw;
            }
        }
        catch (ProjectFileException) { throw; }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new ProjectFileException(ProjectStageCode.Parse, "项目清单不是有效的 UTF-8。", exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or OverflowException or ArgumentException or NotSupportedException)
        {
            throw new ProjectFileException(ProjectStageCode.Read, "无法读取或展开项目文件。", exception);
        }
    }

    private static string ResolveLoadedReference(string projectPath, string reference,
        IReadOnlyDictionary<string, string> extracted)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new ProjectFileException(ProjectStageCode.Parse, "项目媒体路径为空。");
        if (reference.StartsWith(EmbeddedMediaPrefix, StringComparison.Ordinal))
            return extracted.TryGetValue(reference, out string? path) ? path :
                throw new ProjectFileException(ProjectStageCode.Parse, "项目媒体引用未找到对应内容。");
        return ResolveMediaPath(projectPath, reference);
    }

    /// <summary>
    /// Releases only this loaded project's extracted media. Call after closing
    /// preview and source-monitor handles when the editor changes projects.
    /// The process-exit cleanup remains a fallback for unexpected shutdowns.
    /// </summary>
    public static async Task ReleaseCachedMediaAsync(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CacheLease lease;
        lock (CacheGate)
        {
            if (!CacheLeases.TryGetValue(document, out CacheLease? found)) return;
            if (found.Releasing) return;
            lease = found;
            lease.Releasing = true;
        }
        try
        {
            await DeleteCacheAsync(lease);
            lock (CacheGate) CacheLeases.Remove(document);
        }
        finally
        {
            lock (CacheGate) lease.Releasing = false;
        }
    }

    private static Task DeleteCacheAsync(CacheLease lease) => Task.Run(() =>
    {
        try { if (Directory.Exists(lease.Path)) Directory.Delete(lease.Path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileException(ProjectStageCode.Read,
                "旧项目媒体缓存尚被播放器占用，稍后可重试清理。", exception);
        }
    });

    /// <summary>
    /// A history snapshot creates a new document object but keeps the same
    /// extracted absolute paths. Transfer its cache lease before replacing
    /// the active project, so a later project switch still cleans the files.
    /// </summary>
    public static void TransferCachedMedia(ProjectDocument previous, ProjectDocument replacement)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(previous, replacement)) return;
        lock (CacheGate)
        {
            if (!CacheLeases.TryGetValue(previous, out CacheLease? lease)) return;
            if (lease.Releasing)
                throw new InvalidOperationException("Cannot transfer a project cache during release.");
            CacheLeases.Remove(previous);
            CacheLeases.Remove(replacement);
            CacheLeases.Add(replacement, lease);
        }
    }

    private static ZipArchiveEntry GetEmbeddedEntry(
        IReadOnlyDictionary<string, ZipArchiveEntry[]> archiveEntries, string reference)
    {
        string name = reference[EmbeddedMediaPrefix.Length..];
        string[] parts = name.Split('/');
        if (parts.Length != 3 || parts[0] != "media" ||
            string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2]) ||
            parts[1] is "." or ".." || parts[2] is "." or ".." ||
            name.Contains('\\') || name.Length > 512 || name.Any(char.IsControl) ||
            parts[1].Any(character => !char.IsAsciiLetterOrDigit(character)) ||
            !(parts[2].StartsWith("source.", StringComparison.Ordinal) ||
                parts[2].StartsWith("proxy.", StringComparison.Ordinal)))
            throw new ProjectFileException(ProjectStageCode.Parse, "项目媒体引用路径无效。");
        if (!archiveEntries.TryGetValue(name, out ZipArchiveEntry[]? matches) || matches.Length != 1)
            throw new ProjectFileException(ProjectStageCode.Parse, $"项目容器的媒体内容缺失或重复：{name}");
        return matches[0];
    }

    private static string ReadManifest(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        // The declared uncompressed length is checked by the caller. Bound
        // actual decompression too, so a malformed ZIP header cannot expand
        // an unbounded JSON string in memory.
        using Stream source = entry.Open();
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bounded.Length + count > MaximumManifestBytes)
                throw new ProjectFileException(ProjectStageCode.Parse, "项目清单超过支持的大小。");
            bounded.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(bounded.GetBuffer(), 0, checked((int)bounded.Length));
    }

    private static string SafeExtension(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length is > 1 and <= 16 && extension.Skip(1).All(char.IsAsciiLetterOrDigit)
            ? extension : ".bin";
    }

    private static bool IsZipSignature(ReadOnlySpan<byte> signature) =>
        signature.Length == 4 && signature[0] == (byte)'P' && signature[1] == (byte)'K' &&
        signature[2] == 3 && signature[3] == 4;

    private static ProjectDocument ParseDocument(string json, CancellationToken cancellationToken)
    {
        ProjectDocument document;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument probe = JsonDocument.Parse(json);
            JsonElement root = probe.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out JsonElement formatElement) ||
                formatElement.ValueKind != JsonValueKind.String ||
                formatElement.GetString() != ProjectDocument.FormatIdentifier)
                throw new ProjectFileException(ProjectStageCode.Validate, "该文件不是 Tangerine 项目。");
            if (!root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out int version))
                throw new ProjectFileException(ProjectStageCode.Validate, "项目文件缺少有效版本号。");
            if (version > ProjectDocument.CurrentVersion)
                throw new ProjectFileException(ProjectStageCode.UnsupportedVersion,
                    $"此项目由较新版本创建（格式版本 {version}）。请使用较新版本的 TANGERINE 打开，当前版本不会修改该文件。");
            if (version < 1)
                throw new ProjectFileException(ProjectStageCode.UnsupportedVersion, $"不支持项目文件版本 {version}。");
            if (version < ProjectDocument.CurrentVersion)
                json = MigrateJson(json, version);
            cancellationToken.ThrowIfCancellationRequested();
            document = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
                ?? throw new JsonException("Empty project document.");
        }
        catch (ProjectFileException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ProjectFileException(ProjectStageCode.Parse, "项目文件格式无效。", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Validate(document);
        DropDanglingSubclips(document);
        MergeLegacyTrackKinds(document);
        return document;
    }

    /// <summary>
    /// Older projects stored text and subtitles on dedicated Overlay and Caption
    /// tracks. The editor only has video and audio tracks, so those become
    /// video tracks; their position and clips are untouched, so the picture
    /// stacks exactly as before.
    /// </summary>
    private static void MergeLegacyTrackKinds(ProjectDocument document)
    {
        foreach (TimelineTrack track in document.Tracks.Concat(document.Sequences.SelectMany(sequence => sequence.Tracks)))
            if (track.Kind is TrackKind.Overlay or TrackKind.Caption) track.Kind = TrackKind.Video;
    }

    /// <summary>
    /// Removes sub-clips whose parent media no longer exists, or whose range
    /// runs past the parent's known length. Deleting a media item leaves its
    /// sub-clips pointing at nothing, and refusing to open the project over a
    /// dead reference would cost the user far more than the orphan is worth,
    /// so the orphan is dropped instead.
    /// </summary>
    private static void DropDanglingSubclips(ProjectDocument document)
    {
        // Duplicate media ids are possible in a damaged file; building a lookup
        // that throws on them would make a cleanup step block the whole load.
        var parents = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        foreach (MediaAsset asset in document.Media)
            parents.TryAdd(asset.Id, asset);
        document.Subclips.RemoveAll(subclip =>
            !parents.TryGetValue(subclip.ParentAssetId, out MediaAsset? parent) ||
            // A known duration lets us reject a range that starts after the end
            // of the media. An unknown duration (a still image, say) is trusted.
            parent.DurationSeconds is double length && length > 0 &&
                subclip.InSeconds >= length);
    }

    public static async Task SaveAsync(ProjectDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string normalized = await Task.Run(() => NormalizePath(path), cancellationToken);
        await SaveGate.WaitAsync(cancellationToken);
        try { await Task.Run(() => SaveCore(document, normalized, cancellationToken), cancellationToken); }
        finally { SaveGate.Release(); }
    }

    /// <summary>
    /// Checks whether the selected project's volume can hold a complete new
    /// archive after the proposed imports. Pass every source and proxy path,
    /// including media already in the project, to avoid underestimating space.
    /// </summary>
    public static Task EnsureImportCapacityAsync(string projectPath, IEnumerable<string> projectedMediaPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectedMediaPaths);
        // Snapshot on the caller thread. A WPF view can mutate its media list
        // after the first await; enumerating it on the worker would race that
        // edit and occasionally throw or check the wrong archive footprint.
        string[] paths = projectedMediaPaths.ToArray();
        return Task.Run(() =>
        {
            if (paths.Length + 1L > MaximumArchiveEntries)
                throw new ProjectFileException(ProjectStageCode.Validate,
                    "项目媒体条目数量超过支持范围。");
            string normalized = NormalizePath(projectPath);
            long mediaBytes = MeasureMediaBytes(paths, cancellationToken);
            EnsureAvailableSpace(Path.GetDirectoryName(normalized)!, mediaBytes,
                ProjectStageCode.Space, paths.Length + 1);
        }, cancellationToken);
    }

    /// <summary>
    /// Before the first save, use the temporary volume as the editor's
    /// fallback staging location. The final Save As performs the authoritative
    /// check on the chosen destination volume.
    /// </summary>
    public static Task EnsureImportCapacityAsync(IEnumerable<string> projectedMediaPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectedMediaPaths);
        string[] paths = projectedMediaPaths.ToArray();
        return Task.Run(() =>
        {
            if (paths.Length + 1L > MaximumArchiveEntries)
                throw new ProjectFileException(ProjectStageCode.Validate,
                    "项目媒体条目数量超过支持范围。");
            long mediaBytes = MeasureMediaBytes(paths, cancellationToken);
            EnsureAvailableSpace(Path.GetTempPath(), mediaBytes, ProjectStageCode.Space,
                paths.Length + 1);
        }, cancellationToken);
    }

    private static void SaveCore(ProjectDocument document, string projectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.Media is null || document.Media.Any(asset => asset is null))
            throw new ProjectFileException(ProjectStageCode.Validate, "项目媒体数据不完整。");
        string? directory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new ProjectFileException(ProjectStageCode.InvalidPath, "项目保存目录不存在。");
        }
        EnsureDestinationIsWritableVersion(projectPath, cancellationToken);
        ProjectDocument stored;
        try
        {
            string serialized = JsonSerializer.Serialize(document, JsonOptions);
            stored = JsonSerializer.Deserialize<ProjectDocument>(serialized, JsonOptions) ??
                throw new JsonException("Empty project document.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ProjectFileException(ProjectStageCode.Validate, "无法复制项目数据。", exception);
        }
        Validate(stored);
        stored.ModifiedUtc = DateTimeOffset.UtcNow;
        var content = new List<(string Entry, string Source)>();
        var archiveEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (MediaAsset asset in stored.Media)
        {
            cancellationToken.ThrowIfCancellationRequested();
            asset.SourcePath = MakeEmbeddedReference(asset.Id, "source", asset.SourcePath,
                content, archiveEntries, projectPath);
            if (!string.IsNullOrWhiteSpace(asset.ProxyPath))
                asset.ProxyPath = MakeEmbeddedReference(asset.Id, "proxy", asset.ProxyPath,
                    content, archiveEntries, projectPath);
        }
        if (content.Count + 1 > MaximumArchiveEntries)
            throw new ProjectFileException(ProjectStageCode.Validate,
                "项目媒体条目数量超过支持范围。");
        long mediaBytes = MeasureArchiveMediaBytes(content.Select(item => item.Source), cancellationToken);
        string json;
        try { json = JsonSerializer.Serialize(stored, JsonOptions); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new ProjectFileException(ProjectStageCode.Validate, "无法序列化项目清单。", exception);
        }
        // The manifest remains small relative to media, but account for it
        // explicitly when projects contain many short clips and markers.
        long manifestBytes = Encoding.UTF8.GetByteCount(json);
        if (manifestBytes > MaximumManifestBytes)
            throw new ProjectFileException(ProjectStageCode.Validate,
                "项目清单超过支持的大小。");
        try
        {
            EnsureAvailableSpace(directory, checked(mediaBytes + manifestBytes),
                ProjectStageCode.Space, content.Count + 1);
        }
        catch (OverflowException exception)
        {
            throw new ProjectFileException(ProjectStageCode.Space, "项目容量超过可支持范围。", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(projectPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                // Media is already compressed by most camera codecs. Store it
                // unchanged for linear streaming; only the small manifest is
                // compressed to keep parse and save latency low.
                using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                {
                    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        ZipArchiveEntry manifest = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                        using (StreamWriter writer = new(manifest.Open(), new UTF8Encoding(false))) writer.Write(json);
                        foreach ((string entryName, string sourcePath) in content)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                            using Stream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
                            using Stream destination = entry.Open();
                            CopyWithCancellation(source, destination, cancellationToken);
                        }
                    }
                    output.Flush(flushToDisk: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ProjectFileException(ProjectStageCode.Write, "无法写入项目文件。", exception);
            }
            try
            {
                // Recheck just before replacement in case another editor wrote
                // a newer project format while this save was being prepared.
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDestinationIsWritableVersion(projectPath, cancellationToken);
                File.Move(temporaryPath, projectPath, overwrite: true);
                document.ModifiedUtc = stored.ModifiedUtc;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ProjectFileException(ProjectStageCode.Replace, "无法完成项目保存。", exception);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A failed cleanup must not conceal the original save error.
            }
        }
    }

    private static string MakeEmbeddedReference(string assetId, string role, string path,
        List<(string Entry, string Source)> content, HashSet<string> archiveEntries, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ProjectFileException(ProjectStageCode.Validate, "媒体文件路径为空。");
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ProjectFileException(ProjectStageCode.Validate, "媒体标识为空，无法嵌入项目容器。");
        if (path.StartsWith(EmbeddedMediaPrefix, StringComparison.Ordinal))
            throw new ProjectFileException(ProjectStageCode.Validate, "媒体尚未从项目容器提取，无法保存。");
        // The editor keeps loaded and newly imported paths absolute. Relative
        // paths are accepted only for legacy JSON projects before the first
        // portable save, where they belong to the original project's folder.
        string source;
        try { source = ResolveMediaPath(projectPath, path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectFileException(ProjectStageCode.InvalidPath, "媒体文件路径无效。", exception);
        }
        if (!File.Exists(source))
            throw new ProjectFileException(ProjectStageCode.Read, $"媒体已离线，无法嵌入项目：{Path.GetFileName(source)}");
        string safeId = !string.IsNullOrWhiteSpace(assetId) && assetId.Length <= 64 &&
            assetId.All(char.IsAsciiLetterOrDigit)
            ? assetId : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assetId)))[..32];
        string entry = $"media/{safeId}/{role}{SafeExtension(source)}";
        if (!archiveEntries.Add(entry))
            throw new ProjectFileException(ProjectStageCode.Validate, "媒体标识重复，无法写入项目容器。");
        content.Add((entry, source));
        return EmbeddedMediaPrefix + entry;
    }

    private static void CopyWithCancellation(Stream input, Stream output, CancellationToken cancellationToken,
        long? expectedBytes = null)
    {
        byte[] buffer = new byte[128 * 1024];
        int read;
        long copied = 0;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied = checked(copied + read);
            if (expectedBytes is long maximum && copied > maximum)
                throw new InvalidDataException("Archive media exceeds its declared size.");
            output.Write(buffer, 0, read);
        }
        if (expectedBytes is long expected && copied != expected)
            throw new InvalidDataException("Archive media is shorter than its declared size.");
    }

    private static long MeasureMediaBytes(IEnumerable<string> paths, CancellationToken cancellationToken)
        => MeasureArchiveMediaBytes(paths.Where(path => !string.IsNullOrWhiteSpace(path)), cancellationToken);

    private static long MeasureArchiveMediaBytes(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        long bytes = 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists) throw new FileNotFoundException("媒体文件不存在。", file.FullName);
                bytes = checked(bytes + file.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                OverflowException or ArgumentException or NotSupportedException)
            {
                throw new ProjectFileException(ProjectStageCode.Read, "无法读取媒体大小以检查剩余空间。", exception);
            }
        }
        return bytes;
    }

    private static void EnsureAvailableSpace(string destinationDirectory, long contentBytes,
        ProjectStageCode stage, int archiveEntryCount = 0)
    {
        try
        {
            string absolute = Path.GetFullPath(destinationDirectory);
            while (!Directory.Exists(absolute))
            {
                string? parent = Path.GetDirectoryName(absolute);
                if (string.IsNullOrWhiteSpace(parent) || parent == absolute)
                    throw new DirectoryNotFoundException("Destination directory is unavailable.");
                absolute = parent;
            }
            if (contentBytes < 0 || archiveEntryCount < 0)
                throw new ProjectFileException(stage, "项目容量不能为负数。");
            // Even a tiny media file needs a local header and central index
            // entry. Large bins can make ZIP metadata exceed the fixed margin.
            long safetyBytes = Math.Max(ArchiveSafetyBytes,
                Math.Max(contentBytes / 100, checked((long)archiveEntryCount * 1024)));
            if (OperatingSystem.IsWindows())
            {
                // Query the actual destination directory. This also handles
                // UNC shares and mounted volumes nested under a drive letter.
                if (!GetDiskFreeSpaceEx(absolute, out ulong freeBytes, out _, out _))
                    throw new IOException("Destination volume free-space query failed.");
                ulong requiredWindows = (ulong)checked(contentBytes + safetyBytes);
                if (freeBytes < requiredWindows)
                    throw new ProjectFileException(stage,
                        $"目标磁盘空间不足；约需 {requiredWindows / (1024 * 1024)} MiB，可用 {freeBytes / (1024 * 1024)} MiB。");
                return;
            }
            string root = Path.GetPathRoot(absolute)!;
            long free = new DriveInfo(root).AvailableFreeSpace;
            long required = checked(contentBytes + safetyBytes);
            if (free < required)
                throw new ProjectFileException(stage,
                    $"目标磁盘空间不足；约需 {required / (1024 * 1024)} MiB，可用 {free / (1024 * 1024)} MiB。");
        }
        catch (ProjectFileException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            throw new ProjectFileException(stage, "无法检查目标磁盘剩余空间。", exception);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directoryName,
        out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);

    private static void EnsureDestinationIsWritableVersion(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;
        string existingJson;
        try
        {
            using FileStream existing = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[4];
            if (existing.Read(signature) != signature.Length)
                throw new ProjectFileException(ProjectStageCode.Parse, "现有项目文件为空或已损坏，不会覆盖。");
            existing.Position = 0;
            if (IsZipSignature(signature))
            {
                using var archive = new ZipArchive(existing, ZipArchiveMode.Read, leaveOpen: false);
                if (archive.Entries.Count > MaximumArchiveEntries)
                    throw new ProjectFileException(ProjectStageCode.Parse,
                        "现有项目容器条目数量超过支持范围，不会覆盖。");
                ZipArchiveEntry[] manifests = archive.Entries.Where(entry => entry.FullName == ManifestEntryName)
                    .Take(2).ToArray();
                if (manifests.Length != 1)
                    throw new ProjectFileException(ProjectStageCode.Parse, "现有项目容器清单缺失或重复。");
                ZipArchiveEntry manifest = manifests[0];
                if (manifest.Length > MaximumManifestBytes)
                    throw new ProjectFileException(ProjectStageCode.Parse, "现有项目清单超过支持的大小。");
                existingJson = ReadManifest(manifest, cancellationToken);
            }
            else
            {
                if (existing.Length > MaximumManifestBytes)
                    throw new ProjectFileException(ProjectStageCode.Parse, "现有项目清单超过支持的大小。");
                using var reader = new StreamReader(existing, new UTF8Encoding(false, true));
                existingJson = reader.ReadToEnd();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or NotSupportedException)
        {
            throw new ProjectFileException(ProjectStageCode.Read, "无法检查现有项目文件版本。", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(existingJson);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out JsonElement formatElement) ||
                formatElement.ValueKind != JsonValueKind.String ||
                formatElement.GetString() != ProjectDocument.FormatIdentifier ||
                !root.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out int version))
                throw new ProjectFileException(ProjectStageCode.Validate,
                    "目标文件不是有效的 Tangerine 项目，不会覆盖。");
            if (version > ProjectDocument.CurrentVersion)
                throw new ProjectFileException(ProjectStageCode.UnsupportedVersion,
                    $"目标文件使用较新的项目格式版本 {version}，当前版本不会覆盖它。");
        }
        catch (JsonException exception)
        {
            throw new ProjectFileException(ProjectStageCode.Parse,
                "现有项目清单无效，不会覆盖可能有价值的数据。", exception);
        }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new ProjectFileException(ProjectStageCode.Parse,
                "现有项目清单不是有效的 UTF-8，不会覆盖。", exception);
        }
    }

    private static string MigrateJson(string json, int version)
    {
        try
        {
            JsonObject root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Empty project document.");
            for (int sourceVersion = version; sourceVersion < ProjectDocument.CurrentVersion; sourceVersion++)
            {
                Func<JsonObject, JsonObject>? migration;
                lock (MigrationGate)
                {
                    if (!Migrations.TryGetValue(sourceVersion, out migration))
                        throw new ProjectFileException(ProjectStageCode.Migrate,
                            $"缺少从项目格式版本 {sourceVersion} 升级所需的迁移步骤。");
                }
                root = migration(root);
                root["version"] = sourceVersion + 1;
            }
            return root.ToJsonString();
        }
        catch (ProjectFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ProjectFileException(ProjectStageCode.Migrate, "项目文件升级失败。", exception);
        }
    }

    public static string ResolveMediaPath(string projectPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ProjectFileException(ProjectStageCode.InvalidPath, "媒体文件路径为空。");
        if (Path.IsPathFullyQualified(sourcePath)) return sourcePath;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, sourcePath));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProjectFileException(ProjectStageCode.InvalidPath, "请选择项目文件路径。");
        }

        try
        {
            string absolutePath = Path.GetFullPath(path);
            if (Directory.Exists(absolutePath))
                throw new ProjectFileException(ProjectStageCode.InvalidPath, "项目路径指向文件夹。");
            if (!string.Equals(Path.GetExtension(absolutePath), ProjectDocument.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectFileException(ProjectStageCode.InvalidPath, "项目文件必须使用 .tvep 后缀名。");
            }
            return absolutePath;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ProjectFileException(ProjectStageCode.InvalidPath, "项目文件路径无效。", exception);
        }
    }

    private static bool InvalidSequenceTime(ProjectSequence sequence) =>
        !double.IsFinite(sequence.PlayheadSeconds) || sequence.PlayheadSeconds < 0 ||
        !double.IsFinite(sequence.WorkAreaStartSeconds) || sequence.WorkAreaStartSeconds < 0 ||
        sequence.WorkAreaEndSeconds is double end &&
            (!double.IsFinite(end) || end <= sequence.WorkAreaStartSeconds);

    internal static void Validate(ProjectDocument document)
    {
        if (!string.Equals(document.Format, ProjectDocument.FormatIdentifier, StringComparison.Ordinal))
        {
            throw new ProjectFileException(ProjectStageCode.Validate, "该文件不是 Tangerine 项目。");
        }
        if (document.Version != ProjectDocument.CurrentVersion)
        {
            throw new ProjectFileException(ProjectStageCode.UnsupportedVersion,
                $"不支持项目文件版本 {document.Version}，不会修改目标文件。");
        }
        if (document.Sequence is null || document.Sequence.Width <= 0 || document.Sequence.Height <= 0 ||
            !double.IsFinite(document.Sequence.FramesPerSecond) || document.Sequence.FramesPerSecond <= 0)
        {
            throw new ProjectFileException(ProjectStageCode.Validate, "项目画布或帧率无效。");
        }
        if (document.Tracks is null || document.Media is null || document.Subclips is null || document.Markers is null ||
            document.Sequences is null || document.Bins is null || document.MulticamGroups is null)
        {
            throw new ProjectFileException(ProjectStageCode.Validate, "项目数据不完整。");
        }
        if (document.Tracks.Any(track => track is null) ||
            document.Subclips.Any(subclip => subclip is null || string.IsNullOrWhiteSpace(subclip.ParentAssetId) ||
                !double.IsFinite(subclip.InSeconds) || !double.IsFinite(subclip.OutSeconds) ||
                subclip.InSeconds < 0 || subclip.OutSeconds <= subclip.InSeconds) ||
            document.Sequences.Any(sequence => sequence is null) ||
            document.Media.Any(asset => asset is null) ||
            document.MulticamGroups.Any(group => group is null))
            throw new ProjectFileException(ProjectStageCode.Validate, "项目包含空的编辑对象。");
        if (document.Sequences.Any(sequence => sequence.Sequence is null || sequence.Tracks is null ||
            sequence.Tracks.Any(track => track is null)))
            throw new ProjectFileException(ProjectStageCode.Validate, "序列轨道数据不完整。");
        if (document.Sequences.Any(sequence => sequence.Markers is null))
            throw new ProjectFileException(ProjectStageCode.Validate, "序列标记数据不完整。");
        if (document.Tracks.Concat(document.Sequences.SelectMany(sequence => sequence.Tracks))
            .Any(track => track.Clips is null || track.Clips.Any(clip => clip is null)))
            throw new ProjectFileException(ProjectStageCode.Validate, "轨道片段数据不完整。");
        if (document.MulticamGroups.Any(group => group.Angles is null ||
            group.Angles.Any(angle => angle is null || angle.SourceClip is null)))
            throw new ProjectFileException(ProjectStageCode.Validate, "多机位源数据不完整。");
        if (InvalidSequenceTime(document.Sequence) ||
            document.Sequences.Any(workspace => InvalidSequenceTime(workspace.Sequence)))
            throw new ProjectFileException(ProjectStageCode.Validate, "序列播放头或工作区范围无效。");
        if (document.Markers.Any(marker => marker is null || !double.IsFinite(marker.TimeSeconds) ||
            marker.TimeSeconds < 0) || document.Sequences.Any(sequence => sequence.Markers is null ||
            sequence.Markers.Any(marker => marker is null || !double.IsFinite(marker.TimeSeconds) ||
                marker.TimeSeconds < 0)))
            throw new ProjectFileException(ProjectStageCode.Validate, "序列标记数据无效。");
        var sequenceIds = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(document.Sequence.Id) || !sequenceIds.Add(document.Sequence.Id))
            throw new ProjectFileException(ProjectStageCode.Validate, "当前序列标识无效。");
        foreach (ProjectSequenceWorkspace workspace in document.Sequences)
        {
            if (workspace.Sequence is null || workspace.Tracks is null || workspace.Markers is null ||
                string.IsNullOrWhiteSpace(workspace.Sequence.Id) || !sequenceIds.Add(workspace.Sequence.Id))
                throw new ProjectFileException(ProjectStageCode.Validate, "序列数据不完整或标识重复。");
            if (workspace.Sequence.Width <= 0 || workspace.Sequence.Height <= 0 ||
                !double.IsFinite(workspace.Sequence.FramesPerSecond) || workspace.Sequence.FramesPerSecond <= 0)
                throw new ProjectFileException(ProjectStageCode.Validate, "序列画布或帧率无效。");
        }
        var binIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ProjectBin bin in document.Bins)
            if (bin is null || string.IsNullOrWhiteSpace(bin.Id) || !binIds.Add(bin.Id))
                throw new ProjectFileException(ProjectStageCode.Validate, "素材箱标识无效或重复。");
        foreach (ProjectBin bin in document.Bins)
            if (bin.ParentId is not null && (!binIds.Contains(bin.ParentId) || bin.ParentId == bin.Id))
                throw new ProjectFileException(ProjectStageCode.Validate, "素材箱父级引用无效。");
        var byBinId = document.Bins.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (ProjectBin bin in document.Bins)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { bin.Id };
            string? parent = bin.ParentId;
            while (parent is not null)
            {
                if (!visited.Add(parent))
                    throw new ProjectFileException(ProjectStageCode.Validate, "素材箱包含循环父级引用。");
                parent = byBinId[parent].ParentId;
            }
        }
        foreach (MediaAsset asset in document.Media)
        {
            if (asset is null || asset.Tags is null || asset.Metadata is null || asset.Markers is null)
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体素材的标签或标记数据不完整。");
            if (string.IsNullOrWhiteSpace(asset.Id) || string.IsNullOrWhiteSpace(asset.SourcePath))
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体标识或路径无效。");
            if (asset.BinId is not null && !binIds.Contains(asset.BinId))
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体素材引用了不存在的素材箱。");
            if (asset.Markers.Any(marker => marker is null || !double.IsFinite(marker.TimeSeconds) ||
                marker.TimeSeconds < 0))
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体源标记无效。");
            if (!double.IsFinite(asset.SourceInSeconds) || asset.SourceInSeconds < 0 ||
                asset.SourceOutSeconds is double sourceOut && (!double.IsFinite(sourceOut) || sourceOut <= asset.SourceInSeconds))
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体源入出点无效。");
            if (asset.DurationSeconds is double duration && (!double.IsFinite(duration) || duration < 0))
                throw new ProjectFileException(ProjectStageCode.Validate, "媒体时长无效。");
        }
        if (document.Media.Count > (MaximumArchiveEntries - 1) / 2)
            throw new ProjectFileException(ProjectStageCode.Validate, "项目媒体数量超过支持范围。");
        if (document.Media.Select(asset => asset.Id).Distinct(StringComparer.Ordinal).Count() != document.Media.Count)
            throw new ProjectFileException(ProjectStageCode.Validate, "媒体标识重复。");
        var mediaIds = document.Media.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        var multicamIds = new HashSet<string>(StringComparer.Ordinal);
        var anglesByGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (MulticamGroup group in document.MulticamGroups)
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Id) || !multicamIds.Add(group.Id) ||
                !double.IsFinite(group.DurationSeconds) || group.DurationSeconds <= 0 ||
                group.Angles is null || group.Angles.Count is < 2 or > 16)
                throw new ProjectFileException(ProjectStageCode.Validate, "多机位组数据无效。");
            var angleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (MulticamAngle angle in group.Angles)
                if (angle is null || string.IsNullOrWhiteSpace(angle.Id) || !angleIds.Add(angle.Id) ||
                    angle.SourceClip is null || angle.SourceClip.Kind != ClipKind.Video ||
                    angle.SourceClip.MediaAssetId is not string mediaId || !mediaIds.Contains(mediaId) ||
                    !double.IsFinite(angle.SourceClip.DurationSeconds) ||
                    angle.SourceClip.DurationSeconds < group.DurationSeconds - .001)
                    throw new ProjectFileException(ProjectStageCode.Validate, "多机位角度或源素材引用无效。");
            anglesByGroup.Add(group.Id, angleIds);
        }
        if (document.MulticamGroups.Any(group => group.Angles.Any(angle =>
            angle.SourceClip.SourceInSeconds < 0 ||
            !double.IsFinite(angle.SourceClip.SourceInSeconds) ||
            angle.SourceClip.TimeRemapPoints is null ||
            angle.SourceClip.Keyframes is null ||
            !double.IsFinite(angle.SourceClip.Speed) || angle.SourceClip.Speed <= 0 ||
            angle.SourceClip.IsReversed)))
            throw new ProjectFileException(ProjectStageCode.Validate, "多机位源片段数据无效。");
        foreach (TimelineTrack track in document.Sequences.SelectMany(item => item.Tracks).Concat(document.Tracks))
        {
            if (track is null || track.Clips is null)
                throw new ProjectFileException(ProjectStageCode.Validate, "轨道数据不完整。");
            foreach (TimelineClip clip in track.Clips)
            {
                if (clip is null)
                    throw new ProjectFileException(ProjectStageCode.Validate, "轨道包含无效片段。");
                if (clip.Kind == ClipKind.Sequence &&
                    (string.IsNullOrWhiteSpace(clip.NestedSequenceId) || !sequenceIds.Contains(clip.NestedSequenceId)))
                    throw new ProjectFileException(ProjectStageCode.Validate, "时间轴引用了不存在的嵌套序列。");
                if (clip.Kind is (ClipKind.Video or ClipKind.Audio or ClipKind.Image) &&
                    (string.IsNullOrWhiteSpace(clip.MediaAssetId) || !mediaIds.Contains(clip.MediaAssetId)))
                    throw new ProjectFileException(ProjectStageCode.Validate, "时间轴引用了不存在的媒体素材。");
            }
        }
        var sequenceTracks = document.Sequences.ToDictionary(item => item.Sequence.Id, item => item.Tracks,
            StringComparer.Ordinal);
        sequenceTracks.Add(document.Sequence.Id, document.Tracks);
        foreach (string sequenceId in sequenceIds)
            CheckSequenceCycles(sequenceId, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        void CheckSequenceCycles(string sequenceId, HashSet<string> visiting, HashSet<string> visited)
        {
            if (visiting.Contains(sequenceId))
                throw new ProjectFileException(ProjectStageCode.Validate, "序列嵌套包含循环引用。");
            if (!visited.Add(sequenceId)) return;
            visiting.Add(sequenceId);
            foreach (TimelineClip nested in sequenceTracks[sequenceId].SelectMany(track => track.Clips)
                .Where(clip => clip.Kind == ClipKind.Sequence && clip.NestedSequenceId is not null))
                CheckSequenceCycles(nested.NestedSequenceId!, visiting, visited);
            visiting.Remove(sequenceId);
        }
        foreach (TimelineTrack track in document.Sequences.SelectMany(item => item.Tracks).Concat(document.Tracks))
        {
            if (track.Clips is null)
            {
                throw new ProjectFileException(ProjectStageCode.Validate, "轨道数据不完整。");
            }
            foreach (TimelineClip clip in track.Clips)
            {
                if (clip.Transform is null || clip.Effects is null || clip.Transitions is null ||
                    clip.Effects.Any(effect => effect is null || effect.Parameters is null) ||
                    clip.Transitions.Any(transition => transition is null || transition.Parameters is null))
                    throw new ProjectFileException(ProjectStageCode.Validate,
                        "片段变换、效果或转场数据不完整。");
                ClipTransform transform = clip.Transform;
                if (!double.IsFinite(transform.PositionX) || !double.IsFinite(transform.PositionY) ||
                    !double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0 ||
                    !double.IsFinite(transform.ScaleY) || transform.ScaleY <= 0 ||
                    !double.IsFinite(transform.RotationDegrees) ||
                    !double.IsFinite(transform.AnchorX) || transform.AnchorX is < 0 or > 1 ||
                    !double.IsFinite(transform.AnchorY) || transform.AnchorY is < 0 or > 1 ||
                    !double.IsFinite(transform.CropLeft) || transform.CropLeft is < 0 or >= 1 ||
                    !double.IsFinite(transform.CropTop) || transform.CropTop is < 0 or >= 1 ||
                    !double.IsFinite(transform.CropRight) || transform.CropRight is < 0 or >= 1 ||
                    !double.IsFinite(transform.CropBottom) || transform.CropBottom is < 0 or >= 1 ||
                    transform.CropLeft + transform.CropRight >= 1 ||
                    transform.CropTop + transform.CropBottom >= 1)
                    throw new ProjectFileException(ProjectStageCode.Validate, "片段变换参数无效。");
                if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
                    !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
                    !double.IsFinite(clip.Speed) || clip.Speed <= 0)
                {
                    throw new ProjectFileException(ProjectStageCode.Validate, "剪辑片段的时间或速度无效。");
                }
                if (clip.Mask is ClipMask mask &&
                    (mask.Shape is not ("Rectangle" or "Ellipse" or "Path") ||
                     !double.IsFinite(mask.CenterX) || mask.CenterX is < 0 or > 1 ||
                     !double.IsFinite(mask.CenterY) || mask.CenterY is < 0 or > 1 ||
                     !double.IsFinite(mask.Width) || mask.Width is <= 0 or > 1 ||
                     !double.IsFinite(mask.Height) || mask.Height is <= 0 or > 1 ||
                     !double.IsFinite(mask.Feather) || mask.Feather is < 0 or > .5 ||
                     mask.Shape == "Path" && (mask.Points is null || mask.Points.Count is < 3 or > 32 ||
                         mask.Points.Any(point => point is null ||
                             !double.IsFinite(point.X) || point.X is < 0 or > 1 ||
                             !double.IsFinite(point.Y) || point.Y is < 0 or > 1))))
                    throw new ProjectFileException(ProjectStageCode.Validate,
                        "蒙版形状、尺寸或自由路径顶点无效。");
                if (clip.TimeRemapPoints is null ||
                    clip.TimeRemapPoints.Any(point => point is null ||
                        !double.IsFinite(point.TimeSeconds) ||
                        point.TimeSeconds < 0 || point.TimeSeconds >= clip.DurationSeconds ||
                        !double.IsFinite(point.Speed) || point.Speed < .05 || point.Speed > 32) ||
                    clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds)
                        .Zip(clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds).Skip(1),
                            (left, right) => right.TimeSeconds - left.TimeSeconds)
                        .Any(gap => gap < .000001) ||
                    clip.FrameInterpolation is not ("Sampling" or "Blend" or "OpticalFlow"))
                    throw new ProjectFileException(ProjectStageCode.Validate, "时间重映射或帧插值设置无效。");
                if (clip.Keyframes is null || clip.Keyframes.Any(key =>
                    key is null || string.IsNullOrWhiteSpace(key.Property) ||
                    !double.IsFinite(key.TimeSeconds) || key.TimeSeconds < 0 ||
                    key.TimeSeconds > clip.DurationSeconds + .000001 || !double.IsFinite(key.Value) ||
                    key.Interpolation is not ("Linear" or "Hold" or "EaseInOut" or "Bezier") ||
                    !double.IsFinite(key.BezierOutTime) || !double.IsFinite(key.BezierInTime) ||
                    !double.IsFinite(key.BezierOutValue) || !double.IsFinite(key.BezierInValue) ||
                    key.BezierOutTime < 0 || key.BezierOutTime > 1 ||
                    key.BezierInTime < 0 || key.BezierInTime > 1 ||
                    key.BezierOutValue < -4 || key.BezierOutValue > 4 ||
                    key.BezierInValue < -4 || key.BezierInValue > 4))
                    throw new ProjectFileException(ProjectStageCode.Validate, "片段关键帧数据无效。");
                // Time handles belong to opposite keys on a span. Checking
                // their ordering within one key would reject valid adjacent
                // curves and allow an invalid curve between two keys.
                if (clip.Keyframes.GroupBy(key => key.Property).Any(group =>
                    group.OrderBy(key => key.TimeSeconds)
                        .Zip(group.OrderBy(key => key.TimeSeconds).Skip(1),
                            (left, right) => left.Interpolation == "Bezier" &&
                                left.BezierOutTime > right.BezierInTime)
                        .Any(invalid => invalid)))
                    throw new ProjectFileException(ProjectStageCode.Validate,
                        "贝塞尔关键帧的时间控制柄顺序无效。");
                if (clip.MulticamGroupId is not null &&
                    (clip.Kind != ClipKind.Video ||
                     !multicamIds.Contains(clip.MulticamGroupId) ||
                     clip.MulticamAngleId is null ||
                     !anglesByGroup[clip.MulticamGroupId].Contains(clip.MulticamAngleId) ||
                     !double.IsFinite(clip.MulticamLocalStartSeconds) || clip.MulticamLocalStartSeconds < 0 ||
                     clip.MulticamLocalStartSeconds + clip.DurationSeconds >
                     document.MulticamGroups.First(group => group.Id == clip.MulticamGroupId)
                         .DurationSeconds + .001 ||
                     clip.MediaAssetId != document.MulticamGroups.First(group =>
                         group.Id == clip.MulticamGroupId).Angles.First(angle =>
                         angle.Id == clip.MulticamAngleId).SourceClip.MediaAssetId))
                    throw new ProjectFileException(ProjectStageCode.Validate, "多机位切段引用无效。");
            }
        }
    }
}
