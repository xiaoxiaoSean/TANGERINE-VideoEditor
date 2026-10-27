using System.IO;
using Tangerine.VideoEditor.Speech;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public static class SpeechCaptionService
{
    public static async Task<TimelineTrack> GenerateAsync(TimelineTrack speech,
        IReadOnlyDictionary<string, MediaAsset> assets, SpeechCaptionSettings settings,
        TimelineTrack? previous, SpeechCaptionSettings? previousSettings,
        IProgress<SpeechProgress>? progress, CancellationToken token)
    {
        if (speech.Speech is null || speech.Clips.Count == 0)
            throw new ArgumentException("没有可生成字幕的语音片段。");
        var options = new WhisperXOptions { Language = settings.Language, RecognitionModel = "",
            ChineseScript = settings.Language == "zh-Hant" ? "Traditional" : "Simplified",
            WordByWord = settings.WordByWord, AlignmentModel = settings.WordByWord ? settings.AlignmentModel : null };
        bool sameSettings = previous?.Subtitle?.RecognitionModel == "" && previousSettings is not null &&
            previousSettings.Language == settings.Language &&
            previousSettings.WordByWord == settings.WordByWord &&
            (!settings.WordByWord || previousSettings.AlignmentModel == settings.AlignmentModel);
        var recognized = new List<(TimelineClip Clip, MediaAsset Asset, WhisperXResult Result)>();
        var reused = new Dictionary<string, List<SubtitleCueData>>();
        for (int i = 0; i < speech.Clips.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            TimelineClip clip = speech.Clips[i];
            SpeechSentence sentence = speech.Speech.Sentences.First(item => item.Id == clip.Id);
            string language = ResolveLanguage(speech.Speech, sentence, settings.Language);
            if (clip.MediaAssetId is null || !assets.TryGetValue(clip.MediaAssetId, out MediaAsset? asset))
                throw new InvalidOperationException("生成的语音音频不存在，无法生成字幕。");
            List<SubtitleCueData>? cached = sameSettings ? previous?.Subtitle?.Cues
                .Where(cue => cue.SourceClipId == clip.Id && cue.SourceMediaAssetId == asset.Id).ToList() : null;
            if (cached is { Count: 1 })
            {
                reused.Add(clip.Id, cached);
                var cachedResult = new WhisperXResult { Language = language, DurationSeconds = clip.DurationSeconds };
                foreach (SubtitleCueData cue in cached)
                    cachedResult.Segments.Add(new WhisperXSegment
                    {
                        Text = cue.Style.Content, StartSeconds = cue.SourceStartSeconds, EndSeconds = cue.SourceEndSeconds,
                        Characters = cue.Characters.Select(c => new WhisperXWord { Text = c.Text,
                            StartSeconds = c.SourceStartSeconds, EndSeconds = c.SourceEndSeconds,
                            IsAligned = c.SourceStartSeconds.HasValue }).ToList()
                    });
                recognized.Add((clip, asset, cachedResult));
                progress?.Report(new($"字幕已完成 {i + 1}/{speech.Clips.Count} 句", -1)
                    { CompletedUnits = i + 1, TotalUnits = speech.Clips.Count });
                continue;
            }
            WhisperXResult result;
            if (settings.WordByWord)
            {
                int index = i;
                var report = new Progress<WhisperXProgress>(p => progress?.Report(new SpeechProgress(
                    $"字幕已完成 {index}/{speech.Clips.Count} 句 · 第 {index + 1} 句逐字对齐 {p.Fraction:P0} · {p.Message}",
                    -1) { CompletedUnits = index, TotalUnits = speech.Clips.Count }));
                result = await WhisperXService.AlignTextAsync(asset.SourcePath, sentence.Text, language,
                    settings.AlignmentModel, settings.Device, settings.PythonPath, report, token);
                if (result.Segments.Count != 1 || result.Segments[0].Text != sentence.Text ||
                    string.Concat(result.Segments[0].Characters.Select(character => character.Text)) != sentence.Text)
                    throw new InvalidDataException("逐字对齐结果未完整保留语音句子的原文。");
                result.Segments[0].EndSeconds = clip.DurationSeconds;
            }
            else result = new WhisperXResult { Language = language, DurationSeconds = clip.DurationSeconds,
                Segments = [new WhisperXSegment { Text = sentence.Text, StartSeconds = 0, EndSeconds = clip.DurationSeconds }] };
            progress?.Report(new($"字幕已完成 {i + 1}/{speech.Clips.Count} 句", -1)
                { CompletedUnits = i + 1, TotalUnits = speech.Clips.Count });
            recognized.Add((clip, asset, result));
        }
        TimelineTrack subtitles = SubtitleTrackService.CreateSpeechTrack(speech, recognized, options, previous);
        if (reused.Count > 0)
        {
            foreach ((string id, List<SubtitleCueData> old) in reused)
            {
                int first = subtitles.Subtitle!.Cues.FindIndex(cue => cue.SourceClipId == id);
                subtitles.Subtitle.Cues.RemoveRange(first, old.Count);
                subtitles.Subtitle.Cues.InsertRange(first, old.Select(SpeechJson.Copy));
            }
            SubtitleTrackService.Synchronize(new ProjectDocument { Tracks = [subtitles, speech] });
        }
        return subtitles;
    }

    private static string ResolveLanguage(SpeechTrackData speech, SpeechSentence sentence, string selected)
    {
        if (selected != "auto") return WhisperXModelCatalog.RecognitionLanguage(selected);
        string language = sentence.Language ?? speech.Defaults.Language;
        if (language == "auto") language = sentence.Parts.Where(part => part.Language != "auto")
            .OrderByDescending(part => part.Text.Length).FirstOrDefault()?.Language ?? "auto";
        if (language != "auto") return language;
        string text = sentence.Text;
        if (text.Any(c => c is >= '\u3040' and <= '\u30FF')) return "ja";
        if (text.Any(c => c is >= '\uAC00' and <= '\uD7AF')) return "ko";
        if (text.Any(c => c is >= '\u4E00' and <= '\u9FFF')) return "zh";
        if (text.Any(c => c is >= '\u0400' and <= '\u052F')) return "ru";
        return "en";
    }
}
