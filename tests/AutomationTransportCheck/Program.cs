using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tangerine.VideoEditor.Automation;
using Tangerine.VideoEditor.Mcp;

if (args.FirstOrDefault() == "--host")
{
    long revision = 0;
    int cancelled = 0;
    string? owner = null;
    string? releasedBy = null;
    await using var server = new AutomationInstanceServer(async (request, token) =>
    {
        if (request.ExpectedRevision is { } expected && expected != Interlocked.Read(ref revision))
            return AutomationResponse.Fail(request.Id, "revision_conflict", "revision conflict", "TEST-REVISION", revision);
        if (request.Command == "delay")
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), token); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
        }
        if (request.Command == "editor_get_state")
            return AutomationResponse.Ok(request.Id, new JsonObject { ["pid"] = Environment.ProcessId, ["cancelled"] = cancelled,
                ["sessionId"] = request.SessionId, ["controlOwner"] = owner, ["releasedBy"] = releasedBy }, revision);
        if (request.Command == "editor_control_acquire") owner = request.SessionId;
        if (request.Command == "editor_control_release" && owner == request.SessionId) { releasedBy = request.SessionId; owner = null; }
        return AutomationResponse.Ok(request.Id, request.Arguments?.DeepClone(), Interlocked.Increment(ref revision));
    }, () => AutomationInstanceInfo.CreateForCurrentProcess("MCP transport test", projectName: "测试工程-" + Environment.ProcessId,
        projectPath: Path.Combine(Path.GetTempPath(), "MCP 中文工程", Environment.ProcessId + ".tvep"), revision: Interlocked.Read(ref revision)));
    await server.StartAsync();
    Console.WriteLine("READY");
    await Console.In.ReadLineAsync();
    return;
}

var checks = 0;
void Check(bool result, string description)
{
    if (!result) throw new InvalidOperationException("FAIL " + description);
    checks++;
    Console.WriteLine("PASS " + description);
}
var childProcesses = new List<Process>();
try
{
    ModelChecks.Run(Check);
    async Task<Process> StartHostAsync()
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--host");
        var process = Process.Start(info)!;
        childProcesses.Add(process);
        Check(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "READY", "IPC child starts");
        return process;
    }
    var first = await StartHostAsync();
    var second = await StartHostAsync();
    var instances = await AutomationInstanceRegistry.ListAsync();
    var firstInfo = instances.Single(x => x.ProcessId == first.Id);
    Check(instances.Any(x => x.ProcessId == second.Id), "Discovery finds two independently selectable processes");
    Check(!string.IsNullOrWhiteSpace(firstInfo.InstanceId), "Discovery has unique instance identity");
    var direct = await AutomationInstanceClient.SendAsync(first.Id, new("echo", "echo", new JsonObject { ["path"] = "中文 folder\\file.tvep", ["number"] = 12 }));
    Check(direct.Success && direct.Data?["number"]?.GetValue<int>() == 12 && direct.Revision == 1, "IPC round trip preserves structured args and revision");
    Check(direct.Data?["path"]?.GetValue<string>() == "中文 folder\\file.tvep", "IPC preserves Unicode and spaces");
    var stale = await AutomationInstanceClient.SendAsync(first.Id, new("stale", "echo", null, 0));
    Check(!stale.Success && stale.Error?.Code == "revision_conflict", "Stale revision is rejected");
    var wrongInstance = await AutomationInstanceClient.SendAsync(first.Id, new("wrong", "echo"), expectedInstanceId: "previous-incarnation");
    Check(!wrongInstance.Success && wrongInstance.Error?.Code == "instance_changed", "PID reuse cannot silently retarget selected instance");
    var missing = await AutomationInstanceClient.SendAsync(int.MaxValue, new("missing", "echo"));
    Check(!missing.Success && missing.Error?.Code == "instance_not_found", "Missing process returns staged error");

    using (var cancel = new CancellationTokenSource(150))
    {
        try { await AutomationInstanceClient.SendAsync(first.Id, new("cancel", "delay"), cancel.Token); Check(false, "Cancellation should throw"); }
        catch (OperationCanceledException) { Check(true, "Client cancellation is prompt"); }
    }
    for (int attempt = 0; attempt < 40; attempt++)
    {
        var state = await AutomationInstanceClient.SendAsync(first.Id, new("state", "editor_get_state"));
        if (state.Data?["cancelled"]?.GetValue<int>() > 0) break;
        await Task.Delay(25);
    }
    var cancelledState = await AutomationInstanceClient.SendAsync(first.Id, new("state", "editor_get_state"));
    Check(cancelledState.Data?["cancelled"]?.GetValue<int>() == 1, "Disconnect cancellation reaches in-app handler");
    for (int round = 0; round < 8; round++)
    {
        var parallel = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => AutomationInstanceClient.SendAsync(first.Id, new($"parallel-{i}", "editor_get_state"))));
        foreach (var failure in parallel.Where(x => !x.Success)) Console.WriteLine(JsonSerializer.Serialize(failure, AutomationJson.Options));
        Check(parallel.All(x => x.Success), $"Concurrent requests beyond listener capacity complete (round {round + 1})");
    }

    string firstRegistration = Directory.GetFiles(AutomationInstanceRegistry.DirectoryPath, $"{first.Id}-*.json").Single();
    JsonNode registration;
    using (var registrationStream = new FileStream(firstRegistration, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        registration = (await JsonNode.ParseAsync(registrationStream))!;
    await using (var pipe = new NamedPipeClientStream(".", registration["pipeName"]!.GetValue<string>(), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
    {
        await pipe.ConnectAsync(3000);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync("""{"token":"wrong","request":{"id":"auth","command":"echo"}}""");
        var rejected = JsonNode.Parse((await reader.ReadLineAsync())!)!;
        Check(rejected["error"]?["code"]?.GetValue<string>() == "unauthorized", "Invalid authentication token cannot execute commands");
    }

    int helperArgument = Array.IndexOf(args, "--mcp");
    string helper = helperArgument >= 0 && helperArgument + 1 < args.Length
        ? Path.GetFullPath(args[helperArgument + 1])
        : Path.Combine(AppContext.BaseDirectory, "TANGERINE-VideoEditor.Mcp.dll");
    bool helperDll = Path.GetExtension(helper).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = helperDll ? "dotnet" : helper, Arguments = helperDll ? [helper] : [],
        Name = "Tangerine transport verification", WorkingDirectory = Path.GetDirectoryName(helper),
    }));
    var tools = await client.ListToolsAsync();
    Check(tools.Any(t => t.Name == "list_instances") && tools.Any(t => t.Name == "select_instance") && tools.Any(t => t.Name == "editor_get_state"), "Official MCP SDK negotiates and lists editor tools over stdio");
    Check(tools.Count == AutomationCatalog.Commands.Count + 2, "MCP exposes the complete shared command catalog");
    async Task<JsonNode> CallAsync(string name, Dictionary<string, object?>? arguments = null)
    {
        var result = await client.CallToolAsync(name, arguments);
        return JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!;
    }
    var discoveredProjects = await CallAsync("list_instances");
    foreach(var process in new[]{first,second})
    {
        var entry=discoveredProjects["instances"]!.AsArray().Single(item=>item?["processId"]?.GetValue<int>()==process.Id)!;
        Check(entry["projectName"]?.GetValue<string>()=="测试工程-"+process.Id &&
            entry["projectPath"]?.GetValue<string>()==Path.Combine(Path.GetTempPath(),"MCP 中文工程",process.Id+".tvep"),
            "MCP discovery exposes each process's open project without UI interaction or control acquisition");
    }
    Check(discoveredProjects["selectedProcessId"] is null,"Discovery does not select an arbitrary instance");
    await CallAsync("editor_control_acquire",new(){["processId"]=second.Id,["ownerLabel"]="直接接管"});
    var directlyAcquired=await CallAsync("editor_get_state",new(){["processId"]=second.Id});
    Check(directlyAcquired["data"]?["controlOwner"]?.GetValue<string>()==directlyAcquired["data"]?["sessionId"]?.GetValue<string>(),
        "AI locks the discovered PID and reads its live state without selecting or clicking a window");
    var stillUnselected=await CallAsync("list_instances");
    Check(stillUnselected["selectedProcessId"] is null,"Direct PID control does not silently change session selection");
    await CallAsync("editor_control_release",new(){["processId"]=second.Id});
    var directlyReleased=await CallAsync("editor_get_state",new(){["processId"]=second.Id});
    Check(directlyReleased["data"]?["controlOwner"] is null,"AI releases the directly targeted PID without UI interaction");
    var unselected = await CallAsync("editor_get_state");
    Check(unselected["error"]?["code"]?.GetValue<string>() == "selection_required", "Multiple windows require a target before editing");
    var selected = await CallAsync("select_instance", new() { ["processId"] = first.Id });
    Check(selected["selected"]?["processId"]?.GetValue<int>() == first.Id, "MCP session selects first process");
    var selectedState = await CallAsync("editor_get_state");
    Check(selectedState["data"]?["pid"]?.GetValue<int>() == first.Id, "MCP command reaches selected live process");
    string observerSession = selectedState["data"]!["sessionId"]!.GetValue<string>();
    Check(Guid.TryParseExact(observerSession, "N", out _), "MCP bridge automatically forwards a private session identity");
    var explicitState = await CallAsync("editor_get_state", new() { ["processId"] = second.Id });
    Check(explicitState["data"]?["pid"]?.GetValue<int>() == second.Id, "Per-call PID targets another live process");
    selectedState = await CallAsync("editor_get_state");
    Check(selectedState["data"]?["pid"]?.GetValue<int>() == first.Id, "Per-call PID does not overwrite session selection");
    Check(selectedState["data"]?["sessionId"]?.GetValue<string>() == observerSession && explicitState["data"]?["sessionId"]?.GetValue<string>() == observerSession,
        "The MCP session identity stays stable across calls and target processes");

    string ownerSession;
    await using (var ownerClient = await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = helperDll ? "dotnet" : helper, Arguments = helperDll ? [helper] : [],
        Name = "Tangerine control cleanup verification", WorkingDirectory = Path.GetDirectoryName(helper),
    })))
    {
        async Task<JsonNode> OwnerCall(string name, Dictionary<string, object?>? arguments = null)
        {
            var response = await ownerClient.CallToolAsync(name, arguments);
            return JsonNode.Parse(((TextContentBlock)response.Content[0]).Text)!;
        }
        await OwnerCall("select_instance", new() { ["processId"] = first.Id });
        await OwnerCall("editor_control_acquire");
        var ownerState = await OwnerCall("editor_get_state");
        ownerSession = ownerState["data"]!["sessionId"]!.GetValue<string>();
        Check(ownerSession != observerSession && ownerState["data"]!["controlOwner"]!.GetValue<string>() == ownerSession,
            "Separate MCP connections have independent control owner identities");
        await OwnerCall("editor_control_acquire", new() { ["processId"] = second.Id });
        await OwnerCall("editor_control_release", new() { ["processId"] = second.Id });
        await OwnerCall("editor_control_release");
    }
    AutomationResponse releasedFirst = await AutomationInstanceClient.SendAsync(first.Id, new("owner-state", "editor_get_state"));
    AutomationResponse releasedSecond = await AutomationInstanceClient.SendAsync(second.Id, new("owner-state", "editor_get_state"));
    Check(releasedFirst.Data?["controlOwner"] is null && releasedFirst.Data?["releasedBy"]?.GetValue<string>() == ownerSession,
        "Explicit MCP release clears the selected instance's session ownership");
    Check(releasedSecond.Data?["controlOwner"] is null && releasedSecond.Data?["releasedBy"]?.GetValue<string>() == ownerSession,
        "Explicit MCP release also clears the process selected per call");

    string cleanupSession;
    await using (var cleanupBridge = new EditorMcpBridge())
    {
        async Task<JsonNode> BridgeCall(string name, int? processId = null)
        {
            var result = await cleanupBridge.CallAsync(new CallToolRequestParams
            {
                Name = name,
                Arguments = processId is int pid ? new Dictionary<string, JsonElement> { ["processId"] = JsonSerializer.SerializeToElement(pid) } : null,
            }, CancellationToken.None);
            return JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!;
        }
        await BridgeCall("select_instance", first.Id);
        await BridgeCall("editor_control_acquire");
        var cleanupState = await BridgeCall("editor_get_state");
        cleanupSession = cleanupState["data"]!["sessionId"]!.GetValue<string>();
        await BridgeCall("editor_control_acquire", second.Id);
    }
    releasedFirst = await AutomationInstanceClient.SendAsync(first.Id, new("cleanup-state", "editor_get_state"));
    releasedSecond = await AutomationInstanceClient.SendAsync(second.Id, new("cleanup-state", "editor_get_state"));
    Check(releasedFirst.Data?["controlOwner"] is null && releasedFirst.Data?["releasedBy"]?.GetValue<string>() == cleanupSession,
        "Managed bridge shutdown releases the selected instance's control");
    Check(releasedSecond.Data?["controlOwner"] is null && releasedSecond.Data?["releasedBy"]?.GetValue<string>() == cleanupSession,
        "Managed bridge shutdown also releases instances acquired with explicit process IDs");

    await first.StandardInput.WriteLineAsync("quit");
    await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(!File.Exists(firstRegistration), "Graceful app exit removes discovery registration");
    var exited = await CallAsync("editor_get_state");
    Check(exited["error"]?["code"]?.GetValue<string>() == "instance_not_found", "Session never falls through to a different editor after exit");
    string secondRegistration = Directory.GetFiles(AutomationInstanceRegistry.DirectoryPath, $"{second.Id}-*.json").Single();
    second.Kill();
    await second.WaitForExitAsync();
    instances = await AutomationInstanceRegistry.ListAsync();
    Check(!instances.Any(x => x.ProcessId == second.Id) && !File.Exists(secondRegistration), "Discovery removes crashed-process registrations");
    Console.WriteLine($"PASS {checks} transport and MCP interoperability checks");
}
finally
{
    foreach (var process in childProcesses)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("quit");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { if (!process.HasExited) process.Kill(); }
        process.Dispose();
    }
}
