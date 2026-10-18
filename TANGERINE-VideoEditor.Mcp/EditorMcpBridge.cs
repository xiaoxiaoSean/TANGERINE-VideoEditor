using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using Tangerine.VideoEditor.Automation;

namespace Tangerine.VideoEditor.Mcp;

public sealed class EditorMcpBridge : IAsyncDisposable
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<int, string> _controlledInstances = new();
    private AutomationInstanceInfo? _selected;
    private readonly IReadOnlyList<Tool> _tools;

    public EditorMcpBridge()
    {
        var tools = new List<Tool>
        {
            CreateTool("list_instances", "无需操作用户界面或获取控制权，直接列出所有正在运行的编辑器实例及各自打开的工程：processId、instanceId、projectName、projectPath、revision 和版本。未保存工程路径可为空。随后可直接向 editor_control_acquire 传入目标 processId 进行锁定。", EmptySchema(), true),
            CreateTool("select_instance", "为当前 MCP 会话的后续命令选择目标编辑器进程。",
                JsonNode.Parse("""{"type":"object","properties":{"processId":{"type":"integer","minimum":1}},"required":["processId"],"additionalProperties":false}""")!.AsObject(), true),
        };
        foreach (var command in AutomationCatalog.Commands)
        {
            var schema = command.InputSchema.DeepClone().AsObject();
            if (schema["properties"] is not JsonObject properties) schema["properties"] = properties = new();
            properties["processId"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "目标编辑器进程号。省略时使用 select_instance 的选择；仅有一个编辑器运行时可以自动选择。" };
            properties["expectedRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "可选的并发修改保护。修订号过期时拒绝操作，避免覆盖界面中的较新修改。" };
            tools.Add(CreateTool(command.Name, command.Description, schema, command.ReadOnly, command.Destructive));
        }
        _tools = tools;
    }

    public IReadOnlyList<Tool> Tools => _tools;

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams call, CancellationToken token)
    {
        try
        {
            JsonObject arguments = JsonSerializer.SerializeToNode(call.Arguments, AutomationJson.Options)?.AsObject() ?? new();
            if (call.Name == "list_instances")
            {
                var instances = await AutomationInstanceRegistry.ListAsync(token);
                return Result(new JsonObject { ["instances"] = AutomationJson.ToNode(instances), ["selectedProcessId"] = Volatile.Read(ref _selected)?.ProcessId });
            }
            if (call.Name == "select_instance")
            {
                int processId = arguments["processId"]?.GetValue<int>() ?? throw new ArgumentException("缺少参数：processId。");
                var instances = await AutomationInstanceRegistry.ListAsync(token);
                var instance = instances.SingleOrDefault(x => x.ProcessId == processId);
                if (instance is null) return Error("instance_not_found", $"没有找到进程号为 {processId} 的正在运行的编辑器。", "MCPBR0001");
                Volatile.Write(ref _selected, instance);
                return Result(new JsonObject { ["selected"] = AutomationJson.ToNode(instance) });
            }
            if (AutomationCatalog.Find(call.Name) is null) return Error("unknown_tool", $"不支持的工具：{call.Name}。", "MCPBR0002");
            var selected = Volatile.Read(ref _selected);
            int? explicitProcessId = arguments["processId"]?.GetValue<int>();
            arguments.Remove("processId");
            long? expectedRevision = arguments["expectedRevision"]?.GetValue<long>();
            arguments.Remove("expectedRevision");
            if (explicitProcessId is null && selected is null)
            {
                var instances = await AutomationInstanceRegistry.ListAsync(token);
                if (instances.Count == 0) return Error("no_instances", "请先打开 TANGERINE 视频剪辑。", "MCPBR0001");
                if (instances.Count != 1) return Error("selection_required", "当前有多个编辑器运行，请调用 select_instance 或提供 processId。", "MCPBR0001");
                selected = instances[0];
            }
            int target = explicitProcessId ?? selected!.ProcessId;
            var response = await AutomationInstanceClient.SendAsync(target,
                new AutomationRequest(Guid.NewGuid().ToString("N"), call.Name, arguments, expectedRevision, _sessionId), token,
                explicitProcessId is null ? selected?.InstanceId : null);
            if (response.Success && call.Name == "editor_control_acquire")
            {
                _controlledInstances[target] = selected?.ProcessId == target ? selected.InstanceId : "";
            }
            else if (response.Success && call.Name == "editor_control_release") _controlledInstances.TryRemove(target, out _);
            return Result(AutomationJson.ToNode(response)!.AsObject(), !response.Success);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            return Error("invalid_arguments", ex.Message, "MCPBR0003");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A graceful stdio shutdown releases every instance acquired through
        // this connection, including instances targeted with an explicit PID.
        // The application's idle lease remains the fallback after process kill.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAll(_controlledInstances.ToArray().Select(async instance =>
        {
            try
            {
                await AutomationInstanceClient.SendAsync(instance.Key,
                    new AutomationRequest(Guid.NewGuid().ToString("N"), "editor_control_release", SessionId: _sessionId),
                    timeout.Token, instance.Value.Length == 0 ? null : instance.Value);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }));
        _controlledInstances.Clear();
    }

    private static Tool CreateTool(string name, string description, JsonObject schema, bool readOnly, bool destructive = false) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(schema),
        Annotations = new ToolAnnotations { ReadOnlyHint = readOnly, DestructiveHint = destructive, OpenWorldHint = false },
    };

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };
    private static CallToolResult Error(string code, string message, string stageCode) => Result(new JsonObject
    {
        ["success"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["stageCode"] = stageCode },
    }, true);
    private static CallToolResult Result(JsonObject data, bool error = false) => new()
    {
        IsError = error,
        Content = [new TextContentBlock { Text = data.ToJsonString(AutomationJson.Options) }],
        StructuredContent = JsonSerializer.SerializeToElement(data),
    };
}
