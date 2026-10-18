using System.Buffers;
using System.Text.Json;

namespace Tangerine.VideoEditor.Automation;

internal sealed record AutomationEnvelope(string Token, AutomationRequest Request);

internal static class AutomationPipeProtocol
{
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var writer = new ArrayBufferWriter<byte>();
        var buffer = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("连接在完整消息传输前关闭。阶段代码：MCPIP0001");
            int newline = buffer.AsSpan(0, read).IndexOf((byte)'\n');
            int length = newline >= 0 ? newline : read;
            if (writer.WrittenCount + length > MaximumBytes)
                throw new InvalidDataException("本地通信消息超过 16 MiB 限制。阶段代码：MCPIP0002");
            writer.Write(buffer.AsSpan(0, length));
            if (newline >= 0) break;
        }
        return JsonSerializer.Deserialize<T>(writer.WrittenSpan, AutomationJson.Options)
            ?? throw new InvalidDataException("本地通信消息为空。阶段代码：MCPIP0003");
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, AutomationJson.Options);
        if (data.Length > MaximumBytes) throw new InvalidDataException("本地通信消息超过 16 MiB 限制。阶段代码：MCPIP0002");
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
