using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tangerine.VideoEditor.Speech;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Tools;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private readonly SpeechWorkerClient _speechWorker = new();
    private readonly Dictionary<string, long> _speechRequests = [];
    private readonly Dictionary<string, TaskCompletionSource> _speechReaders = [];
    private long _speechSerial;
    private CancellationTokenSource? _speechUiCancellation;
    private bool _speechUiBusy;
    private WaveInRecorder? _speechRecorder;
    private string? _speechRecordingLanguage;
    private BitmapSource? _speechToolCover;
    private SpeechDragState? _speechDrag;
    private string _speechInputText = "";
    private string? _speechParsedInputText;
    private long _speechAuditionVersion;
    private CancellationTokenSource? _speechAuditionCancellation;
    private GeneratedSpeech? _speechAuditionFiles;
    private sealed record SpeechDragState(TimelineTrack Track, string SentenceId, Point Origin, SpeechTrackData Before)
    { public SpeechTrackData? Proposed { get; set; } public bool Moved { get; set; } }

    private void InitializeSpeech()
    {
        Closed += (_, _) =>
        {
            _speechUiCancellation?.Cancel(); _speechRecorder?.Dispose(); _speechRecorder = null; _speechWorker.Dispose();
        };
    }
    private void ResetSpeechContext()
    {
        _speechUiCancellation?.Cancel(); _speechRequests.Clear(); _speechInputText = ""; _speechParsedInputText = null; _speechPageReturn = null;
        _speechRecorder?.Dispose(); _speechRecorder = null; _speechMcpRecordingOwner = null; _speechMcpRecordingPrompt = null;
        FinishSpeechDrag(cancel: true);
    }
    private BitmapSource GetSpeechToolCover() => _speechToolCover ??= LoadBundledThumbnail(
        Path.Combine(AppContext.BaseDirectory, "Resources", "Speech", "voice-tool.png")) ?? throw new SpeechFailure("VOICE0008", "语音工具封面缺失。");
    private static TextBox SpeechBox(string text = "") => new()
    {
        Text = text, Margin = new Thickness(0, 4, 0, 8), Background = Brush("#292B31"),
        Foreground = Brushes.White, CaretBrush = Brushes.White
    };
    private static double SpeechNumber(TextBox box, string name)
    {
        if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value))
            throw new SpeechFailure("VOICE0001", name + "必须是有效数字。");
        return value;
    }
    private static ComboBox SpeechChoice<T>(IEnumerable<T> values, Func<T, bool> selected) => new()
    { ItemsSource = values.ToArray(), SelectedItem = values.FirstOrDefault(selected), Margin = new Thickness(0, 4, 0, 8) };
    private static void SpeechField(Panel panel, string name, UIElement element)
    { panel.Children.Add(new TextBlock { Text = name, Foreground = Brush("#CED3DE") }); panel.Children.Add(element); }
    private void SpeechError(Exception exception)
    {
        string stage = StageCodes.Resolve(exception, "VOICE0008");
        StatusText.Text = exception.Message + " · " + stage;
        ErrorDialog.Show(this, stage, exception);
    }
    private void SpeechAction(Action action) { try { action(); } catch (Exception e) { SpeechError(e); } }
    private void AddSpeechAction(Panel panel, string name, Action action) => AddToolButton(panel, name, () => SpeechAction(action));
    private async Task RunSpeechUiAsync(string title, Func<IProgress<SpeechProgress>, CancellationToken, Task> work)
    {
        if (_speechUiBusy) { StatusText.Text = "已有语音任务正在运行，请等待或取消。"; return; }
        _speechUiBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_experienceLifetime.Token);
        _speechUiCancellation = cancellation;
        var message = new TextBlock { Text = "正在准备…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 10) };
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 6, 16, 6) };
        cancel.Click += (_, _) => cancellation.Cancel();
        var layout = new StackPanel { Margin = new Thickness(18) }; layout.Children.Add(message); layout.Children.Add(bar); layout.Children.Add(cancel);
        var window = new Window { Owner = this, Title = title, Width = Math.Max(1, ActualWidth * .4), SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#191B20"), Foreground = Brushes.White, Content = layout };
        window.Closing += (_, _) => cancellation.Cancel();
        var progress = new Progress<SpeechProgress>(p =>
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_speechUiCancellation, cancellation)) return;
            message.Text = p.Message + (p.Backend is null ? "" : "\n" + p.Backend);
            bar.IsIndeterminate = p.Fraction < 0; if (p.Fraction >= 0) bar.Value = p.Fraction;
        });
        window.Show();
        try { await work(progress, cancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "语音任务已取消，原结果已保留。"; }
        catch (Exception e) { SpeechError(e); }
        finally
        {
            _speechUiCancellation = null; _speechUiBusy = false;
            if (window.IsVisible) window.Close();
        }
    }
    private async Task<TimelineTrack> GenerateSpeechAsync(SpeechTrackData settings, TimelineTrack? track,
        IProgress<SpeechProgress> progress, CancellationToken token, string? forceSentence = null)
    {
        settings = SpeechJson.Copy(settings);
        if (track?.IsLocked == true || track is not null && (track.Speech is null || !_project.Tracks.Contains(track)))
            throw new SpeechFailure("VOICE0004", "语音轨道不存在或已锁定。");
        SpeechText.Validate(settings);
        McpEnsureIdle();
        ProjectDocument owner = _project; long generation = _projectGeneration;
        string sequence = _project.Sequence.Id, requestKey = track?.Id ?? "new-" + Guid.NewGuid().ToString("N");
        long request = ++_speechSerial; _speechRequests[requestKey] = request;
        string? signature = track is null ? null : JsonSerializer.Serialize(track, SpeechJson.Options);
        TimelineTrack? captionTrack = track is null ? null : owner.Tracks.FirstOrDefault(item => item.Subtitle?.SpeechSourceTrackId == track.Id);
        string? captionSignature = captionTrack is null ? null : JsonSerializer.Serialize(captionTrack, SpeechJson.Options);
        string? controlOwner = _mcpControlOwnerSessionId;
        var existing = owner.Media.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => SpeechJson.Copy(g.First()));
        string cache = ProjectFileService.GeneratedMediaDirectory(owner);
        var reader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string readerId = Guid.NewGuid().ToString("N"); _speechReaders[readerId] = reader;
        try
        {
            IProgress<SpeechProgress> voiceProgress = new Progress<SpeechProgress>(value =>
                progress.Report(value with { Fraction = -1 }));
            using GeneratedSpeech generated = await Task.Run(() => SpeechGeneration.GenerateAsync(_speechWorker,
                settings, existing, cache, voiceProgress, token, forceSentence), token);
            token.ThrowIfCancellationRequested();
            bool CaptionsCurrent() => captionTrack is null
                ? track is null || !owner.Tracks.Any(item => item.Subtitle?.SpeechSourceTrackId == track.Id)
                : owner.Tracks.Contains(captionTrack) && !captionTrack.IsLocked &&
                    captionSignature == JsonSerializer.Serialize(captionTrack, SpeechJson.Options);
            if (!ReferenceEquals(owner, _project) || generation != _projectGeneration || sequence != _project.Sequence.Id ||
                controlOwner != _mcpControlOwnerSessionId || !_speechRequests.TryGetValue(requestKey, out long current) || current != request ||
                !CaptionsCurrent() ||
                track is not null && (!_project.Tracks.Contains(track) || track.IsLocked || signature != JsonSerializer.Serialize(track, SpeechJson.Options)))
                throw new SpeechFailure("VOICE0009", "工程、句子或操作权已改变；旧任务没有覆盖当前状态。");
            TimelineTrack? replacementCaptions = null;
            if (generated.Data.Captions is { } captionSettings)
            {
                var preparedSpeech = new TimelineTrack { Id = track?.Id ?? Guid.NewGuid().ToString("N"),
                    Name = track?.Name ?? "语音生成", Kind = TrackKind.Audio };
                SpeechGeneration.Apply(preparedSpeech, generated.Data);
                var assets = existing.Concat(generated.Assets.Select(asset => new KeyValuePair<string, MediaAsset>(asset.Id, asset)))
                    .ToDictionary(item => item.Key, item => item.Value);
                replacementCaptions = await SpeechCaptionService.GenerateAsync(preparedSpeech, assets, captionSettings,
                    captionTrack, track?.Speech?.Captions, progress, token);
            }
            progress.Report(new("正在检查工程空间与提交条件", -1, generated.Data.Backend) { Phase = "commit" });
            await ProjectFileService.EnsureImportCapacityAsync(owner.Media.Select(a => a.SourcePath).Concat(generated.Assets.Select(a => a.SourcePath)), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(owner, _project) || generation != _projectGeneration || sequence != _project.Sequence.Id || controlOwner != _mcpControlOwnerSessionId ||
                !_speechRequests.TryGetValue(requestKey, out current) || current != request || HasActiveEditorGesture ||
                !CaptionsCurrent() ||
                track is not null && (!_project.Tracks.Contains(track) || track.IsLocked || signature != JsonSerializer.Serialize(track, SpeechJson.Options)))
                throw new SpeechFailure("VOICE0009", "空间检查期间工程或句子已改变，未提交语音。" );
            SaveUndoState();
            TimelineTrack destination = track ?? new TimelineTrack { Id = replacementCaptions?.Subtitle?.SpeechSourceTrackId ?? Guid.NewGuid().ToString("N"),
                Name = "语音生成", Kind = TrackKind.Audio, Order = owner.Tracks.Count, IsSnapping = false };
            if (track is null) owner.Tracks.Add(destination);
            owner.Media.AddRange(generated.Assets); SpeechGeneration.Apply(destination, generated.Data);
            if (replacementCaptions is not null)
            {
                if (captionTrack is null) owner.Tracks.Insert(owner.Tracks.IndexOf(destination), replacementCaptions);
                else { captionTrack.Subtitle = replacementCaptions.Subtitle; captionTrack.Clips = replacementCaptions.Clips; }
            }
            else if (captionTrack is not null) owner.Tracks.Remove(captionTrack);
            for (int i = 0; i < owner.Tracks.Count; i++) owner.Tracks[i].Order = i;
            generated.Committed = true;
            _speechInputText = ""; _speechParsedInputText = null;
            MarkProjectDirty(applySnapping: false); RefreshLibrary(); RefreshTimeline();
            _selectedTrack = destination; _contextTrack = destination;
            _selectedClip = forceSentence is null ? destination.Clips.FirstOrDefault() : destination.Clips.FirstOrDefault(c => c.Id == forceSentence);
            _selectedClips.Clear(); if (_selectedClip is not null) _selectedClips.Add(_selectedClip);
            ShowSpeechTool(destination); SeekPreview(_selectedClip?.StartSeconds ?? generated.Data.Origin);
            StatusText.Text = $"已生成 {destination.Clips.Count} 句语音" +
                (replacementCaptions is null ? "" : $"、{replacementCaptions.Subtitle!.Cues.Count} 句字幕") +
                $" · {generated.Data.Backend}";
            progress.Report(new("语音生成已提交", 1, generated.Data.Backend) { Phase = "completed" });
            UpdateMcpInfo(); return destination;
        }
        finally
        {
            if (_speechRequests.GetValueOrDefault(requestKey) == request) _speechRequests.Remove(requestKey);
            _speechReaders.Remove(readerId); reader.TrySetResult();
        }
    }
    private bool CanEditSpeech(FrameworkElement page, TimelineTrack track) => page.IsLoaded && page.IsVisible &&
        _project.Tracks.Contains(track) && track.Speech is not null && !track.IsLocked && !_speechUiBusy;

    private void ShowSpeechTool(TimelineTrack? track = null, SpeechTrackData? draft = null, string? selectedSentence = null)
    {
        if (track is not null && (!_project.Tracks.Contains(track) || track.Speech is null)) return;
        SpeechTrackData data = SpeechJson.Copy(draft ?? track?.Speech ?? new SpeechTrackData { Origin = _project.Sequence.PlayheadSeconds });
        var root = new StackPanel { Margin = new Thickness(4) };
        root.Children.Add(SettingsHeader(track is null ? "语音生成" : track.Name + " · 语音轨道", CloseSettingsTabs));
        SubtitleHint(root, "本地离线 · 十种语言及混合输出 · 每句独立音频\n模型位置：" + SpeechAssets.ModelsRoot);
        var manage = new WrapPanel();
        root.Children.Add(manage);
        var voice = SpeechChoice(SpeechAssets.Voices(), v => v.Id == data.Defaults.VoiceId);
        var language = SpeechChoice(SpeechCatalog.Languages, l => l.Code == data.Defaults.Language);
        var size = new ComboBox { ItemsSource = new[] { "0.6B · 轻量", "1.7B · 预设音色高质量" }, SelectedIndex = data.ModelSize == "1.7b" ? 1 : 0, Margin = new Thickness(0, 4, 0, 8) };
        var devices = new ComboBox { ItemsSource = new[] { new SpeechDevice("auto", "自动（GPU 优先）"), new SpeechDevice("cpu", "CPU") }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 8) };
        devices.SelectedValuePath = "Id"; devices.SelectedValue = data.Device;
        if (devices.SelectedIndex < 0) { devices.ItemsSource = new[] { new SpeechDevice(data.Device, data.Device), new SpeechDevice("auto", "自动（GPU 优先）"), new SpeechDevice("cpu", "CPU") }; devices.SelectedIndex = 0; }
        _ = LoadSpeechDevicesAsync(devices, data.Device);
        var rate = SpeechBox(data.Defaults.Rate.ToString(CultureInfo.CurrentCulture));
        var pitch = SpeechBox(data.Defaults.Pitch.ToString(CultureInfo.CurrentCulture));
        var gap = SpeechBox(data.Defaults.Gap.ToString(CultureInfo.CurrentCulture));
        var origin = SpeechBox(data.Origin.ToString("0.###", CultureInfo.CurrentCulture));
        SpeechField(root, "默认音色", voice); SpeechField(root, "默认语言", language); SpeechField(root, "模型规格（录音克隆使用 0.6B Base）", size);
        SpeechField(root, "计算设备", devices); SpeechField(root, "默认语速（0.5～2 倍）", rate); SpeechField(root, "默认音调（-12～12 半音）", pitch);
        SpeechField(root, "默认句间停顿（秒）", gap); SpeechField(root, "开始排布位置（秒）", origin);
        Action readCaptions = AddSpeechCaptionControls(root, data);
        void ReadDefaults()
        {
            if (voice.SelectedItem is not SpeechVoice v || language.SelectedItem is not SpeechLanguage l || devices.SelectedItem is not SpeechDevice d)
                throw new SpeechFailure("VOICE0001", "请选择音色、语言与设备。");
            data.Defaults = new() { VoiceId = v.Id, Language = l.Code, Rate = SpeechNumber(rate, "语速"), Pitch = SpeechNumber(pitch, "音调"), Gap = SpeechNumber(gap, "停顿") };
            data.Device = d.Id; data.ModelSize = size.SelectedIndex == 1 ? "1.7b" : "0.6b"; data.Origin = SpeechNumber(origin, "起点");
            readCaptions();
        }
        AddSpeechAction(manage, "模型管理", () => { ReadDefaults(); _speechPageReturn = () => ShowSpeechTool(track, data); ShowSpeechModels(); });
        AddSpeechAction(manage, "音色 / 克隆", () =>
        {
            ReadDefaults(); _speechPageReturn = () => ShowSpeechTool(track, data);
            ShowSpeechVoices(id => { data.Defaults.VoiceId = id; ShowSpeechTool(track, data); });
        });
        if (track is null)
        {
            var text = SpeechBox(_speechInputText); text.AcceptsReturn = true; text.TextWrapping = TextWrapping.Wrap;
            text.TextChanged += (_, _) => _speechInputText = text.Text;
            text.MinLines = 5; text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            SpeechField(root, "输入文字（可混合语言）", text);
            SubtitleHint(root, "[s2000] 留出两秒轨道空隙；[sps200]文字[spe200] 以 200% 初始语速生成。标记只执行一次。详见 voice_make.md。");
            AddSpeechAction(root, "自动分句 / 重新解析文字", () =>
            {
                ReadDefaults(); data.Sentences = SpeechText.Parse(text.Text, data.Defaults.Gap);
                _speechParsedInputText = text.Text;
                ShowSpeechTool(null, data);
            });
        }
        else
        {
            SubtitleHint(root, track.IsLocked ? "轨道已锁定，请先解锁。" : $"{data.Sentences.Count} 句 · {data.Backend}\n可拖动整句调整位置；时长由实际语音和语速决定。");
            AddSpeechAction(root, "删除语音轨道", () =>
            {
                if (!CanEditSpeech(root, track)) return;
                SaveUndoState(); _project.Tracks.Remove(track);
                _project.Tracks.RemoveAll(item => item.Subtitle?.SpeechSourceTrackId == track.Id);
                ClearTimelineSelection();
                MarkProjectDirty(applySnapping: false); RefreshTimeline(); CloseSettingsTabs(); SeekPreview(_project.Sequence.PlayheadSeconds);
            });
        }
        if (data.Sentences.Count > 0)
        {
            var sentences = new ListBox { DisplayMemberPath = "Text", ItemsSource = data.Sentences, Margin = new Thickness(0, 8, 0, 8) };
            // Bound the list proportionally instead of allocating every sentence editor.
            sentences.SetBinding(FrameworkElement.MaxHeightProperty, new System.Windows.Data.Binding("ActualHeight")
            { Source = UpperWorkspaceGrid, Converter = new ProportionalSizeConverter(), ConverterParameter = .3 });
            root.Children.Add(sentences);
            var host = new ContentControl(); root.Children.Add(host);
            sentences.SelectionChanged += (_, _) =>
            {
                StopCatalogPreview(restoreTimeline: false);
                if (sentences.SelectedItem is SpeechSentence sentence)
                    host.Content = BuildSpeechSentenceEditor(root, data, sentence, track, () => { sentences.Items.Refresh(); }, ReadDefaults);
            };
            sentences.SelectedItem = data.Sentences.FirstOrDefault(s => s.Id == selectedSentence || s.Id == _selectedClip?.Id) ?? data.Sentences[0];
            var commands = new WrapPanel();
            AddSpeechAction(commands, track is null ? "生成到专用轨道" : "应用参数 / 生成变化的句子", () =>
            {
                if (track is not null && !CanEditSpeech(root, track)) return;
                if (track is null && _speechParsedInputText != _speechInputText)
                    throw new SpeechFailure("VOICE0001", "主文本已改变，请先点击“自动分句 / 重新解析文字”，再生成语音。");
                ReadDefaults(); SpeechText.Validate(data);
                SpeechTrackData requested = SpeechJson.Copy(data);
                _ = RunSpeechUiAsync("语音生成", async (p, t) => { await GenerateSpeechAsync(requested, track, p, t); });
            });
            root.Children.Add(commands);
        }
        OpenSettingsCategory(track is null ? "语音生成" : track.Name + "语音轨道", root);
        if (track is not null) _mcpInspectorRefresh = () =>
        {
            TimelineTrack? current = _project.Tracks.FirstOrDefault(t => t.Id == track.Id);
            if (current?.Speech is not null) ShowSpeechTool(current); else CloseSettingsTabs();
        };
    }
    private Action AddSpeechCaptionControls(Panel root, SpeechTrackData data)
    {
        var enabled = new CheckBox { Content = "为生成语音添加字幕", IsChecked = data.Captions is not null,
            Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 8) };
        root.Children.Add(enabled);
        var details = new Expander { Header = "逐字对齐设置", Foreground = Brushes.White,
            IsExpanded = data.Captions is not null, IsEnabled = enabled.IsChecked == true };
        enabled.Checked += (_, _) => { details.IsEnabled = true; details.IsExpanded = true; };
        enabled.Unchecked += (_, _) => details.IsEnabled = false;
        var panel = new StackPanel { Margin = new Thickness(8, 4, 0, 8) };
        SubtitleHint(panel, "整句字幕直接使用已分句的生成文字与音频时长，不运行识别模型。");
        var words = new CheckBox { Content = "逐字输出", IsChecked = data.Captions?.WordByWord ?? false,
            Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(words);
        var advanced = new StackPanel { IsEnabled = words.IsChecked == true };
        words.Checked += (_, _) => advanced.IsEnabled = true;
        words.Unchecked += (_, _) => advanced.IsEnabled = false;
        var languages = WhisperXModelCatalog.Languages.Select(item => item.Code == "auto"
            ? new WhisperXLanguage("auto", "跟随句子语言（按原文推断）") : item).ToArray();
        var language = new ComboBox { ItemsSource = languages, Margin = new Thickness(0, 4, 0, 8),
            SelectedItem = languages.FirstOrDefault(item => item.Code == data.Captions?.Language) ?? languages[0] };
        AddSubtitleField(advanced, "逐字对齐语言", language);
        var alignment = new ComboBox { Margin = new Thickness(0, 4, 0, 8) };
        void RefreshModels()
        {
            var all = WhisperXModelCatalog.GetModels();
            string? currentAlignment = (alignment.SelectedItem as SubtitleModelChoice)?.Model.Id;
            string code = WhisperXModelCatalog.RecognitionLanguage((language.SelectedItem as WhisperXLanguage)?.Code);
            alignment.IsEnabled = code != "auto";
            var aligners = all.Where(item => item.Kind == WhisperXModelKind.Alignment &&
                (code == "auto" || item.Language is null || item.Language == code)).Select(item => new SubtitleModelChoice(item)).ToArray();
            alignment.ItemsSource = aligners;
            alignment.SelectedItem = aligners.FirstOrDefault(item => MatchesSubtitleModel(item.Model, currentAlignment))
                ?? aligners.FirstOrDefault(item => MatchesSubtitleModel(item.Model, data.Captions?.AlignmentModel))
                ?? aligners.FirstOrDefault(item => WhisperXModelCatalog.IsInstalled(item.Model)) ?? aligners.FirstOrDefault();
        }
        void Import()
        {
            string? code = WhisperXModelCatalog.RecognitionLanguage((language.SelectedItem as WhisperXLanguage)?.Code);
            if (code == "auto")
            {
                var picked = PromptChoice("对齐模型语言", "选择此对齐模型适用的语言",
                    WhisperXModelCatalog.Languages.Where(item => item.Code != "auto")
                        .Select(item => new ProjectPickerItem(item.Code, item.Name)).ToArray(), "zh");
                if (picked is null) return;
                code = WhisperXModelCatalog.RecognitionLanguage(picked.Id);
            }
            var picker = new OpenFolderDialog { Title = "导入 Wav2Vec2 CTC 对齐模型" };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                var model = WhisperXModelManager.ImportModel(picker.FolderName, WhisperXModelKind.Alignment, language: code);
                if (code is not null)
                    language.SelectedItem = WhisperXModelCatalog.Languages.First(item => WhisperXModelCatalog.RecognitionLanguage(item.Code) == code);
                RefreshModels();
                alignment.SelectedItem = alignment.Items.Cast<SubtitleModelChoice>().FirstOrDefault(item => item.Model.Id == model.Id);
            }
            catch (Exception exception) { ReportAiSubtitleError(exception); }
        }
        AddSubtitleField(advanced, "对齐模型", alignment);
        AddToolButton(advanced, "导入第三方对齐模型…", Import);
        language.SelectionChanged += (_, _) => RefreshModels(); RefreshModels();
        SubtitleHint(advanced, "逐字输出只对已知原文做音频强制对齐，按实际发音累积显示；未对齐的字不编造时间。多语言句子可手动指定对齐语言。");
        var python = SubtitleTextBox(data.Captions?.PythonPath ?? Environment.GetEnvironmentVariable("TANGERINE_WHISPERX_PYTHON") ?? "");
        AddSubtitleField(advanced, "Python 路径（留空使用专用 WhisperX 环境）", python);
        var device = new ComboBox { ItemsSource = new[] { "CPU（int8）", "CUDA GPU（float16）" },
            SelectedIndex = data.Captions?.Device == "cuda" ? 1 : 0, Margin = new Thickness(0, 4, 0, 8) };
        AddSubtitleField(advanced, "对齐设备", device);
        panel.Children.Add(advanced);
        details.Content = panel; root.Children.Add(details);
        return () =>
        {
            if (enabled.IsChecked != true) { data.Captions = null; return; }
            if (words.IsChecked == true && (language.SelectedItem as WhisperXLanguage)?.Code != "auto" &&
                alignment.SelectedItem is not SubtitleModelChoice)
                throw new SpeechFailure("VOICE0001", "请选择逐字对齐模型。");
            data.Captions = new SpeechCaptionSettings
            {
                Language = (language.SelectedItem as WhisperXLanguage)?.Code ?? "auto",
                AlignmentModel = words.IsChecked != true || (language.SelectedItem as WhisperXLanguage)?.Code == "auto" ? null :
                    WhisperXModelCatalog.GetModelPath(((SubtitleModelChoice)alignment.SelectedItem).Model),
                WordByWord = words.IsChecked == true, PythonPath = string.IsNullOrWhiteSpace(python.Text) ? null : python.Text.Trim(),
                Device = device.SelectedIndex == 1 ? "cuda" : "cpu"
            };
        };
    }
    private async Task LoadSpeechDevicesAsync(ComboBox box, string selected)
    {
        try
        {
            SpeechEvent message = await _speechWorker.SendAsync(new SpeechRequest { Command = "devices" }, null, _experienceLifetime.Token);
            if (_experienceLifetime.IsCancellationRequested) return;
            string current = (box.SelectedItem as SpeechDevice)?.Id ?? selected;
            var devices = message.Devices ?? throw new SpeechFailure("VOICE0002", "设备查询未返回设备列表。");
            if (!devices.Any(d => d.Id == current))
                devices.Add(new SpeechDevice(current, current + "（当前不可用，请检查设备或重新选择）", false));
            box.ItemsSource = devices; box.SelectedValuePath = "Id"; box.SelectedValue = current;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_experienceLifetime.IsCancellationRequested) box.ToolTip = "设备查询失败 · " + StageCodes.Resolve(e, "VOICE0002"); }
    }
    private UIElement BuildSpeechSentenceEditor(FrameworkElement page, SpeechTrackData data, SpeechSentence sentence, TimelineTrack? track, Action refresh, Action readDefaults)
    {
        var panel = new StackPanel();
        var content = SpeechBox(sentence.Text); content.AcceptsReturn = true; content.TextWrapping = TextWrapping.Wrap; content.MinLines = 2;
        var voice = SpeechChoice(SpeechAssets.Voices(), v => v.Id == (sentence.VoiceId ?? data.Defaults.VoiceId));
        var language = SpeechChoice(SpeechCatalog.Languages, l => l.Code == (sentence.Language ?? data.Defaults.Language));
        var rate = SpeechBox((sentence.Rate ?? data.Defaults.Rate).ToString(CultureInfo.CurrentCulture));
        var pitch = SpeechBox((sentence.Pitch ?? data.Defaults.Pitch).ToString(CultureInfo.CurrentCulture));
        var gap = SpeechBox((sentence.Gap ?? (data.Sentences[0].Id == sentence.Id ? 0 : data.Defaults.Gap)).ToString(CultureInfo.CurrentCulture));
        content.TextChanged += (_, _) => StopCatalogPreview();
        voice.SelectionChanged += (_, _) => StopCatalogPreview();
        language.SelectionChanged += (_, _) => StopCatalogPreview();
        rate.TextChanged += (_, _) => StopCatalogPreview();
        pitch.TextChanged += (_, _) => StopCatalogPreview();
        var anchors = new ComboBox { ItemsSource = new[] { "自动顺排", "固定起点", "固定停止点", "同时固定两端（自动语速）" }, SelectedIndex = (int)sentence.Anchor, Margin = new Thickness(0, 4, 0, 8) };
        var start = SpeechBox(sentence.Start.ToString("0.###", CultureInfo.CurrentCulture));
        var end = SpeechBox(sentence.End.ToString("0.###", CultureInfo.CurrentCulture));
        void AnchorChanged() { rate.IsEnabled = anchors.SelectedIndex != 3; start.IsEnabled = anchors.SelectedIndex is 1 or 3; end.IsEnabled = anchors.SelectedIndex is 2 or 3; }
        anchors.SelectionChanged += (_, _) => AnchorChanged(); AnchorChanged();
        SpeechField(panel, "句子文字（修改后需保存）", content); SpeechField(panel, "本句音色", voice);
        SpeechField(panel, "本句语言 / 为选中文字标注的语言", language);
        var spans = new TextBlock { Text = "可在上方选中文字，再点击“标注选中文字语言”。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#AEB5C2") };
        var marked = SpeechJson.Copy(sentence.Parts);
        AddSpeechAction(panel, "标注选中文字语言", () =>
        {
            if (content.SelectionLength == 0 || language.SelectedItem is not SpeechLanguage l) return;
            StopCatalogPreview();
            marked = SpeechText.MarkLanguage(content.Text, marked, content.SelectionStart, content.SelectionLength, l.Code);
            spans.Text = "已标注：" + content.SelectedText + " → " + l.Name + "（保存句子后生效）";
        });
        panel.Children.Add(spans);
        SpeechField(panel, "本句语速（倍）", rate); SpeechField(panel, "本句音调（半音）", pitch); SpeechField(panel, "本句前停顿（秒）", gap);
        SpeechField(panel, "位置约束", anchors); SpeechField(panel, "起始点（秒）", start); SpeechField(panel, "停止点（秒）", end);
        SubtitleHint(panel, $"实际时长 {sentence.Duration:0.###} 秒 · 生效语速 {sentence.EffectiveRate:0.###} 倍");
        bool CanEdit() => page.IsLoaded && page.IsVisible && (track is null || CanEditSpeech(page, track));
        SpeechTrackData ReadProposal()
        {
            readDefaults();
            SpeechSentence proposed = SpeechJson.Copy(sentence);
            proposed.Text = content.Text.Trim(); proposed.VoiceId = (voice.SelectedItem as SpeechVoice)?.Id;
            proposed.Language = marked.Any(p => p.Language != "auto") ? "auto" : (language.SelectedItem as SpeechLanguage)?.Code;
            double requestedRate = SpeechNumber(rate, "语速"), requestedPitch = SpeechNumber(pitch, "音调"), requestedGap = SpeechNumber(gap, "停顿");
            proposed.Rate = sentence.Rate is null && requestedRate == data.Defaults.Rate ? null : requestedRate;
            proposed.Pitch = sentence.Pitch is null && requestedPitch == data.Defaults.Pitch ? null : requestedPitch;
            proposed.Gap = sentence.Gap is null && requestedGap == (data.Sentences[0].Id == sentence.Id ? 0 : data.Defaults.Gap) ? null : requestedGap;
            proposed.Anchor = (SpeechAnchor)anchors.SelectedIndex; proposed.Start = SpeechNumber(start, "起始点"); proposed.End = SpeechNumber(end, "停止点");
            proposed.Parts = marked.Count > 0 && string.Concat(marked.Select(p => p.Text)).Trim() == proposed.Text
                ? SpeechJson.Copy(marked) : [new SpeechPart { Text = proposed.Text }];
            SpeechTrackData validation = SpeechJson.Copy(data); validation.Sentences[validation.Sentences.FindIndex(s => s.Id == sentence.Id)] = proposed;
            SpeechText.Validate(validation);
            return validation;
        }
        void Save()
        {
            if (!CanEdit()) return;
            SpeechTrackData validation = ReadProposal();
            if (track is null) { data.Sentences = validation.Sentences; ShowSpeechTool(null, data, sentence.Id); }
            else _ = RunSpeechUiAsync("应用句子修改", async (p, t) => { await GenerateSpeechAsync(validation, track, p, t); });
        }
        var actions = new WrapPanel(); AddSpeechAction(actions, track is null ? "保存句子参数" : "保存并生成本句", Save);
        AddSpeechAction(actions, "跟随默认音色 / 语速 / 音调", () =>
        { if (!CanEdit()) return; readDefaults(); sentence.VoiceId = null; sentence.Language = null; sentence.Rate = null; sentence.Pitch = null; sentence.Gap = null; ShowSpeechTool(track, data, sentence.Id); });
        AddSpeechAction(actions, "试听", () =>
        {
            if (track is null || sentence.AudioAssetId is null)
            {
                SpeechTrackData requested = ReadProposal(); requested.Sentences = [requested.Sentences.First(s => s.Id == sentence.Id)];
                _ = RunSpeechUiAsync("合成试听", (p, t) => GenerateSpeechAuditionAsync(requested, p, t)); return;
            }
            MediaAsset? asset = _project.Media.FirstOrDefault(a => a.Id == sentence.AudioAssetId);
            if (asset is null) throw new SpeechFailure("VOICE0007", "句子音频不存在，请重新生成。");
            PreviewSpeechAudio(asset, sentence.Text);
        });
        AddSpeechAction(actions, "合成试听当前修改", () =>
        {
            SpeechTrackData requested = ReadProposal(); requested.Sentences = [requested.Sentences.First(s => s.Id == sentence.Id)];
            _ = RunSpeechUiAsync("合成试听", (p, t) => GenerateSpeechAuditionAsync(requested, p, t));
        });
        AddSpeechAction(actions, "停止试听", () => StopCatalogPreview());
        AddSpeechAction(actions, "重新生成本句", () =>
        {
            if (track is null || !CanEdit()) return;
            readDefaults();
            _ = RunSpeechUiAsync("重新生成语音", async (p, t) => { await GenerateSpeechAsync(data, track, p, t, sentence.Id); });
        });
        AddSpeechAction(actions, "上移一句", () => Reorder(-1)); AddSpeechAction(actions, "下移一句", () => Reorder(1));
        void Reorder(int delta)
        {
            if (!CanEdit()) return;
            int index = data.Sentences.FindIndex(s => s.Id == sentence.Id), next = index + delta;
            if (next < 0 || next >= data.Sentences.Count) return;
            (data.Sentences[index], data.Sentences[next]) = (data.Sentences[next], data.Sentences[index]);
            ShowSpeechTool(track, data, sentence.Id);
        }
        AddSpeechAction(actions, "删除本句", () =>
        {
            if (!CanEdit()) return;
            if (data.Sentences.Count <= 1) throw new SpeechFailure("VOICE0004", "最后一句请通过“删除语音轨道”移除。");
            data.Sentences.RemoveAll(s => s.Id == sentence.Id); ShowSpeechTool(track, data);
        });
        if (track is null)
        {
            AddSpeechAction(actions, "在光标处分句", () =>
            {
                if (!CanEdit()) return; int position = content.CaretIndex;
                if (position <= 0 || position >= content.Text.Length) return;
                int index = data.Sentences.FindIndex(s => s.Id == sentence.Id);
                string leftText = content.Text[..position].Trim(), rightText = content.Text[position..].Trim();
                if (leftText.Length == 0 || rightText.Length == 0) throw new SpeechFailure("VOICE0001", "分句位置两边都必须有正文。");
                var right = SpeechJson.Copy(sentence); right.Id = Guid.NewGuid().ToString("N"); right.Gap = null;
                sentence.Text = leftText;
                sentence.Parts = SpeechText.SliceParts(content.Text, marked, content.Text[..position].Length - content.Text[..position].TrimStart().Length, leftText.Length);
                right.Text = rightText;
                right.Parts = SpeechText.SliceParts(content.Text, marked, position + content.Text[position..].Length - content.Text[position..].TrimStart().Length, rightText.Length);
                data.Sentences.Insert(index + 1, right); ShowSpeechTool(null, data, right.Id);
            });
            AddSpeechAction(actions, "与下句合并", () =>
            {
                int index = data.Sentences.FindIndex(s => s.Id == sentence.Id);
                if (!CanEdit() || index + 1 >= data.Sentences.Count) return;
                var next = data.Sentences[index + 1]; sentence.Text += next.Text;
                sentence.Parts = SpeechGeneration.MergeParts(sentence.Parts.Concat(next.Parts)); data.Sentences.RemoveAt(index + 1);
                ShowSpeechTool(null, data, sentence.Id);
            });
        }
        panel.Children.Add(actions); return panel;
    }

    private void PreviewSpeechAudio(MediaAsset asset, string name)
    {
        string path = Path.GetFullPath(asset.SourcePath);
        if (!File.Exists(path)) throw new SpeechFailure("VOICE0007", "试听音频文件不存在，请重新生成或导入音色。");
        double duration = SpeechAudio.Duration(path);
        if (!double.IsFinite(duration) || duration <= 0) throw new SpeechFailure("VOICE0007", "试听音频无有效采样。");
        // Reuse the request-owned music player and its teardown rather than
        // introducing a third competing preview owner. Keep this inspector open.
        PreviewBuiltinMusic(new BuiltinMusicTrack("speech-" + asset.Id, name, "语音生成", "人声", "试听",
            duration, path,
            Path.Combine(AppContext.BaseDirectory, "Resources", "Speech", "voice-tool.png"), "", "本地生成", ""), selectLibrary: false);
    }
    private void CancelSpeechAudition()
    {
        _speechAuditionVersion++; _speechAuditionCancellation?.Cancel(); _speechAuditionCancellation = null;
        _speechAuditionFiles?.Dispose(); _speechAuditionFiles = null;
    }
    private async Task PreviewSpeechReferenceAsync(string source, double start, double? end, string name,
        Func<bool> isCurrent, IProgress<SpeechProgress> progress, CancellationToken token)
    {
        StopCatalogPreview();
        long request = ++_speechAuditionVersion, generation = _projectGeneration;
        ProjectDocument owner = _project;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _experienceLifetime.Token);
        _speechAuditionCancellation = cancellation;
        string? directory = AppStorage.TempPath("speech-reference", Guid.NewGuid().ToString("N"));
        MediaPlayer? player = null;
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reference.wav");
            progress.Report(new("正在解码所选参考片段…", -1));
            double duration = await Task.Run(() => SpeechAssets.PrepareVoiceReferenceAsync(source, path, start, end, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != _speechAuditionVersion || !ReferenceEquals(owner, _project) || generation != _projectGeneration || !isCurrent())
                throw new OperationCanceledException("参考音频选择已改变。");
            var asset = new MediaAsset { Kind = MediaKind.Audio, Name = name, SourcePath = path, DurationSeconds = duration };
            _speechAuditionCancellation = null;
            PreviewSpeechAudio(asset, name);
            if (!BuiltinMusicOwnsMonitor || _builtinMusicTrack?.Id != "speech-" + asset.Id)
                throw new SpeechFailure("VOICE0007", "无法打开参考音频，请检查音频设备。");
            player = _builtinMusicPlayer!;
            long playbackRequest = _speechAuditionVersion;
            _speechAuditionCancellation = cancellation;
            await WaitForSpeechPlaybackAsync(player, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (playbackRequest != _speechAuditionVersion || !ReferenceEquals(player, _builtinMusicPlayer) || !isCurrent())
                throw new OperationCanceledException("参考音频选择已改变。");
            _speechAuditionFiles = new GeneratedSpeech(directory, new SpeechTrackData(), [asset]); directory = null;
        }
        finally
        {
            if (ReferenceEquals(_speechAuditionCancellation, cancellation)) _speechAuditionCancellation = null;
            if (directory is not null && player is not null && ReferenceEquals(player, _builtinMusicPlayer)) StopBuiltinMusicPreview();
            if (directory is not null) SpeechAssets.DeleteDirectory(directory);
        }
    }
    private async Task GenerateSpeechAuditionAsync(SpeechTrackData input, IProgress<SpeechProgress> progress, CancellationToken token)
    {
        StopCatalogPreview();
        long request = ++_speechAuditionVersion, generation = _projectGeneration;
        ProjectDocument owner = _project;
        SpeechTrackData data = SpeechJson.Copy(input); data.Origin = 0;
        foreach (SpeechSentence s in data.Sentences)
        { s.Anchor = SpeechAnchor.Auto; s.Start = s.End = 0; s.Gap = 0; s.AudioAssetId = s.RawAssetId = s.Fingerprint = s.AudioFingerprint = null; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _experienceLifetime.Token);
        _speechAuditionCancellation = cancellation;
        GeneratedSpeech? generated = null;
        MediaPlayer? player = null;
        try
        {
            generated = await Task.Run(() => SpeechGeneration.GenerateAsync(_speechWorker, data, new Dictionary<string, MediaAsset>(),
                Path.Combine(AppStorage.TempRoot, "speech-audition"), progress, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != _speechAuditionVersion || !ReferenceEquals(owner, _project) || generation != _projectGeneration)
                throw new OperationCanceledException("试听选择已改变。");
            SpeechSentence sentence = generated.Data.Sentences[0];
            MediaAsset audio = generated.Assets.First(a => a.Id == sentence.AudioAssetId);
            // Opening the player revokes the former preview. Detach this
            // completed request first so that teardown cannot cancel itself.
            _speechAuditionCancellation = null;
            PreviewSpeechAudio(audio, sentence.Text);
            if (!BuiltinMusicOwnsMonitor || _builtinMusicTrack?.Id != "speech-" + audio.Id)
                throw new SpeechFailure("VOICE0007", "无法打开合成的试听音频，请检查音频设备。" );
            player = _builtinMusicPlayer!;
            long playbackRequest = _speechAuditionVersion;
            _speechAuditionCancellation = cancellation;
            await WaitForSpeechPlaybackAsync(player, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (playbackRequest != _speechAuditionVersion || !ReferenceEquals(player, _builtinMusicPlayer))
                throw new OperationCanceledException("试听已切换。");
            _speechAuditionFiles = generated; generated = null;
        }
        finally
        {
            if (ReferenceEquals(_speechAuditionCancellation, cancellation)) _speechAuditionCancellation = null;
            if (generated is not null && player is not null && ReferenceEquals(player, _builtinMusicPlayer)) StopBuiltinMusicPreview();
            generated?.Dispose();
        }
    }
    private async Task WaitForSpeechPlaybackAsync(MediaPlayer player, CancellationToken token)
    {
        if (_builtinMusicOpened && ReferenceEquals(player, _builtinMusicPlayer)) return;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler ready = (_, _) => opened.TrySetResult();
        EventHandler<ExceptionEventArgs> failed = (_, e) => opened.TrySetException(new SpeechFailure("VOICE0007", "试听音频打开失败：" + e.ErrorException.Message, e.ErrorException));
        player.MediaOpened += ready; player.MediaFailed += failed;
        try { await opened.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch (TimeoutException e) { throw new SpeechFailure("VOICE0007", "试听音频打开超时，请检查音频设备。", e); }
        finally { player.MediaOpened -= ready; player.MediaFailed -= failed; }
    }
}
