using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Draws grapheme clusters at measured advances because WPF TextBlock does
/// not expose character spacing. The same helper is used for preview and
/// transparent title bitmap export, keeping line wrapping and alignment in
/// sequence-relative coordinates.
/// </summary>
public static class LetterSpacingText
{
    public static double? Draw(Canvas canvas, ClipText style, double scale,
        double maximumWidth, double x, double y, Brush brush, Effect? effect = null,
        bool drawUnderline = true)
    {
        if (Math.Abs(style.LetterSpacing) < .001 || string.IsNullOrEmpty(style.Content)) return null;
        double fontSize = Math.Max(1, style.FontSize * scale);
        double spacing = style.LetterSpacing * scale;
        double lineHeight = Math.Max(fontSize, fontSize * Math.Max(.5, style.LineHeight));
        var family = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily)
            ? "Microsoft YaHei UI" : style.FontFamily);
        var face = new Typeface(family, style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var rows = new List<List<(string Text, double Width)>>();
        foreach (string sourceLine in style.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var row = new List<(string Text, double Width)>();
            double advance = 0;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(sourceLine);
            while (elements.MoveNext())
            {
                string grapheme = elements.GetTextElement();
                var glyph = new FormattedText(grapheme, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, face, fontSize, brush, 1.0);
                double gap = row.Count == 0 ? 0 : Math.Max(spacing, -glyph.Width * .9);
                // Measure and drawing use the same incoming gap. A negative
                // value may overlap glyphs but keeps positive advance.
                if (row.Count > 0 && maximumWidth > 0 &&
                    advance + gap + glyph.Width > maximumWidth + .001)
                {
                    rows.Add(row);
                    row = [];
                    advance = 0;
                }
                if (row.Count == 0) gap = 0;
                row.Add((grapheme, glyph.Width));
                advance += gap + glyph.Width;
            }
            rows.Add(row);
        }
        double height = rows.Count * lineHeight;
        double blockWidth = rows.Select(row => RowWidth(row, spacing)).DefaultIfEmpty(0).Max();
        double alignmentWidth = double.IsFinite(maximumWidth) && maximumWidth > 0 ? maximumWidth : blockWidth;
        // The caller already centers the complete title block. Only narrower
        // lines receive additional local alignment inside that block.
        double blockShift = style.HorizontalAlignment switch
        {
            "Right" => alignmentWidth - blockWidth,
            "Center" => (alignmentWidth - blockWidth) / 2,
            _ => 0
        };
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            IReadOnlyList<(string Text, double Width)> row = rows[rowIndex];
            double width = RowWidth(row, spacing);
            double shift = style.HorizontalAlignment switch
            {
                "Right" => alignmentWidth - width,
                "Center" => (alignmentWidth - width) / 2,
                _ => 0
            };
            double cursor = x + shift - blockShift;
            for (int index = 0; index < row.Count; index++)
            {
                (string text, double glyphWidth) = row[index];
                if (index > 0) cursor += Math.Max(spacing, -glyphWidth * .9);
                var visual = new TextBlock
                {
                    Text = text,
                    FontFamily = family,
                    FontSize = fontSize,
                    FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal,
                    Foreground = brush,
                    TextDecorations = drawUnderline && style.Underline ? TextDecorations.Underline : null,
                    // A Freezable effect may be edited by WPF when attached
                    // to a visual. Give each glyph an independent instance.
                    Effect = effect is null ? null : (Effect)effect.CloneCurrentValue(),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(visual, cursor);
                Canvas.SetTop(visual, y + rowIndex * lineHeight);
                canvas.Children.Add(visual);
                cursor += glyphWidth;
            }
        }
        return height;
    }

    public static Size Measure(ClipText style, double scale, double maximumWidth)
    {
        if (Math.Abs(style.LetterSpacing) < .001 || string.IsNullOrEmpty(style.Content))
            return Size.Empty;
        double fontSize = Math.Max(1, style.FontSize * scale);
        double spacing = style.LetterSpacing * scale;
        double lineHeight = Math.Max(fontSize, fontSize * Math.Max(.5, style.LineHeight));
        var family = new FontFamily(string.IsNullOrWhiteSpace(style.FontFamily)
            ? "Microsoft YaHei UI" : style.FontFamily);
        var face = new Typeface(family, style.Italic ? FontStyles.Italic : FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        double widest = 0;
        double width = 0;
        int glyphs = 0;
        int rows = 0;
        foreach (string sourceLine in (style.Content ?? string.Empty)
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(sourceLine);
            while (elements.MoveNext())
            {
                var glyph = new FormattedText(elements.GetTextElement(), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, face, fontSize, Brushes.White, 1.0);
                double gap = glyphs == 0 ? 0 : Math.Max(spacing, -glyph.Width * .9);
                if (glyphs > 0 && double.IsFinite(maximumWidth) && maximumWidth > 0 &&
                    width + gap + glyph.Width > maximumWidth + .001)
                {
                    widest = Math.Max(widest, width);
                    width = 0;
                    glyphs = 0;
                    rows++;
                }
                if (glyphs > 0) width += gap;
                width += glyph.Width;
                glyphs++;
            }
            widest = Math.Max(widest, width);
            width = 0;
            glyphs = 0;
            rows++;
        }
        return new Size(widest, Math.Max(lineHeight, rows * lineHeight));
    }

    private static double RowWidth(IReadOnlyList<(string Text, double Width)> row, double spacing)
    {
        double width = 0;
        for (int index = 0; index < row.Count; index++)
        {
            if (index > 0) width += Math.Max(spacing, -row[index].Width * .9);
            width += row[index].Width;
        }
        return Math.Max(0, width);
    }
}
