using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Reads optional source metadata outside the UI thread. A failed probe does
/// not reject a media asset: the user can still relink or place it later.
/// </summary>
public static class MediaMetadataProbe
{
    public static async Task<MediaMetadata?> TryReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string executable = File.Exists(bundled) ? bundled : "ffprobe";
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        } };
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-show_entries");
        process.StartInfo.ArgumentList.Add("format=duration,size:format_tags:stream=index,codec_type,codec_name,width,height,r_frame_rate,channels,sample_rate:stream_tags");
        process.StartInfo.ArgumentList.Add("-of");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (!process.Start()) return null;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string json = await stdout;
            await stderr;
            if (process.ExitCode != 0) return null;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            double? duration = null;
            int? width = null;
            int? height = null;
            double? frameRate = null;
            bool hasAudio = false;
            string? videoCodec = null;
            string? audioCodec = null;
            int? audioChannels = null;
            int? audioSampleRate = null;
            var audioStreams = new List<SourceAudioStream>();
            long? fileSize = null;
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("format", out JsonElement format) &&
                format.TryGetProperty("duration", out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
                double.IsFinite(seconds) && seconds > 0)
                duration = seconds;
            if (root.TryGetProperty("format", out format))
            {
                if (format.TryGetProperty("size", out JsonElement size) && size.ValueKind == JsonValueKind.String &&
                    long.TryParse(size.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes))
                    fileSize = bytes;
                ReadTags(format, tags);
            }
            if (root.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string? kind = stream.TryGetProperty("codec_type", out JsonElement codec) ? codec.GetString() : null;
                    if (kind == "audio")
                    {
                        hasAudio = true;
                        var audio = new SourceAudioStream { Index = audioStreams.Count,
                            StreamIndex = stream.TryGetProperty("index", out var si) ? si.GetInt32() : audioStreams.Count,
                            Codec = stream.TryGetProperty("codec_name", out var ac) ? ac.GetString() ?? "" : "",
                            Channels = stream.TryGetProperty("channels", out var ch) ? ch.GetInt32() : 0,
                            SampleRate = stream.TryGetProperty("sample_rate", out var sr) && int.TryParse(sr.GetString(), out int parsedRate) ? parsedRate : 0 };
                        if (stream.TryGetProperty("tags", out var st))
                        {
                            audio.Language = st.TryGetProperty("language", out var lang) ? lang.GetString() ?? "" : "";
                            audio.Title = st.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
                        }
                        audioStreams.Add(audio);
                        audioCodec ??= stream.TryGetProperty("codec_name", out JsonElement audioCodecValue) ? audioCodecValue.GetString() : null;
                        if (audioChannels is null && stream.TryGetProperty("channels", out JsonElement channels) && channels.TryGetInt32(out int channelCount))
                            audioChannels = channelCount;
                        if (audioSampleRate is null && stream.TryGetProperty("sample_rate", out JsonElement sampleRate) &&
                            int.TryParse(sampleRate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rate))
                            audioSampleRate = rate;
                        ReadTags(stream, tags);
                    }
                    if (kind != "video" || width is not null) continue;
                    videoCodec = stream.TryGetProperty("codec_name", out JsonElement videoCodecValue) ? videoCodecValue.GetString() : null;
                    ReadTags(stream, tags);
                    if (stream.TryGetProperty("width", out JsonElement w) && w.TryGetInt32(out int parsedWidth)) width = parsedWidth;
                    if (stream.TryGetProperty("height", out JsonElement h) && h.TryGetInt32(out int parsedHeight)) height = parsedHeight;
                    if (stream.TryGetProperty("r_frame_rate", out JsonElement fps) && fps.ValueKind == JsonValueKind.String)
                        frameRate = ParseRate(fps.GetString());
                }
            }
            DateTimeOffset? modifiedUtc = null;
            try { modifiedUtc = File.GetLastWriteTimeUtc(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Debug.WriteLine($"MED-METADATA-DATE: {exception}"); }
            return new MediaMetadata(duration, width, height, frameRate, hasAudio,
                videoCodec, audioCodec, audioChannels, audioSampleRate, fileSize, modifiedUtc, tags, audioStreams);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception killError) when (killError is Win32Exception or InvalidOperationException) { }
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            if (timeout.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception killError) when (killError is Win32Exception or InvalidOperationException) { }
            }
            return null;
        }
    }

    private static double? ParseRate(string? text)
    {
        string[] parts = (text ?? string.Empty).Split('/');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
            denominator == 0) return null;
        double result = numerator / denominator;
        return double.IsFinite(result) && result > 0 ? result : null;
    }

    /// <summary>Retains camera and container tags as searchable project metadata.</summary>
    private static void ReadTags(JsonElement owner, Dictionary<string, string> tags)
    {
        if (!owner.TryGetProperty("tags", out JsonElement objectValue) || objectValue.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in objectValue.EnumerateObject())
        {
            string value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString();
            if (value.Length > 0) tags[property.Name] = value;
        }
    }
}

public sealed record MediaMetadata(double? DurationSeconds, int? Width, int? Height, double? FramesPerSecond, bool HasAudio,
    string? VideoCodec, string? AudioCodec, int? AudioChannels, int? AudioSampleRate, long? FileSizeBytes,
    DateTimeOffset? FileModifiedUtc,
    IReadOnlyDictionary<string, string> Tags, IReadOnlyList<SourceAudioStream>? AudioStreams = null);
