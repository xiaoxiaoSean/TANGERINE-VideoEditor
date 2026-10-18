using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Tangerine.VideoEditor.Mcp;

var builder = Host.CreateApplicationBuilder(args);
// stdout belongs exclusively to MCP JSON-RPC; diagnostics are sent to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
await using var bridge = new EditorMcpBridge();
builder.Services.AddSingleton(bridge);
builder.Services.AddHostedService<EditorBridgeLifetime>();
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "TANGERINE-VideoEditor", Version = "1.0.0" };
        options.ServerInstructions = "直接通过 MCP 操作已经打开的 TANGERINE 视频剪辑实例，不需要截图、点击用户界面、打开连接窗口或切换前台。list_instances 无需控制权，直接返回所有实例的 processId、instanceId、projectName、projectPath 和 revision；未保存项目的 projectPath 可为空，请结合 projectName 判断。确认目标后，可直接调用 editor_control_acquire 并携带 processId 锁定该实例，无需先 select_instance；后续调用始终携带相同 processId。也可先 select_instance 保存本会话的默认目标。结束后调用 editor_control_release 释放对应进程；editor_control_status 可查询所有者与空闲期限。除这三个控制命令外，所有编辑器命令（包括只读查询）都要求已获取控制权；实例发现和选择不要求控制权。空闲一分钟自动释放，正在执行的操作和作业不计入空闲。用户仍可通过轨道区滚轮浏览，不能编辑。修改前读取 editor_get_state；对象 ID 和 revision 属于目标实时实例。修改命令返回前会刷新界面。通过 model_get_schema 查询完整可编辑字段；耗时工作返回作业 ID，通过 jobs_status 和 jobs_cancel 查询或取消，等待结束后再解锁。替换未保存项目时必须显式设置 discardChanges。完整操作规则随程序附带于 toAI.md。";
    })
    .WithStdioServerTransport()
    .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult { Tools = bridge.Tools.ToList() }))
    .WithCallToolHandler(async (context, cancellationToken) =>
        await bridge.CallAsync(context.Params!, cancellationToken));
await builder.Build().RunAsync();
