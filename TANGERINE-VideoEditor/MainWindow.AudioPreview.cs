using System.IO;
using System.Text.Json;
using System.Windows.Media;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor;

/// <summary>
/// Feeds short rendered windows to an independent audio player. A rendered
/// window is reused while playback remains inside it; the next window begins
/// preparing before the playhead reaches its boundary.
/// </summary>
public partial class MainWindow
{
    private const double MonitorWindowSeconds = 6;
    private const double MonitorPrepareAheadSeconds = 2;
    private readonly MediaPlayer _timelineAudioPlayer = new();
    private CancellationTokenSource? _timelineAudioRenderCancellation;
    private string? _timelineAudioFile;
    private string? _timelineAudioReadyFile;
    private double _timelineAudioReadyStart;
    private bool _timelineAudioReadyHasSound;
    private bool _timelineAudioReady;
    private long _timelineAudioReadyRevision = -1;
    private long _timelineAudioReadyGeneration = -1;
    private double? _timelineAudioQueuedStart;
    private double _timelineAudioStart;
    private double _timelineAudioEnd;
    private long _timelineAudioRevision = -1;
    private long _timelineAudioGeneration = -1;
    private bool _timelineAudioPending;
    private double _timelineAudioPendingStart;
    private long _timelineAudioPendingRevision = -1;
    private long _timelineAudioPendingGeneration = -1;
    private bool _timelineAudioHasSound;
    private bool _timelineAudioPlayerRunning;
    private bool _timelineAudioPlayerOpened;
    private long _timelineAudioEpoch;

    private void InitializeAudioPreview()
    {
        InitializePlaybackMeters();
        _timelineAudioPlayer.MediaOpened += (_, _) =>
        {
            if (_timelineAudioFile is null || !_timelineAudioHasSound) return;
            _timelineAudioPlayerOpened = true;
            if (!_isPlaying) return;
            _timelineAudioPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(
                _project.Sequence.PlayheadSeconds - _timelineAudioStart, 0, MonitorWindowSeconds));
            _timelineAudioPlayer.Play();
            _timelineAudioPlayerRunning = true;
        };
        _timelineAudioPlayer.MediaFailed += (_, args) =>
        {
            StatusText.Text = $"音频监听失败 · {AudioStageCode.Process}：{args.ErrorException?.Message ?? "未知媒体错误"}";
            string? failedFile = _timelineAudioFile;
            _timelineAudioPlayer.Close();
            _timelineAudioFile = null;
            DeleteAudioPreviewFile(failedFile);
            _timelineAudioHasSound = false;
            _timelineAudioPlayerRunning = false;
            _timelineAudioPlayerOpened = false;
        };
    }

    /// <summary>Called after each sequence seek, including playback ticks.</summary>
    private void UpdateAudioPreview(double seconds)
    {
        if (!_isPlaying)
        {
            _timelineAudioPlayer.Pause();
            _timelineAudioPlayerRunning = false;
            return;
        }
        if (!double.IsFinite(seconds) || seconds < 0) return;
        if (_timelineAudioReady &&
            (_timelineAudioReadyRevision != MonitorRevision ||
                _timelineAudioReadyGeneration != _projectGeneration ||
                seconds < _timelineAudioReadyStart - .03 ||
                seconds >= _timelineAudioReadyStart + MonitorWindowSeconds))
        {
            DeleteAudioPreviewFile(_timelineAudioReadyFile);
            _timelineAudioReadyFile = null;
            _timelineAudioReadyHasSound = false;
            _timelineAudioReady = false;
        }
        if (_timelineAudioPending &&
            (_timelineAudioPendingGeneration != _projectGeneration ||
                _timelineAudioPendingRevision != MonitorRevision) &&
            _timelineAudioRenderCancellation?.IsCancellationRequested == false)
            _timelineAudioRenderCancellation.Cancel();
        if (_timelineAudioPending &&
            _timelineAudioRenderCancellation?.IsCancellationRequested == true)
        {
            // An invalidated or stopped FFmpeg process still occupies the
            // single render slot until it exits. Retain only the latest seek.
            _timelineAudioQueuedStart = seconds;
            return;
        }
        bool currentWindow = _timelineAudioGeneration == _projectGeneration &&
            _timelineAudioRevision == MonitorRevision &&
            _timelineAudioHasSound == (_timelineAudioFile is not null) &&
            seconds >= _timelineAudioStart && seconds < _timelineAudioEnd;
        if (!currentWindow && _timelineAudioReady &&
            _timelineAudioReadyRevision == MonitorRevision &&
            _timelineAudioReadyGeneration == _projectGeneration &&
            _timelineAudioReadyStart <= seconds &&
            seconds < _timelineAudioReadyStart + MonitorWindowSeconds)
        {
            ActivateAudioPreviewWindow(_timelineAudioReadyStart,
                _timelineAudioReadyFile, _timelineAudioReadyHasSound);
            currentWindow = true;
        }
        if (currentWindow)
        {
            if (_timelineAudioHasSound && _timelineAudioPlayerOpened)
            {
                double desired = seconds - _timelineAudioStart;
                if (Math.Abs((_timelineAudioPlayer.Position - TimeSpan.FromSeconds(desired)).TotalSeconds) > .2)
                    _timelineAudioPlayer.Position = TimeSpan.FromSeconds(desired);
                if (!_timelineAudioPlayerRunning)
                {
                    _timelineAudioPlayer.Play();
                    _timelineAudioPlayerRunning = true;
                }
            }
            if (_timelineAudioEnd - seconds <= MonitorPrepareAheadSeconds &&
                !_timelineAudioPending && !_timelineAudioReady)
                _ = RenderAudioPreviewWindowAsync(_timelineAudioEnd);
            return;
        }
        // Do not continue the previous window after an edit has invalidated
        // its mix. A new render will pick up changed mutes and effects.
        if (_timelineAudioPlayerOpened)
        {
            _timelineAudioPlayer.Close();
            _timelineAudioPlayerOpened = false;
            DeleteAudioPreviewFile(_timelineAudioFile);
            _timelineAudioFile = null;
        }
        else if (_timelineAudioFile is not null)
        {
            _timelineAudioPlayer.Close();
            DeleteAudioPreviewFile(_timelineAudioFile);
            _timelineAudioFile = null;
        }
        _timelineAudioPlayerRunning = false;
        if (_timelineAudioPending)
        {
            bool sameRevision = _timelineAudioPendingGeneration == _projectGeneration &&
                _timelineAudioPendingRevision == MonitorRevision;
            if (sameRevision && seconds >= _timelineAudioPendingStart &&
                seconds < _timelineAudioPendingStart + MonitorWindowSeconds) return;
            if (_timelineAudioQueuedStart is null ||
                Math.Abs(_timelineAudioQueuedStart.Value - seconds) > .4)
                _timelineAudioQueuedStart = seconds;
            if (_timelineAudioRenderCancellation?.IsCancellationRequested == false)
                _timelineAudioRenderCancellation.Cancel();
            return;
        }
        DeleteAudioPreviewFile(_timelineAudioReadyFile);
        _timelineAudioReadyFile = null;
        _timelineAudioReadyStart = 0;
        _timelineAudioReadyHasSound = false;
        _timelineAudioReady = false;
        _timelineAudioReadyRevision = -1;
        _timelineAudioReadyGeneration = -1;
        _ = RenderAudioPreviewWindowAsync(seconds);
    }

    /// <summary>Called immediately after playback is paused.</summary>
    private void PauseAudioPreview()
    {
        _timelineAudioPlayer.Pause();
        _timelineAudioPlayerRunning = false;
    }

    /// <summary>Called when playback stops or the window closes.</summary>
    private void StopAudioPreview()
    {
        _timelineAudioEpoch++;
        _timelineAudioRenderCancellation?.Cancel();
        _timelineAudioPlayer.Close();
        _timelineAudioHasSound = false;
        _timelineAudioPlayerRunning = false;
        _timelineAudioPlayerOpened = false;
        _timelineAudioStart = _timelineAudioEnd = 0;
        _timelineAudioRevision = -1;
        _timelineAudioGeneration = -1;
        DeleteAudioPreviewFile(_timelineAudioFile);
        DeleteAudioPreviewFile(_timelineAudioReadyFile);
        _timelineAudioFile = null;
        _timelineAudioReadyFile = null;
        _timelineAudioReadyStart = 0;
        _timelineAudioReadyHasSound = false;
        _timelineAudioReady = false;
        _timelineAudioReadyRevision = -1;
        _timelineAudioReadyGeneration = -1;
        _timelineAudioQueuedStart = null;
    }

    private async Task RenderAudioPreviewWindowAsync(double startSeconds)
    {
        if (_timelineAudioPending) return;
        if (!double.IsFinite(startSeconds) || startSeconds < 0) return;
        _timelineAudioPending = true;
        _timelineAudioPendingStart = startSeconds;
        var cancellation = new CancellationTokenSource();
        _timelineAudioRenderCancellation = cancellation;
        ProjectDocument sourceProject = _project;
        long generation = _projectGeneration;
        long revision = MonitorRevision;
        long epoch = _timelineAudioEpoch;
        _timelineAudioPendingGeneration = generation;
        _timelineAudioPendingRevision = revision;
        string path = Path.Combine(Path.GetTempPath(), $"tangerine-monitor-{Guid.NewGuid():N}.wav");
        try
        {
            // Copy and flatten away from the dispatcher. Revision and
            // generation checks discard a copy if an edit races this read;
            // a concurrent collection change is handled below as a stale
            // snapshot instead of freezing or crashing the editor.
            ProjectDocument snapshot = await Task.Run(() =>
            {
                ProjectDocument copy = JsonSerializer.Deserialize<ProjectDocument>(
                    JsonSerializer.Serialize(sourceProject)) ??
                    throw new AudioOperationException(AudioStageCode.Read,
                        "无法读取当前项目的音频监听快照。");
                return SequenceFlattener.Flatten(copy);
            }, cancellation.Token);
            if (_globalMute)
                foreach (TimelineTrack track in snapshot.Tracks) track.IsMuted = true;
            else if (_soloTrackId is not null)
                foreach (TimelineTrack track in snapshot.Tracks)
                    track.IsMuted = !string.Equals(track.Id, _soloTrackId, StringComparison.Ordinal);
            if (generation != _projectGeneration || revision != MonitorRevision ||
                !ReferenceEquals(sourceProject, _project)) return;
            bool hasAudio = await Task.Run(() => TimelineAudioMonitor.RenderWindowAsync(snapshot,
                startSeconds, MonitorWindowSeconds, path, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || epoch != _timelineAudioEpoch ||
                !ReferenceEquals(sourceProject, _project) ||
                generation != _projectGeneration || revision != MonitorRevision || !_isPlaying)
                return;
            if (startSeconds > _project.Sequence.PlayheadSeconds &&
                _project.Sequence.PlayheadSeconds < _timelineAudioEnd)
            {
                _timelineAudioReadyFile = hasAudio ? path : null;
                _timelineAudioReadyStart = startSeconds;
                _timelineAudioReadyHasSound = hasAudio;
                _timelineAudioReady = true;
                _timelineAudioReadyRevision = revision;
                _timelineAudioReadyGeneration = generation;
            }
            else if (_project.Sequence.PlayheadSeconds >= startSeconds &&
                _project.Sequence.PlayheadSeconds < startSeconds + MonitorWindowSeconds)
                ActivateAudioPreviewWindow(startSeconds, hasAudio ? path : null, hasAudio);
        }
        catch (OperationCanceledException) { }
        catch (AudioOperationException exception)
        {
            if (epoch == _timelineAudioEpoch && !cancellation.IsCancellationRequested &&
                generation == _projectGeneration && revision == MonitorRevision)
                ReportAudioError(exception);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or
            InvalidOperationException or ArgumentException)
        {
            if (epoch == _timelineAudioEpoch && !cancellation.IsCancellationRequested &&
                generation == _projectGeneration && revision == MonitorRevision)
                ReportAudioError(new AudioOperationException(AudioStageCode.Read,
                    "无法读取当前项目的音频监听快照。", exception));
        }
        catch (ProjectFileException exception)
        {
            if (epoch == _timelineAudioEpoch && !cancellation.IsCancellationRequested &&
                generation == _projectGeneration && revision == MonitorRevision)
                ReportAudioError(new AudioOperationException(AudioStageCode.Read,
                    "无法展开音频监听的嵌套序列。", exception));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or UriFormatException)
        {
            if (epoch == _timelineAudioEpoch && !cancellation.IsCancellationRequested &&
                generation == _projectGeneration && revision == MonitorRevision)
                ReportAudioError(new AudioOperationException(AudioStageCode.Process,
                    "无法加载本地音频监听窗口。", exception));
        }
        finally
        {
            _timelineAudioPending = false;
            _timelineAudioPendingGeneration = -1;
            _timelineAudioPendingRevision = -1;
            if (_timelineAudioFile != path && _timelineAudioReadyFile != path)
                DeleteAudioPreviewFile(path);
            if (ReferenceEquals(_timelineAudioRenderCancellation, cancellation))
                _timelineAudioRenderCancellation = null;
            cancellation.Dispose();
            // A stopped render may finish after playback has restarted. The
            // newest queued seek belongs to the active play session and must
            // still launch once this process has released its slot.
            if (_timelineAudioQueuedStart is double queued && _isPlaying)
            {
                _timelineAudioQueuedStart = null;
                _ = RenderAudioPreviewWindowAsync(queued);
            }
        }
    }

    private void ActivateAudioPreviewWindow(double startSeconds, string? path, bool hasAudio)
    {
        string? previous = _timelineAudioFile;
        _timelineAudioPlayer.Close();
        _timelineAudioPlayerRunning = false;
        _timelineAudioPlayerOpened = false;
        _timelineAudioFile = path;
        _timelineAudioStart = startSeconds;
        if (path is not null && hasAudio) _ = LoadPlaybackMeterAsync(path, startSeconds);
        else { _meterSamples = []; _meterPath = null; }
        _timelineAudioEnd = startSeconds + MonitorWindowSeconds;
        _timelineAudioRevision = MonitorRevision;
        _timelineAudioGeneration = _projectGeneration;
        _timelineAudioHasSound = hasAudio;
        _timelineAudioReadyFile = null;
        _timelineAudioReadyStart = 0;
        _timelineAudioReadyHasSound = false;
        _timelineAudioReady = false;
        _timelineAudioReadyRevision = -1;
        _timelineAudioReadyGeneration = -1;
        if (hasAudio && path is not null)
        {
            _timelineAudioPlayer.Open(new Uri(path));
            _timelineAudioPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(
                _project.Sequence.PlayheadSeconds - startSeconds, 0, MonitorWindowSeconds));
        }
        if (previous != path) DeleteAudioPreviewFile(previous);
    }

    private static void DeleteAudioPreviewFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        // Playback seeks and stop notifications run on the dispatcher. A
        // temporary WAV may reside on a slow redirected temp volume, so the
        // filesystem cleanup must not stall the preview interaction.
        _ = Task.Run(() =>
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Debug.WriteLine($"AUDIO-PREVIEW-CLEANUP: {exception}"); }
        });
    }
}
