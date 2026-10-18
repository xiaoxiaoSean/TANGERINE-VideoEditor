using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Renders a short, local stereo monitor window with the same clip and track
/// audio filters used for final export. Its only output is a temporary WAV
/// file consumed by the WPF preview player; source media remains untouched.
/// Rendering only the active window avoids exporting an entire sequence for
/// a quick listen after every edit or seek.
/// </summary>
public static class TimelineAudioMonitor
{
    private sealed record AudibleClip(TimelineTrack Track, TimelineClip Clip, string Path,
        int InputIndex, double IntersectionStart, double IntersectionEnd);

    public static async Task<bool> RenderWindowAsync(ProjectDocument project, double startSeconds,
        double durationSeconds, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            !double.IsFinite(durationSeconds) || durationSeconds <= 0 || durationSeconds > 10 ||
            project.Sequence.AudioSampleRate is < 8000 or > 192000)
            throw new AudioOperationException(AudioStageCode.Validate, "音频监听时间或采样率无效。");
        double endSeconds = startSeconds + durationSeconds;
        var sources = new List<AudibleClip>();
        foreach (TimelineTrack track in project.Tracks.Where(track =>
            !track.IsMuted &&
            (track.Kind == TrackKind.Audio || !track.IsHidden)))
        {
            foreach (TimelineClip clip in track.Clips.Where(clip =>
                (clip.Kind is ClipKind.Audio or ClipKind.Video) && !clip.IsMuted && !clip.AudioDetached))
            {
                if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
                    !double.IsFinite(clip.DurationSeconds) || clip.DurationSeconds <= 0 ||
                    !double.IsFinite(clip.Speed) || clip.Speed <= 0 ||
                    !double.IsFinite(clip.SourceInSeconds) || clip.SourceInSeconds < 0 ||
                    clip.TimeRemapPoints is null)
                    throw new AudioOperationException(AudioStageCode.Validate,
                        "音频监听的速度或源入点无效。");
                double intersectionStart = Math.Max(startSeconds, clip.StartSeconds);
                double intersectionEnd = Math.Min(endSeconds, clip.StartSeconds + clip.DurationSeconds);
                if (intersectionEnd <= intersectionStart) continue;
                MediaAsset? asset = project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
                if (asset is null || (clip.Kind == ClipKind.Video && !asset.HasAudio)) continue;
                // Proxy files can intentionally omit their audio stream.
                // Monitor the original stream so it matches export and the
                // project's HasAudio metadata even while picture uses proxy.
                if (!File.Exists(asset.EffectiveExportPath))
                    throw new AudioOperationException(AudioStageCode.Read,
                        $"找不到音频片段“{clip.Name}”的源文件。");
                sources.Add(new AudibleClip(track, clip, asset.EffectiveExportPath,
                    sources.Count, intersectionStart, intersectionEnd));
            }
        }
        if (sources.Count == 0) return false;
        if (sources.Count > 64)
            throw new AudioOperationException(AudioStageCode.Validate, "当前监听窗口有超过 64 条可听片段。");

        string number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        int rate = project.Sequence.AudioSampleRate;
        var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-hide_banner", "-y", "-nostdin", "-loglevel", "error" })
            info.ArgumentList.Add(argument);
        foreach (AudibleClip source in sources)
        {
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add(source.Path);
        }

        var graph = new StringBuilder();
        foreach (AudibleClip source in sources)
        {
            TimelineClip clip = source.Clip;
            double localStart = source.IntersectionStart - clip.StartSeconds;
            double localDuration = source.IntersectionEnd - source.IntersectionStart;
            string sourceLabel = BuildWindowInput(graph, source, localStart,
                localDuration, number);
            string volume = ClipAnimation.HasKeyframes(clip, "Volume")
                ? $"volume='{ClipAnimation.FfmpegExpression(clip, "Volume", $"(t+{number(localStart)})")}':eval=frame"
                : $"volume={number(Math.Clamp(clip.Volume, 0, 4))}";
            string chain = $"atrim=duration={number(localDuration)},asetpts=PTS-STARTPTS,{volume}";
            chain += AudioProcessing.BuildClipFilters(clip);
            // Fades are clip-relative. Apply their intersection with this
            // monitor window instead of replaying a fade at each seek.
            chain += FadeForWindow(clip, localStart, localDuration);
            chain += AudioProcessing.BuildTrackFilters(source.Track);
            chain += MusicDucking.BuildFilter(project, clip, localStart, localDuration);
            chain += $",atrim=duration={number(localDuration)}";
            double delaySeconds = source.IntersectionStart - startSeconds;
            int delay = Math.Max(0, (int)Math.Round(delaySeconds * 1000));
            chain += $",adelay={delay}:all=1,aresample={rate}[a{source.InputIndex}]";
            graph.AppendLine($"[{sourceLabel}]{chain};");
        }
        string labels = string.Concat(sources.Select(source => $"[a{source.InputIndex}]"));
        graph.AppendLine($"{labels}amix=inputs={sources.Count}:duration=longest:dropout_transition=0:normalize=0," +
            $"atrim=duration={number(durationSeconds)},asetpts=PTS-STARTPTS[out]");
        info.ArgumentList.Add("-filter_complex");
        info.ArgumentList.Add(graph.ToString());
        foreach (string argument in new[]
        {
            "-map", "[out]", "-vn", "-ac", "2", "-ar", rate.ToString(CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", "-f", "wav", outputPath
        }) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new AudioOperationException(AudioStageCode.Process, "无法启动本地音频监听解码器。");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new AudioOperationException(AudioStageCode.Process,
                "无法启动本地音频监听解码器。", exception);
        }
        using var stop = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
        });
        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string message = await errors;
            if (process.ExitCode != 0)
                throw new AudioOperationException(AudioStageCode.Process,
                    $"音频监听渲染失败：{message.Trim()}");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (AudioOperationException) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new AudioOperationException(AudioStageCode.Process,
                "无法完成音频监听。", exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
            }
        }
    }

    /// <summary>
    /// Cuts only the intersecting speed intervals and concatenates them on
    /// the monitor's local clock. SourceOffset is shared with the sequence
    /// export and therefore follows the same reverse and speed-node mapping.
    /// </summary>
    private static string BuildWindowInput(StringBuilder graph, AudibleClip source,
        double localStart, double localDuration, Func<double, string> number)
    {
        TimelineClip clip = source.Clip;
        var boundaries = new List<double> { localStart };
        double localEnd = localStart + localDuration;
        double previous = double.NegativeInfinity;
        foreach (ClipTimeRemapPoint point in clip.TimeRemapPoints.OrderBy(point => point.TimeSeconds))
        {
            if (!double.IsFinite(point.TimeSeconds) || !double.IsFinite(point.Speed) ||
                point.TimeSeconds < 0 || point.TimeSeconds >= clip.DurationSeconds ||
                point.Speed < .05 || point.Speed > 32 ||
                point.TimeSeconds - previous < .000001)
                throw new AudioOperationException(AudioStageCode.Validate,
                    $"音频片段“{clip.Name}”的速度节点无效。");
            previous = point.TimeSeconds;
            if (point.TimeSeconds > localStart && point.TimeSeconds < localEnd)
                boundaries.Add(point.TimeSeconds);
        }
        boundaries.Add(localEnd);
        if (boundaries.Count > 257)
            throw new AudioOperationException(AudioStageCode.Validate,
                "当前监听窗口的速度节点过多。");
        string prefix = $"m{source.InputIndex}";
        var pieces = new List<string>();
        if (boundaries.Count > 2)
            graph.AppendLine($"[{source.InputIndex}:a:{clip.SourceAudioStreamIndex}]asplit={boundaries.Count - 1}" +
                string.Concat(Enumerable.Range(0, boundaries.Count - 1).Select(index => $"[{prefix}in{index}]")) + ";");
        for (int index = 0; index < boundaries.Count - 1; index++)
        {
            double partStart = boundaries[index];
            double partEnd = boundaries[index + 1];
            double speed = ClipTimeMap.SpeedAt(clip, partStart);
            double sourceStart = clip.SourceInSeconds + (clip.IsReversed
                ? ClipTimeMap.SourceOffset(clip, clip.DurationSeconds) - ClipTimeMap.SourceOffset(clip, partEnd)
                : ClipTimeMap.SourceOffset(clip, partStart));
            double sourceSpan = (partEnd - partStart) * speed;
            if (!double.IsFinite(sourceStart) || !double.IsFinite(sourceSpan) ||
                sourceStart < 0 || sourceSpan <= 0)
                throw new AudioOperationException(AudioStageCode.Validate,
                    $"音频片段“{clip.Name}”的源范围无效。");
            string input = boundaries.Count > 2 ? $"{prefix}in{index}" : $"{source.InputIndex}:a:{clip.SourceAudioStreamIndex}";
            string label = $"{prefix}piece{index}";
            string chain = $"atrim=start={number(sourceStart)}:duration={number(sourceSpan)}" +
                (clip.IsReversed ? ",areverse" : string.Empty) +
                ",asetpts=PTS-STARTPTS" + TempoFilters(speed) + ",asetpts=PTS-STARTPTS";
            graph.AppendLine($"[{input}]{chain}[{label}];");
            pieces.Add(label);
        }
        string result = $"{prefix}joined";
        if (pieces.Count == 1) graph.AppendLine($"[{pieces[0]}]anull[{result}];");
        else graph.AppendLine($"{string.Concat(pieces.Select(label => $"[{label}]"))}" +
            $"concat=n={pieces.Count}:v=0:a=1[{result}];");
        return result;
    }

    private static string TempoFilters(double speed)
    {
        if (Math.Abs(speed - 1) < .00001) return string.Empty;
        var parts = new List<string>();
        while (speed > 2 + .000001) { parts.Add("atempo=2"); speed /= 2; }
        while (speed < .5 - .000001) { parts.Add("atempo=0.5"); speed *= 2; }
        if (Math.Abs(speed - 1) > .000001)
            parts.Add($"atempo={speed.ToString("0.######", CultureInfo.InvariantCulture)}");
        return parts.Count == 0 ? string.Empty : "," + string.Join(',', parts);
    }

    private static string FadeForWindow(TimelineClip clip, double localStart, double duration)
    {
        string number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        // A clip's Remix fades are stored in extension data. The shared
        // reader validates the stored seconds for preview and export.
        double fadeIn = AudioProcessing.ReadFade(clip, AudioProcessing.RemixFadeInKey);
        double fadeOut = AudioProcessing.ReadFade(clip, AudioProcessing.RemixFadeOutKey);
        if (fadeIn <= .0001 && fadeOut <= .0001) return string.Empty;
        var parts = new List<string>();
        // FFmpeg expressions are quoted so their function commas remain
        // inside the volume filter instead of splitting the filter graph.
        if (fadeIn > localStart)
        {
            if (localStart == 0)
                parts.Add($"afade=t=in:st=0:d={number(fadeIn)}");
            else
            {
                // Linear expression continues the original clip envelope
                // from its current fraction rather than fading from zero.
                string ratio = number(localStart / fadeIn);
                string slope = number(1 / fadeIn);
                string limit = number(fadeIn - localStart);
                parts.Add($"volume='if(lt(t,{limit}),{ratio}+t*{slope},1)':eval=frame");
            }
        }
        double fadeOutStart = clip.DurationSeconds - fadeOut;
        if (fadeOut > 0 && localStart + duration > fadeOutStart)
        {
            if (localStart <= fadeOutStart)
                parts.Add($"afade=t=out:st={number(fadeOutStart - localStart)}:d={number(fadeOut)}");
            else
            {
                string ratio = number((clip.DurationSeconds - localStart) / fadeOut);
                string slope = number(1 / fadeOut);
                parts.Add($"volume='max(0,{ratio}-t*{slope})':eval=frame");
            }
        }
        return parts.Count == 0 ? string.Empty : "," + string.Join(',', parts);
    }
}
