using System.Text.Json.Nodes;
using Tangerine.VideoEditor.Automation;

internal static class ModelChecks
{
    public static void Run(Action<bool, string> check)
    {
        var original = new EditableModel
        {
            Name = "original", Child = new() { Label = "child", Value = 2 }, Items = ["a", "b"],
            Parameters = new() { ["a/b~c"] = 1, ["copy"] = 2 }, Mode = ExampleMode.Fade,
        };
        JsonObject Object(string json) => JsonNode.Parse(json)!.AsObject();
        JsonArray Operations(string json) => JsonNode.Parse(json)!.AsArray();
        void Reject(Action operation, string name)
        {
            try { operation(); check(false, name); }
            catch (JsonModelException ex) { check(!string.IsNullOrWhiteSpace(ex.Message), name); }
        }

        var merged = JsonModelTools.Merge(original, Object("""{"child":{"value":7},"optional":null,"mode":"Slide"}"""));
        check(merged.Child.Value == 7 && merged.Child.Label == "child" && merged.Mode == ExampleMode.Slide, "JSON merge retains unspecified nested fields and parses named enums");
        check(original.Child.Value == 2 && !ReferenceEquals(original.Child, merged.Child), "JSON merge clones model before mutation");
        Reject(() => JsonModelTools.Merge(original, Object("""{"unknown":true}""")), "JSON merge rejects unknown top-level properties");
        Reject(() => JsonModelTools.Merge(original, Object("""{"child":{"unknown":true}}""")), "JSON merge rejects unknown nested properties");
        Reject(() => JsonModelTools.Merge(original, Object("""{"name":null}""")), "JSON merge rejects null for nonnullable properties");
        Reject(() => JsonModelTools.Merge(original, Object("""{"mode":"not-a-mode"}""")), "JSON merge rejects unknown enum names");
        Reject(() => JsonModelTools.Merge(original, Object("""{"mode":999}""")), "JSON merge rejects unnamed numeric enums");
        var replaced = JsonModelTools.Merge(original, Object("""{"items":["replacement"]}"""));
        check(replaced.Items.SequenceEqual(new[] { "replacement" }), "JSON merge replaces arrays as a whole");

        var escaped = JsonModelTools.Patch(original, Operations("""[{"op":"replace","path":"/parameters/a~1b~0c","value":9}]"""));
        check(escaped.Parameters["a/b~c"] == 9 && original.Parameters["a/b~c"] == 1, "JSON Patch unescapes slash and tilde dictionary keys");
        var copied = JsonModelTools.Patch(original, Operations("""[{"op":"copy","from":"/parameters/copy","path":"/parameters/new"},{"op":"move","from":"/items/0","path":"/items/1"}]"""));
        check(copied.Parameters["new"] == 2 && copied.Parameters["copy"] == 2 && copied.Items.SequenceEqual(new[] { "b", "a" }), "JSON Patch copy and move preserve RFC array insertion semantics");
        Reject(() => JsonModelTools.Patch(original, Operations("""[{"op":"replace","path":"/child/value","value":8},{"op":"test","path":"/name","value":"wrong"}]""")), "JSON Patch failed test aborts the detached edit");
        check(original.Child.Value == 2, "Failed multi-operation patch leaves the live model untouched");
        Reject(() => JsonModelTools.Patch(original, Operations("""[{"op":"replace","path":"/items/9","value":"x"}]""")), "JSON Patch rejects out-of-bounds array replacement");
        Reject(() => JsonModelTools.Patch(original, Operations("""[{"op":"add","path":"/items/9","value":"x"}]""")), "JSON Patch rejects out-of-bounds array insertion");
        Reject(() => JsonModelTools.Patch(original, Operations("""[{"op":"replace","path":"/child/unknown","value":"x"}]""")), "JSON Patch rejects unknown nested paths");
        var appended = JsonModelTools.Patch(original, Operations("""[{"op":"add","path":"/items/-","value":"last"}]"""));
        check(appended.Items.SequenceEqual(new[] { "a", "b", "last" }), "JSON Patch supports array append");
        var schema = JsonModelTools.GetSchema<EditableModel>();
        check(schema["$defs"]?[nameof(EditableModel)]?["properties"]?["child"] is not null &&
            schema["$defs"]?[nameof(EditableChild)]?["properties"]?["value"] is not null, "Model schema exposes complete nested editable fields");
        check(schema["$defs"]?[nameof(EditableModel)]?["additionalProperties"]?.GetValue<bool>() == false,
            "Model schema forbids unrecognized properties");
        AutomationCatalog.ValidateArguments("clip_create", Object("""{"trackId":"video","clip":{"kind":"Text","durationSeconds":4,"text":{"content":"nested","motion":{"easing":"Linear"}}}}"""));
        check(true, "Command schema resolves nested clip and title model references");
        Reject(() => AutomationCatalog.ValidateArguments("clip_split", Object("""{"clipId":"clip"}""")), "Command schema rejects a missing required time");
        Reject(() => AutomationCatalog.ValidateArguments("workspace_set", Object("""{"patch":{"unknown":true}}""")), "Command schema rejects unknown nested workspace fields");
        Reject(() => AutomationCatalog.ValidateArguments("track_create", Object("""{"name":"track","kind":"Other"}""")), "Command schema rejects invalid enums before dispatch");
        Reject(() => AutomationCatalog.ValidateArguments("audio_settings_update", Object("""{"clipId":"clip","patch":{"pan":99}}""")), "Command schema rejects out-of-range audio values");
        AutomationCatalog.ValidateArguments("workspace_set", Object("""{"patch":{"soloTrackId":null}}"""));
        check(true, "Command schema accepts explicit null for nullable fields");
        AutomationCatalog.ValidateArguments("transition_add", Object("""{"clipId":"clip","presetId":"transition.rotate","parameters":{"angle":30,"custom":true}}"""));
        check(true, "Command schema preserves extensible preset parameter objects");
        foreach (var command in AutomationCatalog.Commands)
        {
            void VerifyReferences(JsonNode? node)
            {
                if (node is JsonObject obj)
                {
                    if (obj["$ref"] is JsonValue reference)
                    {
                        string path = reference.GetValue<string>();
                        check(path.StartsWith("#/$defs/") && command.InputSchema["$defs"]?[path[8..]] is JsonObject,
                            "Tool schema self-contained reference: " + command.Name + " " + path);
                    }
                    foreach (var child in obj) VerifyReferences(child.Value);
                }
                else if (node is JsonArray array) foreach (var child in array) VerifyReferences(child);
            }
            VerifyReferences(command.InputSchema);
        }
    }

    public enum ExampleMode { Fade, Slide }
    public sealed class EditableModel
    {
        public string Name { get; set; } = "";
        public string? Optional { get; set; }
        public EditableChild Child { get; set; } = new();
        public List<string> Items { get; set; } = [];
        public Dictionary<string, double> Parameters { get; set; } = new();
        public ExampleMode Mode { get; set; }
    }
    public sealed class EditableChild
    {
        public string Label { get; set; } = "";
        public int Value { get; set; }
    }
}
