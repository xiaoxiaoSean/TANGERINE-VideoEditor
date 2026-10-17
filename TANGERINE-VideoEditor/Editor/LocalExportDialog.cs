using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum ExportDialogStageCode { Validate, PresetRead, PresetWrite }

public sealed class ExportDialogException(ExportDialogStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ExportDialogStageCode StageCode { get; } = stageCode;
}

/// <summary>One local render with its source sequence and output settings.</summary>
public sealed record LocalExportJob(string SequenceId, string SequenceName, string OutputPath,
    VideoExportSettings Settings);

/// <summary>
/// Collects a single export request. The user chooses parameters and an
/// output file in one flow, then encoding begins immediately.
/// </summary>
public sealed class LocalExportDialog : Window
{
    public sealed record ExportPreset(string Name, int? Width, int? Height,
        double? FramesPerSecond, int? VideoBitrateKbps);

    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(30, 33, 40));
    private static readonly Brush FieldBrush = new SolidColorBrush(Color.FromRgb(44, 48, 58));
    private static readonly Brush FieldBorderBrush = new SolidColorBrush(Color.FromRgb(80, 86, 98));
    private readonly ProjectDocument _project;
    private readonly Func<Task>? _exportExchange;
    private readonly ComboBox _sequences = new();
    private readonly ComboBox _presets = new();
    private readonly ComboBox _ranges = new();
    private readonly TextBox _width = new();
    private readonly TextBox _height = new();
    private readonly TextBox _fps = new();
    private readonly TextBox _bitrate = new();
    private readonly TextBox _rangeStart = new();
    private readonly TextBox _rangeEnd = new();
    private readonly List<ExportPreset> _savedPresets = [];
    private readonly SemaphoreSlim _presetIoGate = new(1, 1);

    public LocalExportJob? SelectedJob { get; private set; }

    public LocalExportDialog(ProjectDocument project, Window owner,
        Func<Task>? exportExchange = null)
    {
        _project = project;
        _exportExchange = exportExchange;
        Owner = owner;
        Title = "导出视频";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = (owner.ActualWidth > 0 ? owner.ActualWidth : SystemParameters.PrimaryScreenWidth) * .48;
        Height = (owner.ActualHeight > 0 ? owner.ActualHeight : SystemParameters.PrimaryScreenHeight) * .72;
        MinWidth = Width * .75;
        MinHeight = Height * .75;
        Background = PanelBrush;
        Foreground = Brushes.White;
        BuildContent();
        PopulateSequences();
        PopulatePresets();
        Loaded += async (_, _) => await LoadPresetsAsync();
        _ranges.Items.Add("整个序列");
        _ranges.Items.Add("指定范围（秒）");
        _ranges.SelectedIndex = 0;
        UpdateRangeFields();
    }

    private void BuildContent()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var form = new StackPanel();
        form.Children.Add(Label("序列")); form.Children.Add(StyleCombo(_sequences));
        form.Children.Add(Label("导出预设")); form.Children.Add(StyleCombo(_presets));
        form.Children.Add(Label("导出范围")); form.Children.Add(StyleCombo(_ranges));
        form.Children.Add(Label("范围起点（秒）")); form.Children.Add(StyleText(_rangeStart));
        form.Children.Add(Label("范围终点（秒）")); form.Children.Add(StyleText(_rangeEnd));
        form.Children.Add(Label("画面宽度（像素）")); form.Children.Add(StyleText(_width));
        form.Children.Add(Label("画面高度（像素）")); form.Children.Add(StyleText(_height));
        form.Children.Add(Label("帧率（fps）")); form.Children.Add(StyleText(_fps));
        form.Children.Add(Label("视频码率（kbps，留空使用质量模式）")); form.Children.Add(StyleText(_bitrate));
        var savePreset = StyleButton("保存当前参数为预设");
        savePreset.Margin = new Thickness(0, 14, 0, 0);
        savePreset.Click += async (_, _) => await SaveCurrentPresetAsync();
        form.Children.Add(savePreset);
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(formScroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        if (_exportExchange is not null)
        {
            var exchange = StyleButton("导出项目交换文件…");
            exchange.Click += async (_, _) => await _exportExchange();
            actions.Children.Add(exchange);
        }
        var cancel = StyleButton("取消");
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        var start = StyleButton("选择文件并导出…");
        start.Click += (_, _) =>
        {
            LocalExportJob? job = CreateJob();
            if (job is null) return;
            SelectedJob = job;
            DialogResult = true;
        };
        actions.Children.Add(start);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Content = root;

        _sequences.SelectionChanged += (_, _) => { ApplyPreset(); UpdateRangeFields(); };
        _presets.SelectionChanged += (_, _) => ApplyPreset();
        _ranges.SelectionChanged += (_, _) => UpdateRangeFields();
        ApplyPreset();
        UpdateRangeFields();
    }

    private void PopulateSequences()
    {
        _sequences.Items.Clear();
        _sequences.Items.Add(new ComboBoxItem { Content = _project.Sequence.Name, Tag = _project.Sequence });
        foreach (ProjectSequenceWorkspace workspace in _project.Sequences)
            _sequences.Items.Add(new ComboBoxItem { Content = workspace.Sequence.Name, Tag = workspace.Sequence });
        _sequences.SelectedIndex = 0;
    }

    private void PopulatePresets()
    {
        _presets.Items.Clear();
        foreach (ExportPreset preset in new[]
        {
            new ExportPreset("序列原始尺寸 / 高质量", null, null, null, null),
            new ExportPreset("横屏 1080p / 12 Mbps", 1920, 1080, null, 12000),
            new ExportPreset("竖屏 1080p / 12 Mbps", 1080, 1920, null, 12000),
            new ExportPreset("方屏 1080p / 10 Mbps", 1080, 1080, null, 10000),
            new ExportPreset("横屏 720p / 6 Mbps", 1280, 720, null, 6000)
        }.Concat(_savedPresets))
            _presets.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset });
        _presets.SelectedIndex = 0;
    }

    private void ApplyPreset()
    {
        if (_presets.SelectedItem is not ComboBoxItem { Tag: ExportPreset preset } ||
            _sequences.SelectedItem is not ComboBoxItem { Tag: ProjectSequence sequence }) return;
        _width.Text = (preset.Width ?? sequence.Width).ToString(CultureInfo.CurrentCulture);
        _height.Text = (preset.Height ?? sequence.Height).ToString(CultureInfo.CurrentCulture);
        _fps.Text = (preset.FramesPerSecond ?? sequence.FramesPerSecond).ToString("0.###", CultureInfo.CurrentCulture);
        _bitrate.Text = preset.VideoBitrateKbps?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private void UpdateRangeFields()
    {
        if (_sequences.SelectedItem is not ComboBoxItem { Tag: ProjectSequence sequence }) return;
        ProjectSequenceWorkspace? workspace = _project.FindSequence(sequence.Id);
        double duration = workspace?.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max() ?? 0;
        // Never round the sequence end upward in the visible fields. A clip
        // ending at a fractional frame (for example 5.016666...) used to be
        // displayed as 5.017 and then rejected as beyond the sequence.
        _rangeStart.Text = (_ranges.SelectedIndex == 1 ? sequence.WorkAreaStartSeconds : 0)
            .ToString("R", CultureInfo.CurrentCulture);
        _rangeEnd.Text = (_ranges.SelectedIndex == 1 ? sequence.WorkAreaEndSeconds ?? duration : duration)
            .ToString("R", CultureInfo.CurrentCulture);
        _rangeStart.IsEnabled = _ranges.SelectedIndex == 1;
        _rangeEnd.IsEnabled = _ranges.SelectedIndex == 1;
    }

    private LocalExportJob? CreateJob()
    {
        try
        {
            if (_sequences.SelectedItem is not ComboBoxItem { Tag: ProjectSequence sequence })
                throw new ExportDialogException(ExportDialogStageCode.Validate, "请先选择序列。");
            int width = ParseInteger(_width.Text, "宽度");
            int height = ParseInteger(_height.Text, "高度");
            if (width < 16 || width > 8192 || width % 2 != 0 ||
                height < 16 || height > 8192 || height % 2 != 0)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "宽高必须是有效的偶数像素，且不超过 8192 × 8192。");
            if (!double.TryParse(_fps.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double fps) ||
                !double.IsFinite(fps) || fps < 1 || fps > 120)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "帧率必须在 1–120 fps 之间。");
            int? bitrate = string.IsNullOrWhiteSpace(_bitrate.Text) ? null : ParseInteger(_bitrate.Text, "码率");
            if (bitrate is < 250 or > 200000)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "视频码率必须在 250–200000 kbps 之间。");
            ProjectSequenceWorkspace? workspace = _project.FindSequence(sequence.Id);
            double duration = workspace?.Tracks.SelectMany(track => track.Clips)
                .Select(clip => clip.StartSeconds + clip.DurationSeconds).DefaultIfEmpty(0).Max() ?? 0;
            if (duration <= 0)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "此序列没有可导出的片段。");
            double start = 0;
            double end = duration;
            if (_ranges.SelectedIndex == 1 &&
                (!double.TryParse(_rangeStart.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out start) ||
                 !double.TryParse(_rangeEnd.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out end)))
                throw new ExportDialogException(ExportDialogStageCode.Validate, "导出范围必须是秒数。");
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > duration || end <= start)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "导出范围超出序列范围或长度为零。");
            string suggested = SanitizeFileName($"{_project.Name}_{sequence.Name}_{width}x{height}.mp4");
            var picker = new SaveFileDialog { Title = "选择视频输出文件", Filter = "MP4 视频 (*.mp4)|*.mp4",
                DefaultExt = ".mp4", AddExtension = true, FileName = suggested };
            if (picker.ShowDialog(this) != true) return null;
            string outputFullPath = Path.GetFullPath(picker.FileName);
            if (!Path.GetExtension(outputFullPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                throw new ExportDialogException(ExportDialogStageCode.Validate, "视频导出目标必须是 MP4 文件。");
            if (_project.Media.Any(media =>
                string.Equals(SafeFullPath(media.SourcePath), outputFullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SafeFullPath(media.ProxyPath), outputFullPath, StringComparison.OrdinalIgnoreCase)))
                throw new ExportDialogException(ExportDialogStageCode.Validate,
                    "导出目标不能是项目中的源文件或代理文件。");
            var settings = new VideoExportSettings(width, height, fps, bitrate, start, end);
            return new LocalExportJob(sequence.Id, sequence.Name, picker.FileName, settings);
        }
        catch (ExportDialogException exception) { ShowError(exception); return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError(new ExportDialogException(ExportDialogStageCode.Validate, "无法使用该输出文件路径。", exception));
            return null;
        }
    }

    private async Task SaveCurrentPresetAsync()
    {
        try
        {
            int width = ParseInteger(_width.Text, "宽度");
            int height = ParseInteger(_height.Text, "高度");
            if (width < 16 || width > 8192 || width % 2 != 0 ||
                height < 16 || height > 8192 || height % 2 != 0)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "预设宽高无效。");
            if (!double.TryParse(_fps.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double fps) ||
                !double.IsFinite(fps) || fps < 1 || fps > 120)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "预设帧率无效。");
            int? bitrate = string.IsNullOrWhiteSpace(_bitrate.Text) ? null : ParseInteger(_bitrate.Text, "码率");
            if (bitrate is < 250 or > 200000)
                throw new ExportDialogException(ExportDialogStageCode.Validate, "预设码率无效。");
            string name = $"自定义 {width}×{height} · {fps:0.###} fps";
            await _presetIoGate.WaitAsync();
            try
            {
                // Keep the UI collection unchanged until the worker has
                // written the candidate preset list successfully.
                var next = _savedPresets.Where(preset => preset.Name != name).ToList();
                next.Add(new ExportPreset(name, width, height, fps, bitrate));
                await Task.Run(() =>
                {
                    string path = PresetPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, JsonSerializer.Serialize(next));
                });
                _savedPresets.Clear();
                _savedPresets.AddRange(next);
                PopulatePresets();
                _presets.SelectedIndex = _presets.Items.Count - 1;
            }
            finally { _presetIoGate.Release(); }
        }
        catch (ExportDialogException exception) { ShowError(exception); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { ShowError(new ExportDialogException(ExportDialogStageCode.PresetWrite, "保存导出预设失败。", exception)); }
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            await _presetIoGate.WaitAsync();
            try
            {
                List<ExportPreset> presets = await Task.Run(() =>
                {
                    string path = PresetPath();
                    if (!File.Exists(path)) return [];
                    return JsonSerializer.Deserialize<List<ExportPreset>>(File.ReadAllText(path)) ?? [];
                });
                _savedPresets.Clear();
                _savedPresets.AddRange(presets.Where(preset => !string.IsNullOrWhiteSpace(preset.Name)));
                string? selection = (_presets.SelectedItem as ComboBoxItem)?.Content as string;
                PopulatePresets();
                foreach (ComboBoxItem item in _presets.Items)
                    if (string.Equals(item.Content as string, selection, StringComparison.Ordinal))
                    { _presets.SelectedItem = item; break; }
            }
            finally { _presetIoGate.Release(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { ShowError(new ExportDialogException(ExportDialogStageCode.PresetRead, "读取导出预设失败。", exception)); }
    }

    private static string PresetPath() => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "TangerineVideoEditor", "export-presets.json");

    private static int ParseInteger(string text, string name) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? value :
        throw new ExportDialogException(ExportDialogStageCode.Validate, $"{name}必须是整数。");

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private void ShowError(ExportDialogException exception) =>
        MessageBox.Show(this, $"{exception.Message}\n阶段代码：EXPORT-{exception.StageCode}", "导出设置错误",
            MessageBoxButton.OK, MessageBoxImage.Error);

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Brushes.White, Margin = new Thickness(0, 9, 0, 4),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBox StyleText(TextBox box)
    {
        box.Background = FieldBrush; box.Foreground = Brushes.White; box.CaretBrush = Brushes.White;
        box.BorderBrush = FieldBorderBrush; box.Padding = new Thickness(8, 5, 8, 5);
        return box;
    }

    private static ComboBox StyleCombo(ComboBox box)
    {
        box.Background = FieldBrush; box.Foreground = Brushes.White; box.BorderBrush = FieldBorderBrush;
        box.Padding = new Thickness(8, 5, 8, 5);
        // WPF's default popup can keep a white background despite a dark ComboBox.
        // Style the generated entries as well so option text remains readable.
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, FieldBrush));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        box.ItemContainerStyle = itemStyle;
        return box;
    }

    private static Button StyleButton(string content) => new()
    {
        Content = content, Background = FieldBrush, Foreground = Brushes.White,
        BorderBrush = FieldBorderBrush, Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(0, 10, 0, 0)
    };

}
