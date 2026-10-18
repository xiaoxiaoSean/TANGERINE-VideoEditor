using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>Derives responsive control dimensions from their available parent width.</summary>
public sealed class ProportionalSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double size && double.IsFinite(size) ? Math.Max(0, size * double.Parse(parameter.ToString()!, CultureInfo.InvariantCulture)) : 0.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class MainWindow
{
    private readonly CancellationTokenSource _experienceLifetime = new();

    private void InitializePreviewExperience()
    {
        RenderOptions.SetBitmapScalingMode(PreviewRenderedFrame, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(PreviewImage, BitmapScalingMode.HighQuality);
        PreviewZoomSlider.ValueChanged += (_, _) =>
        {
            double zoom = PreviewZoomSlider.Value;
            PreviewStage.RenderTransform = new ScaleTransform(zoom, zoom);
            PreviewZoomText.Text = $"视图 {zoom:P0}";
            InvalidateCompositedPreview();
            InvalidateIdlePreview();
        };
        PreviewZoomResetButton.Click += (_, _) => PreviewZoomSlider.Value = 1;
        MediaViewBox.ItemsSource = new[] { "列表", "小缩略图", "大缩略图" };
        MediaViewBox.SelectionChanged += (_, _) => ApplyMediaView();
        MediaViewBox.SelectedIndex = 1;
        AssetList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => LoadVisibleLibraryThumbnails()));
        AssetList.SizeChanged += (_, _) => LoadVisibleLibraryThumbnails();
        PreviewVisualLayer.SizeChanged += (_, _) =>
        {
            InvalidateIdlePreview();
            _renderedPreviewKey = null;
            ScheduleRenderedPreviewFrame(_activePreviewVisualClip);
        };
        Closed += (_, _) => _experienceLifetime.Cancel();
        InitializeIdlePreview();
    }

    private void ApplyMediaView()
    {
        bool media = CategoryList.SelectedIndex == 0;
        bool list = !media || MediaViewBox.SelectedIndex == 0;
        MediaViewBox.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(AssetSearchBox, media ? 1 : 2);
        AssetList.ItemTemplate = (DataTemplate)FindResource(!media ? "CatalogListTemplate" : list ? "MediaListTemplate" : "MediaThumbnailTemplate");
        var factory = new FrameworkElementFactory(typeof(UniformGrid));
        factory.SetValue(UniformGrid.ColumnsProperty, list ? 1 : MediaViewBox.SelectedIndex == 1 ? 3 : 2);
        AssetList.ItemsPanel = new ItemsPanelTemplate(factory);
    }

    private void LoadLibraryThumbnails(IEnumerable<LibraryItem> items)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(LoadVisibleLibraryThumbnails));
    }

    private readonly HashSet<LibraryItem> _thumbnailLoads = new(ReferenceEqualityComparer.Instance);
    private void LoadVisibleLibraryThumbnails()
    {
        foreach (LibraryItem item in AssetList.Items)
        {
            if (item.Thumbnail is not null || _thumbnailLoads.Contains(item) || item.Source is not MediaAsset asset || asset.Kind == MediaKind.Audio) continue;
            if (AssetList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible) continue;
            Rect bounds = container.TransformToAncestor(AssetList).TransformBounds(new Rect(container.RenderSize));
            if (bounds.Bottom < 0 || bounds.Top > AssetList.ActualHeight) continue;
            _thumbnailLoads.Add(item); _ = LoadAsync(item, asset);
        }
        async Task LoadAsync(LibraryItem item, MediaAsset asset)
        {
            try { item.SetThumbnail(await MediaThumbnailCache.GetAsync(asset, 0, _experienceLifetime.Token)); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"THUMBNAIL-LIBRARY: {exception}"); }
            finally { _thumbnailLoads.Remove(item); }
        }
    }

    private void AddTimelineThumbnails(Border border, TimelineClip clip)
    {
        if (clip.Kind is not (ClipKind.Image or ClipKind.Video)) return;
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (asset is null) return;
        var caption = (UIElement)border.Child; border.Child = null;
        if (caption is FrameworkElement text) text.Margin = new Thickness(TrackSpace * .08, 0, TrackSpace * .08, 0);
        var content = new Grid { ClipToBounds = true };
        content.RowDefinitions.Add(new() { Height = new GridLength(35, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = new GridLength(65, GridUnitType.Star) });
        var strip = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
        Grid.SetRow(strip, 1); content.Children.Add(strip); content.Children.Add(caption); border.Child = content;
        // Only the viewport gets thumbnail requests. Absolute clip positions keep
        // samples stable when scrolling and avoid decoding offscreen media.
        double left = Math.Max(0, TimelineScrollViewer.HorizontalOffset - Canvas.GetLeft(border));
        double right = Math.Min(border.Width, left + TimelineScrollViewer.ViewportWidth);
        double step = Math.Max(1, (TrackSpace - 13) * .65 * 16 / 9);
        for (double x = Math.Floor(left / step) * step; x < right; x += step)
        {
            var image = new Image { Width = step, Height = (TrackSpace - 13) * .65, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            Canvas.SetLeft(image, x); strip.Children.Add(image);
            double source = clip.Kind == ClipKind.Image ? 0 : ClipTimeMap.SourceTime(clip,
                clip.StartSeconds + Math.Clamp(x / PixelsPerSecond, 0, clip.DurationSeconds));
            _ = LoadAsync(image, asset, source);
        }
        async Task LoadAsync(Image image, MediaAsset source, double seconds)
        {
            try { image.Source = await MediaThumbnailCache.GetAsync(source, seconds, _experienceLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"THUMBNAIL-TIMELINE: {exception}"); }
        }
    }
}

