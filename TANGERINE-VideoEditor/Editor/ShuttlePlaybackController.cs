using System;
using System.Windows.Input;
using System.Windows.Threading;
using TANGERINE_VideoEditor.Project;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// J/K/L shuttle playback control for precise trimming and navigation.
/// J = reverse playback, K = pause, L = forward playback (press multiple times to speed up).
/// </summary>
public class ShuttlePlaybackController
{
    private readonly DispatcherTimer _playbackTimer;
    private double _playbackSpeed = 0; // Negative = reverse, 0 = paused, positive = forward
    private DateTime _lastFrameTime;
    private bool _isShuttling;

    public event EventHandler<double>? PlayheadChanged;
    public event EventHandler<double>? SpeedChanged;

    public double CurrentSpeed => _playbackSpeed;
    public bool IsShuttling => _isShuttling;

    public ShuttlePlaybackController()
    {
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16.67) // ~60fps
        };
        _playbackTimer.Tick += OnPlaybackTick;
    }

    /// <summary>
    /// Handles J key press - reverse playback (cumulative speed).
    /// </summary>
    public void PressJ()
    {
        if (_playbackSpeed > 0)
            _playbackSpeed = 0; // Stop forward playback

        _playbackSpeed -= 1.0; // Increase reverse speed: -1x, -2x, -4x, -8x
        if (_playbackSpeed < -8.0)
            _playbackSpeed = -8.0; // Cap at -8x

        StartShuttle();
    }

    /// <summary>
    /// Handles K key press - pause.
    /// </summary>
    public void PressK()
    {
        _playbackSpeed = 0;
        StopShuttle();
    }

    /// <summary>
    /// Handles L key press - forward playback (cumulative speed).
    /// </summary>
    public void PressL()
    {
        if (_playbackSpeed < 0)
            _playbackSpeed = 0; // Stop reverse playback

        _playbackSpeed += 1.0; // Increase forward speed: 1x, 2x, 4x, 8x
        if (_playbackSpeed > 8.0)
            _playbackSpeed = 8.0; // Cap at 8x

        StartShuttle();
    }

    /// <summary>
    /// Frame-accurate step forward (right arrow or Shift+K).
    /// </summary>
    public void StepForward(double frameDuration)
    {
        StopShuttle();
        PlayheadChanged?.Invoke(this, frameDuration);
    }

    /// <summary>
    /// Frame-accurate step backward (left arrow or Shift+J).
    /// </summary>
    public void StepBackward(double frameDuration)
    {
        StopShuttle();
        PlayheadChanged?.Invoke(this, -frameDuration);
    }

    private void StartShuttle()
    {
        if (!_isShuttling)
        {
            _isShuttling = true;
            _lastFrameTime = DateTime.Now;
            _playbackTimer.Start();
            SpeedChanged?.Invoke(this, _playbackSpeed);
        }
        else
        {
            SpeedChanged?.Invoke(this, _playbackSpeed);
        }
    }

    private void StopShuttle()
    {
        _isShuttling = false;
        _playbackTimer.Stop();
        SpeedChanged?.Invoke(this, 0);
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        DateTime now = DateTime.Now;
        double deltaSeconds = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        double playheadDelta = deltaSeconds * _playbackSpeed;
        PlayheadChanged?.Invoke(this, playheadDelta);
    }

    /// <summary>
    /// Resets shuttle state.
    /// </summary>
    public void Reset()
    {
        StopShuttle();
        _playbackSpeed = 0;
    }
}

/// <summary>
/// Trim mode for precise cut editing with dual monitor view.
/// </summary>
public class TrimModeController
{
    public enum TrimType
    {
        RippleStart,    // Adjust clip start, shift following clips
        RippleEnd,      // Adjust clip end, shift following clips
        Roll,           // Adjust boundary between two clips
        Slip,           // Change source offset without moving timeline position
        Slide           // Move clip, adjust neighbors
    }

    public TimelineClip? SelectedClip { get; private set; }
    public TimelineClip? AdjacentClip { get; private set; }
    public TrimType CurrentTrimType { get; private set; }

    private double _originalStart;
    private double _originalDuration;
    private double _originalSourceIn;

    public event EventHandler<TrimChangedEventArgs>? TrimChanged;

    /// <summary>
    /// Enters trim mode for the selected clip.
    /// </summary>
    public void EnterTrimMode(TimelineClip clip, TimelineClip? adjacentClip, TrimType trimType)
    {
        SelectedClip = clip;
        AdjacentClip = adjacentClip;
        CurrentTrimType = trimType;

        _originalStart = clip.StartSeconds;
        _originalDuration = clip.DurationSeconds;
        _originalSourceIn = clip.SourceInSeconds;
    }

    /// <summary>
    /// Applies trim adjustment by frame count.
    /// </summary>
    public void ApplyTrim(int frameCount, double frameDuration)
    {
        if (SelectedClip == null) return;

        double delta = frameCount * frameDuration;

        switch (CurrentTrimType)
        {
            case TrimType.RippleStart:
                SelectedClip.StartSeconds += delta;
                SelectedClip.DurationSeconds -= delta;
                SelectedClip.SourceInSeconds += delta;
                break;

            case TrimType.RippleEnd:
                SelectedClip.DurationSeconds += delta;
                break;

            case TrimType.Roll:
                if (AdjacentClip != null)
                {
                    SelectedClip.DurationSeconds -= delta;
                    AdjacentClip.StartSeconds -= delta;
                    AdjacentClip.DurationSeconds += delta;
                    AdjacentClip.SourceInSeconds -= delta;
                }
                break;

            case TrimType.Slip:
                SelectedClip.SourceInSeconds += delta;
                break;

            case TrimType.Slide:
                SelectedClip.StartSeconds += delta;
                break;
        }

        TrimChanged?.Invoke(this, new TrimChangedEventArgs(delta));
    }

    /// <summary>
    /// Exits trim mode.
    /// </summary>
    public void ExitTrimMode()
    {
        SelectedClip = null;
        AdjacentClip = null;
    }

    /// <summary>
    /// Cancels trim and reverts to original state.
    /// </summary>
    public void CancelTrim()
    {
        if (SelectedClip != null)
        {
            SelectedClip.StartSeconds = _originalStart;
            SelectedClip.DurationSeconds = _originalDuration;
            SelectedClip.SourceInSeconds = _originalSourceIn;
        }
        ExitTrimMode();
    }
}

public class TrimChangedEventArgs : EventArgs
{
    public double Delta { get; }

    public TrimChangedEventArgs(double delta)
    {
        Delta = delta;
    }
}
