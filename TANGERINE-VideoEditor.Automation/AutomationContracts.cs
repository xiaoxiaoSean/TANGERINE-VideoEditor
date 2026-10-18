using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tangerine.VideoEditor.Automation;

public sealed record AutomationRequest(string Id, string Command, JsonObject? Arguments = null, long? ExpectedRevision = null,
    string? SessionId = null);

public sealed record AutomationError(string Code, string Message, string StageCode);

public sealed record AutomationResponse(string Id, bool Success, JsonNode? Data, AutomationError? Error, long Revision)
{
    public static AutomationResponse Ok(string id, JsonNode? data, long revision) => new(id, true, data, null, revision);
    public static AutomationResponse Fail(string id, string code, string message, string stageCode, long revision = 0) =>
        new(id, false, null, new(code, message, stageCode), revision);
}

public sealed record AutomationInstanceInfo
{
    public int ProcessId { get; init; }
    public DateTimeOffset ProcessStartTimeUtc { get; init; }
    public string InstanceId { get; init; } = "";
    public string ProcessName { get; init; } = "TANGERINE-VideoEditor";
    public string DisplayName { get; init; } = "TANGERINE VideoEditor";
    public string? ProjectName { get; init; }
    public string? ProjectPath { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
    public int CommandCount { get; init; }
    public string AppVersion { get; init; } = "1.0.0";

    public static AutomationInstanceInfo CreateForCurrentProcess(string? displayName = null, string? projectName = null,
        string? projectPath = null, long revision = 0, int commandCount = 0)
    {
        using var process = Process.GetCurrentProcess();
        return new()
        {
            ProcessId = process.Id,
            ProcessStartTimeUtc = process.StartTime.ToUniversalTime(),
            DisplayName = displayName ?? "TANGERINE VideoEditor",
            ProjectName = projectName,
            ProjectPath = projectPath,
            Revision = revision,
            UpdatedUtc = DateTimeOffset.UtcNow,
            CommandCount = commandCount,
        };
    }
}

public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        MaxDepth = 96,
    };

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);
}
