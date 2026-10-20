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
        bool media = CategoryList.SelectedIndex is 0 or 5;
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
            if (item.Thumbnail is not null || _thumbnailLoads.Contains(item)) continue;
            if (AssetList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container || !container.IsVisible) continue;
            Rect bounds = container.TransformToAncestor(AssetList).TransformBounds(new Rect(container.RenderSize));
            if (bounds.Bottom < 0 || bounds.Top > AssetList.ActualHeight) continue;
            if (item.Source is MediaAsset asset && asset.Kind != MediaKind.Audio)
            {
                _thumbnailLoads.Add(item); _ = LoadAsync(item, asset);
            }
            else if (item.Source is BuiltinPreset preset)
            {
                _thumbnailLoads.Add(item); _ = LoadCatalogAsync(item, preset);
            }
            else if (item.Source is BuiltinMusicTrack music)
            {
                _thumbnailLoads.Add(item); _ = LoadMusicAsync(item, music);
            }
            else if (item.Source is MediaAsset audio && audio.Kind == MediaKind.Audio &&
                audio.Metadata.TryGetValue("BuiltinMusicId", out string? musicId))
            {
                try
                {
                    if (BuiltinMusicCatalog.Find(musicId) is BuiltinMusicTrack bundled)
                    {
                        _thumbnailLoads.Add(item); _ = LoadMusicAsync(item, bundled);
                    }
                }
                catch (Tools.StageException exception) { System.Diagnostics.Debug.WriteLine($"MUSIC0005: {exception}"); }
            }
        }
        async Task LoadAsync(LibraryItem item, MediaAsset asset)
        {
            ProjectDocument owner = _project;
            long generation = _projectGeneration;
            string requestedPath = asset.EffectivePreviewPath;
            try
            {
                BitmapSource thumbnail = await MediaThumbnailCache.GetAsync(asset, 0, _experienceLifetime.Token);
                if (!_experienceLifetime.IsCancellationRequested && ReferenceEquals(owner, _project) &&
                    generation == _projectGeneration && owner.Media.Contains(asset) &&
                    AssetList.Items.Contains(item) && requestedPath == asset.EffectivePreviewPath)
                    item.SetThumbnail(thumbnail);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"THUMB0001: {exception}"); }
            finally
            {
                _thumbnailLoads.Remove(item);
                if (!_experienceLifetime.IsCancellationRequested && ReferenceEquals(owner, _project) &&
                    generation == _projectGeneration && AssetList.Items.Contains(item) &&
                    requestedPath != asset.EffectivePreviewPath)
                    LoadLibraryThumbnails([item]);
            }
        }
        async Task LoadCatalogAsync(LibraryItem item, BuiltinPreset preset)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource? thumbnail = await Task.Run(
                    () => LoadCatalogThumbnail(preset.Id), _experienceLifetime.Token);
                if (_experienceLifetime.IsCancellationRequested || !AssetList.Items.Contains(item)) return;
                if (thumbnail is not null) item.SetThumbnail(thumbnail);
                else System.Diagnostics.Debug.WriteLine($"THUMB0003: 缺少内置缩略图 {preset.Id}");
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"THUMB0004: {preset.Id}: {exception}"); }
            finally { _thumbnailLoads.Remove(item); }
        }
        async Task LoadMusicAsync(LibraryItem item, BuiltinMusicTrack music)
        {
            try
            {
                BitmapSource? thumbnail = await Task.Run(() => LoadBundledThumbnail(music.ThumbnailPath),
                    _experienceLifetime.Token);
                if (!_experienceLifetime.IsCancellationRequested && AssetList.Items.Contains(item) && thumbnail is not null)
                    item.SetThumbnail(thumbnail);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"MUSIC0005: {music.Id}: {exception}"); }
            finally { _thumbnailLoads.Remove(item); }
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource? LoadCatalogThumbnail(string presetId)
    {
        string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "CatalogThumbnails", presetId + ".jpg");
        return LoadBundledThumbnail(path);
    }

    private static BitmapSource? LoadBundledThumbnail(string path)
    {
        if (_bundledThumbnailCache.TryGetValue(path, out BitmapSource? cached)) return cached;
        if (!System.IO.File.Exists(path)) return null;
        using System.IO.FileStream stream = System.IO.File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 240;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return _bundledThumbnailCache.GetOrAdd(path, image);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _bundledThumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);

    private void AddTimelineThumbnails(Border border, TimelineClip clip)
    {
        MediaAsset? asset = _project.Media.FirstOrDefault(item => item.Id == clip.MediaAssetId);
        if (asset is null) return;
        string? musicArtwork = null;
        if (clip.Kind == ClipKind.Audio && asset.Metadata.TryGetValue("BuiltinMusicId", out string? musicId))
        {
            try { musicArtwork = BuiltinMusicCatalog.Find(musicId)?.ThumbnailPath; }
            catch (Tools.StageException exception) { System.Diagnostics.Debug.WriteLine($"MUSIC0005: {exception}"); }
        }
        if (clip.Kind is not (ClipKind.Image or ClipKind.Video) && musicArtwork is null) return;
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
            ProjectDocument owner = _project;
            long generation = _projectGeneration;
            string requestedPath = source.EffectivePreviewPath;
            try
            {
                BitmapSource? thumbnail = musicArtwork is not null
                    ? await Task.Run(() => LoadBundledThumbnail(musicArtwork), _experienceLifetime.Token)
                    : await MediaThumbnailCache.GetAsync(source, seconds, _experienceLifetime.Token);
                if (!_experienceLifetime.IsCancellationRequested && ReferenceEquals(owner, _project) &&
                    generation == _projectGeneration && owner.Tracks.Any(track => track.Clips.Contains(clip)) &&
                    clip.MediaAssetId == source.Id && owner.Media.Contains(source) && requestedPath == source.EffectivePreviewPath)
                    image.Source = thumbnail;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"THUMB0002: {exception}"); }
        }
    }
}

