using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Places the editable media image on the sequence canvas. The crop rectangle
/// uses fractions of the uncropped source; scale, rotation and position then
/// act around the cropped area's center, matching the exported composition.
/// </summary>
public partial class MainWindow
{
    private enum MediaHandleKind { Move, ScaleTopLeft, ScaleTopRight, ScaleBottomRight, ScaleBottomLeft,
        CropLeft, CropTop, CropRight, CropBottom, Rotate, Anchor }

    private readonly List<UIElement> _mediaSelectionVisuals = [];
    private bool _mediaCropMode;
    private MediaHandleKind _mediaDragKind;
    private double _mediaOriginalFullWidth;
    private double _mediaOriginalFullHeight;
    private double _mediaOriginalCropWidth;
    private double _mediaOriginalCropHeight;
    private double _mediaOriginalCropLeft;
    private double _mediaOriginalCropTop;
    private double _mediaOriginalCropRight;
    private double _mediaOriginalCropBottom;
    private double _mediaDisplayedStartX;
    private double _mediaDisplayedStartY;
    private double _mediaDisplayedStartScaleX;
    private double _mediaDisplayedStartScaleY;
    private double _mediaDisplayedStartRotation;
    private double _rotationLastPointerDegrees;
    private double _rotationAccumulatedDegrees;
    private double _mediaDragCenterX;
    private double _mediaDragCenterY;
    private bool _mediaDraggingComposite;
    private double _mediaDragAnchorX;
    private double _mediaDragAnchorY;
    private DispatcherTimer? _compositeDragRenderTimer;

    private (double Left, double Top, double Width, double Height, double FullWidth, double FullHeight,
        double CenterX, double CenterY, double Angle)? MediaBounds(TimelineClip clip)
    {
        double frameWidth = PreviewVisualLayer.Width;
        double frameHeight = PreviewVisualLayer.Height;
        if (!double.IsFinite(frameWidth) || !double.IsFinite(frameHeight) || frameWidth <= 0 || frameHeight <= 0)
            return null;
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        double sourceWidth = asset?.Width is > 0 ? asset.Width.Value :
            clip.Kind == ClipKind.Image && PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource bitmap ? bitmap.PixelWidth :
            clip.Kind == ClipKind.Video && PreviewMediaElement.NaturalVideoWidth > 0 ? PreviewMediaElement.NaturalVideoWidth : _project.Sequence.Width;
        double sourceHeight = asset?.Height is > 0 ? asset.Height.Value :
            clip.Kind == ClipKind.Image && PreviewImage.Source is System.Windows.Media.Imaging.BitmapSource image ? image.PixelHeight :
            clip.Kind == ClipKind.Video && PreviewMediaElement.NaturalVideoHeight > 0 ? PreviewMediaElement.NaturalVideoHeight : _project.Sequence.Height;
        double ratio = sourceWidth / Math.Max(1, sourceHeight);
        if (!double.IsFinite(ratio) || ratio <= 0) ratio = (double)_project.Sequence.Width / Math.Max(1, _project.Sequence.Height);
        double fullWidth = Math.Min(frameWidth, frameHeight * ratio);
        double fullHeight = fullWidth / ratio;
        ClipTransform t = clip.Transform;
        double left = Math.Clamp(t.CropLeft, 0, .95);
        double right = Math.Clamp(t.CropRight, 0, Math.Min(.95, .99 - left));
        double top = Math.Clamp(t.CropTop, 0, .95);
        double bottom = Math.Clamp(t.CropBottom, 0, Math.Min(.95, .99 - top));
        double cropWidth = fullWidth * (1 - left - right);
        double cropHeight = fullHeight * (1 - top - bottom);
        double frameLeft = (PreviewOverlay.ActualWidth - frameWidth) / 2;
        double frameTop = (PreviewOverlay.ActualHeight - frameHeight) / 2;
        double currentTime = _project.Sequence.PlayheadSeconds;
        double centerX = frameLeft + frameWidth / 2 +
            Editor.ClipAnimation.Evaluate(clip, "PositionX", currentTime) * _previewTitleScale;
        double centerY = frameTop + frameHeight / 2 +
            Editor.ClipAnimation.Evaluate(clip, "PositionY", currentTime) * _previewTitleScale;
        double angle = Editor.ClipAnimation.Evaluate(clip, "RotationDegrees", currentTime);
        double scaleX = Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleX", currentTime), .05, 8);
        double scaleY = Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleY", currentTime), .05, 8);
        Point centerDelta = RotatedPoint(0, 0,
            (.5 - Math.Clamp(t.AnchorX, 0, 1)) * cropWidth * scaleX,
            (.5 - Math.Clamp(t.AnchorY, 0, 1)) * cropHeight * scaleY, angle);
        centerX += centerDelta.X;
        centerY += centerDelta.Y;
        return (fullWidth * left, fullHeight * top, cropWidth, cropHeight, fullWidth, fullHeight,
            centerX, centerY, angle);
    }

    private void ApplyPreviewMediaTransform()
    {
        // A composed bitmap already contains every clip's transform. Applying
        // the selected source transform here would offset the entire frame.
        if (_renderedPreviewIsComposite && PreviewRenderedFrame.Visibility == Visibility.Visible)
        {
            PreviewRenderedFrame.Width = PreviewVisualLayer.Width;
            PreviewRenderedFrame.Height = PreviewVisualLayer.Height;
            PreviewRenderedFrame.Clip = null;
            PreviewRenderedFrame.RenderTransform = Transform.Identity;
            PreviewRenderedFrame.RenderTransformOrigin = new Point(.5, .5);
            PreviewRenderedFrame.Opacity = 1;
            Canvas.SetLeft(PreviewRenderedFrame, 0);
            Canvas.SetTop(PreviewRenderedFrame, 0);
            return;
        }
        TimelineClip? clip = _activePreviewVisualClip;
        if (clip is null || PreviewVisualLayer is null || PreviewOverlay is null ||
            _activePreviewVisualSourcePath != _previewSourcePath) return;
        var bounds = MediaBounds(clip);
        if (bounds is null) return;
        var b = bounds.Value;
        double currentTime = _project.Sequence.PlayheadSeconds;
        double positionX = Editor.ClipAnimation.Evaluate(clip, "PositionX", currentTime);
        double positionY = Editor.ClipAnimation.Evaluate(clip, "PositionY", currentTime);
        double scaleX = Editor.ClipAnimation.Evaluate(clip, "ScaleX", currentTime);
        double scaleY = Editor.ClipAnimation.Evaluate(clip, "ScaleY", currentTime);
        double rotation = Editor.ClipAnimation.Evaluate(clip, "RotationDegrees", currentTime);
        double opacity = Editor.ClipAnimation.Evaluate(clip, "Opacity", currentTime);
        string layoutKey = $"{clip.Id}:{clip.Kind}:{PreviewVisualLayer.Width:0.0}:{PreviewVisualLayer.Height:0.0}:" +
            $"{b.FullWidth:0.0}:{b.FullHeight:0.0}:{b.Left:0.00}:{b.Top:0.00}:{b.Width:0.00}:{b.Height:0.00}:" +
            $"{positionX:0.00}:{positionY:0.00}:{clip.Transform.AnchorX:0.000}:{clip.Transform.AnchorY:0.000}:{scaleX:0.000}:" +
            $"{scaleY:0.000}:{rotation:0.00}:{opacity:0.000}";
        double centerLocalX = b.Left + b.Width * Math.Clamp(clip.Transform.AnchorX, 0, 1);
        double centerLocalY = b.Top + b.Height * Math.Clamp(clip.Transform.AnchorY, 0, 1);
        // Keep the crop geometry in source coordinates. An authored anchor
        // changes the scale/rotation pivot while PositionX/Y place that pivot
        // at the requested sequence coordinate.
        FrameworkElement visual = PreviewRenderedFrame.Visibility == Visibility.Visible
            ? PreviewRenderedFrame : clip.Kind == ClipKind.Image ? PreviewImage : PreviewMediaElement;
        if (layoutKey == _lastMediaTransformKey && visual.Width == b.FullWidth && visual.Height == b.FullHeight &&
            Math.Abs(Canvas.GetLeft(visual) -
                (PreviewVisualLayer.Width / 2 + positionX * _previewTitleScale - centerLocalX)) < .01 &&
            Math.Abs(Canvas.GetTop(visual) -
                (PreviewVisualLayer.Height / 2 + positionY * _previewTitleScale - centerLocalY)) < .01) return;
        _lastMediaTransformKey = layoutKey;
        visual.Width = b.FullWidth;
        visual.Height = b.FullHeight;
        var cropClip = new RectangleGeometry(new Rect(b.Left, b.Top, b.Width, b.Height));
        cropClip.Freeze();
        visual.Clip = cropClip;
        visual.RenderTransformOrigin = new Point(centerLocalX / b.FullWidth, centerLocalY / b.FullHeight);
        visual.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection
            {
                new ScaleTransform(Math.Clamp(scaleX, .05, 8), Math.Clamp(scaleY, .05, 8)),
                new RotateTransform(rotation)
            }
        };
        visual.Opacity = Math.Clamp(opacity, 0, 1);
        Canvas.SetLeft(visual, PreviewVisualLayer.Width / 2 + positionX * _previewTitleScale - centerLocalX);
        Canvas.SetTop(visual, PreviewVisualLayer.Height / 2 + positionY * _previewTitleScale - centerLocalY);
        // Direct monitor gestures update the animated property and the
        // element transform in the same input tick. Do not wait for a later
        // layout pass before the image/video follows the pointer.
        visual.UpdateLayout();
    }

    private string? _lastMediaTransformKey;

    private TimelineClip? InteractiveMediaClip()
    {
        double now = _project.Sequence.PlayheadSeconds;
        if (_renderedPreviewClipId == "composite" &&
            _selectedClip is { Kind: ClipKind.Image or ClipKind.Video } selected &&
            _project.Tracks.Any(track => !track.IsHidden && track.Kind != TrackKind.Audio &&
                track.Clips.Contains(selected)) &&
            now >= selected.StartSeconds && now < selected.StartSeconds + selected.DurationSeconds)
            return selected;
        return _activePreviewVisualClip;
    }

    private static Point RotatedPoint(double centerX, double centerY, double x, double y, double angle)
    {
        double radians = angle * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        return new Point(centerX + x * cosine - y * sine, centerY + x * sine + y * cosine);
    }

    private static Point UnrotatedOffset(Point point, double centerX, double centerY, double angle)
    {
        double radians = angle * Math.PI / 180;
        double cosine = Math.Cos(radians);
        double sine = Math.Sin(radians);
        double x = point.X - centerX;
        double y = point.Y - centerY;
        return new Point(x * cosine + y * sine, -x * sine + y * cosine);
    }

    private void DrawMediaSelectionOverlay()
    {
        foreach (UIElement visual in _mediaSelectionVisuals) PreviewOverlay.Children.Remove(visual);
        _mediaSelectionVisuals.Clear();
        TimelineClip? clip = InteractiveMediaClip();
        if (clip is null || !ReferenceEquals(clip, _selectedClip)) return;
        if (!CanInteractWithMediaClip(clip)) return;
        var bounds = MediaBounds(clip);
        if (bounds is null) return;
        var b = bounds.Value;
        double currentTime = _project.Sequence.PlayheadSeconds;
        double halfWidth = b.Width * Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleX", currentTime), .05, 8) / 2;
        double halfHeight = b.Height * Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleY", currentTime), .05, 8) / 2;
        if (halfWidth < 1 || halfHeight < 1) return;
        Point topLeft = RotatedPoint(b.CenterX, b.CenterY, -halfWidth, -halfHeight, b.Angle);
        Point topRight = RotatedPoint(b.CenterX, b.CenterY, halfWidth, -halfHeight, b.Angle);
        Point bottomRight = RotatedPoint(b.CenterX, b.CenterY, halfWidth, halfHeight, b.Angle);
        Point bottomLeft = RotatedPoint(b.CenterX, b.CenterY, -halfWidth, halfHeight, b.Angle);
        var outline = new Polygon
        {
            Points = new PointCollection { topLeft, topRight, bottomRight, bottomLeft },
            Stroke = BrushFromHex("#FF9957"),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        PreviewOverlay.Children.Add(outline);
        _mediaSelectionVisuals.Add(outline);
        // Show the authored pivot independently from the visible rectangle's
        // center. PositionX/Y place this pivot at the sequence center offset.
        double frameLeft = (PreviewOverlay.ActualWidth - PreviewVisualLayer.Width) / 2;
        double frameTop = (PreviewOverlay.ActualHeight - PreviewVisualLayer.Height) / 2;
        Point anchor = new(
            frameLeft + PreviewVisualLayer.Width / 2 +
                Editor.ClipAnimation.Evaluate(clip, "PositionX", currentTime) * _previewTitleScale,
            frameTop + PreviewVisualLayer.Height / 2 +
                Editor.ClipAnimation.Evaluate(clip, "PositionY", currentTime) * _previewTitleScale);
        double pivotSize = Math.Clamp(Math.Min(PreviewVisualLayer.Width, PreviewVisualLayer.Height) * .013, 6, 11);

        // Never show pivot crosshair for text/graphic clips
        if (clip.Kind != ClipKind.Text)
        {
            var pivotHorizontal = new Line
            {
                X1 = anchor.X - pivotSize, Y1 = anchor.Y, X2 = anchor.X + pivotSize, Y2 = anchor.Y,
                Stroke = BrushFromHex("#54D4C0"), StrokeThickness = 1.5, IsHitTestVisible = false
            };
            var pivotVertical = new Line
            {
                X1 = anchor.X, Y1 = anchor.Y - pivotSize, X2 = anchor.X, Y2 = anchor.Y + pivotSize,
                Stroke = BrushFromHex("#54D4C0"), StrokeThickness = 1.5, IsHitTestVisible = false
            };
            PreviewOverlay.Children.Add(pivotHorizontal);
            PreviewOverlay.Children.Add(pivotVertical);
            _mediaSelectionVisuals.Add(pivotHorizontal);
            _mediaSelectionVisuals.Add(pivotVertical);
        }

        if (_mediaCropMode)
            AddMediaHandle(anchor, pivotSize * 1.45, BrushFromHex("#54D4C0"),
                Cursors.SizeAll, "拖动锚点；画面会绕此处旋转和缩放");
        double handleSize = Math.Clamp(Math.Min(PreviewVisualLayer.Width, PreviewVisualLayer.Height) * .022, 8, 15);
        AddMediaHandle(topLeft, handleSize, Brushes.White, Cursors.SizeNWSE, "拖动角点可拉伸画面");
        AddMediaHandle(topRight, handleSize, Brushes.White, Cursors.SizeNESW, "拖动角点可拉伸画面");
        AddMediaHandle(bottomRight, handleSize, Brushes.White, Cursors.SizeNWSE, "拖动角点可拉伸画面");
        AddMediaHandle(bottomLeft, handleSize, Brushes.White, Cursors.SizeNESW, "拖动角点可拉伸画面");
        if (_mediaCropMode)
        {
            AddMediaHandle(Midpoint(topLeft, bottomLeft), handleSize, BrushFromHex("#54D4C0"), Cursors.SizeWE, "拖动左边裁切");
            AddMediaHandle(Midpoint(topLeft, topRight), handleSize, BrushFromHex("#54D4C0"), Cursors.SizeNS, "拖动上边裁切");
            AddMediaHandle(Midpoint(topRight, bottomRight), handleSize, BrushFromHex("#54D4C0"), Cursors.SizeWE, "拖动右边裁切");
            AddMediaHandle(Midpoint(bottomLeft, bottomRight), handleSize, BrushFromHex("#54D4C0"), Cursors.SizeNS, "拖动下边裁切");
        }
        double rotateGap = Math.Max(handleSize * 2.5, PreviewVisualLayer.Height * .06);
        Point rotate = RotatedPoint(b.CenterX, b.CenterY, 0, -halfHeight - rotateGap, b.Angle);
        var stem = new Line
        {
            X1 = (topLeft.X + topRight.X) / 2,
            Y1 = (topLeft.Y + topRight.Y) / 2,
            X2 = rotate.X, Y2 = rotate.Y,
            Stroke = BrushFromHex("#FF9957"), StrokeThickness = 1,
            IsHitTestVisible = false
        };
        PreviewOverlay.Children.Add(stem);
        _mediaSelectionVisuals.Add(stem);
        AddMediaHandle(rotate, handleSize * 1.2, BrushFromHex("#FF9957"), Cursors.Hand, "拖动可任意角度旋转");
    }

    private void AddMediaHandle(Point center, double size, Brush fill, Cursor cursor, string tooltip)
    {
        var marker = new Ellipse
        {
            Width = size, Height = size, Fill = fill,
            Stroke = BrushFromHex("#22252D"), StrokeThickness = 1,
            Cursor = cursor, ToolTip = tooltip
        };
        Canvas.SetLeft(marker, center.X - size / 2);
        Canvas.SetTop(marker, center.Y - size / 2);
        PreviewOverlay.Children.Add(marker);
        _mediaSelectionVisuals.Add(marker);
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private bool CanInteractWithMediaClip(TimelineClip clip)
    {
        if (_renderedPreviewClipId == "composite")
            return PreviewRenderedFrame.Visibility == Visibility.Visible;
        if (_activePreviewVisualSourcePath != _previewSourcePath) return false;
        if (PreviewRenderedFrame.Visibility == Visibility.Visible &&
            _renderedPreviewClipId == clip.Id) return true;
        return clip.Kind switch
        {
            ClipKind.Image => PreviewImage.Source is not null && PreviewImage.Visibility == Visibility.Visible,
            ClipKind.Video => PreviewMediaElement.Source is not null && PreviewMediaElement.Visibility == Visibility.Visible,
            _ => false
        };
    }

    private bool TryMediaHandle(Point point, TimelineClip clip, out MediaHandleKind kind)
    {
        kind = MediaHandleKind.Move;
        var bounds = MediaBounds(clip);
        if (bounds is null) return false;
        var b = bounds.Value;
        double currentTime = _project.Sequence.PlayheadSeconds;
        double halfWidth = b.Width * Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleX", currentTime), .05, 8) / 2;
        double halfHeight = b.Height * Math.Clamp(Editor.ClipAnimation.Evaluate(clip, "ScaleY", currentTime), .05, 8) / 2;
        Point local = UnrotatedOffset(point, b.CenterX, b.CenterY, b.Angle);
        double tolerance = Math.Clamp(Math.Min(PreviewVisualLayer.Width, PreviewVisualLayer.Height) * .025, 9, 17);
        if (ReferenceEquals(clip, _selectedClip))
        {
            if (_mediaCropMode)
            {
                double frameLeft = (PreviewOverlay.ActualWidth - PreviewVisualLayer.Width) / 2;
                double frameTop = (PreviewOverlay.ActualHeight - PreviewVisualLayer.Height) / 2;
                double anchorX = frameLeft + PreviewVisualLayer.Width / 2 +
                    Editor.ClipAnimation.Evaluate(clip, "PositionX", currentTime) * _previewTitleScale;
                double anchorY = frameTop + PreviewVisualLayer.Height / 2 +
                    Editor.ClipAnimation.Evaluate(clip, "PositionY", currentTime) * _previewTitleScale;
                if (Math.Abs(point.X - anchorX) <= tolerance &&
                    Math.Abs(point.Y - anchorY) <= tolerance)
                { kind = MediaHandleKind.Anchor; return true; }
            }
            double rotateGap = Math.Max(tolerance * 2.5, PreviewVisualLayer.Height * .06);
            if (Math.Abs(local.X) <= tolerance && Math.Abs(local.Y + halfHeight + rotateGap) <= tolerance)
            { kind = MediaHandleKind.Rotate; return true; }
            (MediaHandleKind Kind, double X, double Y)[] corners =
            [
                (MediaHandleKind.ScaleTopLeft, -halfWidth, -halfHeight),
                (MediaHandleKind.ScaleTopRight, halfWidth, -halfHeight),
                (MediaHandleKind.ScaleBottomRight, halfWidth, halfHeight),
                (MediaHandleKind.ScaleBottomLeft, -halfWidth, halfHeight)
            ];
            foreach (var corner in corners)
            {
                if (Math.Abs(local.X - corner.X) <= tolerance && Math.Abs(local.Y - corner.Y) <= tolerance)
                { kind = corner.Kind; return true; }
            }
            if (_mediaCropMode)
            {
                if (Math.Abs(local.X + halfWidth) <= tolerance && Math.Abs(local.Y) <= tolerance)
                { kind = MediaHandleKind.CropLeft; return true; }
                if (Math.Abs(local.X) <= tolerance && Math.Abs(local.Y + halfHeight) <= tolerance)
                { kind = MediaHandleKind.CropTop; return true; }
                if (Math.Abs(local.X - halfWidth) <= tolerance && Math.Abs(local.Y) <= tolerance)
                { kind = MediaHandleKind.CropRight; return true; }
                if (Math.Abs(local.X) <= tolerance && Math.Abs(local.Y - halfHeight) <= tolerance)
                { kind = MediaHandleKind.CropBottom; return true; }
            }
        }
        return Math.Abs(local.X) <= halfWidth && Math.Abs(local.Y) <= halfHeight;
    }

    private void StartMediaDrag(MouseButtonEventArgs args)
    {
        TimelineClip? clip = InteractiveMediaClip();
        if (clip is null || !CanInteractWithMediaClip(clip)) return;
        Point point = args.GetPosition(PreviewOverlay);
        if (!TryMediaHandle(point, clip, out MediaHandleKind kind)) return;
        var bounds = MediaBounds(clip);
        if (bounds is null) return;
        _selectedClip = clip;
        _selectedTrack = _project.Tracks.FirstOrDefault(item => item.Clips.Contains(clip));
        _selectedClips.Clear();
        _selectedClips.Add(clip);
        DrawTimeline();
        _lastTitleLayoutKey = null;
        DrawTextOverlay();
        if (_selectedTrack?.IsLocked == true)
        {
            StatusText.Text = "轨道已锁定，无法编辑画面";
            args.Handled = true;
            return;
        }
        if (_isPlaying) TogglePlayback();
        _draggingMediaClip = clip;
        _mediaDraggingComposite = _renderedPreviewClipId == "composite";
        _mediaDragKind = kind;
        _mediaDragStart = point;
        _mediaOriginalCropLeft = clip.Transform.CropLeft;
        _mediaOriginalCropTop = clip.Transform.CropTop;
        _mediaOriginalCropRight = clip.Transform.CropRight;
        _mediaOriginalCropBottom = clip.Transform.CropBottom;
        double now = _project.Sequence.PlayheadSeconds;
        _mediaDisplayedStartX = Editor.ClipAnimation.Evaluate(clip, "PositionX", now);
        _mediaDisplayedStartY = Editor.ClipAnimation.Evaluate(clip, "PositionY", now);
        _mediaDisplayedStartScaleX = Editor.ClipAnimation.Evaluate(clip, "ScaleX", now);
        _mediaDisplayedStartScaleY = Editor.ClipAnimation.Evaluate(clip, "ScaleY", now);
        _mediaDisplayedStartRotation = Editor.ClipAnimation.Evaluate(clip, "RotationDegrees", now);
        // Use the exact rendered corner as the drag origin. The pointer can
        // land anywhere inside the hit ellipse; using that raw hit position
        // makes the corner visibly lag behind the cursor by the click offset.
        if (kind is MediaHandleKind.ScaleTopLeft or MediaHandleKind.ScaleTopRight or
            MediaHandleKind.ScaleBottomRight or MediaHandleKind.ScaleBottomLeft)
        {
            double halfWidth = bounds.Value.Width * Math.Clamp(_mediaDisplayedStartScaleX, .05, 8) / 2;
            double halfHeight = bounds.Value.Height * Math.Clamp(_mediaDisplayedStartScaleY, .05, 8) / 2;
            (double X, double Y) localCorner = kind switch
            {
                MediaHandleKind.ScaleTopLeft => (-halfWidth, -halfHeight),
                MediaHandleKind.ScaleTopRight => (halfWidth, -halfHeight),
                MediaHandleKind.ScaleBottomRight => (halfWidth, halfHeight),
                _ => (-halfWidth, halfHeight)
            };
            _mediaDragStart = RotatedPoint(bounds.Value.CenterX, bounds.Value.CenterY,
                localCorner.X, localCorner.Y, _mediaDisplayedStartRotation);
        }
        _mediaOriginalFullWidth = bounds.Value.FullWidth;
        _mediaOriginalFullHeight = bounds.Value.FullHeight;
        _mediaOriginalCropWidth = bounds.Value.Width;
        _mediaOriginalCropHeight = bounds.Value.Height;
        _mediaDragAnchorX = clip.Transform.AnchorX;
        _mediaDragAnchorY = clip.Transform.AnchorY;
        _mediaDragCenterX = bounds.Value.CenterX;
        _mediaDragCenterY = bounds.Value.CenterY;
        _rotationLastPointerDegrees = Math.Atan2(point.Y - bounds.Value.CenterY, point.X - bounds.Value.CenterX) * 180 / Math.PI;
        _rotationAccumulatedDegrees = 0;
        _mediaDragChanged = false;
        if (!PreviewOverlay.CaptureMouse())
        {
            _draggingMediaClip = null;
            _mediaDraggingComposite = false;
            StatusText.Text = "无法捕获鼠标，画面拖动未开始 · PREVIEW-INPUT";
            return;
        }
        args.Handled = true;
    }

    private void MoveMediaDrag(MouseEventArgs args)
    {
        TimelineClip? clip = _draggingMediaClip;
        if (clip is null || args.LeftButton != MouseButtonState.Pressed) return;
        Point point = args.GetPosition(PreviewOverlay);
        Vector delta = point - _mediaDragStart;
        if (!_mediaDragChanged && Math.Abs(delta.X) + Math.Abs(delta.Y) < 2) return;
        if (!_mediaDragChanged) { SaveUndoState(); _mediaDragChanged = true; }
        ClipTransform t = clip.Transform;
        double radians = _mediaDisplayedStartRotation * Math.PI / 180;
        double localDx = delta.X * Math.Cos(radians) + delta.Y * Math.Sin(radians);
        double localDy = -delta.X * Math.Sin(radians) + delta.Y * Math.Cos(radians);
        switch (_mediaDragKind)
        {
            case MediaHandleKind.Move:
                ShiftAnimatedVisualProperty(clip, "PositionX",
                    Math.Clamp(_mediaDisplayedStartX + delta.X / Math.Max(.001, _previewTitleScale),
                        -_project.Sequence.Width * 4, _project.Sequence.Width * 4));
                ShiftAnimatedVisualProperty(clip, "PositionY",
                    Math.Clamp(_mediaDisplayedStartY + delta.Y / Math.Max(.001, _previewTitleScale),
                        -_project.Sequence.Height * 4, _project.Sequence.Height * 4));
                break;
            case MediaHandleKind.Rotate:
                double pointerAngle = Math.Atan2(point.Y - _mediaDragCenterY, point.X - _mediaDragCenterX) * 180 / Math.PI;
                double change = pointerAngle - _rotationLastPointerDegrees;
                if (change > 180) change -= 360;
                if (change < -180) change += 360;
                _rotationAccumulatedDegrees += change;
                ShiftAnimatedVisualProperty(clip, "RotationDegrees",
                    Math.Clamp(_mediaDisplayedStartRotation + _rotationAccumulatedDegrees, -36000, 36000));
                _rotationLastPointerDegrees = pointerAngle;
                break;
            case MediaHandleKind.Anchor:
                double anchorHalfWidth = Math.Max(1, _mediaOriginalCropWidth * _mediaDisplayedStartScaleX);
                double anchorHalfHeight = Math.Max(1, _mediaOriginalCropHeight * _mediaDisplayedStartScaleY);
                // Move the pivot inside the displayed rectangle while
                // compensating Position so the image itself stays in place.
                double newAnchorX = Math.Clamp(_mediaDragAnchorX + localDx / anchorHalfWidth, 0, 1);
                double newAnchorY = Math.Clamp(_mediaDragAnchorY + localDy / anchorHalfHeight, 0, 1);
                double anchorDx = (newAnchorX - t.AnchorX) * anchorHalfWidth;
                double anchorDy = (newAnchorY - t.AnchorY) * anchorHalfHeight;
                Point anchorShift = RotatedPoint(0, 0, anchorDx, anchorDy, _mediaDisplayedStartRotation);
                t.AnchorX = newAnchorX;
                t.AnchorY = newAnchorY;
                ShiftAnimatedVisualProperty(clip, "PositionX",
                    Editor.ClipAnimation.Evaluate(clip, "PositionX", _project.Sequence.PlayheadSeconds) +
                    anchorShift.X / Math.Max(.001, _previewTitleScale));
                ShiftAnimatedVisualProperty(clip, "PositionY",
                    Editor.ClipAnimation.Evaluate(clip, "PositionY", _project.Sequence.PlayheadSeconds) +
                    anchorShift.Y / Math.Max(.001, _previewTitleScale));
                break;
            case MediaHandleKind.ScaleTopLeft:
            case MediaHandleKind.ScaleTopRight:
            case MediaHandleKind.ScaleBottomRight:
            case MediaHandleKind.ScaleBottomLeft:
                double xSign = _mediaDragKind is MediaHandleKind.ScaleTopLeft or MediaHandleKind.ScaleBottomLeft ? -1 : 1;
                double ySign = _mediaDragKind is MediaHandleKind.ScaleTopLeft or MediaHandleKind.ScaleTopRight ? -1 : 1;
                double newScaleX = Math.Clamp(
                    _mediaDisplayedStartScaleX + xSign * localDx / Math.Max(1, _mediaOriginalCropWidth), .05, 8);
                double newScaleY = Math.Clamp(
                    _mediaDisplayedStartScaleY + ySign * localDy / Math.Max(1, _mediaOriginalCropHeight), .05, 8);
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    // Ctrl locks the corner drag to a uniform scale. Preserve
                    // the current X/Y scale ratio while using the axis that
                    // moved farther as the requested uniform factor.
                    double ratioX = newScaleX / Math.Max(.0001, _mediaDisplayedStartScaleX);
                    double ratioY = newScaleY / Math.Max(.0001, _mediaDisplayedStartScaleY);
                    double ratio = Math.Abs(ratioX - 1) >= Math.Abs(ratioY - 1) ? ratioX : ratioY;
                    ratio = Math.Clamp(ratio, .05 / Math.Max(.0001, _mediaDisplayedStartScaleX),
                        8 / Math.Max(.0001, _mediaDisplayedStartScaleX));
                    newScaleX = Math.Clamp(_mediaDisplayedStartScaleX * ratio, .05, 8);
                    newScaleY = Math.Clamp(_mediaDisplayedStartScaleY * ratio, .05, 8);
                }
                ShiftAnimatedVisualProperty(clip, "ScaleX", newScaleX);
                ShiftAnimatedVisualProperty(clip, "ScaleY", newScaleY);
                // Position places the anchor, not the center (see MediaBounds),
                // so the picture grows outward from the anchor and the corner
                // opposite the pointer drifts. Pin that opposite corner by
                // shifting Position against the change in its own offset from
                // the anchor. Dragging right pins the left edge, whose offset
                // is -AnchorX of the width, and the reverse for a left drag.
                double pinnedFractionX = xSign > 0
                    ? -Math.Clamp(t.AnchorX, 0, 1)
                    : 1 - Math.Clamp(t.AnchorX, 0, 1);
                double pinnedFractionY = ySign > 0
                    ? -Math.Clamp(t.AnchorY, 0, 1)
                    : 1 - Math.Clamp(t.AnchorY, 0, 1);
                double pinnedShiftX = -_mediaOriginalCropWidth *
                    (newScaleX - _mediaDisplayedStartScaleX) * pinnedFractionX;
                double pinnedShiftY = -_mediaOriginalCropHeight *
                    (newScaleY - _mediaDisplayedStartScaleY) * pinnedFractionY;
                Point cornerShift = RotatedPoint(0, 0, pinnedShiftX, pinnedShiftY,
                    _mediaDisplayedStartRotation);
                ShiftAnimatedVisualProperty(clip, "PositionX",
                    _mediaDisplayedStartX + cornerShift.X / Math.Max(.001, _previewTitleScale));
                ShiftAnimatedVisualProperty(clip, "PositionY",
                    _mediaDisplayedStartY + cornerShift.Y / Math.Max(.001, _previewTitleScale));
                break;
            case MediaHandleKind.CropLeft:
                t.CropLeft = Math.Clamp(_mediaOriginalCropLeft + localDx / Math.Max(1, _mediaOriginalFullWidth * _mediaDisplayedStartScaleX),
                    0, Math.Min(.95, .99 - _mediaOriginalCropRight));
                break;
            case MediaHandleKind.CropRight:
                t.CropRight = Math.Clamp(_mediaOriginalCropRight - localDx / Math.Max(1, _mediaOriginalFullWidth * _mediaDisplayedStartScaleX),
                    0, Math.Min(.95, .99 - _mediaOriginalCropLeft));
                break;
            case MediaHandleKind.CropTop:
                t.CropTop = Math.Clamp(_mediaOriginalCropTop + localDy / Math.Max(1, _mediaOriginalFullHeight * _mediaDisplayedStartScaleY),
                    0, Math.Min(.95, .99 - _mediaOriginalCropBottom));
                break;
            case MediaHandleKind.CropBottom:
                t.CropBottom = Math.Clamp(_mediaOriginalCropBottom - localDy / Math.Max(1, _mediaOriginalFullHeight * _mediaDisplayedStartScaleY),
                    0, Math.Min(.95, .99 - _mediaOriginalCropTop));
                break;
        }
        ApplyPreviewMediaTransform();
        DrawMediaSelectionOverlay();
        if (_mediaDraggingComposite) QueueCompositeDragFrame();
        args.Handled = true;
    }

    private void QueueCompositeDragFrame()
    {
        // A composite bitmap contains all tracks. Repositioning that bitmap
        // would move unrelated clips, so request a fresh frame on a short
        // idle interval while keeping the previous frame responsive.
        if (_compositeDragRenderTimer is null)
        {
            // Keep composite image gestures responsive. The timer coalesces
            // bursts to one UI render tick without making the pointer visibly
            // lag behind the image.
            _compositeDragRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _compositeDragRenderTimer.Tick += (_, _) =>
            {
                _compositeDragRenderTimer.Stop();
                if (_draggingMediaClip is null || !_mediaDraggingComposite) return;
                _renderedPreviewKey = null;
                ScheduleRenderedPreviewFrame(_draggingMediaClip);
            };
        }
        _compositeDragRenderTimer.Stop();
        _compositeDragRenderTimer.Start();
    }

    /// <summary>
    /// A direct monitor gesture translates the authored animation curve as a
    /// whole. Changing only the base value would have no visible effect at a
    /// keyed playhead and would jump back as soon as the next frame is drawn.
    /// </summary>
    private double ShiftAnimatedVisualProperty(TimelineClip clip, string property,
        double desiredDisplayed)
    {
        double current = Editor.ClipAnimation.Evaluate(clip, property, _project.Sequence.PlayheadSeconds);
        double shift = desiredDisplayed - current;
        if (Math.Abs(shift) < .0000001) return 0;
        double minimum = property is "ScaleX" or "ScaleY" ? .05 :
            property == "Opacity" ? 0 :
            property == "RotationDegrees" ? -36000 : -_project.Sequence.Width * 4;
        double maximum = property is "ScaleX" or "ScaleY" ? 8 :
            property == "Opacity" ? 1 :
            property == "RotationDegrees" ? 36000 :
            property == "PositionY" ? _project.Sequence.Height * 4 : _project.Sequence.Width * 4;
        if (property == "PositionY") minimum = -_project.Sequence.Height * 4;
        // Restrict the gesture by the whole authored path. Clamping each key
        // separately would flatten peaks and change the animation shape.
        double baseValue = Editor.ClipAnimation.BaseValue(clip, property);
        ClipKeyframe[] keys = clip.Keyframes.Where(item => item.Property == property &&
            double.IsFinite(item.Value)).ToArray();
        double low = Math.Min(baseValue, keys.Select(item => item.Value).DefaultIfEmpty(baseValue).Min());
        double high = Math.Max(baseValue, keys.Select(item => item.Value).DefaultIfEmpty(baseValue).Max());
        double minimumShift = minimum - low;
        double maximumShift = maximum - high;
        if (minimumShift <= maximumShift)
            shift = Math.Clamp(shift, minimumShift, maximumShift);
        else
        {
            // An old project can already contain a wider animation range.
            // Preserve its shape while clamping the current visible value.
            shift = Math.Clamp(shift, minimum - current, maximum - current);
        }
        if (Math.Abs(shift) < .0000001) return 0;
        foreach (ClipKeyframe key in keys) key.Value += shift;
        switch (property)
        {
            case "PositionX": clip.Transform.PositionX += shift; break;
            case "PositionY": clip.Transform.PositionY += shift; break;
            case "ScaleX": clip.Transform.ScaleX += shift; break;
            case "ScaleY": clip.Transform.ScaleY += shift; break;
            case "RotationDegrees": clip.Transform.RotationDegrees += shift; break;
            case "Opacity": clip.Opacity += shift; break;
        }
        return shift;
    }

    private void EndMediaDrag(MouseButtonEventArgs args)
    {
        if (_draggingMediaClip is null) return;
        PreviewOverlay.ReleaseMouseCapture();
        FinishMediaDrag();
        args.Handled = true;
    }

    private void FinishMediaDrag()
    {
        TimelineClip? clip = _draggingMediaClip;
        if (clip is null) return;
        bool changed = _mediaDragChanged;
        _compositeDragRenderTimer?.Stop();
        _draggingMediaClip = null;
        _mediaDraggingComposite = false;
        _mediaDragChanged = false;
        if (!changed) return;
        MarkProjectDirty();
        StatusText.Text = $"已调整画面：{clip.Name}";
        _lastTitleLayoutKey = null;
        SeekPreview(_project.Sequence.PlayheadSeconds);
    }
}
