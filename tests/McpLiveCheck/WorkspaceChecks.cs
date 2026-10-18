using System.IO;
using System.Text.Json.Nodes;
using Tangerine.VideoEditor.Automation;
using TANGERINE_VideoEditor;
using TANGERINE_VideoEditor.Editor;
using TANGERINE_VideoEditor.Project;

internal static class WorkspaceChecks
{
    public static async Task RunAsync(MainWindow window,
        Func<string, JsonObject?, Task<AutomationResponse>> call, Action<bool, string> check)
    {
        _ = window;
        CheckCatalogModels(check);
        var original = await call("workspace_get", null);
        if (!original.Success) throw new InvalidOperationException(original.Error?.Message);
        JsonObject snapshot = original.Data!.AsObject();
        string path = snapshot["workspacePath"]!.GetValue<string>();
        byte[]? originalFile = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        JsonObject restore = new();
        foreach (string key in new[] { "upperRatio", "libraryRatio", "zoom", "shortcuts", "snap", "gpuMode", "autoSave",
            "previewQualityIndex", "categoryIndex", "globalMute", "soloTrackId" })
            restore[key] = snapshot[key]?.DeepClone();
        async Task<AutomationResponse> Success(string name, JsonObject? args)
        {
            var response = await call(name, args);
            if (!response.Success) throw new InvalidOperationException(name + ": " + response.Error?.Message);
            return response;
        }
        try
        {
            var changed = await Success("workspace_set", new()
            {
                ["action"] = "reset", ["upperRatio"] = .63, ["libraryRatio"] = .36, ["zoom"] = 1.6,
                ["shortcuts"] = new JsonObject { ["分割片段"] = "Ctrl+Alt+F8", ["删除片段"] = "Ctrl+Alt+F9" },
                ["patch"] = new JsonObject { ["autoSave"] = false, ["gpuMode"] = "CPU 软件渲染" }
            });
            check(Math.Abs(changed.Data!["upperRatio"]!.GetValue<double>() - .63) < .000001 &&
                Math.Abs(changed.Data["libraryRatio"]!.GetValue<double>() - .36) < .000001,
                "Workspace ratios report the newly applied star layout before the next measure pass");
            check(changed.Data["zoom"]!.GetValue<double>() == 1.6 && changed.Data["gpuMode"]!.GetValue<string>() == "CPU 软件渲染",
                "Workspace preferences update the actual timeline and preview mode");
            check(changed.Data["shortcuts"]!["分割片段"]!.GetValue<string>() == "Ctrl+Alt+F8" && changed.Revision == original.Revision,
                "Custom shortcut changes leave project revision and undo history untouched");
            string savedText = await File.ReadAllTextAsync(path);
            var saved = JsonNode.Parse(savedText)!;
            check(saved["UpperRatio"]!.GetValue<double>() == .63 && saved["Shortcuts"]!["删除片段"]!.GetValue<string>() == "Ctrl+Alt+F9",
                "Workspace ratios and real native shortcut names persist to disk");
            var invalidRatio = await call("workspace_set", new() { ["upperRatio"] = .99 });
            check(!invalidRatio.Success && await File.ReadAllTextAsync(path) == savedText,
                "Invalid workspace ratio fails without changing disk or displaying a modal dialog");
            var unknown = await call("workspace_set", new() { ["shortcuts"] = new JsonObject { ["not-a-native-command"] = "Ctrl+K" } });
            check(!unknown.Success && await File.ReadAllTextAsync(path) == savedText,
                "Unknown shortcut command fails atomically");
            var duplicate = await call("workspace_set", new() { ["shortcuts"] = new JsonObject { ["分割片段"] = "Alt+Ctrl+F8", ["删除片段"] = "Ctrl+Alt+F8" } });
            check(!duplicate.Success && await File.ReadAllTextAsync(path) == savedText,
                "Equivalent modifier orders cannot create duplicate custom shortcuts");
            var invalidKey = await call("workspace_set", new() { ["shortcuts"] = new JsonObject { ["分割片段"] = "F8" } });
            check(!invalidKey.Success && await File.ReadAllTextAsync(path) == savedText,
                "Shortcut gestures use the native parser and require a modifier");
            var removed = await Success("workspace_set", new() { ["shortcuts"] = new JsonObject { ["删除片段"] = null } });
            check(!removed.Data!["shortcuts"]!.AsObject().ContainsKey("删除片段"),
                "Null shortcut values remove the live custom key binding");
            await Success("workspace_set", new() { ["upperRatio"] = .4 });
            await File.WriteAllTextAsync(path, savedText);
            var restored = await Success("workspace_set", new() { ["action"] = "restore" });
            check(Math.Abs(restored.Data!["upperRatio"]!.GetValue<double>() - .63) < .000001 &&
                restored.Data["shortcuts"]!["删除片段"]!.GetValue<string>() == "Ctrl+Alt+F9",
                "Nonmodal restore loads saved ratios and native shortcut bindings");
            var broken = JsonNode.Parse(savedText)!;
            broken["UpperRatio"] = .99;
            await File.WriteAllTextAsync(path, broken.ToJsonString());
            var badRestore = await call("workspace_set", new() { ["action"] = "restore" });
            var afterBadRestore = await Success("workspace_get", null);
            check(!badRestore.Success && Math.Abs(afterBadRestore.Data!["upperRatio"]!.GetValue<double>() - .63) < .000001,
                "Malformed saved layout is rejected before changing live controls");
            var reset = await Success("workspace_set", new() { ["action"] = "reset" });
            check(Math.Abs(reset.Data!["upperRatio"]!.GetValue<double>() - .56) < .000001 && reset.Data["shortcuts"]!.AsObject().Count == 0,
                "Workspace reset restores defaults and removes custom shortcut overrides");
        }
        finally
        {
            // Restore the real user's settings both in the hidden test window
            // and byte-for-byte on disk, including a previously absent file.
            await call("workspace_set", new() { ["action"] = "reset", ["patch"] = restore });
            if (originalFile is not null) await File.WriteAllBytesAsync(path, originalFile);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void CheckCatalogModels(Action<bool, string> check)
    {
        (string Name, Type Type)[] models =
        [
            ("project", typeof(ProjectDocument)), ("sequence", typeof(ProjectSequence)),
            ("track", typeof(TimelineTrack)), ("clip", typeof(TimelineClip)), ("media", typeof(MediaAsset)),
            ("text", typeof(ClipText)), ("motion", typeof(TitleMotion)), ("transform", typeof(ClipTransform)),
            ("mask", typeof(ClipMask)), ("effect", typeof(ClipEffect)), ("transition", typeof(ClipTransition)),
            ("keyframe", typeof(ClipKeyframe)), ("marker", typeof(TimelineMarker)), ("audio", typeof(AudioSettings)),
            ("shape", typeof(GraphicShape)), ("bin", typeof(ProjectBin)), ("subclip", typeof(ProjectSubclip)),
            ("multicam", typeof(MulticamGroup))
        ];
        foreach ((string name, Type type) in models)
        {
            JsonObject reflected = JsonModelTools.SchemaFor(type);
            var actual = reflected["$defs"]![type.Name]!["properties"]!.AsObject().Select(pair => pair.Key).Order().ToArray();
            var published = AutomationCatalog.Models[name]["properties"]!.AsObject().Select(pair => pair.Key).Order().ToArray();
            check(actual.SequenceEqual(published), "Published " + name + " schema contains every actual editable field; missing: " +
                string.Join(",", actual.Except(published)) + "; extra: " + string.Join(",", published.Except(actual)));
        }
    }
}
