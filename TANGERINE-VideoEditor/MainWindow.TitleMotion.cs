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
                case "title.box-reveal":
                case "title.fade":
                    summary.Text = $"淡入与淡出加起来不会超过片段长度（{clip.DurationSeconds:0.##} 秒），超出时会按比例缩短。";
                    break;
                case "title.clean-lower-third":
                case "title.slide":
                    summary.Text = "文字从所选方向滑到原位。开始延迟期间，文字停在起始位置。";
                    break;
                case "title.outline-pop":
                case "title.pop":
                    summary.Text = "文字从起始大小弹到正常大小，回弹幅度越大弹得越过头。";
                    break;
                case "title.kinetic":
                    summary.Text = "标题同时滑入和弹跳缩放，两组运动参数均可独立调整。";
                    break;
                case "title.crawl":
                    summary.Text = "字幕从画面一侧持续横向滚动到另一侧；可调整方向、完整路程、时长和缓动。";
                    break;
                case "title.zoom":
                    summary.Text = "文字在指定时长内持续从起始比例缩放到结束比例；结束比例较小即为缓慢拉远。";
                    break;
                case "title.credits":
                    summary.Text = "文字会从画面底部向上滚动；速度越大，滚动越快。";
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

        void Choice(string label, string[] names, string[] ids, string current, Action<string> assign)
        {
            body.Children.Add(new TextBlock { Text = label, Foreground = Brush("#D9DCE3") });
            var choice = new ComboBox
            {
                ItemsSource = names, SelectedIndex = Math.Max(0, Array.IndexOf(ids, current)),
                Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"),
                Foreground = Brushes.White, BorderBrush = Brush("#565B65")
            };
            choice.SelectionChanged += (_, _) =>
            {
                if (choice.SelectedIndex < 0) return;
                string chosen = ids[choice.SelectedIndex];
                if (chosen == current) return;
                captureUndo(); current = chosen; assign(chosen); Applied($"已更新“{label}”");
            };
            body.Children.Add(choice);
        }

        void SlideFields()
        {
            Number("滑入用时（秒）", "从起始位置滑到原位所用的时间", DynamicTitlePreset.SlideSeconds(clip), .05, 60,
                value => Settings().SlideSeconds = value);
            Number("滑入距离（占画面宽或高的 %）", "左右方向按画面宽度计算，上下方向按画面高度计算",
                DynamicTitlePreset.SlideDistance(clip) * 100, 0, 200, value => Settings().SlideDistance = value / 100);
            Choice("滑入方向", ["从左侧滑入", "从右侧滑入", "从上方滑入", "从下方滑入"],
                ["Left", "Right", "Top", "Bottom"], DynamicTitlePreset.SlideFrom(clip), value => Settings().SlideFrom = value);
        }

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
            case "title.box-reveal":
            case "title.fade":
            {
                (double fadeIn, double fadeOut) = DynamicTitlePreset.FadeSeconds(clip);
                Number("淡入时长（秒，0 = 不淡入）", "片段开头由透明变为不透明所用的时间", fadeIn, 0, 60,
                    value => Settings().FadeInSeconds = value);
                Number("淡出时长（秒，0 = 不淡出）", "片段结尾由不透明变为透明所用的时间", fadeOut, 0, 60,
                    value => Settings().FadeOutSeconds = value);
                break;
            }
            case "title.clean-lower-third":
            case "title.slide":
                SlideFields(); Delay();
                break;
            case "title.outline-pop":
            case "title.pop":
            case "title.kinetic":
            {
                Number("弹跳用时（秒）", "从起始大小弹到正常大小所用的时间", DynamicTitlePreset.PopSeconds(clip), .05, 60,
                    value => Settings().PopSeconds = value);
                Number("起始大小（占正常大小的 %）", "弹跳开始时文字的大小",
                    DynamicTitlePreset.PopStartScale(clip) * 100, 5, 100,
                    value => Settings().PopStartScale = value / 100);
                Number("回弹幅度（%）", "越大，文字越会短暂弹得比正常大小更大",
                    DynamicTitlePreset.PopOvershoot(clip) * 100, 0, 100,
                    value => Settings().PopOvershoot = value / 100);
                if (clip.TemplateId == "title.kinetic") SlideFields();
                Delay();
                break;
            }
            case "title.crawl":
                Number("滚动时长（秒）", "滚动完整路程所需的时间；超过片段时长时自动适配",
                    DynamicTitlePreset.ScrollSeconds(clip), .05, 3600, value => Settings().ScrollSeconds = value);
                Number("滚动路程（画面宽度的 %）", "以文字原位为中点，起止各占一半路程",
                    DynamicTitlePreset.ScrollDistance(clip) * 100, 0, 800, value => Settings().ScrollDistance = value / 100);
                Choice("滚动方向", ["从右向左", "从左向右"], ["Left", "Right"],
                    DynamicTitlePreset.ScrollDirection(clip), value => Settings().ScrollDirection = value);
                Delay();
                break;
            case "title.zoom":
                Number("缩放时长（秒）", "从起始比例连续变化到结束比例所需时间",
                    DynamicTitlePreset.ZoomSeconds(clip), .05, 3600, value => Settings().ZoomSeconds = value);
                Number("起始比例（%）", "动画开始前的文字大小", DynamicTitlePreset.ZoomStartScale(clip) * 100,
                    5, 800, value => Settings().ZoomStartScale = value / 100);
                Number("结束比例（%）", "动画完成后的文字大小", DynamicTitlePreset.ZoomEndScale(clip) * 100,
                    5, 800, value => Settings().ZoomEndScale = value / 100);
                Delay();
                break;
            case "title.credits":
                Number("滚动速度（画面高度/片段）", "控制片尾文字向上滚动的速度", style.Motion?.CreditsSpeed ?? .45,
                    .05, 4, value => Settings().CreditsSpeed = value);
                break;
        }

        if (DynamicTitlePreset.HasSlide(clip) || DynamicTitlePreset.HasPop(clip) || clip.TemplateId is "title.crawl" or "title.zoom")
            Choice("缓动曲线", ["匀速", "加速进入", "减速到位", "平滑起止"],
                ["Linear", "EaseIn", "EaseOut", "Smooth"], DynamicTitlePreset.Easing(clip), value => Settings().Easing = value);

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
