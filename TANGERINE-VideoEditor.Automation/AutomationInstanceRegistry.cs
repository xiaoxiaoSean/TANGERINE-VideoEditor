using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tangerine.VideoEditor.Automation;

internal sealed record AutomationEndpoint(AutomationInstanceInfo Instance, string PipeName, string Token);

public static class AutomationInstanceRegistry
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TANGERINE-VideoEditor", "mcp", "instances");

    public static async Task<IReadOnlyList<AutomationInstanceInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await ReadEndpointsAsync(cancellationToken).ConfigureAwait(false)).Select(e => e.Instance).ToArray();
    }

    internal static async Task<IReadOnlyList<AutomationEndpoint>> ReadEndpointsAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            var endpoints = ReadEndpoints(out bool transientFailure);
            if (!transientFailure || attempt >= 3) return endpoints;
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<AutomationEndpoint> ReadEndpoints(out bool transientFailure)
    {
        transientFailure = false;
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<AutomationEndpoint>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                // Share deletion so an atomic heartbeat replacement never conflicts with readers.
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var endpoint = JsonSerializer.Deserialize<AutomationEndpoint>(input, AutomationJson.Options);
                if (endpoint is null || !IsAlive(endpoint.Instance))
                {
                    TryDelete(file);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(endpoint.PipeName) && endpoint.Token.Length == 64)
                    result.Add(endpoint);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A concurrently replaced registration is ignored; a subsequent scan retries it.
                transientFailure = true;
            }
        }
        return result.OrderBy(e => e.Instance.ProcessId).ToArray();
    }

    internal static bool IsAlive(AutomationInstanceInfo instance)
    {
        try
        {
            using var process = Process.GetProcessById(instance.ProcessId);
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - instance.ProcessStartTimeUtc.UtcDateTime).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
