using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

public enum GraphicShapeStageCode
{
    Validate,
    Read
}

public sealed class GraphicShapeException(GraphicShapeStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public GraphicShapeStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Represents a simple editable vector shape inside a title clip. Width and
/// height are fractions of the sequence canvas, so geometry scales with every
/// preview size and export resolution rather than hard-coded control sizes.
/// </summary>
public sealed class GraphicShape
{
    public const string ExtensionKey = "TangerineGraphicShape";
    public string Kind { get; set; } = "Rectangle";
    public double WidthFraction { get; set; } = .5;
    public double HeightFraction { get; set; } = .15;
    public string FillColor { get; set; } = "#FF9957";
    public string StrokeColor { get; set; } = "#000000";
    public double StrokeFraction { get; set; }
    public List<Point> Points { get; set; } = [];

    public static GraphicShape? FromClip(TimelineClip clip)
    {
        if (clip.Text?.AdditionalData?.TryGetValue(ExtensionKey, out JsonElement value) != true) return null;
        try
        {
            GraphicShape result = value.Deserialize<GraphicShape>() ??
                throw new GraphicShapeException(GraphicShapeStageCode.Read, "图形对象为空。");
            result.Validate();
            return result;
        }
        catch (GraphicShapeException) { throw; }
        catch (JsonException exception)
        {
            throw new GraphicShapeException(GraphicShapeStageCode.Read, "项目中的图形对象无效。", exception);
        }
    }

    public void Save(TimelineClip clip)
    {
        Validate();
        clip.Text ??= new ClipText { Content = string.Empty };
        clip.Text.AdditionalData ??= [];
        clip.Text.AdditionalData[ExtensionKey] = JsonSerializer.SerializeToElement(this);
    }

    public Shape CreateVisual(int canvasWidth, int canvasHeight, double scale)
    {
        Validate();
        Shape shape;
        if (Kind == "Ellipse" || Kind == "Circle") shape = new Ellipse();
        else
        {
            IEnumerable<Point> points = Points;
            if (points.Count() == 0)
            {
                points = Kind switch
                {
                    "Rectangle" => [new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1)],
                    "Parallelogram" => [new Point(.16, 0), new Point(1, 0), new Point(.84, 1), new Point(0, 1)],
                    _ => Enumerable.Range(0, Kind switch
                    {
                        "Triangle" => 3, "Pentagon" => 5, "Hexagon" => 6, "Decagon" => 10, _ => 4
                    }).Select(index =>
                    {
                        double angle = -Math.PI / 2 + index * Math.PI * 2 /
                            (Kind switch { "Triangle" => 3, "Pentagon" => 5, "Hexagon" => 6, "Decagon" => 10, _ => 4 });
                        return new Point(.5 + Math.Cos(angle) * .46, .5 + Math.Sin(angle) * .46);
                    })
                };
            }
            var polygon = new Polygon { Points = new PointCollection(points.Select(point =>
                new Point(point.X * canvasWidth * WidthFraction * scale,
                    point.Y * canvasHeight * HeightFraction * scale))) };
            shape = polygon;
        }
        shape.Width = canvasWidth * WidthFraction * scale;
        shape.Height = canvasHeight * HeightFraction * scale;
        shape.Fill = ParseBrush(FillColor, Brushes.Transparent);
        shape.Stroke = ParseBrush(StrokeColor, Brushes.Transparent);
        shape.StrokeThickness = Math.Min(canvasWidth, canvasHeight) * StrokeFraction * scale;
        return shape;
    }

    public void Validate()
    {
        if (Kind is not ("Rectangle" or "Ellipse" or "Circle" or "Parallelogram" or "Triangle" or
            "Pentagon" or "Hexagon" or "Decagon" or "Quadrilateral" or "Custom") ||
            !double.IsFinite(WidthFraction) || WidthFraction <= 0 || WidthFraction > 4 ||
            !double.IsFinite(HeightFraction) || HeightFraction <= 0 || HeightFraction > 4 ||
            !double.IsFinite(StrokeFraction) || StrokeFraction < 0 || StrokeFraction > .1 ||
            !TryColor(FillColor) || !TryColor(StrokeColor) ||
            // Any polygon kind may carry edited points, not just the two that
            // require them, so validate whenever points are present.
            (Kind is "Custom" or "Quadrilateral" || Points.Count > 0) && !ValidPoints(Points))
            throw new GraphicShapeException(GraphicShapeStageCode.Validate, "图形尺寸或颜色无效。");
        if (Points.Count > 0 && !HasDrawableArea(Points))
            throw new GraphicShapeException(GraphicShapeStageCode.Validate,
                "图形的顶点落在一条直线上，没有可见面积，请调整顶点。");
    }

    /// <summary>
    /// Rejects a polygon whose vertices are collinear. Such a shape has no
    /// area: it renders as nothing, so preview would look empty and export
    /// would silently produce a blank layer. The shoelace sum is zero exactly
    /// when every vertex lies on one line.
    /// </summary>
    private static bool HasDrawableArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return false;
        double doubleArea = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point current = points[index];
            Point next = points[(index + 1) % points.Count];
            doubleArea += current.X * next.Y - next.X * current.Y;
        }
        // Points are normalized to 0..1, so this threshold is well below any
        // shape a user could see while still catching a flattened polygon.
        return Math.Abs(doubleArea) / 2 > .0005;
    }

    private static bool ValidPoints(IReadOnlyList<Point>? points) => points is not null && points.Count >= 3 &&
        points.Count <= 64 && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
            point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1) &&
        points.Select((point, index) => (point, next: points[(index + 1) % points.Count]))
            .All(item => Math.Abs(item.point.X - item.next.X) + Math.Abs(item.point.Y - item.next.Y) > .000001);

    private static bool TryColor(string value)
    {
        try { return ColorConverter.ConvertFromString(value) is Color; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static Brush ParseBrush(string value, Brush fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
        { return fallback; }
    }
}
