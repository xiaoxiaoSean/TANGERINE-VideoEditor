using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Settings page of a shape sticker (rectangle, triangle, ellipse and so on).
/// A shape is stored as a text clip that carries a <see cref="GraphicShape"/>,
/// but it has no text, so it gets its own page with the shape's size, colors
/// and outline instead of the font settings.
/// </summary>
public partial class MainWindow
{
    private static bool IsGraphicClip(TimelineClip clip) =>
        clip.Kind == ClipKind.Text && clip.Text?.AdditionalData?.ContainsKey(GraphicShape.ExtensionKey) == true;

    private static string GraphicKindName(string kind) => kind switch
    {
        "Rectangle" => "矩形", "Ellipse" => "椭圆", "Circle" => "圆形", "Parallelogram" => "平行四边形",
        "Triangle" => "三角形", "Pentagon" => "五边形", "Hexagon" => "六边形", "Decagon" => "十边形",
        "Quadrilateral" => "四边形", _ => "自定义图形"
    };

    private UIElement BuildGraphicTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(SettingsHeader("形状设置", back));

        GraphicShape? initial;
        try { initial = GraphicShape.FromClip(clip); }
        catch (GraphicShapeException exception)
        {
            panel.Children.Add(new TextBlock { Text = $"形状数据读取失败：{exception.Message}",
                Foreground = Brush("#FFB070"), TextWrapping = TextWrapping.Wrap });
            return panel;
        }
        if (initial is null) return panel;
        GraphicShape original = initial;

        panel.Children.Add(new TextBlock
        {
            Text = $"形状：{GraphicKindName(initial.Kind)}。在预览区拖动形状可移动，拖动顶点可改变形状；设置会即时生效。",
            Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });

        static string Show(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

        // Every change is applied to the stored shape, validated, and only then
        // written, so an invalid entry can never leave a half-edited shape.
        bool Apply(Action<GraphicShape> change, string message)
        {
            TimelineTrack? owner = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
            if (owner is null || owner.IsLocked)
            {
                StatusText.Text = owner is null ? "形状已不在当前项目中" : "轨道已锁定，无法编辑形状";
                return false;
            }
            try
            {
                GraphicShape candidate = GraphicShape.FromClip(clip) ??
                    throw new GraphicShapeException(GraphicShapeStageCode.Read, "该片段不是形状。");
                change(candidate);
                candidate.Validate();
                SaveUndoState();
                candidate.Save(clip);
            }
            catch (GraphicShapeException exception)
            {
                StatusText.Text = $"{exception.Message} · {exception.StageCode}";
                return false;
            }
            MarkProjectDirty();
            _lastTitleLayoutKey = null;
            DrawTextOverlay();
            SeekPreview(_project.Sequence.PlayheadSeconds);
            StatusText.Text = message;
            return true;
        }

        GraphicShape? Stored()
        {
            try { return GraphicShape.FromClip(clip); }
            catch (GraphicShapeException) { return null; }
        }

        void Percent(string label, string tip, Func<GraphicShape, double> read, double minimum, double maximum,
            Action<GraphicShape, double> assign)
        {
            TextBox box = AddTextToolField(panel, label, Show(read(original) * 100));
            box.ToolTip = tip;
            void Commit()
            {
                GraphicShape? stored = Stored();
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                    !double.IsFinite(value) || value < minimum || value > maximum)
                {
                    StatusText.Text = $"“{label}”须在 {Show(minimum)} 到 {Show(maximum)} 之间 · GRAPHIC-RANGE";
                    if (stored is not null) box.Text = Show(read(stored) * 100);
                    return;
                }
                if (stored is not null && Math.Abs(read(stored) * 100 - value) < .0001) return;
                Apply(shape => assign(shape, value / 100), $"已更新“{label}”");
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                Commit();
                args.Handled = true;
            };
        }

        void ColorField(string label, Func<GraphicShape, string> read, Action<GraphicShape, string> assign)
        {
            TextBox box = AddTextToolField(panel, label, read(original));
            void Commit()
            {
                GraphicShape? stored = Stored();
                if (!TryColorValue(box.Text))
                {
                    StatusText.Text = $"“{label}”不是有效的颜色，例如 #FF9957 · GRAPHIC-COLOR";
                    if (stored is not null) box.Text = read(stored);
                    return;
                }
                string text = box.Text.Trim();
                if (stored is not null && string.Equals(read(stored), text, StringComparison.OrdinalIgnoreCase)) return;
                Apply(shape => assign(shape, text), $"已更新“{label}”");
            }
            box.LostKeyboardFocus += (_, _) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key != Key.Enter) return;
                Commit();
                args.Handled = true;
            };
        }

        Percent("宽度（占画面宽度的 %）", "形状的外框宽度，相对于整个画面", shape => shape.WidthFraction, .1, 400,
            (shape, value) => shape.WidthFraction = value);
        Percent("高度（占画面高度的 %）", "形状的外框高度，相对于整个画面", shape => shape.HeightFraction, .1, 400,
            (shape, value) => shape.HeightFraction = value);
        ColorField("填充颜色", shape => shape.FillColor, (shape, value) => shape.FillColor = value);
        ColorField("描边颜色", shape => shape.StrokeColor, (shape, value) => shape.StrokeColor = value);
        Percent("描边粗细（占画面短边的 %，0 = 无描边）", "相对于画面较短的一边", shape => shape.StrokeFraction, 0, 10,
            (shape, value) => shape.StrokeFraction = value);

        // Dragging vertices stores custom points. Regular shapes can go back to
        // their standard outline; a quadrilateral or free shape has no standard one.
        if (initial.Points.Count > 0 && initial.Kind is not ("Custom" or "Quadrilateral"))
        {
            var reset = new Button
            {
                Content = "恢复标准顶点", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#292B31"),
                Foreground = Brushes.White, BorderBrush = Brush("#626874")
            };
            reset.Click += (_, _) =>
            {
                if (Apply(shape => shape.Points = [], "已恢复标准顶点")) OpenTextToolPage(clip);
            };
            panel.Children.Add(reset);
        }
        return panel;
    }
}
