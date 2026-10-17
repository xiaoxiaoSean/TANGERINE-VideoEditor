using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// The "动画参数" group of the text settings page. Each animated preset
/// (typewriter, counter, fade, slide, pop) exposes the numbers that shape its
/// motion instead of fixing them in code. Every field validates its range,
/// applies immediately, and shares one undo snapshot per editing session.
/// </summary>
public partial class MainWindow
{
    /// <summary>The group's visual, and a callback that refreshes its summary line.</summary>
    private sealed record TitleMotionSection(UIElement Element, Action Refresh);

    private TitleMotionSection? BuildTitleMotionSection(TimelineClip clip, ClipText style, Action captureUndo)
    {
        if (!DynamicTitlePreset.IsDynamic(clip)) return null;
        string presetName = BuiltinCatalog.Titles.FirstOrDefault(item => item.Id == clip.TemplateId)?.Name ?? "动画";

        var section = new Border
        {
            BorderBrush = Brush("#34363D"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 12, 0, 0)
        };
        var body = new StackPanel();
        section.Child = body;
        body.Children.Add(new TextBlock { Text = $"动画参数 · {presetName}", FontSize = 14,
            FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#C5CAD4"),
            Margin = new Thickness(0, 0, 0, 10) };
        body.Children.Add(summary);

        // The motion object is created only when the user changes something,
        // so an untouched title keeps meaning "built-in defaults".
        TitleMotion Settings() => style.Motion ??= new TitleMotion();

        void Refresh()
        {
            summary.Foreground = Brush("#C5CAD4");
            switch (clip.TemplateId)
            {
                case "title.typewriter":
                    int count = new StringInfo(style.Content ?? string.Empty).LengthInTextElements;
                    double total = DynamicTitlePreset.TypewriterTotalSeconds(clip);
                    bool tooLong = total > clip.DurationSeconds + .0001;
                    summary.Text = $"共 {count} 个字，全部显示约需 {total:0.##} 秒，片段长 {clip.DurationSeconds:0.##} 秒。" +
                        (tooLong ? "片段太短，文字没打完就结束了，请减小每字用时或延长片段。" : string.Empty);
                    if (tooLong) summary.Foreground = Brush("#FFB070");
                    break;
                case "title.counter":
                    summary.Text = "在文字内容里写成“起始 → 结束”（例如 0 → 100），数字会在计数时长内从起始值变到结束值。";
                    break;
                case "title.fade":
                    summary.Text = $"淡入与淡出加起来不会超过片段长度（{clip.DurationSeconds:0.##} 秒），超出时会按比例缩短。";
                    break;
                case "title.slide":
                    summary.Text = "文字从所选方向滑到原位。开始延迟期间，文字停在起始位置。";
                    break;
                case "title.pop":
                    summary.Text = "文字从起始大小弹到正常大小，回弹幅度越大弹得越过头。";
                    break;
            }
        }

        void Applied(string message)
        {
            MarkProjectDirty(); DrawTimeline(); DrawTextOverlay();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            Refresh();
            StatusText.Text = message;
        }

        static string Show(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

        // One validated numeric field. An out-of-range entry is refused with the
        // allowed range and the box returns to the last accepted value.
        void Number(string label, string tip, double current, double minimum, double maximum, Action<double> assign)
        {
            double accepted = current;
            TextBox box = AddTextToolField(body, label, Show(current));
            box.ToolTip = tip;
            void Commit()
            {
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                    !double.IsFinite(value) || value < minimum || value > maximum)
                {
                    StatusText.Text = $"“{label}”须在 {Show(minimum)} 到 {Show(maximum)} 之间 · TEXT-MOTION-RANGE";
                    box.Text = Show(accepted);
                    return;
                }
                if (Math.Abs(value - accepted) < .000000001) return;
                accepted = value;
                captureUndo();
                assign(value);
                Applied($"已更新“{label}”");
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                Commit();
                args.Handled = true;
            };
        }

        void Delay() => Number("开始延迟（秒）", "动画开始前先等待的时间", DynamicTitlePreset.DelaySeconds(clip), 0, 60,
            value => Settings().DelaySeconds = value);

        switch (clip.TemplateId)
        {
            case "title.typewriter":
            {
                int characters = Math.Max(1, new StringInfo(style.Content ?? string.Empty).LengthInTextElements);
                Number("每个字用时（秒/字）", "数值越小打字越快；表情符号和带音标的字母按一个字计算",
                    DynamicTitlePreset.TypewriterSecondsPerCharacter(clip, characters), .01, 10,
                    value => Settings().SecondsPerCharacter = value);
                Delay();
                break;
            }
            case "title.counter":
            {
                Number("计数时长（秒，0 = 整个片段）", "从起始数字变到结束数字所用的时间",
                    style.Motion?.CountSeconds ?? 0, 0, 3600,
                    value => Settings().CountSeconds = value <= 0 ? null : value);
                TextBox decimals = AddTextToolField(body, "小数位数（留空 = 跟随输入的数字）",
                    style.Motion?.Decimals?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
                string acceptedDecimals = decimals.Text;
                void CommitDecimals()
                {
                    string text = decimals.Text.Trim();
                    if (text == acceptedDecimals) return;
                    int? chosen = null;
                    if (text.Length > 0)
                    {
                        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) ||
                            parsed is < 0 or > 6)
                        {
                            StatusText.Text = "小数位数须是 0 到 6 的整数，或留空 · TEXT-MOTION-RANGE";
                            decimals.Text = acceptedDecimals;
                            return;
                        }
                        chosen = parsed;
                    }
                    acceptedDecimals = text;
                    captureUndo();
                    Settings().Decimals = chosen;
                    Applied("已更新“小数位数”");
                }
                decimals.LostKeyboardFocus += (_, _) => CommitDecimals();
                decimals.KeyDown += (_, args) =>
                {
                    if (args.Key != Key.Enter) return;
                    CommitDecimals();
                    args.Handled = true;
                };
                Delay();
                break;
            }
            case "title.fade":
            {
                (double fadeIn, double fadeOut) = DynamicTitlePreset.FadeSeconds(clip);
                Number("淡入时长（秒，0 = 不淡入）", "片段开头由透明变为不透明所用的时间", fadeIn, 0, 60,
                    value => Settings().FadeInSeconds = value);
                Number("淡出时长（秒，0 = 不淡出）", "片段结尾由不透明变为透明所用的时间", fadeOut, 0, 60,
                    value => Settings().FadeOutSeconds = value);
                break;
            }
            case "title.slide":
            {
                Number("滑入用时（秒）", "从起始位置滑到原位所用的时间", DynamicTitlePreset.SlideSeconds(clip), .05, 60,
                    value => Settings().SlideSeconds = value);
                Number("滑入距离（占画面宽或高的 %）", "左右方向按画面宽度计算，上下方向按画面高度计算",
                    DynamicTitlePreset.SlideDistance(clip) * 100, 0, 200,
                    value => Settings().SlideDistance = value / 100);
                string[] directionNames = ["从左侧滑入", "从右侧滑入", "从上方滑入", "从下方滑入"];
                string[] directionIds = ["Left", "Right", "Top", "Bottom"];
                body.Children.Add(new TextBlock { Text = "滑入方向", Foreground = Brush("#D9DCE3") });
                var from = new ComboBox
                {
                    ItemsSource = directionNames,
                    SelectedIndex = Math.Max(0, Array.IndexOf(directionIds, DynamicTitlePreset.SlideFrom(clip))),
                    Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"),
                    Foreground = Brushes.White, BorderBrush = Brush("#565B65")
                };
                from.SelectionChanged += (_, _) =>
                {
                    if (from.SelectedIndex < 0) return;
                    string chosen = directionIds[from.SelectedIndex];
                    if (chosen == DynamicTitlePreset.SlideFrom(clip)) return;
                    captureUndo();
                    Settings().SlideFrom = chosen;
                    Applied("已更新“滑入方向”");
                };
                body.Children.Add(from);
                Delay();
                break;
            }
            case "title.pop":
            {
                Number("弹跳用时（秒）", "从起始大小弹到正常大小所用的时间", DynamicTitlePreset.PopSeconds(clip), .05, 60,
                    value => Settings().PopSeconds = value);
                Number("起始大小（占正常大小的 %）", "弹跳开始时文字的大小",
                    DynamicTitlePreset.PopStartScale(clip) * 100, 5, 100,
                    value => Settings().PopStartScale = value / 100);
                Number("回弹幅度（%）", "越大，文字越会短暂弹得比正常大小更大",
                    DynamicTitlePreset.PopOvershoot(clip) * 100, 0, 100,
                    value => Settings().PopOvershoot = value / 100);
                Delay();
                break;
            }
        }

        var reset = new Button
        {
            Content = "恢复默认动画参数", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#292B31"),
            Foreground = Brushes.White, BorderBrush = Brush("#626874")
        };
        reset.Click += (_, _) =>
        {
            captureUndo();
            style.Motion = DynamicTitlePreset.DefaultMotionFor(clip.TemplateId);
            Applied("已恢复默认动画参数");
            // Rebuild the page so every field shows the restored defaults.
            OpenTextToolPage(clip);
        };
        body.Children.Add(reset);

        Refresh();
        return new TitleMotionSection(section, Refresh);
    }
}
