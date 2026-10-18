using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tangerine.VideoEditor.Automation;

public sealed class AutomationInstanceServer : IAsyncDisposable
{
    private readonly Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> _handler;
    private readonly Func<AutomationInstanceInfo> _instanceProvider;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly SemaphoreSlim _connectionSlots = new(15, 15);
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _token = AutomationInstanceRegistry.NewToken();
    private readonly string _pipeName;
    private readonly string _registrationPath;
    private Task? _listenTask;
    private Task? _heartbeatTask;
    private int _connectionId;

    public AutomationInstanceServer(Func<AutomationRequest, CancellationToken, Task<AutomationResponse>> handler,
        Func<AutomationInstanceInfo> instanceProvider)
    {
        _handler = handler;
        _instanceProvider = instanceProvider;
        _pipeName = $"Tangerine.VideoEditor.Mcp.{Environment.ProcessId}.{_instanceId}";
        _registrationPath = Path.Combine(AutomationInstanceRegistry.DirectoryPath, $"{Environment.ProcessId}-{_instanceId}.json");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listenTask is not null) return;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(AutomationInstanceRegistry.DirectoryPath);
        await PublishAsync(cancellationToken).ConfigureAwait(false);
        _listenTask = ListenAsync(_stop.Token);
        _heartbeatTask = HeartbeatAsync(_stop.Token);
    }

    private async Task PublishAsync(CancellationToken token)
    {
        await _publishLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var info = _instanceProvider() with { InstanceId = _instanceId, UpdatedUtc = DateTimeOffset.UtcNow };
            var endpoint = new AutomationEndpoint(info, _pipeName, _token);
            string temporary = _registrationPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(endpoint, AutomationJson.Options), token).ConfigureAwait(false);
            File.Move(temporary, _registrationPath, true);
        }
        finally { _publishLock.Release(); }
    }

    private async Task HeartbeatAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await PublishAsync(token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _connectionSlots.WaitAsync(token).ConfigureAwait(false);
                var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 16,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch { _connectionSlots.Release(); await pipe.DisposeAsync().ConfigureAwait(false); throw; }
                int id = Interlocked.Increment(ref _connectionId);
                var connection = HandleAsync(pipe, token);
                _connections[id] = connection;
                _ = connection.ContinueWith(_ => { _connections.TryRemove(id, out var ignored); _connectionSlots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopToken)
    {
        await using (pipe.ConfigureAwait(false))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var envelope = await AutomationPipeProtocol.ReadAsync<AutomationEnvelope>(pipe, timeout.Token).ConfigureAwait(false);
                var request = envelope.Request;
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(envelope.Token), Encoding.UTF8.GetBytes(_token)))
                {
                    await AutomationPipeProtocol.WriteAsync(pipe, AutomationResponse.Fail(request.Id, "unauthorized", "本地通信身份验证失败。", "MCPIP0004"), timeout.Token).ConfigureAwait(false);
                    return;
                }
                AutomationResponse response;
                using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var disconnected = MonitorDisconnectAsync(pipe, timeout, disconnect.Token);
                try { response = await _handler(request, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    response = AutomationResponse.Fail(request.Id, "cancelled", "操作已取消或超时。", "MCPIP0005");
                }
                catch (Exception ex)
                {
                    response = AutomationResponse.Fail(request.Id, "command_failed", ex.Message, "MCPIP0006");
                }
                await disconnect.CancelAsync().ConfigureAwait(false);
                await disconnected.ConfigureAwait(false);
                await AutomationPipeProtocol.WriteAsync(pipe, response, timeout.Token).ConfigureAwait(false);
                try { await PublishAsync(stopToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException) { }
        }
    }

    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource operation, CancellationToken token)
    {
        try
        {
            var buffer = new byte[1];
            if (await pipe.ReadAsync(buffer, token).ConfigureAwait(false) == 0)
                await operation.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { await operation.CancelAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listenTask is not null) await _listenTask.ConfigureAwait(false);
        if (_heartbeatTask is not null) await _heartbeatTask.ConfigureAwait(false);
        try { await Task.WhenAll(_connections.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        AutomationInstanceRegistry.TryDelete(_registrationPath);
        AutomationInstanceRegistry.TryDelete(_registrationPath + ".tmp");
        _stop.Dispose();
    }
}
