using System.Diagnostics;
using System.Globalization;
using System.IO;
using Tangerine.VideoEditor.Speech;

namespace TANGERINE_VideoEditor.Editor;

public static class SpeechAudio
{
    private const int MaximumPcmBytes = 512 * 1024 * 1024;
    private sealed record PcmInfo(int Rate, long DataOffset, int DataLength);
    private static string N(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
    public static async Task ConvertAsync(string input, string output, double rate, double pitch,
        double start, double? duration, int sampleRate, CancellationToken token, Action<double>? onOutputSeconds = null)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(pitch) || pitch is < -12 or > 12)
            throw new SpeechFailure("VOICE0001", "音频处理参数无效。");
        double factor = Math.Pow(2, pitch / 12), tempo = rate / factor;
        var filters = new List<string> { "aresample=48000", $"asetrate={N(48000 * factor)}", $"aresample={sampleRate}" };
        while (tempo > 2) { filters.Add("atempo=2"); tempo /= 2; }
        while (tempo < .5) { filters.Add("atempo=0.5"); tempo /= .5; }
        filters.Add("atempo=" + N(tempo));
        if (Math.Abs(rate - 1) < .000000001 && Math.Abs(pitch) < .000000001)
            filters = [$"aresample={sampleRate}"];
        // The bundled FFmpeg cannot open an absolute input beyond MAX_PATH.
        // Stage long inputs and every output in the executable's short cache path.
        input = Path.GetFullPath(input);
        if (!File.Exists(input))
            throw new SpeechFailure("VOICE0007", "音频处理输入文件不存在：" + input);
        double sourceDuration = duration ?? 0;
        if (duration is null)
        {
            try { sourceDuration = Math.Max(0, Duration(input) - start); }
            catch (Exception e) when (e is SpeechFailure or IOException) { }
        }
        double expectedDuration = Math.Max(0, sourceDuration / rate);
        long inputBytes = new FileInfo(input).Length;
        output = Path.GetFullPath(output);
        string outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);
        string stageDirectory = Path.Combine(AppStorage.TempRoot, "speech-audio-" + Guid.NewGuid().ToString("N"));
        string pendingOutput = Path.Combine(stageDirectory, "output.wav");
        if (pendingOutput.Length >= 240)
            throw new SpeechFailure("VOICE0007", "程序缓存路径过长，无法使用随附音频处理程序。请将程序移到较短的目录。");
        Directory.CreateDirectory(stageDirectory);
        string encoderInput = input;
        Process startedProcess;
        try
        {
            if (input.Length >= 240 || input.Any(c => c > 127))
            {
                string extension = Path.GetExtension(input);
                if (extension.Length is < 2 or > 10 || !extension[1..].All(char.IsAsciiLetterOrDigit))
                    extension = ".bin";
                string stagedInput = Path.Combine(stageDirectory, "input" + extension);
                await using (var source = File.OpenRead(input))
                await using (var destination = File.Create(stagedInput))
                    await source.CopyToAsync(destination, token).ConfigureAwait(false);
                encoderInput = Path.GetFileName(stagedInput);
            }
            var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-stats_period", "1", "-y", "-ss", N(start), "-i", encoderInput };
            if (duration is double seconds) { args.Add("-t"); args.Add(N(seconds)); }
            args.AddRange(["-vn", "-af", string.Join(',', filters), "-ac", "1", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "output.wav"]);
            var info = new ProcessStartInfo(TimelineExporter.ResolveEncoderPath())
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = stageDirectory };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            startedProcess = Process.Start(info) ?? throw new SpeechFailure("VOICE0007", "无法启动音频处理程序。");
        }
        catch
        {
            SpeechAssets.DeleteDirectory(stageDirectory);
            throw;
        }
        using var process = startedProcess;
        long started = Stopwatch.GetTimestamp();
        long lastOutputTime = 0;
        SpeechWorkerClient.WriteDiagnostic(FormattableString.Invariant(
            $"audio-convert-start pid={process.Id} inputBytes={inputBytes} expectedSeconds={expectedDuration:F2} rate={rate:F3} pitch={pitch:F2}"));
        void StopEncoder()
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            { Trace.WriteLine("VOICE0007: " + e.Message); }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var finished = new ManualResetEventSlim(false);
        using var registration = cancellation.Token.Register(StopEncoder);
        int timedOut = 0;
        Task monitor = Task.Factory.StartNew(() =>
        {
            double lastCpu = 0;
            while (!finished.Wait(TimeSpan.FromSeconds(15)))
            {
                double cpu = 0;
                long memory = 0;
                long outputBytes = 0;
                try { process.Refresh(); cpu = process.TotalProcessorTime.TotalSeconds; memory = process.PrivateMemorySize64; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
                try { if (File.Exists(pendingOutput)) outputBytes = new FileInfo(pendingOutput).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                long outTime = Interlocked.Read(ref lastOutputTime);
                SpeechWorkerClient.WriteDiagnostic(FormattableString.Invariant(
                    $"audio-convert-running pid={process.Id} elapsed={Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s outSeconds={outTime / 1_000_000d:F2} cpu={cpu:F2}s cpuDelta={Math.Max(0, cpu - lastCpu):F2}s private={memory} outputBytes={outputBytes}"));
                lastCpu = cpu;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // The deadline must remain active even if model work starves the CLR
        // thread pool. This dedicated waiter exists only for this conversion.
        Task watchdog = Task.Factory.StartNew(() =>
        {
            if (finished.Wait(TimeSpan.FromMinutes(5))) return;
            Interlocked.Exchange(ref timedOut, 1);
            cancellation.Cancel();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellation.Token);
            Task progress = ReadProgressAsync(process.StandardOutput, cancellation.Token, value =>
            {
                Interlocked.Exchange(ref lastOutputTime, value);
                onOutputSeconds?.Invoke(value / 1_000_000d);
            });
            Task drained = Task.WhenAll(process.WaitForExitAsync(cancellation.Token), errors, progress);
            // Observe pipe failures even if cancellation wins the outer wait.
            _ = drained.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await drained.WaitAsync(cancellation.Token).ConfigureAwait(false);
            string error = await errors.ConfigureAwait(false);
            finished.Set();
            await watchdog.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new SpeechFailure("VOICE0007", "音频变速/移调失败：" + error[..Math.Min(error.Length, 2000)]);
            File.Move(pendingOutput, output, overwrite: true);
            SpeechWorkerClient.WriteDiagnostic(FormattableString.Invariant(
                $"audio-convert-complete pid={process.Id} elapsed={Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s outputBytes={new FileInfo(output).Length}"));
        }
        catch (Exception e) when (Volatile.Read(ref timedOut) != 0 && !token.IsCancellationRequested)
        {
            SpeechWorkerClient.WriteDiagnostic(FormattableString.Invariant(
                $"audio-convert-timeout pid={process.Id} elapsed={Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s outSeconds={Interlocked.Read(ref lastOutputTime) / 1_000_000d:F2}"));
            throw new SpeechFailure("VOICE0007", "音频处理超过 5 分钟，已停止处理进程；原音频已保留。", e);
        }
        catch (Exception e)
        {
            long outputBytes = 0;
            try { if (File.Exists(pendingOutput)) outputBytes = new FileInfo(pendingOutput).Length; }
            catch (Exception fileError) when (fileError is IOException or UnauthorizedAccessException) { }
            SpeechWorkerClient.WriteDiagnostic(FormattableString.Invariant(
                $"audio-convert-failure pid={process.Id} error={e.GetType().Name} elapsed={Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s outSeconds={Interlocked.Read(ref lastOutputTime) / 1_000_000d:F2} outputBytes={outputBytes}"));
            StopEncoder();
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            finished.Set();
            await watchdog.ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            StopEncoder();
            if (!process.HasExited)
            {
                await Task.Factory.StartNew(() => process.WaitForExit(3000), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
            }
            if (process.HasExited) SpeechAssets.DeleteDirectory(stageDirectory);
            else SpeechWorkerClient.WriteDiagnostic("audio-convert-retirement-unconfirmed pid=" + process.Id);
        }
    }

    private static async Task ReadProgressAsync(StreamReader output, CancellationToken token, Action<long> report)
    {
        while (await output.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ||
                value < 0) continue;
            report(value);
        }
    }
    // All generated files are PCM. Read their actual sample count rather than
    // trusting estimates from text length or synthesis token counts.
    public static (int Rate, byte[] Data) Read(string path)
    {
        PcmInfo info = Inspect(path);
        byte[] data = new byte[info.DataLength];
        using var stream = File.OpenRead(path);
        stream.Position = info.DataOffset;
        stream.ReadExactly(data);
        return (info.Rate, data);
    }

    public static double Duration(string path)
    {
        PcmInfo info = Inspect(path);
        return info.DataLength / (2d * info.Rate);
    }

    public static void FitWindow(string path, double seconds)
    {
        PcmInfo pcm = Inspect(path);
        int expected = checked((int)Math.Round(seconds * pcm.Rate)) * 2;
        if (expected < 0 || expected > MaximumPcmBytes || Math.Abs(pcm.DataLength - expected) > pcm.Rate * .05 * 2)
            throw new SpeechFailure("VOICE0004", "变速后的音频无法准确放入固定区间，请调整区间或语速。");
        string fullPath = Path.GetFullPath(path);
        string pending = Path.Combine(Path.GetDirectoryName(fullPath)!, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var source = File.OpenRead(fullPath))
            using (var destination = File.Create(pending))
            {
                source.Position = pcm.DataOffset;
                if (pcm.DataLength > expected)
                {
                    source.Position += expected;
                    byte[] tail = new byte[64 * 1024];
                    int tailRemaining = pcm.DataLength - expected;
                    while (tailRemaining > 0)
                    {
                        int read = source.Read(tail, 0, Math.Min(tail.Length, tailRemaining));
                        if (read == 0) throw new EndOfStreamException("PCM WAV 数据在读取期间被截断。");
                        for (int i = 0; i + 1 < read; i += 2)
                            if (Math.Abs((int)BitConverter.ToInt16(tail, i)) > 32)
                                throw new SpeechFailure("VOICE0004", "语音结尾超出固定区间，请稍微延长区间。");
                        tailRemaining -= read;
                    }
                }

                using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
                WriteHeader(writer, pcm.Rate, expected);
                source.Position = pcm.DataOffset;
                CopyBytes(source, destination, Math.Min(pcm.DataLength, expected));
                WriteZeros(destination, expected - Math.Min(pcm.DataLength, expected));
            }
            File.Move(pending, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine("VOICE0007: " + e.Message); }
        }
    }

    public static void Join(IEnumerable<string> paths, string destination)
    {
        string[] sources = paths.ToArray();
        PcmInfo[] pieces = sources.Select(Inspect).ToArray();
        if (pieces.Length == 0 || pieces.Any(p => p.Rate != pieces[0].Rate))
            throw new SpeechFailure("VOICE0007", "语音片段采样率不一致。");
        long total = pieces.Sum(p => (long)p.DataLength);
        if (total > MaximumPcmBytes) throw new SpeechFailure("VOICE0007", "合并后的语音文件超过支持大小。");
        int length = checked((int)total);
        string fullDestination = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var output = File.Create(pending))
            {
                using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
                WriteHeader(writer, pieces[0].Rate, length);
                foreach (string sourcePath in sources)
                {
                    PcmInfo piece = Inspect(sourcePath);
                    using var input = File.OpenRead(sourcePath);
                    input.Position = piece.DataOffset;
                    CopyBytes(input, output, piece.DataLength);
                }
            }
            File.Move(pending, fullDestination, overwrite: true);
        }
        finally
        {
            try { File.Delete(pending); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine("VOICE0007: " + e.Message); }
        }
    }

    private static PcmInfo Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new SpeechFailure("VOICE0007", "语音文件不是 PCM WAV。");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new SpeechFailure("VOICE0007", "语音文件头无效。");
        int rate = 0;
        bool valid = false;
        while (stream.Position + 8 <= stream.Length)
        {
            string id = new(reader.ReadChars(4));
            uint size = reader.ReadUInt32();
            long offset = stream.Position;
            long next = checked(offset + size);
            if (next > stream.Length || size > MaximumPcmBytes)
                throw new SpeechFailure("VOICE0007", "语音文件长度无效。");
            if (id == "fmt ")
            {
                if (size < 16) throw new SpeechFailure("VOICE0007", "语音 PCM 格式块不完整。");
                ushort format = reader.ReadUInt16(), channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                int bytesPerSecond = reader.ReadInt32();
                ushort alignment = reader.ReadUInt16(), bits = reader.ReadUInt16();
                valid = format == 1 && channels == 1 && rate is >= 8000 and <= 192000 &&
                    bytesPerSecond == rate * 2 && alignment == 2 && bits == 16;
            }
            else if (id == "data" && valid && size > 0 && size % 2 == 0)
                return new PcmInfo(rate, offset, checked((int)size));
            stream.Position = next + size % 2;
        }
        throw new SpeechFailure("VOICE0007", "语音文件缺少 16 位单声道 PCM 数据。");
    }

    private static void WriteHeader(BinaryWriter writer, int rate, int dataLength)
    {
        writer.Write("RIFF"u8);
        writer.Write(checked(dataLength + 36));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
    }

    private static void CopyBytes(Stream input, Stream output, int count)
    {
        byte[] buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            int read = input.Read(buffer, 0, Math.Min(buffer.Length, count));
            if (read == 0) throw new EndOfStreamException("PCM WAV 数据在读取期间被截断。");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static void WriteZeros(Stream stream, int count)
    {
        byte[] zeros = new byte[Math.Min(64 * 1024, count)];
        while (count > 0)
        {
            int write = Math.Min(zeros.Length, count);
            stream.Write(zeros, 0, write);
            count -= write;
        }
    }
}
