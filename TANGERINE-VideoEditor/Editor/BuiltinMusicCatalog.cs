using System.IO;
using System.Text.Json;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// A packaged instrumental recording, with its original author and public
/// domain dedication kept next to the asset. Paths resolve inside the shipped
/// resource directory and never depend on an online catalog or user cache.
/// </summary>
public sealed record BuiltinMusicTrack(
    string Id, string Title, string Artist, string Genre, string Mood,
    double DurationSeconds, string AudioPath, string ThumbnailPath,
    string SourceUrl, string LicenseId, string LicenseUrl)
{
    public string Attribution => $"{Title} · {Artist} · {LicenseId} · {SourceUrl}";
}

/// <summary>
/// Reads the curated offline library once. The manifest is data, not code:
/// reject duplicate identities and escaping paths before any item can be
/// previewed or inserted. Asset existence is checked when used so opening the
/// catalog does not perform a hundred audio decodes or hashes on the UI thread.
/// </summary>
public static class BuiltinMusicCatalog
{
    private static readonly Lazy<IReadOnlyList<BuiltinMusicTrack>> Catalog = new(Load);
    public static IReadOnlyList<BuiltinMusicTrack> Tracks => Catalog.Value;

    public static BuiltinMusicTrack? Find(string id) => Tracks.FirstOrDefault(track => track.Id == id);

    public static BuiltinMusicTrack Get(string id) => Find(id) ??
        throw new StageException("MUSIC0002", "找不到指定的内置音乐，请重新读取音乐目录。");

    private static IReadOnlyList<BuiltinMusicTrack> Load()
    {
        try
        {
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Resources", "BuiltinMusic"));
            string manifestPath = Path.Combine(root, "manifest.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("不支持的内置音乐清单版本。");
            var tracks = new List<BuiltinMusicTrack>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in manifest.GetProperty("tracks").EnumerateArray())
            {
                string id = Required(item, "id");
                if (!id.StartsWith("music.", StringComparison.Ordinal) || !identities.Add(id))
                    throw new InvalidDataException("内置音乐的曲目标识无效或重复。");
                double duration = item.GetProperty("durationSeconds").GetDouble();
                if (!double.IsFinite(duration) || duration <= 0 || duration > 24 * 3600)
                    throw new InvalidDataException("内置音乐的时长无效。");
                string license = Required(item, "licenseId");
                if (license != "CC0-1.0")
                    throw new InvalidDataException("此内置曲目没有登记 CC0 公共领域授权。");
                tracks.Add(new BuiltinMusicTrack(id, Required(item, "title"), Required(item, "artist"),
                    Required(item, "genre"), Required(item, "mood"), duration,
                    ResourcePath(root, Required(item, "audioFile"), "audio"),
                    ResourcePath(root, Required(item, "thumbnailFile"), "thumbnails"),
                    Required(item, "sourceUrl"), license, Required(item, "licenseUrl")));
            }
            if (tracks.Count != 100)
                throw new InvalidDataException("内置音乐清单应包含完整的 100 首曲目。");
            return tracks.AsReadOnly();
        }
        catch (StageException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException or KeyNotFoundException or FormatException or
            ArgumentException or NotSupportedException or OverflowException)
        {
            throw new StageException("MUSIC0001", "无法读取内置音乐资源，请确认安装目录中的音乐资源完整。", exception);
        }
    }

    private static string Required(JsonElement item, string property) =>
        item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new InvalidDataException($"音乐清单缺少字段：{property}。");

    private static string ResourcePath(string root, string relative, string folder)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string expected = Path.Combine(root, folder) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || !path.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("音乐清单中的资源路径超出安装目录。");
        return path;
    }
}
