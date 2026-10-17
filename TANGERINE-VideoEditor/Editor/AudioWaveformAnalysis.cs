using System.Diagnostics;
using System.Globalization;
using System.IO;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Decodes low-rate mono analysis samples with the bundled FFmpeg and finds
/// the offset that maximizes normalized waveform correlation. This is a local
/// alternative for syncing camera scratch audio with a separate recorder.
/// It deliberately examines bounded windows to avoid loading long recordings.
/// </summary>
public static class AudioWaveformAnalysis
{
    private const int Rate = 400;
    private const int WindowSeconds = 30;
    private const int MaxOffsetSeconds = 15;

    public static async Task<double> FindOffsetAsync(TimelineClip reference, string referencePath,
        TimelineClip moving, string movingPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.Kind is not (ClipKind.Audio or ClipKind.Video) ||
            moving.Kind is not (ClipKind.Audio or ClipKind.Video))
            throw new AudioOperationException(AudioStageCode.Validate, "音频同步需要两个含音轨的片段。");
        if (!double.IsFinite(reference.Speed) || !double.IsFinite(moving.Speed) ||
            !double.IsFinite(reference.SourceInSeconds) || reference.SourceInSeconds < 0 ||
            !double.IsFinite(moving.SourceInSeconds) || moving.SourceInSeconds < 0 ||
            !double.IsFinite(reference.DurationSeconds) || reference.DurationSeconds <= 0 ||
            !double.IsFinite(moving.DurationSeconds) || moving.DurationSeconds <= 0 ||
            reference.IsReversed || moving.IsReversed ||
            reference.TimeRemapPoints is null || moving.TimeRemapPoints is null ||
            Math.Abs(reference.Speed - 1) > .000001 || Math.Abs(moving.Speed - 1) > .000001 ||
            reference.TimeRemapPoints.Count > 0 || moving.TimeRemapPoints.Count > 0)
            throw new AudioOperationException(AudioStageCode.Validate, "请先对正向、原速片段执行音频同步。");
        float[] first = await ReadSamplesAsync(referencePath, reference.SourceInSeconds,
            Math.Min(WindowSeconds, reference.DurationSeconds), cancellationToken);
        float[] second = await ReadSamplesAsync(movingPath, moving.SourceInSeconds,
            Math.Min(WindowSeconds, moving.DurationSeconds), cancellationToken);
        if (first.Length < Rate * 2 || second.Length < Rate * 2)
            throw new AudioOperationException(AudioStageCode.Process, "可分析的音频不足两秒。");
        // Correlation is CPU intensive for a long search window. Execute it
        // away from the WPF dispatcher so the user can still move the editor.
        return await Task.Run(() => FindOffset(first, second, cancellationToken), cancellationToken);
    }

    private static double FindOffset(float[] first, float[] second, CancellationToken cancellationToken)
    {
        // One recording can be much shorter than the other. Candidate lags
        // are limited by the longer recording, while the loop independently
        // requires at least two seconds of actual overlap.
        int maxLag = Math.Min(MaxOffsetSeconds * Rate, Math.Max(first.Length, second.Length) - Rate * 2);
        const int stride = 4;
        double bestScore = double.NegativeInfinity;
        int bestLag = 0;
        // The search compares first[t + lag] with second[t]. A positive lag
        // means the event is later inside the reference clip. The moving
        // clip must therefore start later on the sequence clock by that lag.
        for (int lag = -maxLag; lag <= maxLag; lag += stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leftStart = Math.Max(0, lag);
            int rightStart = Math.Max(0, -lag);
            int available = Math.Min(first.Length - leftStart, second.Length - rightStart);
            if (available < Rate * 2) continue;
            double dot = 0, leftEnergy = 0, rightEnergy = 0;
            for (int offset = 0; offset < available; offset += stride)
            {
                double x = first[leftStart + offset];
                double y = second[rightStart + offset];
                dot += x * y; leftEnergy += x * x; rightEnergy += y * y;
            }
            if (leftEnergy < .000001 || rightEnergy < .000001) continue;
            double score = dot / Math.Sqrt(leftEnergy * rightEnergy);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        // Refine around the best coarse lag at single-sample resolution.
        // This improves final placement without paying the full search cost
        // across the complete thirty-second correlation window.
        if (!double.IsFinite(bestScore))
            throw new AudioOperationException(AudioStageCode.Process,
                "两个片段没有足够的有效波形重叠，无法自动同步。");
        int coarseBest = bestLag;
        for (int lag = Math.Max(-maxLag, coarseBest - stride + 1);
             lag <= Math.Min(maxLag, coarseBest + stride - 1); lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leftStart = Math.Max(0, lag);
            int rightStart = Math.Max(0, -lag);
            int available = Math.Min(first.Length - leftStart, second.Length - rightStart);
            if (available < Rate * 2) continue;
            double dot = 0, leftEnergy = 0, rightEnergy = 0;
            for (int offset = 0; offset < available; offset += stride)
            {
                double x = first[leftStart + offset];
                double y = second[rightStart + offset];
                dot += x * y; leftEnergy += x * x; rightEnergy += y * y;
            }
            if (leftEnergy < .000001 || rightEnergy < .000001) continue;
            double score = dot / Math.Sqrt(leftEnergy * rightEnergy);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        if (bestScore < .2)
            throw new AudioOperationException(AudioStageCode.Process,
                "两个片段的波形匹配度不足，无法可靠地自动同步。可手动调整位置。");
        return bestLag / (double)Rate;
    }

    /// <summary>
    /// Estimates a steady musical pulse from positive short-time energy
    /// changes. The result is a starting point for the editable BPM field;
    /// complex meter, half-time rhythms, and rubato still need human review.
    /// </summary>
    public static async Task<double> EstimateTempoAsync(string path, double sourceInSeconds,
        double availableSeconds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float[] samples = await ReadSamplesAsync(path, sourceInSeconds,
            Math.Min(60, availableSeconds), cancellationToken);
        // Beat search is modest but still work on up to a minute of samples.
        // Keep it off the dispatcher, just like waveform synchronization.
        return await Task.Run(() => EstimateTempo(samples, cancellationToken), cancellationToken);
    }

    private static double EstimateTempo(float[] samples, CancellationToken cancellationToken)
    {
        const int hop = Rate / 20;
        if (samples.Length < Rate * 8)
            throw new AudioOperationException(AudioStageCode.Process, "音乐不足八秒，无法估计节拍。");
        var onset = new double[samples.Length / hop];
        double preceding = 0;
        for (int frame = 0; frame < onset.Length; frame++)
        {
            double energy = 0;
            for (int sample = frame * hop; sample < (frame + 1) * hop; sample++)
                energy += Math.Abs(samples[sample]);
            energy /= hop;
            onset[frame] = Math.Max(0, energy - preceding);
            preceding = energy;
        }
        double onsetPower = onset.Sum(value => value * value);
        if (onsetPower < .000001)
            throw new AudioOperationException(AudioStageCode.Process, "没有检测到足够清晰的音乐节拍。");
        double bestScore = double.NegativeInfinity;
        double bestTempo = 120;
        for (int bpm = 60; bpm <= 200; bpm++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lag = (int)Math.Round(1200d / bpm);
            if (lag <= 0 || lag >= onset.Length) continue;
            double dot = 0, left = 0, right = 0;
            for (int frame = lag; frame < onset.Length; frame++)
            {
                double first = onset[frame];
                double second = onset[frame - lag];
                dot += first * second; left += first * first; right += second * second;
            }
            if (left < .000001 || right < .000001) continue;
            double score = dot / Math.Sqrt(left * right);
            if (score > bestScore) { bestScore = score; bestTempo = bpm; }
        }
        if (bestScore < .08)
            throw new AudioOperationException(AudioStageCode.Process, "未能可靠地估计节拍，请手动输入 BPM。");
        return bestTempo;
    }

    private static async Task<float[]> ReadSamplesAsync(string path, double startSeconds, double durationSeconds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new AudioOperationException(AudioStageCode.Read, "找不到用于同步的音频源文件。");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new AudioOperationException(AudioStageCode.Validate, "音频同步的源时间范围无效。");
        var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-ss",
            startSeconds.ToString("0.######", CultureInfo.InvariantCulture), "-t",
            durationSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            "-i", path, "-vn", "-ac", "1", "-ar", Rate.ToString(CultureInfo.InvariantCulture),
            "-f", "f32le", "-c:a", "pcm_f32le", "pipe:1"
        }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new AudioOperationException(AudioStageCode.Process, "无法启动本地音频解码器。"); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new AudioOperationException(AudioStageCode.Process, "无法启动本地音频解码器。", exception);
        }
        using var stop = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var buffer = new MemoryStream();
        try
        {
            // A corrupt or unexpectedly long source must not turn a bounded
            // analysis request into an unbounded allocation. FFmpeg receives
            // a maximum duration, and this cap also guards its output pipe.
            int maximumBytes = checked(Rate * 61 * sizeof(float));
            byte[] chunk = new byte[16 * 1024];
            while (true)
            {
                int read = await process.StandardOutput.BaseStream.ReadAsync(chunk, cancellationToken);
                if (read == 0) break;
                if (buffer.Length + read > maximumBytes)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception exception) when (exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception) { }
                    throw new AudioOperationException(AudioStageCode.Process,
                        "音频分析数据超出本地处理上限。");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new AudioOperationException(AudioStageCode.Process,
                    $"音频波形解码失败：{(await errorTask).Trim()}");
            await errorTask;
            byte[] bytes = buffer.ToArray();
            var samples = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
            if (samples.Length == 0) return samples;
            // Removing the DC component improves correlation for camera
            // microphone tracks with a different baseline offset.
            double mean = samples.Average(value => (double)value);
            for (int index = 0; index < samples.Length; index++) samples[index] -= (float)mean;
            return samples;
        }
        catch (OperationCanceledException) { throw; }
        catch (AudioOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            throw new AudioOperationException(AudioStageCode.Process, "无法分析音频波形。", exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
