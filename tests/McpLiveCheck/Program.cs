using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class Program
{
    private static int _checks;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string SessionId = Guid.NewGuid().ToString("N");

    [STAThread]
    private static void Main()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { WindowState = WindowState.Normal, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        application.MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            int exit = 0;
            try { await RunAsync(window); Console.WriteLine($"PASS {_checks} real-window MCP checks"); }
            catch (Exception exception) { exit = 1; Console.Error.WriteLine(exception); }
            finally
            {
                typeof(MainWindow).GetField("_isProjectDirty", Private)!.SetValue(window, false);
                window.Close();
                application.Shutdown(exit);
            }
        };
        application.Run(window);
    }

    private static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException("FAIL " + description);
        _checks++;
        Console.WriteLine("PASS " + description);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var item in Descendants(VisualTreeHelper.GetChild(root, index))) yield return item;
    }

    private static async Task RunAsync(MainWindow window)
    {
        for (int attempt = 0; attempt < 100 && !(await AutomationInstanceRegistry.ListAsync()).Any(x => x.ProcessId == Environment.ProcessId); attempt++)
            await Task.Delay(20);
        var discovered = (await AutomationInstanceRegistry.ListAsync()).SingleOrDefault(x => x.ProcessId == Environment.ProcessId);
        Check(discovered is not null && discovered.CommandCount == AutomationCatalog.Commands.Count, "Actual WPF window registers its live PID and complete command count");
        async Task<AutomationResponse> Call(string name, JsonObject? args = null, long? expected = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var response = await AutomationInstanceClient.SendAsync(Environment.ProcessId, new(Guid.NewGuid().ToString("N"), name, args, expected, SessionId), timeout.Token);
            return response;
        }
        async Task<AutomationResponse> Succeed(string name, JsonObject? args = null, long? expected = null)
        {
            var response = await Call(name, args, expected);
            if (!response.Success) throw new InvalidOperationException(name + ": " + response.Error);
            return response;
        }
        JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();
        await Succeed("editor_control_acquire", new() { ["ownerLabel"] = "MCP 实时界面测试" });
        var initial = await Succeed("editor_get_state");
        string videoTrack = initial.Data!["project"]!["tracks"]![0]!["id"]!.GetValue<string>();
        long initialRevision = initial.Revision;
        int initialUndo = initial.Data["history"]!["undo"]!.GetValue<int>();
        var title = await Succeed("title_apply", new() { ["trackId"] = videoTrack, ["presetId"] = "title.basic", ["content"] = "MCP live title", ["startSeconds"] = 0, ["durationSeconds"] = 4 });
        string clipId = title.Data!["id"]!.GetValue<string>();
        Check(title.Revision == initialRevision + 1, "Title insertion advances project revision once");
        var state = await Succeed("editor_get_state");
        Check(state.Data!["project"]!["tracks"]![0]!["clips"]!.AsArray().Any(x => x!["id"]!.GetValue<string>() == clipId), "Pipe mutation updates the actual window model");
        var canvas = (Canvas)window.FindName("TimelineCanvas");
        Check(Descendants(canvas).OfType<Border>().Any(border => border.Tag is ValueTuple<TimelineTrack, TimelineClip> pair && pair.Item2.Id == clipId), "Timeline visual tree contains the inserted clip before acknowledgement");
        Check(((TextBlock)window.FindName("SaveStateText")).Text == "未保存", "Project chrome immediately reflects unsaved MCP changes");
        var select = await Succeed("editor_select", new() { ["clipIds"] = new JsonArray(clipId) });
        Check(select.Data!["selection"]!["clipId"]!.GetValue<string>() == clipId, "AI selection reaches the live selection model");
        var liveClip = ((ProjectDocument)typeof(MainWindow).GetField("_project", Private)!.GetValue(window)!).Tracks.SelectMany(track => track.Clips).Single(item => item.Id == clipId);
        typeof(MainWindow).GetMethod("OpenTextToolPage", Private)!.Invoke(window, [liveClip]);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(150);
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP live title"), "Inspector opens with the selected title's current content");
        var afterOpen = await Succeed("editor_get_state", new() { ["includeProject"] = false });
        Check(afterOpen.Revision == title.Revision, "Opening and populating the inspector does not create phantom project edits");
        var styleUpdate = await Succeed("title_style_update", new() { ["clipId"] = clipId, ["patch"] = new JsonObject { ["content"] = "MCP updated live", ["fontSize"] = 72 } });
        await Task.Delay(150);
        var afterStyle = await Succeed("editor_get_state", new() { ["includeProject"] = false });
        Check(afterStyle.Revision == styleUpdate.Revision, "Refreshing title controls adds no changes after the acknowledged edit");
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP updated live"), "Inspector text refreshes after an AI title edit");
        Check(!Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP live title"), "Inspector discards stale controls from the previous state");

        state = await Succeed("editor_get_state");
        long beforeInvalid = state.Revision;
        int undoBeforeInvalid = state.Data!["history"]!["undo"]!.GetValue<int>();
        var invalid = await Call("clip_update", new() { ["clipId"] = clipId, ["patch"] = new JsonObject { ["durationSeconds"] = -1 } });
        Check(!invalid.Success, "Invalid clip duration is rejected");
        state = await Succeed("editor_get_state");
        var clip = state.Data!["project"]!["tracks"]![0]!["clips"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == clipId)!;
        Check(state.Revision == beforeInvalid && clip["durationSeconds"]!.GetValue<double>() == 4 && state.Data["history"]!["undo"]!.GetValue<int>() == undoBeforeInvalid,
            "Failed model validation preserves project revision, undo history and clip duration");
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP updated live"), "Rollback restores the live inspector");
        var invalidTransition = await Call("transition_add", new() { ["clipId"] = clipId, ["presetId"] = "transition.missing", ["edge"] = "In", ["durationSeconds"] = 1 });
        Check(!invalidTransition.Success, "Business validation rejects an unsupported transition after applying the detached transaction");
        state = await Succeed("editor_get_state");
        clip = state.Data!["project"]!["tracks"]![0]!["clips"]![0]!;
        Check(state.Revision == beforeInvalid && clip["transitions"]!.AsArray().Count == 0 && state.Data["history"]!["undo"]!.GetValue<int>() == undoBeforeInvalid,
            "Rolled-back transition transaction preserves revision, history and live clip contents");
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP updated live"), "Transaction rollback rebuilds the selected detail page from the restored clip");
        var badSelection = await Call("editor_select", new() { ["clipIds"] = new JsonArray(clipId, "nonexistent") });
        Check(!badSelection.Success, "Invalid multi-object selection is rejected");
        state = await Succeed("editor_get_state");
        Check(state.Data!["selection"]!["clipId"]!.GetValue<string>() == clipId && state.Data["selection"]!["clipIds"]!.AsArray().Count == 1,
            "Failed selection leaves the previous complete selection intact");
        var stale = await Call("marker_create", new() { ["name"] = "stale", ["timeSeconds"] = 0 }, expected: beforeInvalid - 1);
        Check(!stale.Success && stale.Error?.Code == "revision_conflict", "Live optimistic concurrency rejects an outdated revision");
        await Succeed("track_update", new() { ["trackId"] = videoTrack, ["patch"] = new JsonObject { ["isLocked"] = true } });
        var locked = await Call("title_style_update", new() { ["clipId"] = clipId, ["patch"] = new JsonObject { ["content"] = "must not apply" } });
        Check(!locked.Success, "AI cannot modify an object on a locked track");
        await Succeed("track_update", new() { ["trackId"] = videoTrack, ["patch"] = new JsonObject { ["isLocked"] = false } });

        state = await Succeed("editor_get_state");
        long beforePatch = state.Revision;
        var patched = await Succeed("project_patch", Object("""{"operations":[{"op":"test","path":"/tracks/0/clips/0/text/content","value":"MCP updated live"},{"op":"replace","path":"/tracks/0/clips/0/text/content","value":"MCP atomic patch"}]}"""), beforePatch);
        Check(patched.Revision == beforePatch + 1 && Descendants(window).OfType<TextBox>().Any(box => box.Text == "MCP atomic patch"), "Atomic project patch refreshes selected-object controls");
        var undone = await Succeed("history_undo");
        Check(undone.Data!["project"] is null, "Undo can return compact state without serializing the entire project");
        state = await Succeed("editor_get_state");
        clip = state.Data!["project"]!["tracks"]![0]!["clips"]![0]!;
        Check(clip["text"]!["content"]!.GetValue<string>() == "MCP updated live", "Undo reverses a complete MCP transaction");
        await Succeed("history_redo");
        state = await Succeed("editor_get_state");
        Check(state.Data!["project"]!["tracks"]![0]!["clips"]![0]!["text"]!["content"]!.GetValue<string>() == "MCP atomic patch", "Redo restores the MCP transaction");

        var patchFailure = await Call("project_patch", Object("""{"operations":[{"op":"replace","path":"/name","value":"must rollback"},{"op":"test","path":"/name","value":"wrong"}]}"""));
        Check(!patchFailure.Success, "Atomic project patch rejects a failed precondition");
        state = await Succeed("editor_get_state");
        Check(state.Data!["project"]!["name"]!.GetValue<string>() != "must rollback", "Failed patch does not partially change the live project");
        await Succeed("preview_seek", new() { ["timeSeconds"] = 1.25 });
        state = await Succeed("editor_get_state", new() { ["includeProject"] = false });
        Check(Math.Abs(state.Data!["preview"]!["timeSeconds"]!.GetValue<double>() - 1.25) < .04, "Seek changes the open preview's current time");
        Check((long)typeof(MainWindow).GetField("_mcpUiRevision", Private)!.GetValue(window)! > 0, "Live UI refresh has an observable revision");
        Check(state.Data["history"]!["undo"]!.GetValue<int>() > initialUndo, "Live undo history tracks successful AI edits");

        state = await Succeed("editor_get_state", new() { ["includeProject"] = false });
        long uiRevision = (long)typeof(MainWindow).GetField("_mcpUiRevision", Private)!.GetValue(window)!;
        var watch = Call("editor_wait_for_change", new() { ["afterUiRevision"] = uiRevision, ["timeoutMilliseconds"] = 3000 });
        await Task.Delay(75);
        Check(!watch.IsCompleted, "Live-state watcher waits for an external change rather than waking itself");
        var timer = Stopwatch.StartNew();
        await Succeed("marker_create", new() { ["name"] = "wake watch", ["timeSeconds"] = 1 });
        Check(timer.Elapsed < TimeSpan.FromSeconds(1.5), "Change watcher does not block another MCP edit");
        var watchResult = await watch;
        Check(watchResult.Success && watchResult.Data!["changed"]!.GetValue<bool>(), "Change watcher observes an AI edit in the open window");

        using (var cancelledWatch = new CancellationTokenSource(100))
        {
            try
            {
                await AutomationInstanceClient.SendAsync(Environment.ProcessId, new("watch-cancel", "editor_wait_for_change",
                    new JsonObject { ["afterUiRevision"] = long.MaxValue - 1, ["timeoutMilliseconds"] = 3000 }, SessionId: SessionId), cancelledWatch.Token);
                Check(false, "Watch cancellation must propagate");
            }
            catch (OperationCanceledException) { Check(true, "AI can cancel a pending live-state watcher"); }
        }
        timer.Restart();
        await Succeed("marker_create", new() { ["name"] = "after cancel", ["timeSeconds"] = 2 });
        Check(timer.Elapsed < TimeSpan.FromSeconds(1.5), "Cancelled watcher leaves the editing channel responsive");

        long revisionBeforeCommit = (long)typeof(MainWindow).GetField("_projectRevision", Private)!.GetValue(window)!;
        using var afterCommitCancellation = new CancellationTokenSource();
        System.Windows.Threading.DispatcherHookEventHandler hook = (_, eventArgs) =>
        {
            if (eventArgs.Operation.Priority == System.Windows.Threading.DispatcherPriority.Render &&
                (long)typeof(MainWindow).GetField("_projectRevision", Private)!.GetValue(window)! > revisionBeforeCommit)
                afterCommitCancellation.Cancel();
        };
        window.Dispatcher.Hooks.OperationPosted += hook;
        AutomationResponse committed;
        try
        {
            committed = await (Task<AutomationResponse>)typeof(MainWindow).GetMethod("HandleMcpRequestAsync", Private)!.Invoke(window,
                [new AutomationRequest("commit-cancel", "marker_create", new JsonObject { ["name"] = "committed marker", ["timeSeconds"] = 3d }, SessionId: SessionId), afterCommitCancellation.Token])!;
        }
        finally { window.Dispatcher.Hooks.OperationPosted -= hook; }
        Check(afterCommitCancellation.IsCancellationRequested && committed.Success && committed.Revision == revisionBeforeCommit + 1,
            "Cancellation after a committed edit returns its actual successful result");
        var lutSchema = await Succeed("catalog_schema", new() { ["presetId"] = "effect.lut" });
        Check(lutSchema.Data!["parameterSchema"]!["properties"]!["path"] is not null &&
            lutSchema.Data["parameterSchema"]!["properties"]!["intensity"] is not null,
            "LUT discovery includes its file path and common effect strength");
        var chromaSchema = await Succeed("catalog_schema", new() { ["presetId"] = "effect.key" });
        Check(chromaSchema.Data!["parameterSchema"]!["properties"]!["color"] is not null,
            "Chroma key discovery includes its nonnumeric color parameter");
        await Succeed("track_update", new() { ["trackId"] = videoTrack, ["patch"] = new JsonObject { ["isLocked"] = true } });
        var lockedContents = await Call("track_update", new() { ["trackId"] = videoTrack, ["patch"] = new JsonObject { ["isLocked"] = false, ["clips"] = new JsonArray() } });
        Check(!lockedContents.Success && CurrentProject().Tracks.Single(track => track.Id == videoTrack).IsLocked && CurrentClip() is not null,
            "A bulk track patch cannot bypass its lock while simultaneously unlocking it");
        await Succeed("track_update", new() { ["trackId"] = videoTrack, ["patch"] = new JsonObject { ["isLocked"] = false } });

        await Succeed("editor_select", new() { ["clipIds"] = new JsonArray(clipId) });
        var keyResponse = await Succeed("keyframe_set", new()
        {
            ["clipId"] = clipId,
            ["keyframe"] = new JsonObject { ["property"] = "PositionX", ["timeSeconds"] = .5, ["value"] = 12d, ["interpolation"] = "Linear" }
        });
        string keyId = keyResponse.Data!["id"]!.GetValue<string>();
        ProjectDocument CurrentProject() => (ProjectDocument)typeof(MainWindow).GetField("_project", Private)!.GetValue(window)!;
        TimelineClip CurrentClip() => CurrentProject().Tracks.SelectMany(track => track.Clips).Single(item => item.Id == clipId);
        var keyClip = CurrentClip();
        var keyTrack = CurrentProject().Tracks.Single(track => track.Clips.Contains(keyClip));
        typeof(MainWindow).GetMethod("OpenKeyframeSettingsTab", Private)!.Invoke(window, [keyClip, keyTrack, keyClip.Keyframes.Single(key => key.Id == keyId)]);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "12"), "User's keyframe inspector opens with the live value");
        await Succeed("keyframe_set", new()
        {
            ["clipId"] = clipId,
            ["keyframe"] = new JsonObject { ["id"] = keyId, ["property"] = "PositionX", ["timeSeconds"] = .5, ["value"] = 42d, ["interpolation"] = "Linear" }
        });
        Check(Descendants(window).OfType<TextBox>().Any(box => box.Text == "42") && !Descendants(window).OfType<TextBox>().Any(box => box.Text == "12"),
            "AI keyframe changes rebuild the already-open inspector from the updated stable ID");
        await Succeed("keyframe_remove", new() { ["clipId"] = clipId, ["keyframeId"] = keyId });
        Check(!Descendants(window).OfType<TextBox>().Any(box => box.Text == "42"), "Removing an inspected keyframe closes its stale value controls");

        var transitionResponse = await Succeed("transition_add", new()
        {
            ["clipId"] = clipId, ["presetId"] = "transition.rotate", ["edge"] = "In", ["durationSeconds"] = 1d,
            ["parameters"] = new JsonObject { ["angle"] = 45d }
        });
        string transitionId = transitionResponse.Data!["id"]!.GetValue<string>();
        var transitionClip = CurrentClip();
        typeof(MainWindow).GetField("_selectedTransition", Private)!.SetValue(window, transitionClip.Transitions.Single(item => item.Id == transitionId));
        typeof(MainWindow).GetField("_selectedTransitionIncoming", Private)!.SetValue(window, transitionClip);
        typeof(MainWindow).GetField("_selectedTransitionPrior", Private)!.SetValue(window, null);
        typeof(MainWindow).GetField("_selectedTransitionTrack", Private)!.SetValue(window, CurrentProject().Tracks.Single(track => track.Clips.Contains(transitionClip)));
        typeof(MainWindow).GetMethod("ShowTransitionSettings", Private)!.Invoke(window, null);
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        Check(((TextBlock)window.FindName("LibraryTitle")).Text == "转场设置", "User's transition inspector is open before the AI edit");
        await Succeed("transition_update", new()
        {
            ["clipId"] = clipId, ["transitionId"] = transitionId,
            ["patch"] = new JsonObject { ["durationSeconds"] = 1.5, ["parameters"] = new JsonObject { ["angle"] = 30d } }
        });
        Check(((TextBlock)window.FindName("LibraryTitle")).Text == "转场设置" && Descendants(window).OfType<TextBox>().Any(box => box.Text == "1.5"),
            "AI transition changes preserve and refresh the open transition inspector");
        Check(((ClipTransition)typeof(MainWindow).GetField("_selectedTransition", Private)!.GetValue(window)!).Parameters["angle"].GetDouble() == 30,
            "Transition inspector now references the updated parameter model");
        await Succeed("transition_remove", new() { ["clipId"] = clipId, ["transitionId"] = transitionId });
        Check(((ContentControl)window.FindName("SettingsHost")).Content is null,
            "Removing an inspected transition closes its stale settings page");
        var stickerResponse = await Succeed("title_apply", new()
        {
            ["trackId"] = videoTrack, ["presetId"] = "sticker.rectangle", ["startSeconds"] = 5d, ["durationSeconds"] = 2d
        });
        string stickerId = stickerResponse.Data!["id"]!.GetValue<string>();
        Check(GraphicShape.FromClip(CurrentProject().Tracks.SelectMany(track => track.Clips).Single(item => item.Id == stickerId)) is not null,
            "Catalog sticker insertion creates a real editable graphic model");

        await WorkspaceChecks.RunAsync(window, (name, arguments) => Call(name, arguments), Check);
        await Succeed("editor_control_release");

        string helperPath = Path.Combine(AppContext.BaseDirectory, "mcp", "TANGERINE-VideoEditor.Mcp.exe");
        if (!File.Exists(helperPath))
            helperPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../TANGERINE-VideoEditor/bin/Debug/net10.0-windows/mcp/TANGERINE-VideoEditor.Mcp.exe"));
        await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = helperPath, Name = "Tangerine live-window integration", WorkingDirectory = Path.GetDirectoryName(helperPath),
        }));
        async Task<JsonNode> ProtocolCall(string name, Dictionary<string, object?>? arguments = null)
        {
            var response = await mcp.CallToolAsync(name, arguments);
            return JsonNode.Parse(((TextContentBlock)response.Content[0]).Text)!;
        }
        var protocolInstances = await ProtocolCall("list_instances");
        var liveInstance=protocolInstances["instances"]!.AsArray().Single(instance => instance!["processId"]!.GetValue<int>() == Environment.ProcessId)!;
        Check(liveInstance["projectName"]?.GetValue<string>()==CurrentProject().Name,
            "Official MCP discovery identifies the actual open editor and project without UI interaction");
        var unowned = await ProtocolCall("editor_get_state",new(){["processId"]=Environment.ProcessId});
        Check(unowned["success"]?.GetValue<bool>() == false && unowned["error"]?["code"]?.GetValue<string>() == "control_required",
            "Official MCP client cannot read an actual editor before acquiring control");
        var acquired = await ProtocolCall("editor_control_acquire", new() { ["processId"]=Environment.ProcessId,["ownerLabel"] = "官方 MCP 实时集成测试" });
        Check(acquired["success"]!.GetValue<bool>() && ((TextBlock)window.FindName("McpControlNotice")).Visibility == Visibility.Visible,
            "Official MCP acquire directly locks the discovered actual PID without selecting or clicking a window");
        var protocolMarker = await ProtocolCall("marker_create", new() { ["processId"]=Environment.ProcessId,["name"] = "SDK live marker", ["timeSeconds"] = 2.5 });
        Check(protocolMarker["success"]!.GetValue<bool>() && CurrentProject().Markers.Any(marker => marker.Name == "SDK live marker"),
            "Official MCP tool updates the actual open project through the bridge and pipe");
        Check(Descendants(canvas).OfType<FrameworkElement>().Any(element => element.ToolTip?.ToString()?.Contains("SDK live marker", StringComparison.Ordinal) == true)
            || Descendants(canvas).OfType<TextBlock>().Any(block => block.Text.Contains("SDK live marker", StringComparison.Ordinal)),
            "The actual timeline rebuilds its marker visuals before MCP acknowledgement");
        var released = await ProtocolCall("editor_control_release",new(){["processId"]=Environment.ProcessId});
        Check(released["success"]!.GetValue<bool>() && ((Menu)window.FindName("EditorMenuBar")).Visibility == Visibility.Visible,
            "Official MCP release restores the actual editor's normal menu");
    }
}
