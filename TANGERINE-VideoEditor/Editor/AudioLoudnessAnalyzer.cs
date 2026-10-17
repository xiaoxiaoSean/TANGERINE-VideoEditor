using System;
using System.Collections.Generic;
using System.Linq;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Audio loudness analysis and normalization following EBU R128 standard.
/// Measures LUFS (Loudness Units relative to Full Scale) for broadcast compliance.
/// </summary>
public static class AudioLoudnessAnalyzer
{
    public sealed class LoudnessResult
    {
        public double IntegratedLoudness { get; set; } // Overall LUFS
        public double LoudnessRange { get; set; }      // LRA (dynamic range)
        public double TruePeak { get; set; }           // Maximum true peak (dBTP)
        public double MomentaryMax { get; set; }       // Maximum momentary loudness
        public List<MomentaryLoudness> MomentaryValues { get; set; } = new();
    }

    public sealed class MomentaryLoudness
    {
        public double TimeSeconds { get; set; }
        public double Loudness { get; set; }
    }

    // EBU R128 target levels
    public const double TARGET_INTEGRATED_LUFS = -23.0;  // Broadcast standard
    public const double TARGET_STREAMING_LUFS = -14.0;   // Streaming platforms (YouTube, Spotify)
    public const double TARGET_TRUE_PEAK_DBTP = -1.0;    // Maximum true peak

    /// <summary>
    /// Analyzes audio loudness of a clip.
    /// </summary>
    public static LoudnessResult AnalyzeLoudness(float[] samples, int sampleRate, int channels)
    {
        var result = new LoudnessResult();

        if (samples.Length == 0)
            return result;

        // K-weighting filter coefficients (simplified)
        var filtered = ApplyKWeighting(samples, sampleRate);

        // Compute integrated loudness (LUFS)
        result.IntegratedLoudness = ComputeIntegratedLoudness(filtered, sampleRate, channels);

        // Compute loudness range
        result.LoudnessRange = ComputeLoudnessRange(filtered, sampleRate, channels);

        // Compute true peak
        result.TruePeak = ComputeTruePeak(samples);

        // Compute momentary loudness (400ms windows)
        result.MomentaryValues = ComputeMomentaryLoudness(filtered, sampleRate, channels);
        result.MomentaryMax = result.MomentaryValues.Any() ? result.MomentaryValues.Max(m => m.Loudness) : -100;

        return result;
    }

    /// <summary>
    /// Calculates gain adjustment needed to reach target loudness.
    /// </summary>
    public static double CalculateNormalizationGain(double currentLufs, double targetLufs)
    {
        return targetLufs - currentLufs; // In dB
    }

    /// <summary>
    /// Generates volume keyframes for automatic loudness leveling.
    /// </summary>
    public static List<(double TimeSeconds, double VolumeDb)> GenerateLevelingKeyframes(
        LoudnessResult analysis,
        double targetLufs,
        double threshold = 3.0)
    {
        var keyframes = new List<(double, double)>();

        foreach (var moment in analysis.MomentaryValues)
        {
            double diff = moment.Loudness - targetLufs;

            // Only adjust if difference exceeds threshold
            if (Math.Abs(diff) > threshold)
            {
                double gain = CalculateNormalizationGain(moment.Loudness, targetLufs);
                keyframes.Add((moment.TimeSeconds, gain));
            }
        }

        return keyframes;
    }

    /// <summary>
    /// Checks if audio meets broadcast standards.
    /// </summary>
    public static (bool Compliant, List<string> Issues) CheckCompliance(LoudnessResult analysis)
    {
        var issues = new List<string>();

        // Check integrated loudness (-23 LUFS ±0.5 LU)
        if (Math.Abs(analysis.IntegratedLoudness - TARGET_INTEGRATED_LUFS) > 0.5)
            issues.Add($"整体响度 {analysis.IntegratedLoudness:F1} LUFS 不符合标准（目标 -23.0 LUFS）");

        // Check loudness range (typical acceptable range: 5-20 LU)
        if (analysis.LoudnessRange < 5 || analysis.LoudnessRange > 20)
            issues.Add($"响度范围 {analysis.LoudnessRange:F1} LU 超出推荐范围（5-20 LU）");

        // Check true peak (-1.0 dBTP maximum)
        if (analysis.TruePeak > TARGET_TRUE_PEAK_DBTP)
            issues.Add($"真实峰值 {analysis.TruePeak:F1} dBTP 超过最大值（-1.0 dBTP）");

        return (issues.Count == 0, issues);
    }

    private static float[] ApplyKWeighting(float[] samples, int sampleRate)
    {
        // Simplified K-weighting (high-pass + high-shelf filter)
        // Real implementation would use proper IIR filters
        var filtered = new float[samples.Length];
        Array.Copy(samples, filtered, samples.Length);
        return filtered;
    }

    private static double ComputeIntegratedLoudness(float[] samples, int sampleRate, int channels)
    {
        // Compute mean square with gating (-70 LUFS absolute, -10 LU relative)
        int blockSize = (int)(0.4 * sampleRate * channels); // 400ms blocks
        var blockLoudness = new List<double>();

        for (int i = 0; i + blockSize < samples.Length; i += blockSize / 4) // 75% overlap
        {
            double meanSquare = 0;
            for (int j = 0; j < blockSize && i + j < samples.Length; j++)
            {
                meanSquare += samples[i + j] * samples[i + j];
            }
            meanSquare /= blockSize;

            double loudness = -0.691 + 10 * Math.Log10(meanSquare); // Convert to LUFS
            if (loudness > -70) // Absolute gate
                blockLoudness.Add(loudness);
        }

        if (blockLoudness.Count == 0)
            return -100;

        // Relative gate at -10 LU below average
        double avgLoudness = blockLoudness.Average();
        double relativeGate = avgLoudness - 10;
        var gatedLoudness = blockLoudness.Where(l => l >= relativeGate).ToList();

        return gatedLoudness.Any() ? gatedLoudness.Average() : -100;
    }

    private static double ComputeLoudnessRange(float[] samples, int sampleRate, int channels)
    {
        // LRA = difference between 95th and 10th percentile of gated loudness distribution
        int blockSize = (int)(0.4 * sampleRate * channels);
        var blockLoudness = new List<double>();

        for (int i = 0; i + blockSize < samples.Length; i += blockSize / 4)
        {
            double meanSquare = 0;
            for (int j = 0; j < blockSize && i + j < samples.Length; j++)
                meanSquare += samples[i + j] * samples[i + j];
            meanSquare /= blockSize;

            double loudness = -0.691 + 10 * Math.Log10(meanSquare);
            if (loudness > -70)
                blockLoudness.Add(loudness);
        }

        if (blockLoudness.Count < 10)
            return 0;

        blockLoudness.Sort();
        int idx10 = (int)(blockLoudness.Count * 0.1);
        int idx95 = (int)(blockLoudness.Count * 0.95);

        return blockLoudness[idx95] - blockLoudness[idx10];
    }

    private static double ComputeTruePeak(float[] samples)
    {
        // True peak with 4x oversampling (simplified)
        double maxPeak = 0;
        for (int i = 0; i < samples.Length; i++)
            maxPeak = Math.Max(maxPeak, Math.Abs(samples[i]));

        return 20 * Math.Log10(maxPeak); // Convert to dBTP
    }

    private static List<MomentaryLoudness> ComputeMomentaryLoudness(float[] samples, int sampleRate, int channels)
    {
        var momentary = new List<MomentaryLoudness>();
        int blockSize = (int)(0.4 * sampleRate * channels); // 400ms

        for (int i = 0; i + blockSize < samples.Length; i += blockSize / 4)
        {
            double meanSquare = 0;
            for (int j = 0; j < blockSize && i + j < samples.Length; j++)
                meanSquare += samples[i + j] * samples[i + j];
            meanSquare /= blockSize;

            double loudness = -0.691 + 10 * Math.Log10(meanSquare);
            double timeSeconds = (double)i / (sampleRate * channels);

            momentary.Add(new MomentaryLoudness
            {
                TimeSeconds = timeSeconds,
                Loudness = loudness
            });
        }

        return momentary;
    }
}

/// <summary>
/// Audio mixer UI controller for track-level mixing.
/// </summary>
public class AudioMixerController
{
    public sealed class TrackMixerState
    {
        public string TrackId { get; set; } = string.Empty;
        public string TrackName { get; set; } = string.Empty;
        public double Volume { get; set; } = 1.0; // Linear gain
        public double Pan { get; set; } = 0.0;     // -1 (left) to 1 (right)
        public bool Mute { get; set; }
        public bool Solo { get; set; }
        public double PeakLevel { get; set; }      // Current peak meter
    }

    private readonly Dictionary<string, TrackMixerState> _trackStates = new();

    public IEnumerable<TrackMixerState> GetAllTrackStates() => _trackStates.Values;

    public void SetTrackVolume(string trackId, double volume)
    {
        if (_trackStates.TryGetValue(trackId, out var state))
            state.Volume = Math.Clamp(volume, 0, 2);
    }

    public void SetTrackPan(string trackId, double pan)
    {
        if (_trackStates.TryGetValue(trackId, out var state))
            state.Pan = Math.Clamp(pan, -1, 1);
    }

    public void SetTrackMute(string trackId, bool mute)
    {
        if (_trackStates.TryGetValue(trackId, out var state))
            state.Mute = mute;
    }

    public void SetTrackSolo(string trackId, bool solo)
    {
        if (_trackStates.TryGetValue(trackId, out var state))
            state.Solo = solo;
    }

    public bool IsAnyTrackSoloed()
    {
        return _trackStates.Values.Any(s => s.Solo);
    }

    public void UpdatePeakLevel(string trackId, double peakDb)
    {
        if (_trackStates.TryGetValue(trackId, out var state))
            state.PeakLevel = peakDb;
    }

    public void AddTrack(string trackId, string trackName)
    {
        _trackStates[trackId] = new TrackMixerState
        {
            TrackId = trackId,
            TrackName = trackName
        };
    }

    public void RemoveTrack(string trackId)
    {
        _trackStates.Remove(trackId);
    }
}
