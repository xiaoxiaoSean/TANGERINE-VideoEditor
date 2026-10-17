using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Detects available hardware encoders (NVENC, QSV, AMF) and provides optimal
/// encoding configurations. Hardware acceleration dramatically speeds up 4K/HDR exports.
/// </summary>
public static class HardwareEncoderDetector
{
    public enum EncoderType
    {
        Software,      // libx264 (always available, slowest)
        NVENC,         // NVIDIA GPU encoder
        QSV,           // Intel Quick Sync Video
        AMF,           // AMD Advanced Media Framework
        VideoToolbox   // Apple Silicon/Intel Mac (reserved for future)
    }

    public sealed record EncoderCapability(
        EncoderType Type,
        string FfmpegCodec,
        string DisplayName,
        bool Available,
        int Priority,
        string? UnavailableReason = null
    );

    private static EncoderCapability[]? _cachedEncoders;

    /// <summary>
    /// Detects all available hardware encoders on this system.
    /// Priority: NVENC > QSV > AMF > Software (higher = better performance).
    /// </summary>
    public static async Task<EncoderCapability[]> DetectAvailableEncodersAsync(string ffmpegPath)
    {
        if (_cachedEncoders != null)
            return _cachedEncoders;

        var capabilities = new List<EncoderCapability>
        {
            // Software encoder always available as fallback
            new(EncoderType.Software, "libx264", "软件编码 (H.264)", true, 0)
        };

        // The encoder list only says what the ffmpeg binary was BUILT with. The
        // bundled build includes NVENC, AMF and QSV unconditionally, so on a
        // machine with no matching hardware the list would still advertise
        // them, and the best "available" encoder would then fail mid-export.
        // Being in the list is therefore only a precondition; each candidate is
        // proven with a real trial encode before it is offered.
        var compiledIn = await QueryFfmpegEncodersAsync(ffmpegPath);
        (EncoderType Type, string Codec, string Name, int Priority, string Missing)[] candidates =
        [
            (EncoderType.NVENC, "h264_nvenc", "NVIDIA NVENC (H.264)", 3, "未检测到可用的 NVIDIA 显卡或驱动版本过旧"),
            (EncoderType.QSV, "h264_qsv", "Intel Quick Sync (H.264)", 2, "未检测到 Intel 核显或不支持 Quick Sync"),
            (EncoderType.AMF, "h264_amf", "AMD AMF (H.264)", 2, "未检测到可用的 AMD 显卡或驱动不支持 AMF")
        ];
        // Probe in parallel: each trial launches ffmpeg and a driver may take
        // a moment to initialise, so serial probing would delay the first export.
        bool[] proven = await Task.WhenAll(candidates.Select(async candidate =>
            compiledIn.Contains(candidate.Codec) &&
            await ValidateEncoderAsync(ffmpegPath, candidate.Codec).ConfigureAwait(false)));
        for (int index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            capabilities.Add(proven[index]
                ? new(candidate.Type, candidate.Codec, candidate.Name, true, candidate.Priority)
                : new(candidate.Type, candidate.Codec, candidate.Name, false, candidate.Priority,
                    compiledIn.Contains(candidate.Codec)
                        ? candidate.Missing
                        : "当前 FFmpeg 不包含该编码器"));
        }

        _cachedEncoders = capabilities.OrderByDescending(c => c.Priority).ToArray();
        return _cachedEncoders;
    }

    /// <summary>
    /// Gets the best available encoder for this system.
    /// </summary>
    public static async Task<EncoderCapability> GetBestEncoderAsync(string ffmpegPath)
    {
        var encoders = await DetectAvailableEncodersAsync(ffmpegPath);
        return encoders.First(e => e.Available);
    }

    /// <summary>
    /// Gets FFmpeg encoding arguments optimized for the specified encoder type.
    /// </summary>
    public static string[] GetEncoderArguments(EncoderType encoderType, int bitrateKbps, string preset = "medium")
    {
        return encoderType switch
        {
            EncoderType.NVENC => new[]
            {
                "-c:v", "h264_nvenc",
                "-preset", MapPresetToNvenc(preset),
                "-b:v", $"{bitrateKbps}k",
                "-maxrate", $"{bitrateKbps * 1.2}k",
                "-bufsize", $"{bitrateKbps * 2}k",
                "-profile:v", "high",
                "-level", "4.2",
                "-rc", "vbr",
                "-spatial-aq", "1",
                "-temporal-aq", "1"
            },

            EncoderType.QSV => new[]
            {
                "-c:v", "h264_qsv",
                "-preset", preset,
                "-b:v", $"{bitrateKbps}k",
                "-maxrate", $"{bitrateKbps * 1.2}k",
                "-bufsize", $"{bitrateKbps * 2}k",
                "-profile:v", "high",
                "-level", "4.2",
                "-look_ahead", "1"
            },

            EncoderType.AMF => new[]
            {
                "-c:v", "h264_amf",
                "-quality", MapPresetToAmf(preset),
                "-b:v", $"{bitrateKbps}k",
                "-maxrate", $"{bitrateKbps * 1.2}k",
                "-bufsize", $"{bitrateKbps * 2}k",
                "-profile:v", "high",
                "-level", "4.2",
                "-rc", "vbr_latency"
            },

            _ => new[] // Software libx264
            {
                "-c:v", "libx264",
                "-preset", preset,
                "-b:v", $"{bitrateKbps}k",
                "-maxrate", $"{bitrateKbps * 1.2}k",
                "-bufsize", $"{bitrateKbps * 2}k",
                "-profile:v", "high",
                "-level", "4.2",
                "-movflags", "+faststart"
            }
        };
    }

    /// <summary>
    /// Estimates encoding speed multiplier compared to software encoding.
    /// NVENC: ~10-15x faster, QSV: ~5-8x faster, AMF: ~5-8x faster.
    /// </summary>
    public static double GetSpeedMultiplier(EncoderType encoderType) => encoderType switch
    {
        EncoderType.NVENC => 12.0,
        EncoderType.QSV => 6.5,
        EncoderType.AMF => 6.5,
        _ => 1.0
    };

    private static string MapPresetToNvenc(string preset) => preset switch
    {
        "ultrafast" => "p1",
        "superfast" => "p2",
        "veryfast" => "p3",
        "faster" => "p4",
        "fast" => "p5",
        "medium" => "p6",
        "slow" => "p7",
        "slower" => "p7",
        "veryslow" => "p7",
        _ => "p6"
    };

    private static string MapPresetToAmf(string preset) => preset switch
    {
        "ultrafast" or "superfast" or "veryfast" => "speed",
        "faster" or "fast" or "medium" => "balanced",
        "slow" or "slower" or "veryslow" => "quality",
        _ => "balanced"
    };

    private static async Task<HashSet<string>> QueryFfmpegEncodersAsync(string ffmpegPath)
    {
        var encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            ProcessResult run = await ExternalProcessRunner.RunAsync(ffmpegPath, "-encoders -hide_banner")
                .ConfigureAwait(false);
            if (!run.Succeeded) return encoders;

            // Format: " V..... h264_nvenc          NVIDIA NVENC H.264 encoder (codec h264)"
            // The second column is the encoder name. Only hardware encoders
            // matter here; libx264 is assumed and needs no probing.
            foreach (string line in run.StandardOutput.Split('\n'))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Contains('_') &&
                    (parts[1].EndsWith("_nvenc", StringComparison.Ordinal) ||
                     parts[1].EndsWith("_qsv", StringComparison.Ordinal) ||
                     parts[1].EndsWith("_amf", StringComparison.Ordinal)))
                    encoders.Add(parts[1]);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // ffmpeg could not be started: only the software encoder is offered.
        }

        return encoders;
    }

    /// <summary>
    /// Validates hardware encoder availability by attempting a test encode.
    /// </summary>
    public static async Task<bool> ValidateEncoderAsync(string ffmpegPath, string encoderCodec,
        CancellationToken cancellationToken = default)
    {
        string tempOutput = Path.Combine(Path.GetTempPath(), $"encoder-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            // A generated test frame needs no input file. Most hardware
            // encoders refuse tiny frames, so probe at a size they accept.
            ProcessResult run = await ExternalProcessRunner.RunAsync(ffmpegPath,
                $"-f lavfi -i color=c=black:s=256x256:r=30:d=0.2 -frames:v 3 " +
                $"-c:v {encoderCodec} -pix_fmt yuv420p -y \"{tempOutput}\"", cancellationToken);
            return run.Succeeded && File.Exists(tempOutput) && new FileInfo(tempOutput).Length > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // The probe could not run at all: treat the encoder as unusable
            // rather than offering it and failing in the middle of an export.
            return false;
        }
        finally
        {
            try { if (File.Exists(tempOutput)) File.Delete(tempOutput); }
            catch (IOException) { /* a temp file left behind is harmless */ }
        }
    }
}
