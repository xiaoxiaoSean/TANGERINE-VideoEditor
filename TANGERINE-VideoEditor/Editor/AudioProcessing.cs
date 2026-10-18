using System.Globalization;
using System.Text.Json;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum AudioStageCode
{
    Validate,
    Read,
    Write,
    Capture,
    Process
}

public sealed class AudioOperationException(AudioStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public AudioStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Stores clip and track audio decisions in the version-tolerant extension area
/// of a project. The same settings drive both the editor and FFmpeg export.
/// All values are intentionally bounded before they become filter arguments.
/// </summary>
public sealed class AudioSettings
{
    public const string ExtensionKey = "TangerineAudioSettings";

    public string Category { get; set; } = "未分类";
    public double Gain { get; set; } = 1;
    public bool NormalizeEnabled { get; set; }
    public double TargetLufs { get; set; } = -14;
    public double TargetTruePeakDb { get; set; } = -1;
    public double? MeasuredLufs { get; set; }
    public double? MeasuredTruePeakDb { get; set; }
    public double NormalizationGainDb { get; set; }

    public double Pan { get; set; }
    public string ChannelMode { get; set; } = "立体声";
    public double HighPassHz { get; set; }
    public double LowPassHz { get; set; }
    public double EqualizerHz { get; set; }
    public double EqualizerGainDb { get; set; }
    public bool CompressorEnabled { get; set; }
    public double CompressorThresholdDb { get; set; } = -18;
    public double CompressorRatio { get; set; } = 3;
    public bool LimiterEnabled { get; set; }
    public double LimiterCeiling { get; set; } = .95;
    public bool NoiseReductionEnabled { get; set; }
    public double NoiseReductionDb { get; set; } = 12;
    public bool EchoEnabled { get; set; }
    public double EchoDelayMs { get; set; } = 100;
    public double EchoDecay { get; set; } = .25;
    public bool AutoDuckingEnabled { get; set; }
    public double DuckingGain { get; set; } = .3;
    public double DuckingFadeSeconds { get; set; } = .2;

    public static AudioSettings FromClip(TimelineClip clip) => Read(clip.AdditionalData);

    public static AudioSettings FromTrack(TimelineTrack track) => Read(track.AdditionalData);

    public void Save(TimelineClip clip) => clip.AdditionalData = Save(clip.AdditionalData);

    public void Save(TimelineTrack track) => track.AdditionalData = Save(track.AdditionalData);

    private Dictionary<string, JsonElement> Save(Dictionary<string, JsonElement>? data)
    {
        Validate();
        data ??= [];
        data[ExtensionKey] = JsonSerializer.SerializeToElement(this);
        return data;
    }

    private static AudioSettings Read(Dictionary<string, JsonElement>? data)
    {
        if (data?.TryGetValue(ExtensionKey, out JsonElement value) != true) return new AudioSettings();
        try
        {
            AudioSettings result = value.Deserialize<AudioSettings>() ??
                throw new AudioOperationException(AudioStageCode.Read, "项目中的音频设置为空。");
            result.Validate();
            return result;
        }
        catch (AudioOperationException) { throw; }
        catch (JsonException exception)
        {
            throw new AudioOperationException(AudioStageCode.Read, "项目中的音频设置无效。", exception);
        }
    }

    public void Validate()
    {
        if (Category is null || Category.Length > 64 ||
            Category is not ("未分类" or "对白" or "音乐" or "音效" or "环境声") ||
            ChannelMode is not ("立体声" or "单声道" or "仅左声道" or "仅右声道") ||
            !InRange(TargetLufs, -36, -5) || !InRange(TargetTruePeakDb, -9, 0) || !InRange(NormalizationGainDb, -60, 60) ||
            !InRange(Gain, 0, 4) || !InRange(Pan, -1, 1) ||
            !InRange(HighPassHz, 0, 20000) || !InRange(LowPassHz, 0, 20000) ||
            !InRange(EqualizerHz, 0, 20000) || !InRange(EqualizerGainDb, -24, 24) ||
            !InRange(CompressorThresholdDb, -60, 0) || !InRange(CompressorRatio, 1, 20) ||
            !InRange(LimiterCeiling, .05, 1) || !InRange(NoiseReductionDb, 0, 40) ||
            !InRange(EchoDelayMs, 1, 2000) || !InRange(EchoDecay, 0, .9) ||
            !InRange(DuckingGain, .05, 1) || !InRange(DuckingFadeSeconds, 0, 2))
            throw new AudioOperationException(AudioStageCode.Validate, "音频参数超出允许范围。");
    }

    private static bool InRange(double value, double low, double high) =>
        double.IsFinite(value) && value >= low && value <= high;
}

/// <summary>
/// Produces safe, comma-prefixed FFmpeg audio filter fragments. The caller
/// appends these after trimming and speed conversion, before timeline delay.
/// </summary>
public static class AudioProcessing
{
    public const string RemixFadeInKey = "TangerineRemixFadeInSeconds";
    public const string RemixFadeOutKey = "TangerineRemixFadeOutSeconds";

    public static string BuildClipFilters(TimelineClip clip) => BuildFilters(AudioSettings.FromClip(clip));

    public static string BuildRemixFadeFilters(TimelineClip clip)
    {
        double fadeIn = ReadFade(clip, RemixFadeInKey);
        double fadeOut = ReadFade(clip, RemixFadeOutKey);
        string filters = string.Empty;
        if (fadeIn > .0001)
            filters += $",afade=t=in:st=0:d={Number(fadeIn)}";
        if (fadeOut > .0001)
        {
            double start = Math.Max(0, clip.DurationSeconds - fadeOut);
            filters += $",afade=t=out:st={Number(start)}:d={Number(fadeOut)}";
        }
        return filters;
    }

    public static double ReadFade(TimelineClip clip, string key)
    {
        if (clip.AdditionalData?.TryGetValue(key, out JsonElement value) != true) return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds > Math.Min(1, clip.DurationSeconds))
            throw new AudioOperationException(AudioStageCode.Validate, "音乐重定时的淡化参数无效。");
        return seconds;
    }

    public static string BuildTrackFilters(TimelineTrack track) => BuildFilters(AudioSettings.FromTrack(track));

    public static string BuildFilters(AudioSettings settings)
    {
        settings.Validate();
        var filters = new List<string>();
        if (Math.Abs(settings.Gain - 1) > .0001) filters.Add($"volume={Number(settings.Gain)}");
        // Normalize mono or multichannel sources to stereo before channel
        // selection and a common pan law. Mono voiceovers remain pan-able.
        filters.Add("aformat=channel_layouts=stereo");
        if (settings.ChannelMode != "立体声")
            filters.Add(settings.ChannelMode switch
            {
                "单声道" => "pan=stereo|c0=.5*c0+.5*c1|c1=.5*c0+.5*c1",
                "仅左声道" => "pan=stereo|c0=c0|c1=c0",
                _ => "pan=stereo|c0=c1|c1=c1"
            });
        if (Math.Abs(settings.Pan) > .0001)
        {
            double left = settings.Pan > 0 ? Math.Cos(settings.Pan * Math.PI / 2) : 1;
            double right = settings.Pan < 0 ? Math.Cos(-settings.Pan * Math.PI / 2) : 1;
            filters.Add($"pan=stereo|c0={Number(left)}*c0|c1={Number(right)}*c1");
        }
        if (settings.HighPassHz > 0) filters.Add($"highpass=f={Number(Math.Clamp(settings.HighPassHz, 20, 19999))}");
        if (settings.LowPassHz > 0) filters.Add($"lowpass=f={Number(Math.Clamp(settings.LowPassHz, 20, 19999))}");
        if (settings.EqualizerHz >= 20 && Math.Abs(settings.EqualizerGainDb) >= .01)
            filters.Add($"equalizer=f={Number(settings.EqualizerHz)}:t=q:w=1:g={Number(settings.EqualizerGainDb)}");
        if (settings.NoiseReductionEnabled && settings.NoiseReductionDb > 0)
            filters.Add($"afftdn=nr={Number(settings.NoiseReductionDb)}");
        if (settings.CompressorEnabled)
            filters.Add($"acompressor=threshold={Number(Math.Pow(10, settings.CompressorThresholdDb / 20))}:ratio={Number(settings.CompressorRatio)}:attack=20:release=250");
        if (settings.EchoEnabled)
            filters.Add($"aecho=0.8:0.65:{Number(settings.EchoDelayMs)}:{Number(settings.EchoDecay)}");
        if (settings.LimiterEnabled) filters.Add($"alimiter=limit={Number(settings.LimiterCeiling)}");
        if (settings.NormalizeEnabled)
        {
            filters.Add($"volume={Number(settings.NormalizationGainDb)}dB");
            // Oversample the limiter so intersample peaks are constrained too.
            filters.Add($"aresample=192000,apad=pad_dur=0.005,alimiter=limit={Number(Math.Pow(10, settings.TargetTruePeakDb / 20))}:level=0,atrim=start=0.005,asetpts=PTS-STARTPTS,aresample=48000");
        }
        return "," + string.Join(',', filters);
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
