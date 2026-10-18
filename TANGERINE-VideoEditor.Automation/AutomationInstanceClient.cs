using System.IO.Pipes;

namespace Tangerine.VideoEditor.Automation;

public static class AutomationInstanceClient
{
    public static async Task<AutomationResponse> SendAsync(int processId, AutomationRequest request,
        CancellationToken cancellationToken = default, string? expectedInstanceId = null)
    {
        var endpoint = (await AutomationInstanceRegistry.ReadEndpointsAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(e => e.Instance.ProcessId == processId);
        if (endpoint is null)
            return AutomationResponse.Fail(request.Id, "instance_not_found", $"没有找到进程号为 {processId} 的正在运行的编辑器。", "MCPIP0007");
        if (expectedInstanceId is not null && !StringComparer.Ordinal.Equals(endpoint.Instance.InstanceId, expectedInstanceId))
            return AutomationResponse.Fail(request.Id, "instance_changed", "选中的编辑器进程已经重启，请重新选择实例。", "MCPIP0008");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await pipe.ConnectAsync(connectionTimeout.Token).ConfigureAwait(false);
            }
            await AutomationPipeProtocol.WriteAsync(pipe, new AutomationEnvelope(endpoint.Token, request), timeout.Token).ConfigureAwait(false);
            return await AutomationPipeProtocol.ReadAsync<AutomationResponse>(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return AutomationResponse.Fail(request.Id, "connection_failed", $"无法连接编辑器进程 {processId}：{ex.Message}", "MCPIP0009");
        }
    }
}
