using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>Bounded source-window PCM decoding. Peaks retain polarity-independent transients.</summary>
public static class AudioPeakCache
{
    public const int BinsPerSecond = 100;
    public const int WindowSeconds = 30;
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly Dictionary<string, Task<float[]>> Cache = [];
    public static Task<float[]> GetAsync(string path, int stream, int window, CancellationToken token)
    {
        var file = new FileInfo(path);
        string key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{stream}|{window}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var existing) && !existing.IsCanceled && !existing.IsFaulted) return existing.WaitAsync(token);
            if (Cache.Count >= 256) foreach (string stale in Cache.Where(p => p.Value.IsCompleted).Take(64).Select(p => p.Key).ToArray()) Cache.Remove(stale);
            var task = DecodeAsync(path, stream, window * WindowSeconds, token); Cache[key] = task; return task;
        }
    }
    private static async Task<float[]> DecodeAsync(string path, int stream, double start, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-v", "error", "-nostdin", "-ss", start.ToString("R", CultureInfo.InvariantCulture), "-i", path, "-map", $"0:a:{stream}", "-t", "30", "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new AudioOperationException(AudioStageCode.Process, "无法解码波形");
            using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var errors = process.StandardError.ReadToEndAsync(token);
            var peaks = new List<float>(); byte[] bytes = new byte[480 * 4];
            while (true)
            {
                int size = 0;
                while (size < bytes.Length) { int read = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(size), token); if (read == 0) break; size += read; }
                if (size == 0) break;
                float peak = 0;
                for (int i = 0; i + 3 < size; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(bytes, i)));
                peaks.Add(peak);
            }
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new AudioOperationException(AudioStageCode.Process, await errors);
            return peaks.ToArray();
        }
        finally { Gate.Release(); }
    }
}

/// <summary>R128 measurement is performed by FFmpeg, not a simplified approximate K-weighting implementation.</summary>
public static class ClipLoudnessNormalizer
{
    public sealed record Measurement(double IntegratedLufs, double TruePeakDb, double GainDb);
    public static async Task<Measurement> MeasureAsync(TimelineClip clip, MediaAsset asset, double target, double ceiling, CancellationToken token)
    {
        if (target is < -36 or > -5 || ceiling is < -9 or > 0 || !double.IsFinite(target) || !double.IsFinite(ceiling))
            throw new AudioOperationException(AudioStageCode.Validate, "响度目标范围无效");
        var settings = AudioSettings.FromClip(clip); settings.NormalizeEnabled = false;
        string filters = "anull" + (clip.IsReversed ? ",areverse" : "") + TimelineExporter.TempoFilters(clip.Speed) +
            (ClipAnimation.HasKeyframes(clip, "Volume") ? $",volume='{ClipAnimation.FfmpegExpression(clip, "Volume", "t")}':eval=frame" : $",volume={clip.Volume.ToString("R", CultureInfo.InvariantCulture)}") + AudioProcessing.BuildFilters(settings) +
            $",loudnorm=I={target.ToString("R", CultureInfo.InvariantCulture)}:TP={ceiling.ToString("R", CultureInfo.InvariantCulture)}:print_format=json";
        if (clip.TimeRemapPoints.Count > 0) return await MeasureRetimedAsync(clip, asset, target, ceiling, token);
        var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-ss", clip.SourceInSeconds.ToString("R", CultureInfo.InvariantCulture), "-t", (clip.DurationSeconds * clip.Speed).ToString("R", CultureInfo.InvariantCulture), "-i", asset.EffectiveExportPath, "-map", $"0:a:{clip.SourceAudioStreamIndex}", "-vn", "-af", filters, "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new AudioOperationException(AudioStageCode.Process, "无法启动响度测量");
        using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        string error = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
        return ParseMeasurement(error, process.ExitCode, target, ceiling);
    }

    private static Measurement ParseMeasurement(string error, int exitCode, double target, double ceiling)
    {
        int first = error.LastIndexOf('{'), last = error.LastIndexOf('}');
        if (exitCode != 0 || first < 0 || last < first) throw new AudioOperationException(AudioStageCode.Process, "响度测量失败：" + error);
        using var json = JsonDocument.Parse(error[first..(last + 1)]);
        double Value(string key)
        {
            string? text = json.RootElement.GetProperty(key).GetString();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
        }
        double loudness = Value("input_i"), peak = Value("input_tp");
        if (!double.IsFinite(loudness) || !double.IsFinite(peak)) throw new AudioOperationException(AudioStageCode.Validate, "所选片段为静音，无法归一化");
        return new(loudness, peak, Math.Clamp(Math.Min(target - loudness, ceiling - peak), -60, 60));
    }
    private static async Task<Measurement> MeasureRetimedAsync(TimelineClip clip, MediaAsset asset, double target, double ceiling, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "tangerine-loudness-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var copy = System.Text.Json.JsonSerializer.Deserialize<TimelineClip>(System.Text.Json.JsonSerializer.Serialize(clip))!;
            copy.StartSeconds = 0; copy.IsMuted = false; copy.AudioDetached = false;
            var settings = AudioSettings.FromClip(copy); settings.NormalizeEnabled = false; settings.Save(copy);
            var project = new ProjectDocument { Media = [asset], Tracks = [new() { Kind = TrackKind.Audio, Clips = [copy] }] };
            copy.Kind = ClipKind.Audio;
            var files = new List<string>();
            for (double start = 0; start < copy.DurationSeconds; start += 6)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(directory, files.Count + ".wav");
                await TimelineAudioMonitor.RenderWindowAsync(project, start, Math.Min(6, copy.DurationSeconds - start), file, token);
                files.Add(file);
            }
            string manifest = Path.Combine(directory, "segments.txt");
            await File.WriteAllLinesAsync(manifest, files.Select(f => "file '" + f.Replace("'", "'\\''") + "'"), token);
            var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-nostdin", "-f", "concat", "-safe", "0", "-i", manifest, "-af",
                $"loudnorm=I={target.ToString("R", CultureInfo.InvariantCulture)}:TP={ceiling.ToString("R", CultureInfo.InvariantCulture)}:print_format=json", "-f", "null", "-" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new AudioOperationException(AudioStageCode.Process, "无法测量变速曲线响度");
            using var kill = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            string errors = await process.StandardError.ReadToEndAsync(token); await process.WaitForExitAsync(token);
            return ParseMeasurement(errors, process.ExitCode, target, ceiling);
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

}
