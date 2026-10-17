using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using TANGERINE_VideoEditor.Project;
using TANGERINE_VideoEditor.Editor;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Exposes every built-in text style property through a localized editor.
/// Values remain in ClipText so they round-trip through .tvep projects.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Opens text controls in the left settings area. Changes are applied
    /// immediately so a preview double-click never opens a blocking dialog.
    /// </summary>
    private void OpenTextToolPage(TimelineClip clip)
    {
        if (clip.Kind != ClipKind.Text) return;
        SelectClip(clip);
        // A shape sticker has no text, so it opens its own shape page.
        if (IsGraphicClip(clip)) OpenObjectToolPage(clip, "形状设置", BuildGraphicTool);
        else OpenObjectToolPage(clip, $"{clip.Name}设置", BuildTextTool);
    }

    private UIElement BuildTextTool(TimelineClip clip, Action back)
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(SettingsHeader("文字设置", back));
        panel.Children.Add(new TextBlock { Text = "双击预览区文字即可编辑；设置会即时生效。", Foreground = Brush("#C5CAD4"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        ClipText style = clip.Text ??= new ClipText { Content = clip.Name };
        bool updating = true;
        var content = new TextBox { Text = style.Content, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 3, 0, 8) };
        panel.Children.Add(new TextBlock { Text = "文字内容", Foreground = Brush("#D9DCE3") }); panel.Children.Add(content);
        var font = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Text = style.FontFamily, ItemsSource = new[] { "Microsoft YaHei UI", "Arial", "Calibri", "Consolas", "Times New Roman" }, Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#565B65") };
        panel.Children.Add(new TextBlock { Text = "字体", Foreground = Brush("#D9DCE3") }); panel.Children.Add(font); _ = PopulateFontsAsync(font);
        var size = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, Text = style.FontSize.ToString(CultureInfo.CurrentCulture), ItemsSource = new[] { "12", "16", "20", "24", "28", "32", "40", "48", "56", "64", "72", "96", "120", "144", "192" }, Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#565B65") };
        panel.Children.Add(new TextBlock { Text = "字号", Foreground = Brush("#D9DCE3") }); panel.Children.Add(size);
        var toggles = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var bold = new CheckBox { Content = "加粗", IsChecked = style.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 0) };
        var italic = new CheckBox { Content = "斜体", IsChecked = style.Italic, Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 0) };
        var underline = new CheckBox { Content = "下划线", IsChecked = style.Underline, Foreground = Brushes.White };
        toggles.Children.Add(bold); toggles.Children.Add(italic); toggles.Children.Add(underline); panel.Children.Add(toggles);
        var foreground = AddTextToolField(panel, "文字颜色", style.ForegroundColor);
        var background = AddTextToolField(panel, "文字背景颜色（留空透明）", style.BackgroundColor ?? string.Empty);
        var outlineEnabled = new CheckBox { Content = "启用描边", IsChecked = style.OutlineEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) }; panel.Children.Add(outlineEnabled);
        var outlineColor = AddTextToolField(panel, "描边颜色", style.OutlineColor); var outlineThickness = AddTextToolField(panel, "描边粗细", style.OutlineThickness.ToString(CultureInfo.CurrentCulture));
        var shadowEnabled = new CheckBox { Content = "启用阴影", IsChecked = style.ShadowEnabled, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) }; panel.Children.Add(shadowEnabled);
        var shadowColor = AddTextToolField(panel, "阴影颜色", style.ShadowColor); var shadowBlur = AddTextToolField(panel, "阴影模糊", style.ShadowBlur.ToString(CultureInfo.CurrentCulture)); var shadowOpacity = AddTextToolField(panel, "阴影透明度（0-1）", style.ShadowOpacity.ToString(CultureInfo.CurrentCulture));
        var shadowX = AddTextToolField(panel, "阴影 X 偏移", style.ShadowOffsetX.ToString(CultureInfo.CurrentCulture));
        var shadowY = AddTextToolField(panel, "阴影 Y 偏移", style.ShadowOffsetY.ToString(CultureInfo.CurrentCulture));
        var letterSpacing = AddTextToolField(panel, "字距（-100 至 300）", style.LetterSpacing.ToString(CultureInfo.CurrentCulture));
        var lineHeight = AddTextToolField(panel, "行高倍数（0.5 至 5）", style.LineHeight.ToString(CultureInfo.CurrentCulture));
        var horizontal = new ComboBox { ItemsSource = new[] { "左对齐", "居中", "右对齐" }, SelectedItem = style.HorizontalAlignment switch { "Left" => "左对齐", "Right" => "右对齐", _ => "居中" }, Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#565B65") };
        panel.Children.Add(new TextBlock { Text = "水平对齐", Foreground = Brush("#D9DCE3") }); panel.Children.Add(horizontal);
        var vertical = new ComboBox { ItemsSource = new[] { "顶部", "居中", "底部" }, SelectedItem = style.VerticalAlignment switch { "Top" => "顶部", "Bottom" => "底部", _ => "居中" }, Margin = new Thickness(0, 3, 0, 8), Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#565B65") };
        panel.Children.Add(new TextBlock { Text = "垂直对齐", Foreground = Brush("#D9DCE3") }); panel.Children.Add(vertical);
        bool undoCaptured = false;
        bool applying = false;
        TitleMotionSection? motion = BuildTitleMotionSection(clip, style,
            () => { if (!undoCaptured) { SaveUndoState(); undoCaptured = true; } });
        void Apply()
        {
            if (updating || applying || !(_project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip))?.Clips.Contains(clip) == true)) return;
            if (!double.TryParse(size.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double fontSize) || !double.IsFinite(fontSize) || fontSize is < 1 or > 1000 ||
                !double.TryParse(outlineThickness.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double border) || !double.IsFinite(border) || border is < 0 or > 50 ||
                !double.TryParse(shadowBlur.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double blur) || !double.IsFinite(blur) || blur is < 0 or > 100 ||
                !double.TryParse(shadowOpacity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double opacity) || !double.IsFinite(opacity) || opacity is < 0 or > 1 ||
                !double.TryParse(shadowX.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double offsetX) || !double.IsFinite(offsetX) || offsetX is < -200 or > 200 ||
                !double.TryParse(shadowY.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double offsetY) || !double.IsFinite(offsetY) || offsetY is < -200 or > 200 ||
                !double.TryParse(letterSpacing.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double spacing) || !double.IsFinite(spacing) || spacing is < -100 or > 300 ||
                !double.TryParse(lineHeight.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double lineHeightValue) || !double.IsFinite(lineHeightValue) || lineHeightValue is < .5 or > 5 ||
                !TryColorValue(foreground.Text) || !TryColorValue(background.Text, true) || !TryColorValue(outlineColor.Text) || !TryColorValue(shadowColor.Text)) return;
            applying = true;
            try
            {
                // One undo snapshot covers the current editing session. Do
                // not rebuild the settings tab on every keystroke: that would
                // steal focus and make multiline text impossible to enter.
                if (!undoCaptured) { SaveUndoState(); undoCaptured = true; }
                style.Content = content.Text; style.FontFamily = string.IsNullOrWhiteSpace(font.Text) ? style.FontFamily : font.Text.Trim(); style.FontSize = fontSize; style.Bold = bold.IsChecked == true; style.Italic = italic.IsChecked == true; style.Underline = underline.IsChecked == true; style.ForegroundColor = foreground.Text; style.BackgroundColor = string.IsNullOrWhiteSpace(background.Text) ? null : background.Text; style.OutlineEnabled = outlineEnabled.IsChecked == true; style.OutlineColor = outlineColor.Text; style.OutlineThickness = border; style.ShadowEnabled = shadowEnabled.IsChecked == true; style.ShadowColor = shadowColor.Text; style.ShadowBlur = blur; style.ShadowOpacity = opacity; style.ShadowOffsetX = offsetX; style.ShadowOffsetY = offsetY; style.LetterSpacing = spacing; style.LineHeight = lineHeightValue; style.HorizontalAlignment = (horizontal.SelectedItem as string) switch { "左对齐" => "Left", "右对齐" => "Right", _ => "Center" }; style.VerticalAlignment = (vertical.SelectedItem as string) switch { "顶部" => "Top", "底部" => "Bottom", _ => "Center" }; clip.Name = style.Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "文字"; MarkProjectDirty(); DrawTimeline(); DrawTextOverlay(); SeekPreview(_project.Sequence.PlayheadSeconds); LibraryTitle.Text = $"{clip.Name}设置"; motion?.Refresh();
            }
            finally { applying = false; }
        }
        content.TextChanged += (_, _) => Apply(); font.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Apply())); size.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => Apply())); bold.Click += (_, _) => Apply(); italic.Click += (_, _) => Apply(); underline.Click += (_, _) => Apply(); foreground.TextChanged += (_, _) => Apply(); background.TextChanged += (_, _) => Apply(); outlineEnabled.Click += (_, _) => Apply(); outlineColor.TextChanged += (_, _) => Apply(); outlineThickness.TextChanged += (_, _) => Apply(); shadowEnabled.Click += (_, _) => Apply(); shadowColor.TextChanged += (_, _) => Apply(); shadowBlur.TextChanged += (_, _) => Apply(); shadowOpacity.TextChanged += (_, _) => Apply(); shadowX.TextChanged += (_, _) => Apply(); shadowY.TextChanged += (_, _) => Apply(); letterSpacing.TextChanged += (_, _) => Apply(); lineHeight.TextChanged += (_, _) => Apply(); horizontal.SelectionChanged += (_, _) => Apply(); vertical.SelectionChanged += (_, _) => Apply(); updating = false;
        if (motion is not null) panel.Children.Add(motion.Element);
        return panel;
    }

    private static TextBox AddTextToolField(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brush("#D9DCE3") });
        var box = new TextBox { Text = value, Background = Brush("#282B32"), Foreground = Brushes.White, BorderBrush = Brush("#444851"), Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 3, 0, 8) }; panel.Children.Add(box); return box;
    }

    private static bool TryColorValue(string value, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return allowEmpty;
        try { return ColorConverter.ConvertFromString(value) is Color; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException) { return false; }
    }

    private void ShowTextProperties(TimelineClip clip, bool unattachedDraft = false,
        Window? dialogOwner = null)
    {
        if (!unattachedDraft)
        {
            SelectClip(clip);
            OpenTextToolPage(clip);
            return;
        }
        ProjectDocument dialogProject = _project;
        long dialogGeneration = _projectGeneration;
        TimelineTrack? ownerTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
        if (!unattachedDraft && (ownerTrack is null || ownerTrack.IsLocked))
        {
            StatusText.Text = ownerTrack is null ? "文字片段已不在当前项目中" : "轨道已锁定，无法编辑文字";
            return;
        }
        ClipText current = clip.Text ?? new ClipText { Content = clip.Name };
        var dialog = new Window
        {
            Title = "文字属性",
            Owner = dialogOwner ?? this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#191B20"),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Width = Math.Max(1, SystemParameters.PrimaryScreenWidth * 0.4),
            Height = Math.Max(1, SystemParameters.PrimaryScreenHeight * 0.72),
            ResizeMode = ResizeMode.CanResize,
        };
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var form = new Grid();
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(35, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65, GridUnitType.Star) });
        int row = 0;

        var content = AddTextBox(form, ref row, "内容", current.Content, multiline: true);
        double editTime = _project.Sequence.PlayheadSeconds;
        var positionX = AddTextBox(form, ref row, "画面位置 X",
            ClipAnimation.Evaluate(clip, "PositionX", editTime).ToString(CultureInfo.CurrentCulture));
        var positionY = AddTextBox(form, ref row, "画面位置 Y",
            ClipAnimation.Evaluate(clip, "PositionY", editTime).ToString(CultureInfo.CurrentCulture));
        var font = AddCombo(form, ref row, "字体", [current.FontFamily], current.FontFamily);
        // Enumerating installed fonts may touch slow font providers. Populate
        // the dropdown after the editor is visible, without blocking input.
        _ = PopulateFontsAsync(font);
        var size = AddCombo(form, ref row, "字号",
            ["12", "16", "20", "24", "28", "32", "40", "48", "56", "64", "72", "96", "120", "144", "192"],
            current.FontSize.ToString(CultureInfo.CurrentCulture));
        var letterSpacing = AddTextBox(form, ref row, "字距（-100 至 300）",
            current.LetterSpacing.ToString(CultureInfo.CurrentCulture));
        var bold = AddCheck(form, ref row, "加粗", current.Bold);
        var italic = AddCheck(form, ref row, "斜体", current.Italic);
        var underline = AddCheck(form, ref row, "下划线", current.Underline);
        var foreground = AddColorBox(form, ref row, "文字颜色", current.ForegroundColor, dialog);
        var background = AddColorBox(form, ref row, "背景颜色（留空透明）", current.BackgroundColor ?? string.Empty, dialog);
        string horizontalLabel = current.HorizontalAlignment switch
        { "Left" => "左对齐", "Right" => "右对齐", _ => "居中" };
        string verticalLabel = current.VerticalAlignment switch
        { "Top" => "顶部", "Bottom" => "底部", _ => "居中" };
        var horizontal = AddCombo(form, ref row, "水平对齐", ["左对齐", "居中", "右对齐"], horizontalLabel);
        var vertical = AddCombo(form, ref row, "垂直对齐", ["顶部", "居中", "底部"], verticalLabel);
        horizontal.IsEditable = false;
        vertical.IsEditable = false;
        horizontal.SelectedItem = horizontalLabel;
        vertical.SelectedItem = verticalLabel;
        var outlineEnabled = AddCheck(form, ref row, "启用描边", current.OutlineEnabled);
        var outlineColor = AddColorBox(form, ref row, "描边颜色", current.OutlineColor, dialog);
        var outlineThickness = AddTextBox(form, ref row, "描边粗细", current.OutlineThickness.ToString(CultureInfo.CurrentCulture));
        var shadowEnabled = AddCheck(form, ref row, "启用阴影", current.ShadowEnabled);
        var shadowColor = AddColorBox(form, ref row, "阴影颜色", current.ShadowColor, dialog);
        var shadowBlur = AddTextBox(form, ref row, "阴影模糊", current.ShadowBlur.ToString(CultureInfo.CurrentCulture));
        var shadowOpacity = AddTextBox(form, ref row, "阴影透明度（0-1）", current.ShadowOpacity.ToString(CultureInfo.CurrentCulture));
        var shadowX = AddTextBox(form, ref row, "阴影 X 偏移", current.ShadowOffsetX.ToString(CultureInfo.CurrentCulture));
        var shadowY = AddTextBox(form, ref row, "阴影 Y 偏移", current.ShadowOffsetY.ToString(CultureInfo.CurrentCulture));
        var lineHeight = AddTextBox(form, ref row, "行高倍数", current.LineHeight.ToString(CultureInfo.CurrentCulture));
        GraphicShape? graphic;
        try { graphic = GraphicShape.FromClip(clip); }
        catch (GraphicShapeException exception)
        {
            StatusText.Text = $"图形读取失败 · {exception.StageCode}";
            graphic = null;
        }
        var shapeKind = AddCombo(form, ref row, "图形形状", ["无", "矩形", "椭圆"], graphic?.Kind switch
        {
            "Rectangle" => "矩形", "Ellipse" => "椭圆", _ => "无"
        });
        shapeKind.IsEditable = false;
        shapeKind.SelectedItem = graphic?.Kind switch
        { "Rectangle" => "矩形", "Ellipse" => "椭圆", _ => "无" };
        var shapeWidth = AddTextBox(form, ref row, "图形宽度（画幅比例）",
            (graphic?.WidthFraction ?? .5).ToString(CultureInfo.CurrentCulture));
        var shapeHeight = AddTextBox(form, ref row, "图形高度（画幅比例）",
            (graphic?.HeightFraction ?? .15).ToString(CultureInfo.CurrentCulture));
        var shapeFill = AddColorBox(form, ref row, "图形填充颜色", graphic?.FillColor ?? "#FF9957", dialog);
        var shapeStroke = AddColorBox(form, ref row, "图形描边颜色", graphic?.StrokeColor ?? "#000000", dialog);
        var shapeStrokeWidth = AddTextBox(form, ref row, "图形描边宽度（画幅比例）",
            (graphic?.StrokeFraction ?? 0).ToString(CultureInfo.CurrentCulture));
        scroller.Content = form;
        Grid.SetRow(scroller, 0);
        root.Children.Add(scroller);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var confirm = new Button { Content = "应用", Padding = new Thickness(16, 6, 16, 6), Background = Brush("#FF9957"), Foreground = Brushes.Black };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) =>
        {
            if (!ReferenceEquals(dialogProject, _project) || dialogGeneration != _projectGeneration)
            {
                StatusText.Text = "文字编辑已中止：项目已经切换 · TEXT-VALIDATE";
                dialog.DialogResult = false;
                return;
            }
            TimelineTrack? currentTrack = _project.Tracks.FirstOrDefault(track => track.Clips.Contains(clip));
            if (!unattachedDraft && (currentTrack is null || currentTrack.IsLocked))
            {
                StatusText.Text = currentTrack is null ? "文字片段已不在当前项目中" : "轨道已锁定，无法编辑文字";
                return;
            }
            if (!TryNumber(positionX.Text, -_project.Sequence.Width * 4, _project.Sequence.Width * 4, "画面位置 X", out double x) ||
                !TryNumber(positionY.Text, -_project.Sequence.Height * 4, _project.Sequence.Height * 4, "画面位置 Y", out double y) ||
                !TryNumber(size.Text, 1, 1000, "字号", out double fontSize) ||
                !TryNumber(letterSpacing.Text, -100, 300, "字距", out double letterSpacingValue) ||
                !TryNumber(outlineThickness.Text, 0, 50, "描边粗细", out double borderSize) ||
                !TryNumber(shadowBlur.Text, 0, 100, "阴影模糊", out double blur) ||
                !TryNumber(shadowOpacity.Text, 0, 1, "阴影透明度", out double opacity) ||
                !TryNumber(shadowX.Text, -200, 200, "阴影 X 偏移", out double offsetX) ||
                !TryNumber(shadowY.Text, -200, 200, "阴影 Y 偏移", out double offsetY) ||
                !TryNumber(lineHeight.Text, 0.5, 5, "行高倍数", out double lineHeightValue) ||
                !TryNumber(shapeWidth.Text, .001, 4, "图形宽度", out double graphicWidth) ||
                !TryNumber(shapeHeight.Text, .001, 4, "图形高度", out double graphicHeight) ||
                !TryNumber(shapeStrokeWidth.Text, 0, .1, "图形描边宽度", out double graphicStroke)) return;
            if (!TryColor(foreground.Text, false) || !TryColor(background.Text, true) ||
                !TryColor(outlineColor.Text, false) || !TryColor(shadowColor.Text, false) ||
                !TryColor(shapeFill.Text, false) || !TryColor(shapeStroke.Text, false)) return;
            GraphicShape? newShape = null;
            if (shapeKind.Text is "矩形" or "椭圆")
            {
                newShape = new GraphicShape
                {
                    Kind = shapeKind.Text == "矩形" ? "Rectangle" : "Ellipse",
                    WidthFraction = graphicWidth,
                    HeightFraction = graphicHeight,
                    FillColor = shapeFill.Text,
                    StrokeColor = shapeStroke.Text,
                    StrokeFraction = graphicStroke
                };
                try { newShape.Validate(); }
                catch (GraphicShapeException exception)
                {
                    StatusText.Text = $"图形设置无效 · {exception.StageCode}";
                    return;
                }
            }
            if (content.Text.Length > 100000)
            {
                MessageBox.Show(dialog, "文字内容超过 100000 个字符。", "输入无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (font.Text.Trim().Length > 256)
            {
                MessageBox.Show(dialog, "字体名称超过 256 个字符。", "输入无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (unattachedDraft)
            {
                // A new subtitle is not in the project yet. Its parent
                // dialog owns the single undo step when the draft is added.
                if (currentTrack is not null)
                {
                    StatusText.Text = "文字草稿状态已变化，请重新打开样式 · TEXT-VALIDATE";
                    return;
                }
            }
            else SaveUndoState();
            ShiftAnimatedVisualProperty(clip, "PositionX", x);
            ShiftAnimatedVisualProperty(clip, "PositionY", y);
            clip.Text = new ClipText
            {
                Content = content.Text,
                FontFamily = string.IsNullOrWhiteSpace(font.Text) ? current.FontFamily : font.Text.Trim(),
                FontSize = fontSize,
                Bold = bold.IsChecked == true,
                Italic = italic.IsChecked == true,
                Underline = underline.IsChecked == true,
                ForegroundColor = foreground.Text,
                BackgroundColor = string.IsNullOrWhiteSpace(background.Text) ? null : background.Text,
                HorizontalAlignment = (horizontal.SelectedItem as string) switch
                { "左对齐" => "Left", "右对齐" => "Right", _ => "Center" },
                VerticalAlignment = (vertical.SelectedItem as string) switch
                { "顶部" => "Top", "底部" => "Bottom", _ => "Center" },
                OutlineEnabled = outlineEnabled.IsChecked == true,
                OutlineColor = outlineColor.Text,
                OutlineThickness = borderSize,
                ShadowEnabled = shadowEnabled.IsChecked == true,
                ShadowColor = shadowColor.Text,
                ShadowBlur = blur,
                ShadowOpacity = opacity,
                ShadowOffsetX = offsetX,
                ShadowOffsetY = offsetY,
                LetterSpacing = letterSpacingValue,
                LineHeight = lineHeightValue,
                Motion = current.Motion,
                AdditionalData = current.AdditionalData
            };
            if (newShape is not null) newShape.Save(clip);
            else clip.Text.AdditionalData?.Remove(GraphicShape.ExtensionKey);
            clip.Name = content.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "文字";
            if (!unattachedDraft)
            {
                MarkProjectDirty();
                RefreshTimeline();
                SeekPreview(_project.Sequence.PlayheadSeconds);
            }
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        dialog.Content = root;
        // A preview double-click should immediately land in the text itself.
        // Keep the full style controls in the same editor for follow-up edits.
        dialog.ContentRendered += (_, _) =>
        {
            content.Focus();
            content.SelectAll();
        };
        dialog.ShowDialog();
    }

    private static TextBox AddTextBox(Grid grid, ref int row, string label, string value, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = value,
            Background = Brush("#282B32"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#444851"),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 0, 0, 8),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? SystemParameters.PrimaryScreenHeight * .075 : 0
        };
        AddField(grid, ref row, label, box);
        return box;
    }

    private static ComboBox AddCombo(Grid grid, ref int row, string label, IEnumerable<string> values, string selected)
    {
        var combo = new ComboBox
        {
            ItemsSource = values,
            IsEditable = true,
            Text = selected,
            IsTextSearchEnabled = true,
            Background = Brush("#282B32"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#565B65"),
            Margin = new Thickness(0, 0, 0, 8)
        };
        // WPF's default editable template can use dark text in the inner
        // PART_EditableTextBox even when the ComboBox foreground is white.
        combo.Loaded += (_, _) =>
        {
            if (combo.Template.FindName("PART_EditableTextBox", combo) is TextBox editor)
            {
                editor.Foreground = Brushes.White;
                editor.Background = Brush("#282B32");
                editor.CaretBrush = Brushes.White;
            }
        };
        AddField(grid, ref row, label, combo);
        return combo;
    }

    private static async Task PopulateFontsAsync(ComboBox combo)
    {
        try
        {
            var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(Fonts.SystemFontFamilies
                        .Select(family => family.Source)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(name => name).ToArray());
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            string[] families = await completion.Task;
            // Preserve edits entered while font discovery was running.
            string selected = combo.Text;
            combo.ItemsSource = families;
            combo.Text = selected;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"TEXT-FONT-LIST: {exception}");
        }
    }

    private static CheckBox AddCheck(Grid grid, ref int row, string label, bool value)
    {
        var check = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
        AddField(grid, ref row, label, check);
        return check;
    }

    private static TextBox AddColorBox(Grid grid, ref int row, string label, string value, Window owner)
    {
        var box = new TextBox
        {
            Text = value,
            Background = Brush("#282B32"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#444851"),
            Padding = new Thickness(7, 5, 7, 5)
        };
        box.ToolTip = "输入颜色，例如 #FFFFFF、#80FFFFFF 或命名颜色";
        var choose = new System.Windows.Controls.Button
        {
            Content = "选择…",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(7, 0, 0, 0)
        };
        choose.Click += (_, _) => ShowColorPalette(owner, box);
        var rowContent = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(choose, 1);
        rowContent.Children.Add(box);
        rowContent.Children.Add(choose);
        AddField(grid, ref row, label, rowContent);
        return box;
    }

    private static void ShowColorPalette(Window owner, TextBox target)
    {
        var dialog = new Window
        {
            Title = "选择颜色",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#191B20"),
            Foreground = Brushes.White,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize
        };
        var grid = new UniformGrid { Columns = 6, Margin = new Thickness(12) };
        string[] colors =
        [
            "#FFFFFF", "#C9CDD5", "#9198A3", "#4A5059", "#17191D", "#000000",
            "#FF5555", "#FF9957", "#FFD45C", "#59D69B", "#53C5EC", "#7189F7",
            "#ED71B7", "#B67DF2", "#A76A4B", "#5A9A63", "#3575A9", "#7E5BA8"
        ];
        foreach (string color in colors)
        {
            var swatch = new System.Windows.Controls.Button
            {
                Background = Brush(color),
                Content = color,
                Foreground = color == "#FFFFFF" || color == "#C9CDD5" || color == "#FFD45C" ? Brushes.Black : Brushes.White,
                Padding = new Thickness(8, 8, 8, 8),
                Margin = new Thickness(3)
            };
            swatch.Click += (_, _) => { target.Text = color; dialog.Close(); };
            grid.Children.Add(swatch);
        }
        dialog.Content = grid;
        dialog.ShowDialog();
    }

    private static void AddField(Grid grid, ref int row, string label, UIElement input)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, Foreground = Brush("#D9DCE3"), Margin = new Thickness(0, 5, 12, 8), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        grid.Children.Add(caption);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        row++;
    }

    private bool TryNumber(string text, double minimum, double maximum, string label, out double result)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out result) && double.IsFinite(result) && result >= minimum && result <= maximum)
            return true;
        MessageBox.Show(this, $"{label}必须在 {minimum} 到 {maximum} 之间。", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private bool TryColor(string value, bool allowEmpty)
    {
        if (allowEmpty && string.IsNullOrWhiteSpace(value)) return true;
        try
        {
            if (ColorConverter.ConvertFromString(value) is Color) return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        {
            // The shared warning below covers every invalid color form.
        }
        MessageBox.Show(this, $"颜色格式无效：{value}", "输入无效", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
