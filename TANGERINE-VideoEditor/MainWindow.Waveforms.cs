using System.Windows.Media;
using System.Windows.Shapes;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private readonly Dictionary<string, float[]> _waveformWindows = [];
    private readonly HashSet<string> _waveformPending = [];
    private readonly HashSet<string> _waveformFailed = [];
    private void DrawRealWaveform(TimelineClip clip, MediaAsset asset, double left, double top, double width)
    {
        double visibleLeft = Math.Max(left, TimelineScrollViewer.HorizontalOffset);
        double visibleRight = Math.Min(left + width, TimelineScrollViewer.HorizontalOffset + TimelineScrollViewer.ViewportWidth);
        double center = top + (TrackSpace - 24) * .46;
        double amplitude = (TrackSpace - 27) * .45;
        string stamp = $"{asset.Id}:{asset.EffectiveExportPath}:{asset.FileModifiedUtc}:{clip.SourceAudioStreamIndex}";
        for (double x = visibleLeft; x < visibleRight; x += 3)
        {
            double local = Math.Clamp((x - left) / PixelsPerSecond, 0, clip.DurationSeconds);
            double end = Math.Min(clip.DurationSeconds, local + 3 / PixelsPerSecond);
            double a = ClipTimeMap.SourceTime(clip, clip.StartSeconds + local);
            double b = ClipTimeMap.SourceTime(clip, clip.StartSeconds + end);
            double low = Math.Min(a, b), high = Math.Max(a, b);
            float peak = 0;
            for (int w = (int)(low / 30); w <= (int)(high / 30); w++)
            {
                string key = stamp + ":" + w;
                if (!_waveformWindows.TryGetValue(key, out var peaks))
                {
                    if (!_waveformPending.Contains(key) && !_waveformFailed.Contains(key))
                    { _waveformPending.Add(key); _ = LoadAsync(key, w); }
                    continue;
                }
                int from = Math.Max(0, (int)((low - w * 30) * 100));
                int to = Math.Min(peaks.Length - 1, (int)Math.Ceiling((high - w * 30) * 100));
                for (int i = from; i <= to; i++) peak = Math.Max(peak, peaks[i]);
            }
            TimelineCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = center - amplitude * Math.Min(1, peak), Y2 = center + amplitude * Math.Min(1, peak),
                Stroke = Brush("#A7D8D2"), StrokeThickness = 1, IsHitTestVisible = false, Opacity = .8 });
        }
        async Task LoadAsync(string key, int window)
        {
            try { _waveformWindows[key] = await AudioPeakCache.GetAsync(asset.EffectiveExportPath, clip.SourceAudioStreamIndex, window, _experienceLifetime.Token);
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(DrawTimeline)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _waveformFailed.Add(key); System.Diagnostics.Debug.WriteLine($"WAVEFORM-DECODE: {ex}"); }
            finally { _waveformPending.Remove(key); }
        }
    }
}
