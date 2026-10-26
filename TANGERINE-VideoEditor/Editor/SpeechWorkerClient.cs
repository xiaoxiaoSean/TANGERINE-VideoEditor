using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Tangerine.VideoEditor.Speech;

namespace TANGERINE_VideoEditor.Editor;

/// <summary>Local stdio workers with independent protocol readers and deadlines.</summary>
public sealed class SpeechWorkerClient : IDisposable
{
    private readonly WorkerSlot _devices = new();
    private readonly WorkerPool _cpu = new(SpeechPerformance.MaxWorkers);
    private readonly ConcurrentDictionary<string, WorkerPool> _gpu = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failedAutomaticGpu = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _preferHybrid = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _deviceLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private string? _automaticDevice;
    private bool _disposed;

    public async Task<SpeechEvent> SendAsync(SpeechRequest request, IProgress<SpeechProgress>? progress, CancellationToken token)
    {
        CancellationTokenSource lifetime;
        lock (_deviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        }
        using (lifetime)
        {
            token = lifetime.Token;
            try
            {
                if (request.Command == "devices")
                    return await _devices.SendAsync(request, progress, token).ConfigureAwait(false);
                if (request.Device == "cpu")
                    return await SendOnCpuAsync(request, progress, token).ConfigureAwait(false);
                if (request.Device == "auto")
                {
                    string device = await AutomaticDeviceAsync(progress, token).ConfigureAwait(false);
                    if (device == "cpu" || _failedAutomaticGpu.ContainsKey(DeviceModelKey(device, request.ModelPath)))
                        return await SendOnCpuAsync(CopyRequest(request, "cpu"), progress, token).ConfigureAwait(false);
                    return await SendOnGpuAsync(device, request, progress, token, automatic: true).ConfigureAwait(false);
                }
                if (!request.Device.StartsWith("dml:", StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(request.Device.AsSpan(4), out int index) || index is < 0 or >= 32)
                    throw new SpeechFailure("VOICE0001", "未知语音推理设备。");
                return await SendOnGpuAsync($"dml:{index}", request, progress, token, automatic: false).ConfigureAwait(false);
            }
            catch (SpeechWorkerTimeoutException e)
            {
                throw new SpeechFailure("VOICE0002", e.Message, e);
            }
        }
    }

    private async Task<string> AutomaticDeviceAsync(IProgress<SpeechProgress>? progress, CancellationToken token)
    {
        lock (_deviceLock) if (_automaticDevice is { } cached) return cached;
        try
        {
            SpeechEvent response = await _devices.SendAsync(new SpeechRequest { Command = "devices" }, null, token).ConfigureAwait(false);
            string selected = response.Devices?.FirstOrDefault(d => d.Available && d.Id.StartsWith("dml:", StringComparison.OrdinalIgnoreCase))?.Id ?? "cpu";
            lock (_deviceLock) return _automaticDevice ??= selected;
        }
        catch (Exception e) when (e is SpeechFailure or SpeechWorkerTimeoutException)
        {
            progress?.Report(new("显卡查询失败，本次改用 CPU：" + e.Message, -1, "CPU"));
            lock (_deviceLock) return _automaticDevice ??= "cpu";
        }
    }

    private static string DeviceModelKey(string device, string model) => device + "|" + Path.GetFullPath(model);

    private async Task<SpeechEvent> SendOnGpuAsync(string device, SpeechRequest request, IProgress<SpeechProgress>? progress,
        CancellationToken token, bool automatic)
    {
        WorkerPool pool;
        lock (_deviceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Each GPU worker owns a complete ONNX session and model copy.
            // Parallel sentence workers on one adapter multiply memory use
            // without improving DirectML throughput for this autoregressive model.
            pool = _gpu.GetOrAdd(device, static _ => new WorkerPool(1));
        }
        string key = DeviceModelKey(device, request.ModelPath);
        bool hybridRetry = request.PreferHybrid || _preferHybrid.ContainsKey(key);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool retryHybrid = false, useCpu = false;
            WorkerSlot slot = await pool.EnterAsync(token).ConfigureAwait(false);
            hybridRetry |= _preferHybrid.ContainsKey(key);
            try
            {
                if (automatic && _failedAutomaticGpu.ContainsKey(key)) useCpu = true;
                else
                {
                    try
                    {
                        SpeechEvent response = await slot.RunAsync(CopyRequest(request, automatic ? "auto" : device, hybridRetry), progress, token,
                            message =>
                            {
                                if (message.GpuMode == "hybrid") _preferHybrid.TryAdd(key, 0);
                                if (automatic && message.GpuMode == "cpu") _failedAutomaticGpu.TryAdd(key, 0);
                            }).ConfigureAwait(false);
                        if (automatic && (response.GpuMode == "cpu" || response.Backend?.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) == true))
                        {
                            _failedAutomaticGpu.TryAdd(key, 0);
                            slot.RetireCurrent();
                        }
                        return response;
                    }
                    catch (SpeechWorkerTimeoutException e) when (!token.IsCancellationRequested && IsGpuStage(e.Stage))
                    {
                        // A full graph timeout must not blacklist a working GPU predictor.
                        // Keep this decision in the editor so idle retirement cannot reset it.
                        if (!hybridRetry && e.GpuMode == "full")
                        {
                            _preferHybrid.TryAdd(key, 0);
                            retryHybrid = true;
                        }
                        else if (automatic)
                        {
                            _failedAutomaticGpu.TryAdd(key, 0);
                            useCpu = true;
                        }
                        else throw;
                    }
                }
            }
            finally { pool.Leave(slot); }
            DeletePartialOutput(request.OutputPath);
            if (retryHybrid)
            {
                hybridRetry = true;
                progress?.Report(new("显卡完整模型无进展，正在使用所选显卡的编码预测器重试", -1, device));
                continue;
            }
            if (useCpu)
            {
                progress?.Report(new("显卡推理无进展，本次任务后续句子改用 CPU", -1, "CPU"));
                return await SendOnCpuAsync(CopyRequest(request, "cpu"), progress, token).ConfigureAwait(false);
            }
        }
        throw new SpeechFailure("VOICE0002", "显卡推理重试失败，请检查本地语音诊断日志。");
    }

    private static bool IsGpuStage(string? stage) => stage?.EndsWith("-gpu", StringComparison.Ordinal) == true ||
        stage is "gpu-graph" or "prefill" or "frames";

    private async Task<SpeechEvent> SendOnCpuAsync(SpeechRequest request, IProgress<SpeechProgress>? progress, CancellationToken token)
    {
        WorkerSlot slot = await _cpu.EnterAsync(token).ConfigureAwait(false);
        try { return await slot.RunAsync(request, progress, token).ConfigureAwait(false); }
        finally { _cpu.Leave(slot); }
    }

    private static SpeechRequest CopyRequest(SpeechRequest request, string device, bool? preferHybrid = null) => new()
    {
        Command = request.Command, ModelPath = request.ModelPath, Device = device, Text = request.Text,
        Language = request.Language, Speaker = request.Speaker, ReferencePath = request.ReferencePath,
        ReferenceText = request.ReferenceText, OutputPath = request.OutputPath,
        CpuThreads = request.CpuThreads, PreferHybrid = preferHybrid ?? request.PreferHybrid
    };

    private static void DeletePartialOutput(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine("VOICE0002: " + e.Message); }
    }

    public void Dispose()
    {
        lock (_deviceLock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _devices.Close();
            _cpu.Close();
            foreach (WorkerPool pool in _gpu.Values) pool.Close();
        }
        // Requests may still inspect their linked token while native pipes are
        // closing. Do not dispose the lifetime source underneath those readers.
    }

    public async Task RetireIdleInferenceWorkersAsync()
    {
        WorkerPool[] pools;
        lock (_deviceLock)
        {
            pools = [_cpu, .. _gpu.Values];
        }
        bool[] retired = await Task.WhenAll(pools.Select(pool => pool.RetireIdleAsync())).ConfigureAwait(false);
        if (retired.Any(success => !success))
            throw new SpeechFailure("VOICE0002", "语音模型工作进程未能及时退出；已停止后处理，旧音频保持不变。");
    }

    private sealed class SpeechWorkerTimeoutException(string message, string? stage, string? backend, string? gpuMode) : Exception(message)
    {
        public string? Stage { get; } = stage;
        public string? Backend { get; } = backend;
        public string? GpuMode { get; } = gpuMode;
    }

    private sealed class WorkerPool
    {
        private readonly WorkerSlot[] _slots;
        private readonly SemaphoreSlim _available;
        private int _closed;

        public WorkerPool(int capacity)
        {
            capacity = Math.Max(1, capacity);
            _slots = Enumerable.Range(0, capacity).Select(_ => new WorkerSlot()).ToArray();
            _available = new SemaphoreSlim(capacity, capacity);
        }

        public async Task<WorkerSlot> EnterAsync(CancellationToken token)
        {
            await _available.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                foreach (WorkerSlot slot in _slots) if (slot.Gate.Wait(0)) return slot;
                throw new InvalidOperationException("语音工作进程队列状态无效。");
            }
            catch { _available.Release(); throw; }
        }
        public void Leave(WorkerSlot slot) { slot.Gate.Release(); _available.Release(); }
        public async Task<bool> RetireIdleAsync()
        {
            bool success = true;
            foreach (WorkerSlot slot in _slots)
            {
                if (!await _available.WaitAsync(0).ConfigureAwait(false)) continue;
                if (!slot.Gate.Wait(0)) { _available.Release(); continue; }
                try { success &= await slot.RetireIfIdleAsync().ConfigureAwait(false); }
                finally { slot.Gate.Release(); _available.Release(); }
            }
            return success;
        }
        public void Close()
        {
            Interlocked.Exchange(ref _closed, 1);
            foreach (WorkerSlot slot in _slots) slot.Close();
        }
    }

    private sealed class WorkerSlot
    {
        private static readonly TimeSpan IdleRetirement = TimeSpan.FromMinutes(2);
        public readonly SemaphoreSlim Gate = new(1, 1);
        private readonly object _processLock = new();
        private readonly Timer _idleTimer;
        private Process? _process;
        private Task<bool> _retirement = Task.FromResult(true);
        private bool _closed, _active;
        private int _cpuThreads;
        private long _idleSince;

        public WorkerSlot() => _idleTimer = new Timer(_ => RetireWhenIdle(), null, Timeout.Infinite, Timeout.Infinite);

        public async Task<SpeechEvent> SendAsync(SpeechRequest request, IProgress<SpeechProgress>? progress, CancellationToken token)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try { return await RunAsync(request, progress, token).ConfigureAwait(false); }
            finally { Gate.Release(); }
        }

        public async Task<SpeechEvent> RunAsync(SpeechRequest request, IProgress<SpeechProgress>? progress, CancellationToken token,
            Action<SpeechEvent>? observe = null)
        {
            token.ThrowIfCancellationRequested();
            int cpuThreads;
            Task<bool> retirement;
            lock (_processLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                cpuThreads = request.CpuThreads > 0 ? Math.Clamp(request.CpuThreads, 1, Environment.ProcessorCount)
                    : SpeechPerformance.ThreadsPerWorker(SpeechPerformance.MaxWorkers);
                if (_process is not null && request.Command == "synthesize" && _cpuThreads != cpuThreads) RetireCurrent();
                retirement = _retirement;
            }
            if (!await retirement.ConfigureAwait(false))
                throw new SpeechFailure("VOICE0002", "上一个语音工作进程未能退出，已阻止加载第二份模型。");
            token.ThrowIfCancellationRequested();
            Process process;
            lock (_processLock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                process = StartOrReuse(cpuThreads);
                _active = true;
            }
            var state = new RequestState(request.Command, process, token);
            // Neither stdin/stdout consumption nor deadline enforcement relies on
            // ThreadPool continuations. Native inference can saturate those workers.
            var reader = new Thread(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    process.StandardInput.WriteLine(JsonSerializer.Serialize(request, SpeechJson.Options));
                    process.StandardInput.Flush();
                    long lastUiReport = 0;
                    string? lastUiBackend = null, lastUiPhase = null, observedGpuMode = null;
                    while (!state.IsFinished)
                    {
                        string? line = process.StandardOutput.ReadLine();
                        if (line is null) break;
                        if (line.Length > 2_000_000) throw new SpeechFailure("VOICE0002", "语音工作进程返回内容过大。");
                        SpeechEvent message = JsonSerializer.Deserialize<SpeechEvent>(line, SpeechJson.Options)
                            ?? throw new SpeechFailure("VOICE0002", "语音工作进程返回空结果。");
                        if (state.IsFinished) return;
                        state.RecordDiagnostic(message);
                        if (message.GpuMode != observedGpuMode || message.Type is "error" or "result" || message.DiagnosticCode is not null)
                        {
                            observedGpuMode = message.GpuMode;
                            observe?.Invoke(message);
                        }
                        if (message.Type == "error") throw new SpeechFailure(message.StageCode ?? "VOICE0003", message.Message);
                        if (message.Type == "result")
                        {
                            token.ThrowIfCancellationRequested();
                            state.Complete(message, () => MarkIdle(process));
                            return;
                        }
                        state.Progress(message);
                        string phase = UiPhase(message.Stage);
                        if (message.DiagnosticCode is not null || lastUiReport == 0 || message.Backend != lastUiBackend || phase != lastUiPhase ||
                            Stopwatch.GetElapsedTime(lastUiReport) >= TimeSpan.FromMilliseconds(150))
                        {
                            lastUiReport = Stopwatch.GetTimestamp();
                            lastUiBackend = message.Backend; lastUiPhase = phase;
                            progress?.Report(new(message.Message, -1, message.Backend));
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    throw new SpeechFailure("VOICE0002", "语音工作进程意外退出；请检查模型、显存和显卡驱动。");
                }
                catch (Exception error)
                {
                    Exception failure = token.IsCancellationRequested ? new OperationCanceledException(token) : error;
                    FinishFailureAfterRetirement(process, state, failure);
                }
                finally { state.ThreadExited(); }
            }) { IsBackground = true, Name = "Speech protocol" };
            var watchdog = new Thread(() =>
            {
                try
                {
                    while (!state.WaitForCompletion(250))
                    {
                        Exception? failure = state.DeadlineFailure();
                        if (failure is not null)
                        {
                            FinishFailureAfterRetirement(process, state, failure);
                            return;
                        }
                        state.LogIfDue();
                    }
                }
                catch (Exception error)
                {
                    FinishFailureAfterRetirement(process, state,
                        new SpeechFailure("VOICE0002", "语音监控线程失败：" + error.Message, error));
                }
                finally { state.ThreadExited(); }
            }) { IsBackground = true, Name = "Speech deadline" };
            bool readerStarted = false, watchdogStarted = false;
            try
            {
                watchdog.Start(); watchdogStarted = true;
                reader.Start(); readerStarted = true;
            }
            catch (Exception error)
            {
                // This catch is asynchronous; wait without blocking the UI
                // thread if either dedicated protocol thread cannot start.
                if (state.BeginFailure(error))
                {
                    bool retired = false;
                    try { retired = await Retire(process).ConfigureAwait(false); }
                    catch (Exception retirementError)
                    {
                        SpeechDiagnostics.Write("worker-retirement-error pid=" + SafeId(process) + " code=" + retirementError.GetType().Name);
                    }
                    if (!retired) state.RetirementUnconfirmed();
                    state.FinishFailure();
                }
                if (!readerStarted) state.ThreadExited();
                if (!watchdogStarted) state.ThreadExited();
            }
            try { return await state.Completion.Task.ConfigureAwait(false); }
            catch
            {
                // Do not let the caller delete request files while the failed
                // native worker may still have them open for writing.
                if (!await Retire(process).ConfigureAwait(false))
                    throw new SpeechFailure("VOICE0002", "语音工作进程未能确认退出；临时音频文件已保留。");
                throw;
            }
        }

        private void FinishFailureAfterRetirement(Process process, RequestState state, Exception failure)
        {
            if (!state.BeginFailure(failure)) return;
            bool retired = false;
            try { retired = Retire(process).GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                SpeechDiagnostics.Write("worker-retirement-error pid=" + SafeId(process) + " code=" + error.GetType().Name);
            }
            if (!retired) state.RetirementUnconfirmed();
            state.FinishFailure();
        }

        private static string UiPhase(string? stage) => stage switch
        {
            "frames" or "gpu-graph" or "decode-cpu" or "decode-gpu" or "predict-cpu" or "predict-gpu" => "frames",
            "loading" or "loading-cpu" or "loading-gpu" => "loading",
            "prefill" or "prefill-cpu" or "prefill-gpu" => "prefill", _ => stage ?? ""
        };
        private void MarkIdle(Process process)
        {
            lock (_processLock)
            {
                if (_closed || !ReferenceEquals(_process, process)) return;
                _active = false;
                _idleSince = Stopwatch.GetTimestamp();
                _idleTimer.Change(IdleRetirement, Timeout.InfiniteTimeSpan);
            }
        }
        private void RetireWhenIdle()
        {
            lock (_processLock)
                if (!_closed && !_active && _process is { } process && Stopwatch.GetElapsedTime(_idleSince) >= IdleRetirement)
                    _ = Retire(process);
        }
        private static string StageName(string stage) => stage switch
        {
            "loading" or "loading-cpu" or "loading-gpu" => "模型加载",
            "prefill" or "prefill-cpu" or "prefill-gpu" => "文本预处理",
            "frames" or "decode-cpu" or "decode-gpu" => "语音帧生成",
            "reference" => "参考录音编码", "gpu-graph" or "predict-gpu" => "显卡编码预测",
            "predict-cpu" => "CPU 编码预测", "vocoder" => "声码器", "writing" => "音频写入", _ => "推理"
        };
        private static TimeSpan IdleLimit(string command, string stage) => command == "devices" ? TimeSpan.FromSeconds(20) : stage switch
        {
            "loading" or "loading-cpu" or "loading-gpu" => TimeSpan.FromMinutes(4),
            "prefill" or "prefill-cpu" or "prefill-gpu" => TimeSpan.FromMinutes(3),
            "frames" or "gpu-graph" or "decode-cpu" or "decode-gpu" or "predict-cpu" or "predict-gpu" => TimeSpan.FromMinutes(2),
            "writing" => TimeSpan.FromSeconds(45), _ => TimeSpan.FromMinutes(3)
        };

        private Process StartOrReuse(int cpuThreads)
        {
            if (_process is not null && !_process.HasExited) return _process;
            _process?.Dispose();
            string executable = Path.Combine(AppContext.BaseDirectory, "speech", "TANGERINE-VideoEditor.Speech.exe");
            if (!File.Exists(executable)) throw new SpeechFailure("VOICE0002", "缺少语音工作进程，请重新构建完整 Debug 程序。");
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            startInfo.Environment["TANGERINE_APP_ROOT"] = AppStorage.Root;
            Process process = Process.Start(startInfo) ?? throw new SpeechFailure("VOICE0002", "无法启动语音工作进程。");
            _process = process; _cpuThreads = cpuThreads;
            // A full stderr pipe blocks native ONNX Runtime logging. Drain it on
            // an independent reader; never block that reader in a Trace listener.
            new Thread(() =>
            {
                try
                {
                    var buffer = new char[4096];
                    while (process.StandardError.Read(buffer, 0, buffer.Length) != 0) { }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
            }) { IsBackground = true, Name = "Speech stderr" }.Start();
            return process;
        }
        public void Close()
        {
            lock (_processLock)
            {
                if (_closed) return;
                _closed = true; _idleTimer.Dispose();
                if (_process is { } process) Retire(process);
            }
        }
        public void RetireCurrent()
        {
            lock (_processLock) if (_process is { } process) Retire(process);
        }
        public async Task<bool> RetireIfIdleAsync()
        {
            Process? process;
            Task<bool> retirement;
            lock (_processLock)
            {
                if (_active) return true;
                if (_process is { } current)
                {
                    _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    process = current;
                    retirement = Task.FromResult(true);
                }
                else
                {
                    process = null;
                    retirement = _retirement;
                }
            }
            return await (process is null ? retirement : Retire(process)).ConfigureAwait(false);
        }
        private Task<bool> Retire(Process process)
        {
            lock (_processLock)
            {
                if (!ReferenceEquals(_process, process)) return _retirement;
                _process = null; _active = false;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                { SpeechDiagnostics.Write("worker-stop-failed pid=" + SafeId(process) + " code=" + e.GetType().Name); }
                _retirement = Task.Factory.StartNew(() => WaitForRetiredProcess(process), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return _retirement;
            }
        }
        private static bool WaitForRetiredProcess(Process process)
        {
            try
            {
                if (process.HasExited) return true;
                if (!process.WaitForExit(15_000))
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    { SpeechDiagnostics.Write("worker-stop-retry-failed pid=" + SafeId(process) + " code=" + e.GetType().Name); }
                    if (!process.WaitForExit(5_000) && !process.HasExited)
                    {
                        SpeechDiagnostics.Write("worker-stop-timeout pid=" + SafeId(process));
                        return false;
                    }
                }
                return true;
            }
            catch (InvalidOperationException) { return true; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                SpeechDiagnostics.Write("worker-wait-failed pid=" + SafeId(process) + " code=" + e.GetType().Name);
                return false;
            }
            finally { process.Dispose(); }
        }
        private static int SafeId(Process process) { try { return process.Id; } catch (InvalidOperationException) { return 0; } }

        private sealed class RequestState
        {
            public readonly TaskCompletionSource<SpeechEvent> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _sync = new();
            private readonly ManualResetEventSlim _completed = new(false);
            private readonly CancellationToken _token;
            private readonly Process _process;
            private readonly string _command;
            private readonly int _pid;
            private readonly long _started = Stopwatch.GetTimestamp();
            private long _lastProgress = Stopwatch.GetTimestamp(), _lastLog = Stopwatch.GetTimestamp();
            private double _lastCpuSeconds;
            private string _stage = "starting", _key = "";
            private string? _backend, _gpuMode;
            private Exception? _failure;
            private bool _finished;
            private int _threads = 2;
            public RequestState(string command, Process process, CancellationToken token)
            {
                _command = command; _process = process; _pid = SafeId(process); _token = token;
                _lastCpuSeconds = ReadCpuSeconds();
                SpeechDiagnostics.Write("start pid=" + _pid + " command=" + command);
            }
            public void RecordDiagnostic(SpeechEvent message)
            {
                if (message.DiagnosticCode is null) return;
                SpeechDiagnostics.Write("graph-diagnostic " + JsonSerializer.Serialize(new
                {
                    pid = _pid, stage = message.Stage, gpuMode = message.GpuMode,
                    code = message.DiagnosticCode, graph = message.DiagnosticGraph,
                    exception = message.DiagnosticException, reason = message.DiagnosticReason,
                    elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds
                }, SpeechJson.Options));
            }
            public bool IsFinished { get { lock (_sync) return _finished || _failure is not null; } }
            public bool WaitForCompletion(int milliseconds) => _completed.Wait(milliseconds);
            public void Progress(SpeechEvent message)
            {
                lock (_sync)
                {
                    if (_finished || _failure is not null) return;
                    _backend = message.Backend ?? _backend; _gpuMode = message.GpuMode ?? _gpuMode;
                    string next = message.Stage + "|" + message.Message;
                    if (next == _key) return;
                    _key = next; _stage = message.Stage ?? ""; _lastProgress = Stopwatch.GetTimestamp();
                }
            }
            public Exception? DeadlineFailure()
            {
                lock (_sync)
                {
                    if (_finished || _failure is not null) return null;
                    if (_token.IsCancellationRequested) return new OperationCanceledException(_token);
                    if (Stopwatch.GetElapsedTime(_lastProgress) < IdleLimit(_command, _stage)) return null;
                    return new SpeechWorkerTimeoutException($"语音工作进程在 {StageName(_stage)} 阶段长时间没有真实进展，已停止该进程。", _stage, _backend, _gpuMode);
                }
            }
            public bool Complete(SpeechEvent message, Action beforeCompletion)
            {
                lock (_sync)
                {
                    if (_finished || _failure is not null) return false;
                    _backend = message.Backend ?? _backend; _gpuMode = message.GpuMode ?? _gpuMode;
                    beforeCompletion();
                    Log("complete");
                    _finished = true;
                    Completion.TrySetResult(message); _completed.Set(); return true;
                }
            }
            public bool BeginFailure(Exception error)
            {
                lock (_sync)
                {
                    if (_finished || _failure is not null) return false;
                    _failure = error; Log(error is OperationCanceledException ? "cancel" : "failure:" + error.GetType().Name); return true;
                }
            }
            public void FinishFailure()
            {
                lock (_sync)
                {
                    _finished = true;
                    if (_failure is OperationCanceledException) Completion.TrySetCanceled(_token);
                    else Completion.TrySetException(_failure ?? new SpeechFailure("VOICE0002", "语音请求中断。"));
                    _completed.Set();
                }
            }
            public void RetirementUnconfirmed()
            {
                lock (_sync)
                    if (!_finished)
                        _failure = new SpeechFailure("VOICE0002", "语音工作进程未能确认退出；临时音频文件已保留。");
            }
            public void LogIfDue()
            {
                lock (_sync)
                {
                    if (_finished || Stopwatch.GetElapsedTime(_lastLog) < TimeSpan.FromSeconds(15)) return;
                    long now = Stopwatch.GetTimestamp();
                    double cpuSeconds = ReadCpuSeconds();
                    double cpuDelta = cpuSeconds >= 0 && _lastCpuSeconds >= 0 ? cpuSeconds - _lastCpuSeconds : 0;
                    _lastCpuSeconds = cpuSeconds;
                    if (cpuDelta >= .05) _lastProgress = now;
                    Log(cpuDelta >= .05 ? "running-cpu-progress" : "running-no-cpu-progress", cpuDelta);
                    _lastLog = now;
                }
            }
            private double ReadCpuSeconds()
            {
                try { _process.Refresh(); return _process.TotalProcessorTime.TotalSeconds; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
                { return -1; }
            }
            private void Log(string status, double? cpuDelta = null)
            {
                // Diagnostics must never prevent a request from publishing its result.
                try
                {
                    double cpu = 0; long memory = 0;
                    try { _process.Refresh(); cpu = _process.TotalProcessorTime.TotalSeconds; memory = _process.PrivateMemorySize64; }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    string delta = cpuDelta is double value ? FormattableString.Invariant($" cpuDelta={value:F2}s") : "";
                    SpeechDiagnostics.Write(FormattableString.Invariant($"{status} pid={_pid} stage={_stage} gpu={_gpuMode} elapsed={Stopwatch.GetElapsedTime(_started).TotalSeconds:F1}s noProgress={Stopwatch.GetElapsedTime(_lastProgress).TotalSeconds:F1}s cpu={cpu:F2}s private={memory} backend={_backend}") + delta);
                }
                catch (Exception) { }
            }
            public void ThreadExited() { if (Interlocked.Decrement(ref _threads) == 0) _completed.Dispose(); }
        }
    }

    /// <summary>Bounded local diagnostic queue; contains no text or reference audio.</summary>
    private static class SpeechDiagnostics
    {
        private static readonly BlockingCollection<string> Queue = new(256);
        static SpeechDiagnostics() => new Thread(Drain) { IsBackground = true, Name = "Speech diagnostics" }.Start();
        public static void Write(string message) => Queue.TryAdd(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + message);
        private static void Drain()
        {
            string directory = Path.Combine(AppStorage.DataRoot, "Logs");
            string path = Path.Combine(directory, "speech-worker.log");
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                        File.Move(path, path + ".previous", overwrite: true);
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
