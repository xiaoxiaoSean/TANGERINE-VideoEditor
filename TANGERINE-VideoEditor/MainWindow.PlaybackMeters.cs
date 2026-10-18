using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TANGERINE_VideoEditor;

public partial class MainWindow
{
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private (double Left, double Right)[] _meterSamples = [];
    private string? _meterPath;
    private double _meterStart;
    private double _meterPeakL, _meterPeakR;
    private long _meterPeakUntil;
    private bool _meterClipped;
    private void InitializePlaybackMeters()
    {
        _meterTimer.Tick += (_, _) => UpdatePlaybackMeters(); _meterTimer.Start();
        MeterResetButton.Click += (_, _) => { _meterPeakL = _meterPeakR = 0; _meterClipped = false; };
        Closed += (_, _) => _meterTimer.Stop();
    }
    private async Task LoadPlaybackMeterAsync(string path, double start)
    {
        _meterPath = path; _meterStart = start; _meterSamples = [];
        try
        {
            var samples = await Task.Run(() => ReadMeterPcm(path));
            if (_meterPath == path) _meterSamples = samples;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"AUMTR0001: {ex}"); }
    }
    /// <summary>Read the actual post-mix stereo PCM WAV used by the monitor, at 50 ms windows.</summary>
    private static (double, double)[] ReadMeterPcm(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF") return [];
        reader.ReadUInt32(); if (new string(reader.ReadChars(4)) != "WAVE") return [];
        int channels = 0, rate = 0, bits = 0; ushort format = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4)); uint length = reader.ReadUInt32(); long end = Math.Min(stream.Length, stream.Position + length);
            if (id == "fmt ") { format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32(); reader.ReadBytes(6); bits = reader.ReadUInt16(); }
            else if (id == "data" && format == 1 && channels == 2 && bits == 16 && rate > 0)
            {
                var result = new List<(double, double)>(); int frames = Math.Max(1, rate / 20);
                while (stream.Position + 4 <= end)
                {
                    double left = 0, right = 0;
                    for (int i = 0; i < frames && stream.Position + 4 <= end; i++)
                    { left = Math.Max(left, Math.Abs(reader.ReadInt16() / 32768d)); right = Math.Max(right, Math.Abs(reader.ReadInt16() / 32768d)); }
                    result.Add((left, right));
                }
                return result.ToArray();
            }
            stream.Position = end + (length % 2);
        }
        return [];
    }
    private void UpdatePlaybackMeters()
    {
        double l = 0, r = 0;
        if (_isPlaying && _timelineAudioPlayerRunning && !_globalMute)
        {
            int index = (int)((_project.Sequence.PlayheadSeconds - _meterStart) * 20);
            if (index >= 0 && index < _meterSamples.Length) (l, r) = _meterSamples[index];
        }
        if (Environment.TickCount64 > _meterPeakUntil) { _meterPeakL *= .93; _meterPeakR *= .93; }
        if (l > _meterPeakL || r > _meterPeakR) _meterPeakUntil = Environment.TickCount64 + 1500;
        _meterPeakL = Math.Max(l, _meterPeakL); _meterPeakR = Math.Max(r, _meterPeakR);
        _meterClipped |= l >= .999 || r >= .999;
        static double Db(double value) => value > .000001 ? 20 * Math.Log10(value) : -60;
        MeterLeft.Value = Math.Clamp(Db(l) + 60, 0, 60); MeterRight.Value = Math.Clamp(Db(r) + 60, 0, 60);
        MeterPeakText.Text = $"峰 L {Db(_meterPeakL):0.0} / R {Db(_meterPeakR):0.0} dBFS";
        MeterResetButton.Content = _meterClipped ? "削波！点击复位" : "电平复位";
        MeterResetButton.Foreground = _meterClipped ? Brushes.OrangeRed : Brushes.White;
    }
}
