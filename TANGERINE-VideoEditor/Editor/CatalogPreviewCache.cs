using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Stores completed demonstrations independently of the disposable playback
/// files owned by the window. Only a successful, uncancelled encode is published
/// by an atomic rename. A player can delete its lease without losing the cache.
/// </summary>
internal static class CatalogPreviewCache
{
    private const long MaximumBytes = 256L * 1024 * 1024;
    private const int MaximumEntries = 128;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TangerineVideoEditor", "catalog-previews");

    public static string Key(string ffmpegPath, string content)
    {
        // ModuleVersionId changes on every renderer rebuild. Encoder identity,
        // sample revision and serialized title style prevent stale pictures
        // after a binary, demo source or preset has changed.
        string encoder = PreviewEncodingProfile.EncoderIdentity(ffmpegPath);
        string identity = $"catalog-v1|landscape-v2|{typeof(CatalogPreviewService).Module.ModuleVersionId}|{encoder}|{content}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static bool TryCopy(string key, string playbackPath)
    {
        string cached = Path.Combine(DirectoryPath, key + ".mp4");
        try
        {
            if (!File.Exists(cached) || new FileInfo(cached).Length == 0) return false;
            File.Copy(cached, playbackPath, overwrite: true);
            File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A cache eviction or a read-only profile cannot prevent rendering.
            return false;
        }
    }

    public static void Store(string key, string renderedPath, CancellationToken cancellationToken)
    {
        string? pending = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);
            string cached = Path.Combine(DirectoryPath, key + ".mp4");
            pending = Path.Combine(DirectoryPath, key + "." + Guid.NewGuid().ToString("N") + ".partial");
            File.Copy(renderedPath, pending);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, cached, overwrite: true);
            Trim(cached);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (pending is not null) File.Delete(pending); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Trim(string currentPath)
    {
        // A process crash can leave an unpublished copy. Leave young files
        // alone because another editor process may still be writing them.
        foreach (FileInfo pending in new DirectoryInfo(DirectoryPath).GetFiles("*.partial"))
            if (pending.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                try { pending.Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        FileInfo[] files = new DirectoryInfo(DirectoryPath).GetFiles("*.mp4")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        for (int index = 0; index < files.Length; index++)
        {
            bytes += files[index].Length;
            if ((index >= MaximumEntries || bytes > MaximumBytes) &&
                !files[index].FullName.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                try { files[index].Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
