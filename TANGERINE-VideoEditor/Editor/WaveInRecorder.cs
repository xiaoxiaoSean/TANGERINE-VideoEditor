using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TANGERINE_VideoEditor.Editor;

public sealed record CaptureDevice(uint Id, string Name);

/// <summary>
/// Captures PCM through the local Windows multimedia API. The two native
/// buffers are pinned for the entire recording; callbacks copy only completed
/// samples, and all buffers are released after the device has been reset.
/// This component requires no account, server, or extra driver installation.
/// </summary>
public sealed class WaveInRecorder : IDisposable
{
    private const uint CallbackFunction = 0x00030000;
    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BufferSize = SampleRate * 2 / 10;
    private const long MaximumCaptureBytes = 24L * 3600 * SampleRate * Channels * BitsPerSample / 8;
    private readonly object _gate = new();
    private readonly WaveCallback _callback;
    private readonly List<(IntPtr Header, IntPtr Data, bool Prepared)> _buffers = [];
    private Exception? _captureError;
    private IntPtr _handle;
    private MemoryStream? _samples;
    private bool _running;
    private bool _disposed;

    public WaveInRecorder()
    {
        _callback = OnWaveData;
    }

    public static IReadOnlyList<CaptureDevice> GetDevices()
    {
        try
        {
            uint count = waveInGetNumDevs();
            var devices = new List<CaptureDevice>();
            for (uint id = 0; id < count; id++)
            {
                uint result = waveInGetDevCaps(id, out WaveInCaps caps, (uint)Marshal.SizeOf<WaveInCaps>());
                if (result == 0) devices.Add(new CaptureDevice(id, caps.Name.TrimEnd('\0')));
            }
            return devices;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioOperationException(AudioStageCode.Capture, "无法获取本机录音设备。", exception);
        }
    }

    public void Start(uint deviceId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WaveInRecorder));
        if (_running) throw new AudioOperationException(AudioStageCode.Capture, "录音已经开始。");
        _captureError = null;
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = Channels, SamplesPerSec = SampleRate,
            AvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
            BlockAlign = Channels * BitsPerSample / 8, BitsPerSample = BitsPerSample
        };
        uint result;
        try { result = waveInOpen(out _handle, deviceId, ref format, _callback, IntPtr.Zero, CallbackFunction); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioOperationException(AudioStageCode.Capture, "无法启动本机录音服务。", exception);
        }
        if (result != 0)
        {
            if (_handle != IntPtr.Zero) { waveInClose(_handle); _handle = IntPtr.Zero; }
            throw new AudioOperationException(AudioStageCode.Capture, $"无法打开录音设备，系统代码：{result}。");
        }
        try
        {
            _samples = new MemoryStream();
            _running = true;
            for (int index = 0; index < 4; index++)
            {
                IntPtr data = Marshal.AllocHGlobal(BufferSize);
                IntPtr headerPointer;
                try { headerPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>()); }
                catch { Marshal.FreeHGlobal(data); throw; }
                var header = new WaveHeader { Data = data, BufferLength = BufferSize };
                Marshal.StructureToPtr(header, headerPointer, false);
                _buffers.Add((headerPointer, data, false));
                Check(waveInPrepareHeader(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>()));
                _buffers[^1] = (headerPointer, data, true);
                Check(waveInAddBuffer(_handle, headerPointer, (uint)Marshal.SizeOf<WaveHeader>()));
            }
            Check(waveInStart(_handle));
        }
        catch (AudioOperationException)
        {
            StopNative();
            _samples?.Dispose();
            _samples = null;
            throw;
        }
        catch (Exception exception) when (exception is OutOfMemoryException or ExternalException)
        {
            StopNative();
            _samples?.Dispose();
            _samples = null;
            throw new AudioOperationException(AudioStageCode.Capture, "无法分配或启动录音缓冲区。", exception);
        }
    }

    public byte[] Stop()
    {
        if (_handle == IntPtr.Zero) throw new AudioOperationException(AudioStageCode.Capture, "录音尚未开始。");
        StopNative();
        lock (_gate)
        {
            byte[] samples = _samples?.ToArray() ?? [];
            _samples?.Dispose();
            _samples = null;
            if (_captureError is not null)
                throw new AudioOperationException(AudioStageCode.Capture, "录音数据处理失败。", _captureError);
            return samples;
        }
    }

    public static async Task WriteWaveAsync(string path, byte[] samples, CancellationToken cancellationToken = default)
    {
        if (samples.LongLength > int.MaxValue - 44L || samples.Length % (Channels * BitsPerSample / 8) != 0)
            throw new AudioOperationException(AudioStageCode.Validate, "录音数据长度无效，无法写入 WAV。");
        try
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true);
            using var headerStream = new MemoryStream(44);
            using (var writer = new BinaryWriter(headerStream, Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)Channels);
                writer.Write(SampleRate); writer.Write(SampleRate * Channels * BitsPerSample / 8);
                writer.Write((short)(Channels * BitsPerSample / 8)); writer.Write((short)BitsPerSample);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Length);
            }
            await file.WriteAsync(headerStream.ToArray(), cancellationToken);
            await file.WriteAsync(samples, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AudioOperationException(AudioStageCode.Write, "无法保存画外音录音。", exception);
        }
    }

    private void OnWaveData(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2)
    {
        // WIM_DATA = 0x3C0. Never let a managed exception cross a native
        // callback boundary; a failed callback merely ends capture.
        if (message != 0x3C0 || parameter1 == IntPtr.Zero) return;
        try
        {
            lock (_gate)
            {
                if (!_running || _samples is null) return;
                WaveHeader header = Marshal.PtrToStructure<WaveHeader>(parameter1);
                int count = (int)Math.Min(header.BytesRecorded, (uint)BufferSize);
                if (count > 0)
                {
                    // RIFF stores its payload length in a 32-bit field. Stop
                    // capture before an oversized session would overflow the
                    // WAV header or exhaust process memory.
                    if (_samples.Length > Math.Min(int.MaxValue - 44L, MaximumCaptureBytes) - count)
                    {
                        _captureError = new AudioOperationException(AudioStageCode.Capture,
                            "录音达到单次可保存的长度上限，请停止并保存当前录音。");
                        _running = false;
                        return;
                    }
                    byte[] data = new byte[count];
                    Marshal.Copy(header.Data, data, 0, count);
                    _samples.Write(data, 0, count);
                }
                header.BytesRecorded = 0;
                Marshal.StructureToPtr(header, parameter1, false);
                if (_running)
                {
                    // A buffer that fails to re-queue is never filled again, so
                    // the recording would thin out and cut off in silence.
                    // Surface it as a capture error instead of ignoring the code.
                    uint queued = waveInAddBuffer(handle, parameter1, (uint)Marshal.SizeOf<WaveHeader>());
                    if (queued != 0)
                    {
                        lock (_gate)
                        {
                            _captureError ??= new AudioOperationException(AudioStageCode.Capture,
                                $"录音缓冲区无法继续提交，系统代码：{queued}。请检查麦克风是否仍然连接。");
                            _running = false;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OutOfMemoryException or ArgumentException)
        {
            lock (_gate) { _captureError = exception; _running = false; }
        }
    }

    private void StopNative()
    {
        if (_handle == IntPtr.Zero) return;
        lock (_gate) _running = false;
        waveInReset(_handle);
        foreach ((IntPtr header, IntPtr data, bool prepared) in _buffers)
        {
            if (prepared) waveInUnprepareHeader(_handle, header, (uint)Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }
        _buffers.Clear();
        waveInClose(_handle);
        _handle = IntPtr.Zero;
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new AudioOperationException(AudioStageCode.Capture,
            $"录音设备操作失败，系统代码：{result}。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopNative();
        _samples?.Dispose();
        _disposed = true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WaveCallback(IntPtr handle, uint message, IntPtr instance, IntPtr parameter1, IntPtr parameter2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort ManufacturerId, ProductId;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }

    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCaps(uint deviceId, out WaveInCaps caps, uint size);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormat format,
        WaveCallback callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern uint waveInClose(IntPtr handle);
}
