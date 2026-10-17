using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Speech-to-text transcription service using Whisper.cpp or Windows Speech Recognition.
/// Converts audio to timestamped subtitles for text-based editing workflows.
/// </summary>
public static class SpeechToTextService
{
    public enum Engine
    {
        WhisperCpp,        // Local Whisper.cpp (highest quality, requires model download)
        WindowsSpeech,     // Windows built-in speech recognition (lower quality, no setup)
        Auto               // Auto-select best available
    }

    public sealed record TranscriptionWord(
        string Text,
        double StartSeconds,
        double EndSeconds,
        double Confidence
    );

    public sealed record TranscriptionSegment(
        string Text,
        double StartSeconds,
        double EndSeconds,
        List<TranscriptionWord> Words
    );

    public sealed record TranscriptionResult(
        List<TranscriptionSegment> Segments,
        string Language,
        double TotalDuration,
        Engine UsedEngine
    );

    /// <summary>
    /// Transcribes audio from a media file to timestamped text.
    /// </summary>
    public static async Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        Engine engine = Engine.Auto,
        string language = "zh",
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Extract audio to temporary WAV file for processing
        string tempWav = Path.Combine(Path.GetTempPath(), $"transcribe-{Guid.NewGuid():N}.wav");

        try
        {
            await ExtractAudioAsync(mediaPath, tempWav, cancellationToken);

            Engine selectedEngine = engine == Engine.Auto ? DetectBestEngine() : engine;

            return selectedEngine switch
            {
                Engine.WhisperCpp => await TranscribeWithWhisperAsync(tempWav, language, progress, cancellationToken),
                Engine.WindowsSpeech => await TranscribeWithWindowsSpeechAsync(tempWav, language, progress, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported engine: {selectedEngine}")
            };
        }
        finally
        {
            try { File.Delete(tempWav); } catch { }
        }
    }

    /// <summary>
    /// Converts transcription result to subtitle track.
    /// </summary>
    public static List<TimelineClip> ConvertToSubtitles(
        TranscriptionResult transcription,
        string trackId,
        bool wordByWord = false)
    {
        var clips = new List<TimelineClip>();

        if (wordByWord)
        {
            // Word-by-word captions for social media style
            foreach (var segment in transcription.Segments)
            {
                foreach (var word in segment.Words)
                {
                    clips.Add(new TimelineClip
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = ClipKind.Text,
                        Name = word.Text,
                        StartSeconds = word.StartSeconds,
                        DurationSeconds = word.EndSeconds - word.StartSeconds,
                        Text = new ClipText
                        {
                            Content = word.Text,
                            FontFamily = "Microsoft YaHei UI",
                            FontSize = 48,
                            Bold = true,
                            ForegroundColor = "#FFFFFF",
                            OutlineEnabled = true,
                            OutlineColor = "#000000",
                            OutlineThickness = 3,
                            HorizontalAlignment = "Center",
                            VerticalAlignment = "Bottom"
                        }
                    });
                }
            }
        }
        else
        {
            // Sentence-based subtitles
            foreach (var segment in transcription.Segments)
            {
                clips.Add(new TimelineClip
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = ClipKind.Text,
                    Name = segment.Text.Length > 30 ? segment.Text.Substring(0, 30) + "…" : segment.Text,
                    StartSeconds = segment.StartSeconds,
                    DurationSeconds = segment.EndSeconds - segment.StartSeconds,
                    Text = new ClipText
                    {
                        Content = segment.Text,
                        FontFamily = "Microsoft YaHei UI",
                        FontSize = 32,
                        Bold = false,
                        ForegroundColor = "#FFFFFF",
                        OutlineEnabled = true,
                        OutlineColor = "#000000",
                        OutlineThickness = 2,
                        HorizontalAlignment = "Center",
                        VerticalAlignment = "Bottom"
                    }
                });
            }
        }

        return clips;
    }

    private static Engine DetectBestEngine()
    {
        // Check if Whisper.cpp is available
        string whisperPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper", "whisper.exe");
        if (File.Exists(whisperPath))
            return Engine.WhisperCpp;

        // Fall back to Windows Speech Recognition
        return Engine.WindowsSpeech;
    }

    private static async Task ExtractAudioAsync(string inputPath, string outputWav, CancellationToken cancellationToken)
    {
        string ffmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException("FFmpeg not found", ffmpeg);

        // The runner drains stderr. Extracting a long recording prints a
        // statistics line per chunk, and an unread redirected pipe would fill
        // and freeze the encoder for good.
        ProcessResult result = await ExternalProcessRunner.RunAsync(ffmpeg,
            $"-i \"{inputPath}\" -vn -ar 16000 -ac 1 -c:a pcm_s16le -y \"{outputWav}\"", cancellationToken);
        if (!result.Succeeded || !File.Exists(outputWav))
            throw new AudioOperationException(AudioStageCode.Read,
                "无法从媒体中提取音频：" + (result.ErrorTail().Length > 0 ? result.ErrorTail() : "编码器没有输出任何信息"));
    }

    private static async Task<TranscriptionResult> TranscribeWithWhisperAsync(
        string wavPath,
        string language,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string whisperExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper", "whisper.exe");
        string modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whisper", "models", "ggml-base.bin");

        if (!File.Exists(whisperExe))
            throw new FileNotFoundException("Whisper.cpp not found. Please download from https://github.com/ggerganov/whisper.cpp");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Whisper model not found at {modelPath}. Please download ggml-base.bin");

        string outputJson = Path.Combine(Path.GetTempPath(), $"whisper-{Guid.NewGuid():N}.json");

        // Whisper logs heavily to both streams while it works. They are drained
        // by the runner, and a cancel kills the whole process tree so a
        // half-finished transcription does not keep burning CPU.
        ProcessResult run = await ExternalProcessRunner.RunAsync(whisperExe,
            $"-m \"{modelPath}\" -l {language} -f \"{wavPath}\" -oj -of \"{outputJson}\"", cancellationToken);
        if (!run.Succeeded)
            throw new AudioOperationException(AudioStageCode.Process,
                "语音转写失败：" + (run.ErrorTail().Length > 0 ? run.ErrorTail() : "转写程序没有输出任何信息"));

        // Parse JSON output
        string jsonPath = outputJson + ".json";
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Whisper output not found");

        string json = await File.ReadAllTextAsync(jsonPath, cancellationToken);
        var result = ParseWhisperJson(json);

        try { File.Delete(jsonPath); } catch { }

        return result;
    }

    private static TranscriptionResult ParseWhisperJson(string json)
    {
        var segments = new List<TranscriptionSegment>();
        double totalDuration = 0;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("transcription", out var transcriptionArray))
        {
            foreach (var item in transcriptionArray.EnumerateArray())
            {
                if (item.TryGetProperty("timestamps", out var timestamps) &&
                    item.TryGetProperty("text", out var text))
                {
                    double start = timestamps.GetProperty("from").GetDouble() / 1000.0;
                    double end = timestamps.GetProperty("to").GetDouble() / 1000.0;
                    string content = text.GetString() ?? "";

                    var words = new List<TranscriptionWord>();
                    // Whisper.cpp doesn't provide word-level timestamps by default
                    // Approximate by splitting text evenly
                    var wordTexts = content.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double wordDuration = (end - start) / Math.Max(1, wordTexts.Length);
                    for (int i = 0; i < wordTexts.Length; i++)
                    {
                        words.Add(new TranscriptionWord(
                            wordTexts[i],
                            start + i * wordDuration,
                            start + (i + 1) * wordDuration,
                            1.0
                        ));
                    }

                    segments.Add(new TranscriptionSegment(content.Trim(), start, end, words));
                    totalDuration = Math.Max(totalDuration, end);
                }
            }
        }

        return new TranscriptionResult(segments, "zh", totalDuration, Engine.WhisperCpp);
    }

    private static async Task<TranscriptionResult> TranscribeWithWindowsSpeechAsync(
        string wavPath,
        string language,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // TODO: Implement Windows Speech Recognition integration using System.Speech
        // This is a placeholder implementation
        var segments = new List<TranscriptionSegment>
        {
            new("Windows 语音识别暂未实现，请使用 Whisper.cpp 引擎", 0, 10, new List<TranscriptionWord>())
        };

        return new TranscriptionResult(segments, language, 10, Engine.WindowsSpeech);
    }
}
