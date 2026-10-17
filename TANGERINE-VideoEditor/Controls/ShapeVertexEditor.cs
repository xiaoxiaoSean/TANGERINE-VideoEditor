using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TANGERINE_VideoEditor.Controls;

/// <summary>
/// Interactive shape editor control with draggable vertex points.
/// Allows editing rectangle, triangle, and custom polygon vertices.
/// </summary>
public class ShapeVertexEditor : Canvas
{
    private List<Point> _vertices = new();
    private readonly List<Ellipse> _vertexHandles = new();
    private int _draggingVertexIndex = -1;
    private Point _dragStartPosition;

    public event EventHandler<List<Point>>? VerticesChanged;

    public ShapeVertexEditor()
    {
        Background = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        Width = 400;
        Height = 300;
        ClipToBounds = true;

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    /// <summary>
    /// Sets the vertices to edit (normalized 0-1 coordinates).
    /// </summary>
    public void SetVertices(List<Point> vertices)
    {
        _vertices = new List<Point>(vertices);
        RedrawShape();
    }

    /// <summary>
    /// Gets the current vertices (normalized 0-1 coordinates).
    /// </summary>
    public List<Point> GetVertices()
    {
        return new List<Point>(_vertices);
    }

    /// <summary>
    /// Sets shape by type (Rectangle, Triangle, etc).
    /// </summary>
    public void SetShapeType(string shapeType, int sides = 4)
    {
        _vertices.Clear();

        switch (shapeType)
        {
            case "Rectangle":
                _vertices.Add(new Point(0.2, 0.2));
                _vertices.Add(new Point(0.8, 0.2));
                _vertices.Add(new Point(0.8, 0.8));
                _vertices.Add(new Point(0.2, 0.8));
                break;

            case "Triangle":
                _vertices.Add(new Point(0.5, 0.1));
                _vertices.Add(new Point(0.9, 0.9));
                _vertices.Add(new Point(0.1, 0.9));
                break;

            case "Pentagon":
            case "Hexagon":
            case "Custom":
                for (int i = 0; i < sides; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI * 2 / sides;
                    _vertices.Add(new Point(
                        0.5 + Math.Cos(angle) * 0.4,
                        0.5 + Math.Sin(angle) * 0.4
                    ));
                }
                break;
        }

        RedrawShape();
    }

    private void RedrawShape()
    {
        Children.Clear();
        _vertexHandles.Clear();

        if (_vertices.Count < 3) return;

        // Draw shape outline
        var polygon = new Polygon
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            Points = new PointCollection(_vertices.Select(v => ToCanvasPoint(v)))
        };
        Children.Add(polygon);

        // Draw vertex handles
        for (int i = 0; i < _vertices.Count; i++)
        {
            var handle = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.Orange,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Cursor = Cursors.Hand,
                Tag = i
            };

            Point canvasPoint = ToCanvasPoint(_vertices[i]);
            SetLeft(handle, canvasPoint.X - 6);
            SetTop(handle, canvasPoint.Y - 6);

            Children.Add(handle);
            _vertexHandles.Add(handle);
        }
    }

    private Point ToCanvasPoint(Point normalized)
    {
        return new Point(normalized.X * ActualWidth, normalized.Y * ActualHeight);
    }

    private Point ToNormalizedPoint(Point canvas)
    {
        return new Point(
            Math.Clamp(canvas.X / ActualWidth, 0, 1),
            Math.Clamp(canvas.Y / ActualHeight, 0, 1)
        );
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point pos = e.GetPosition(this);

        // Check if clicking on a vertex handle
        for (int i = 0; i < _vertexHandles.Count; i++)
        {
            var handle = _vertexHandles[i];
            double handleX = GetLeft(handle) + 6;
            double handleY = GetTop(handle) + 6;
            double distance = Math.Sqrt(Math.Pow(pos.X - handleX, 2) + Math.Pow(pos.Y - handleY, 2));

            if (distance < 15)
            {
                _draggingVertexIndex = i;
                _dragStartPosition = pos;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingVertexIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            Point pos = e.GetPosition(this);
            _vertices[_draggingVertexIndex] = ToNormalizedPoint(pos);
            RedrawShape();
            VerticesChanged?.Invoke(this, _vertices);
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingVertexIndex >= 0)
        {
            _draggingVertexIndex = -1;
            ReleaseMouseCapture();
        }
    }
}
