using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace Tangerine.VideoEditor.Speech;

public static class SpeechJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public sealed class SpeechFailure(string stageCode, string message, Exception? inner = null) : Exception(message, inner)
{ public string StageCode { get; } = stageCode; }
public enum SpeechAnchor { Auto, Start, End, Window }
public sealed record SpeechLanguage(string Code, string Name, string EngineName)
{ public override string ToString() => Name; }
public sealed record SpeechDevice(string Id, string Name, bool Available = true)
{
    public ulong DedicatedVideoMemory { get; init; }
    public ulong SharedVideoMemory { get; init; }
    public override string ToString() => Name;
}
public sealed class SpeechVoice
{
    public string Id { get; set; } = "vivian";
    public string Name { get; set; } = "Vivian · 明亮女声";
    public string Language { get; set; } = "zh";
    public string Gender { get; set; } = "unspecified";
    public string? ReferencePath { get; set; }
    public string? ReferenceText { get; set; }
    public string? ReferencePromptId { get; set; }
    public bool Builtin { get; set; }
    public string ModelFamily { get; set; } = "qwen3-base";
    public override string ToString() => Builtin ? Name : Name + (Gender == "male" ? " · 男声" : Gender == "female" ? " · 女声" : " · 未标注性别");
}
public sealed record SpeechModel(string Id, string Name, string Repository, string Kind)
{ public override string ToString() => Name; }
public sealed class SpeechPart
{
    public string Text { get; set; } = "";
    public string Language { get; set; } = "auto";
    // Initial markup is consumed only by the first generation. The editor
    // stores clean sentence parts afterwards, so later edits cannot replay it.
    public double InitialRate { get; set; } = 1;
    public bool HasInitialRate { get; set; }
}
public sealed class SpeechParameters
{
    public string VoiceId { get; set; } = "vivian";
    public string Language { get; set; } = "auto";
    public double Rate { get; set; } = 1;
    public double Pitch { get; set; }
    public double Gap { get; set; } = .2;
}
public sealed class SpeechSentence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public string? VoiceId { get; set; }
    public string? Language { get; set; }
    public double? Rate { get; set; }
    public double? Pitch { get; set; }
    public double? Gap { get; set; }
    public List<SpeechPart> Parts { get; set; } = [];
    public SpeechAnchor Anchor { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public double Duration { get; set; }
    public double RawDuration { get; set; }
    public double EffectiveRate { get; set; } = 1;
    public string? RawAssetId { get; set; }
    public string? AudioAssetId { get; set; }
    public string? Fingerprint { get; set; }
    public string? AudioFingerprint { get; set; }
}
public sealed class SpeechTrackData
{
    public int Version { get; set; } = 1;
    public string ModelSize { get; set; } = "0.6b";
    public string Device { get; set; } = "auto";
    public double Origin { get; set; }
    public SpeechParameters Defaults { get; set; } = new();
    public List<SpeechSentence> Sentences { get; set; } = [];
    public string Backend { get; set; } = "";
    public SpeechCaptionSettings? Captions { get; set; }
}
public sealed class SpeechCaptionSettings
{
    public string Language { get; set; } = "auto";
    public string? AlignmentModel { get; set; }
    public bool WordByWord { get; set; }
    public string Device { get; set; } = "cpu";
    public string? PythonPath { get; set; }
}
public sealed class SpeechRequest
{
    public string Command { get; set; } = "synthesize";
    public string ModelPath { get; set; } = "";
    public string Device { get; set; } = "auto";
    public bool PreferHybrid { get; set; }
    public int CpuThreads { get; set; }
    public string Text { get; set; } = "";
    public string Language { get; set; } = "auto";
    public string Speaker { get; set; } = "vivian";
    public string? ReferencePath { get; set; }
    public string? ReferenceText { get; set; }
    public string OutputPath { get; set; } = "";
}
public sealed class SpeechEvent
{
    public string Type { get; set; } = "progress";
    public string? Stage { get; set; }
    public string Message { get; set; } = "";
    public string? StageCode { get; set; }
    public string? Backend { get; set; }
    public string? GpuMode { get; set; }
    public string? DiagnosticCode { get; set; }
    public string? DiagnosticGraph { get; set; }
    public string? DiagnosticException { get; set; }
    public string? DiagnosticReason { get; set; }
    public double Duration { get; set; }
    public List<SpeechDevice>? Devices { get; set; }
}
public sealed record SpeechProgress(string Message, double Fraction, string? Backend = null)
{
    public string? Phase { get; init; }
    public int? CompletedUnits { get; init; }
    public int? TotalUnits { get; init; }
}
public static class SpeechPerformance
{
    // Independent sentences own independent sessions. Bound the number of
    // model replicas; give each worker a share of the machine's CPU threads.
    public static int MaxWorkers => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    public static int SentenceWorkers(string modelSize) => modelSize == "1.7b"
        ? Math.Min(2, MaxWorkers) : MaxWorkers;
    public static int ThreadsPerWorker(int workers) => Math.Max(1, Environment.ProcessorCount / Math.Max(1, workers));
}
public static class SpeechCatalog
{
    public static readonly SpeechLanguage[] Languages =
    [new("auto", "自动 / 混合语言", "auto"), new("zh", "中文", "chinese"), new("en", "英语", "english"),
     new("ja", "日语", "japanese"), new("ko", "韩语", "korean"), new("de", "德语", "german"),
     new("fr", "法语", "french"), new("ru", "俄语", "russian"), new("pt", "葡萄牙语", "portuguese"),
     new("es", "西班牙语", "spanish"), new("it", "意大利语", "italian")];
    public static readonly SpeechVoice[] Voices =
    [Voice("vivian", "Vivian · 明亮女声", "zh"), Voice("serena", "Serena · 温柔女声", "zh"),
     Voice("uncle_fu", "Uncle Fu · 沉稳男声", "zh"), Voice("dylan", "Dylan · 北京男声", "zh"),
     Voice("eric", "Eric · 四川男声", "zh"), Voice("ryan", "Ryan · 英语男声", "en"),
     Voice("aiden", "Aiden · 年轻男声", "en"), Voice("ono_anna", "Ono Anna · 日语女声", "ja"),
     Voice("sohee", "Sohee · 韩语女声", "ko")];
    private static SpeechVoice Voice(string id, string name, string lang) => new()
    { Id = id, Name = name, Language = lang, Builtin = true, ModelFamily = "qwen3-custom" };
    public static readonly SpeechModel[] Models =
    [new("custom-0.6b", "0.6B · 九种音色", "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX", "custom"),
     new("base-0.6b", "0.6B · 录音克隆", "elbruno/Qwen3-TTS-12Hz-0.6B-Base-ONNX", "base"),
     new("custom-1.7b", "1.7B · 高质量九种音色", "elbruno/Qwen3-TTS-12Hz-1.7B-CustomVoice-ONNX", "custom")];
    // Keep the coverage cues inside normal words. Pinyin/IPA inventories and
    // recording directions are metadata, never part of the reference transcript.
    public const string ChinesePrompt = "早晨，爸爸陪妈妈到河边散步，波光映着蓝天白云，白鸽从树梢飞过。小李提着竹篮，买了白菜、豆腐、苹果和鲜鱼。桥旁的姑娘轻声问：“你觉得绿茶香，还是热牛奶甜？”我笑着说：“雨后的空气真清新，院里的花儿也开了。”窗外，春风吹过长廊，远处的钟声渐渐传来。回家后，女儿翻开一本图画书，看见熊猫、蝴蝶和飞翔的小鸟。";
    public const string EnglishPrompt = "Early this morning, we walked along the quiet road beside the river. The bright sun warmed the grass, and a gentle breeze moved through the trees. My brother brought fresh bread, warm milk, and a few sweet oranges. Julia laughed as a small brown dog jumped over a wooden chair. “Would you like some tea?” she asked. “Yes, thank you,” I replied. We heard three children playing with a yellow toy nearby. It was a simple pleasure, and we were glad to be together.";
    public const string CloneRecordingInstructions = "只朗读“固定朗读示例”中的正文，不要朗读发音覆盖说明或录音指引。请使用平常说话的音高、口音和语速，保持连贯，按标点自然停顿，不要逐字拖长或把句子读成音节表；保持环境安静、麦克风距离稳定，不要配乐、耳语、喊叫或刻意模仿其他声音。";
    public static string ClonePrompt(string language) => language switch
    {
        "zh" => ChinesePrompt, "en" => EnglishPrompt,
        _ => throw new SpeechFailure("VOICE0006", "克隆录音支持中文或英语。")
    };
    public static string ClonePromptId(string language) => language switch
    {
        "zh" => "phonetic-zh-v3", "en" => "phonetic-en-v3",
        _ => throw new SpeechFailure("VOICE0006", "克隆录音支持中文或英语。")
    };
    public static string ClonePromptCoverage(string language) => language switch
    {
        "zh" => "自然句中覆盖 21 个普通话声母，兼顾主要单韵母、复韵母及鼻韵母；包括平翘舌、送气与不送气、前后鼻音、四声和轻声，例如白菜／竹篮、绿茶／鲜鱼、小李／妈妈。请自然朗读正文，不单独拼读这些词。",
        "en" => "自然句中兼顾常见元音、双元音、清浊辅音及连读环境；包括 three／this、fresh／pleasure、chair／Julia、morning，以及 bright／brown／toy／road。保留自己平常的口音，不要刻意模仿或逐个读音标。",
        _ => throw new SpeechFailure("VOICE0006", "克隆录音支持中文或英语。")
    };
}
public static class SpeechText
{
    private static readonly Regex Commands = new(@"\[(sps|spe|s)(\d+)\]", RegexOptions.CultureInvariant);
    public static List<SpeechSentence> Parse(string text, double defaultGap = .2)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100000)
            throw new SpeechFailure("VOICE0001", "请输入 1～100000 字符的文字。");
        var result = new List<SpeechSentence>();
        var parts = new List<SpeechPart>();
        double rate = 1;
        double? gap = null;
        int? speedTag = null;
        int offset = 0;
        string group = Guid.NewGuid().ToString("N");
        void Flush()
        {
            if (parts.Count == 0) return;
            string raw = string.Concat(parts.Select(p => p.Text));
            string clean = raw.Trim();
            if (clean.Length == 0) { parts = []; return; }
            result.Add(new SpeechSentence { GroupId = group, Text = clean,
                Parts = SliceParts(raw, parts, raw.Length - raw.TrimStart().Length, clean.Length), Gap = gap });
            parts = []; gap = null;
        }
        void Append(string value)
        {
            foreach (var chunk in SplitSentences(value))
            {
                if (chunk.Text.Length == 0) continue;
                parts.Add(new SpeechPart { Text = chunk.Text, InitialRate = rate, HasInitialRate = speedTag is not null });
                if (chunk.End) { Flush(); group = Guid.NewGuid().ToString("N"); }
            }
        }
        foreach (Match match in Commands.Matches(text))
        {
            Append(text[offset..match.Index]); offset = match.Index + match.Length;
            if (!int.TryParse(match.Groups[2].Value, out int number)) throw new SpeechFailure("VOICE0001", "语音标记数值过大。");
            switch (match.Groups[1].Value)
            {
                case "s":
                    if (number > 3600000 || (gap ?? 0) + number / 1000d > 3600) throw new SpeechFailure("VOICE0001", "单次停顿不能超过一小时。");
                    Flush(); gap = (gap ?? 0) + number / 1000d; break;
                case "sps":
                    if (speedTag is not null || number is < 50 or > 200)
                        throw new SpeechFailure("VOICE0001", "速度标记不得嵌套，范围为 50～200%。");
                    speedTag = number; rate = number / 100d; break;
                case "spe":
                    if (speedTag != number) throw new SpeechFailure("VOICE0001", "spe 必须与前面的 sps 数值一致并成对出现。");
                    speedTag = null; rate = 1; break;
            }
        }
        Append(text[offset..]);
        if (speedTag is not null) throw new SpeechFailure("VOICE0001", "sps 后缺少对应的 spe。");
        Flush();
        if (result.Count == 0) throw new SpeechFailure("VOICE0001", "文字中没有可生成的句子。");
        if (result.Count > 2000) throw new SpeechFailure("VOICE0001", "一次最多生成 2000 个音频对象。");
        return result;
    }
    private static IEnumerable<(string Text, bool End)> SplitSentences(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool end = "。！？!?\n\r".Contains(c);
            if (c == '.')
            {
                string word = Regex.Match(text[..(i + 1)], @"[A-Za-z.]+\.$").Value;
                bool abbreviation = Regex.IsMatch(word, @"^(?:Mr|Mrs|Ms|Dr|Prof|Sr|Jr|vs|etc|e\.g|i\.e)\.$", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(word, @"^(?:[A-Za-z]\.)+$");
                end = !abbreviation && !(i > 0 && i + 1 < text.Length && char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1])) &&
                    (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));
            }
            if (!end) continue;
            yield return (text[start..(i + 1)], true); start = i + 1;
        }
        if (start < text.Length) yield return (text[start..], false);
    }
    public static void Validate(SpeechTrackData data)
    {
        if (data.Defaults is null || data.Sentences is null || data.Sentences.Any(s => s is null || s.Parts is null || s.Parts.Any(p => p is null)))
            throw new SpeechFailure("VOICE0001", "语音轨道缺少默认参数或句子数据。");
        if (!double.IsFinite(data.Origin) || data.Origin < 0 || data.Origin > 86400 || data.Sentences.Count is < 1 or > 2000 ||
            data.ModelSize is not ("0.6b" or "1.7b")) throw new SpeechFailure("VOICE0001", "语音轨道参数无效。");
        if (!double.IsFinite(data.Defaults.Rate) || data.Defaults.Rate is < .5 or > 2 ||
            !double.IsFinite(data.Defaults.Pitch) || data.Defaults.Pitch is < -12 or > 12 ||
            !double.IsFinite(data.Defaults.Gap) || data.Defaults.Gap is < 0 or > 3600)
            throw new SpeechFailure("VOICE0001", "语速为 0.5～2 倍，音调为 ±12 半音，停顿为 0～3600 秒。");
        if (data.Captions is { } captions &&
            (captions.Language is not ("auto" or "zh" or "zh-Hant" or "en" or "ja" or "ko" or "fr" or "de" or "es" or "it" or "pt" or "ru") ||
             captions.Device is not ("cpu" or "cuda")))
            throw new SpeechFailure("VOICE0001", "逐字对齐语言或设备无效。");
        var ids = new HashSet<string>();
        foreach (SpeechSentence s in data.Sentences)
        {
            double rate = s.Rate ?? data.Defaults.Rate, pitch = s.Pitch ?? data.Defaults.Pitch, gap = s.Gap ?? data.Defaults.Gap;
            if (!ids.Add(s.Id) || !Guid.TryParseExact(s.Id, "N", out _) || string.IsNullOrWhiteSpace(s.Text) || s.Text.Length > 10000 ||
                !Enum.IsDefined(s.Anchor) || !double.IsFinite(rate) || rate is < .5 or > 2 || !double.IsFinite(pitch) || pitch is < -12 or > 12 ||
                !double.IsFinite(gap) || gap is < 0 or > 3600 || !double.IsFinite(s.Start) || !double.IsFinite(s.End) ||
                s.Start < 0 || s.End < 0 || s.RawDuration < 0 || !double.IsFinite(s.RawDuration))
                throw new SpeechFailure("VOICE0001", "句子、语速、音调或位置参数无效。");
            string language = s.Language ?? data.Defaults.Language;
            if (!SpeechCatalog.Languages.Any(l => l.Code == language) || s.Parts.Any(p => !SpeechCatalog.Languages.Any(l => l.Code == p.Language) ||
                !double.IsFinite(p.InitialRate) || p.InitialRate is < .5 or > 2) ||
                s.Parts.Count > 0 && string.Concat(s.Parts.Select(p => p.Text)).Trim() != s.Text.Trim())
                throw new SpeechFailure("VOICE0001", "语言片段必须覆盖当前句子的全部文字。");
        }
    }
    public static List<SpeechPart> SliceParts(string text, List<SpeechPart> prior, int start, int length)
    {
        if (start < 0 || length < 1 || start > text.Length - length)
            throw new SpeechFailure("VOICE0001", "文字片段范围无效。");
        if (string.Concat(prior.Select(p => p.Text)) != text) prior = [new() { Text = text }];
        var result = new List<SpeechPart>(); int offset = 0;
        foreach (SpeechPart part in prior)
        {
            int from = Math.Max(start, offset), until = Math.Min(start + length, offset + part.Text.Length);
            if (until > from)
            {
                SpeechPart slice = SpeechJson.Copy(part); slice.Text = part.Text[(from - offset)..(until - offset)];
                result.Add(slice);
            }
            offset += part.Text.Length;
        }
        return result;
    }
    public static List<SpeechPart> MarkLanguage(string text, List<SpeechPart> prior, int start, int length, string language)
    {
        if (start < 0 || length < 1 || start + length > text.Length || !SpeechCatalog.Languages.Any(l => l.Code == language))
            throw new SpeechFailure("VOICE0001", "请选择有效文字与语言。");
        if (string.Concat(prior.Select(p => p.Text)) != text) prior = [new() { Text = text }];
        var result = new List<SpeechPart>(); int offset = 0;
        foreach (SpeechPart part in prior)
        {
            int end = offset + part.Text.Length;
            int from = Math.Clamp(start - offset, 0, part.Text.Length), until = Math.Clamp(start + length - offset, 0, part.Text.Length);
            if (offset >= start + length || end <= start) result.Add(SpeechJson.Copy(part));
            else
            {
                if (from > 0) result.Add(new() { Text = part.Text[..from], Language = part.Language, InitialRate = part.InitialRate, HasInitialRate = part.HasInitialRate });
                result.Add(new() { Text = part.Text[from..until], Language = language, InitialRate = part.InitialRate, HasInitialRate = part.HasInitialRate });
                if (until < part.Text.Length) result.Add(new() { Text = part.Text[until..], Language = part.Language, InitialRate = part.InitialRate, HasInitialRate = part.HasInitialRate });
            }
            offset = end;
        }
        return result;
    }
    public static void Arrange(SpeechTrackData data, bool preserveDurations = false)
    {
        Validate(data);
        double end = data.Origin;
        for (int index = 0; index < data.Sentences.Count; index++)
        {
            SpeechSentence s = data.Sentences[index];
            if (s.RawDuration <= 0) throw new SpeechFailure("VOICE0004", "请先生成语音再设置位置。");
            double rate = s.Rate ?? data.Defaults.Rate;
            if (s.Anchor == SpeechAnchor.Window)
            {
                if (s.End <= s.Start) throw new SpeechFailure("VOICE0004", "固定区间的停止点必须晚于起点。");
                rate = s.RawDuration / (s.End - s.Start);
                if (rate < .5 - .0001 || rate > 2 + .0001) throw new SpeechFailure("VOICE0004", "固定区间需要的语速超出 0.5～2 倍，请调整位置。");
            }
            s.EffectiveRate = Math.Clamp(rate, .5, 2);
            if (!preserveDurations) s.Duration = s.RawDuration / s.EffectiveRate;
            double gap = s.Gap ?? data.Defaults.Gap;
            if (s.Anchor == SpeechAnchor.Auto) s.Start = end + (index == 0 ? s.Gap ?? 0 : gap);
            else if (s.Anchor == SpeechAnchor.End) s.Start = s.End - s.Duration;
            if (s.Start < -.0001 || index > 0 && s.Start < end - .001 || s.Start + s.Duration > 86400)
                throw new SpeechFailure("VOICE0004", $"句子“{s.Text[..Math.Min(24, s.Text.Length)]}”与前句冲突或超出时间范围；固定句不会自动移动。");
            s.Start = Math.Max(0, s.Start); s.End = s.Start + s.Duration; end = s.End;
        }
    }
}
