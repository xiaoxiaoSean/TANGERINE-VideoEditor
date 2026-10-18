using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Encoding settings for disposable library demos. These settings deliberately
/// stay separate from delivery exports, which keep their existing quality defaults.
/// GPU availability is established by encoding a real MP4 with the same options
/// used by the demo, rather than trusting FFmpeg's compiled-in encoder list.
/// </summary>
public sealed class PreviewEncodingProfile
{
    public const string CacheVersion = "gpu-preview-v1";

    private static readonly ConcurrentDictionary<string, Lazy<Task<PreviewEncodingProfile>>> Profiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> FailedProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    public static PreviewEncodingProfile Software { get; } = new("libx264", "软件编码", false,
        ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "23"], "software");

    private readonly string _cacheKey;

    public string EncoderName { get; }
    public string DisplayName { get; }
    public bool IsHardwareAccelerated { get; }
    public IReadOnlyList<string> VideoArguments { get; }

    private PreviewEncodingProfile(string encoderName, string displayName, bool hardware,
        string[] arguments, string cacheKey)
    {
        EncoderName = encoderName;
        DisplayName = displayName;
        IsHardwareAccelerated = hardware;
        VideoArguments = Array.AsReadOnly(arguments);
        _cacheKey = cacheKey;
    }

    /// <summary>
    /// Concurrent requests share one bounded probe per FFmpeg binary identity.
    /// Cancelling a caller stops its wait; the shared probe can finish for the
    /// next demo and can never outlive its own five-second driver timeout.
    /// </summary>
    public static async Task<PreviewEncodingProfile> SelectAsync(string ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string key = EncoderIdentity(ffmpegPath);
        Lazy<Task<PreviewEncodingProfile>> pending = Profiles.GetOrAdd(key, _ =>
            new(() => ProbeAsync(ffmpegPath, key), LazyThreadSafetyMode.ExecutionAndPublication));
        PreviewEncodingProfile profile = await pending.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return profile.IsHardwareAccelerated && FailedProfiles.ContainsKey(profile._cacheKey)
            ? Software : profile;
    }

    /// <summary>
    /// A driver can reject a real filter graph after passing the small probe.
    /// Stop offering that session profile after its first encode failure, so
    /// every later click does not pay for another failed GPU render.
    /// </summary>
    public void MarkUnavailable()
    {
        if (IsHardwareAccelerated) FailedProfiles.TryAdd(_cacheKey, 0);
    }

    private static async Task<PreviewEncodingProfile> ProbeAsync(string ffmpegPath, string key)
    {
        PreviewEncodingProfile[] candidates =
        [
            // Legacy preset names work with the bundled FFmpeg 4.4 as well as
            // current NVENC builds. Do not require newer p1/p2 preset aliases.
            new("h264_nvenc", "NVIDIA NVENC", true,
                ["-c:v", "h264_nvenc", "-preset", "fast", "-rc", "constqp", "-qp", "27", "-bf", "0"], key),
            new("h264_qsv", "Intel Quick Sync", true,
                ["-c:v", "h264_qsv", "-preset", "veryfast", "-global_quality", "27",
                    "-look_ahead", "0", "-async_depth", "1"], key),
            new("h264_amf", "AMD AMF", true,
                ["-c:v", "h264_amf", "-quality", "speed", "-rc", "cqp", "-qp_i", "27",
                    "-qp_p", "27", "-qp_b", "27", "-bf", "0"], key)
        ];
        bool[] available = await Task.WhenAll(candidates.Select(candidate =>
            ProbeCandidateAsync(ffmpegPath, candidate))).ConfigureAwait(false);
        for (int index = 0; index < candidates.Length; index++)
            if (available[index]) return candidates[index];
        return Software;
    }

    private static async Task<bool> ProbeCandidateAsync(string ffmpegPath, PreviewEncodingProfile candidate)
    {
        string output = Path.Combine(Path.GetTempPath(), $"tangerine-preview-probe-{Guid.NewGuid():N}.mp4");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            } };
            string[] input = ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "lavfi",
                "-i", "color=c=black:s=640x360:r=24:d=0.2", "-frames:v", "3", "-an"];
            foreach (string argument in input.Concat(candidate.VideoArguments)
                .Concat(["-pix_fmt", "yuv420p", "-movflags", "+faststart", output]))
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            using var stopProcess = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or NotSupportedException) { }
            });
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // The token registration kills the child. Do not wait for a
                // wedged GPU driver to acknowledge shutdown on the UI path.
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return false;
            }
            await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            bool success = !timeout.IsCancellationRequested && process.ExitCode == 0 &&
                File.Exists(output) && new FileInfo(output).Length > 0;
            if (!success) Debug.WriteLine($"PREVIEW-ENCODER-PROBE {candidate.EncoderName}: {error.Trim()}");
            return success;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"PREVIEW-ENCODER-PROBE {candidate.EncoderName}: {exception.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(output); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string EncoderIdentity(string ffmpegPath)
    {
        string resolved = ffmpegPath;
        if (!Path.IsPathRooted(resolved) && !resolved.Contains(Path.DirectorySeparatorChar))
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string executable = Path.Combine(directory.Trim('"'),
                    Path.HasExtension(resolved) ? resolved : resolved + ".exe");
                if (File.Exists(executable)) { resolved = executable; break; }
            }
        }
        try
        {
            var binary = new FileInfo(resolved);
            return binary.Exists ? $"{binary.FullName}|{binary.Length}|{binary.LastWriteTimeUtc.Ticks}"
                : resolved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return resolved; }
    }
}
