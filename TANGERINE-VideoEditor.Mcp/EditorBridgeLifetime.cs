using Microsoft.Extensions.Hosting;

namespace Tangerine.VideoEditor.Mcp;

/// <summary>
/// Release the control lease during host shutdown rather than waiting for the
/// stdio transport and service provider to finish their own disposal sequence.
/// </summary>
internal sealed class EditorBridgeLifetime(EditorMcpBridge bridge) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => bridge.DisposeAsync().AsTask();
}
