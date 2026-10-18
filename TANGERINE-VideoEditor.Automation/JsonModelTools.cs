using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tangerine.VideoEditor.Automation;

public enum JsonModelStageCode { Validate, Patch, Deserialize }

public sealed class JsonModelException(JsonModelStageCode stageCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public JsonModelStageCode StageCode { get; } = stageCode;
}

/// <summary>
/// Converts only public JSON model data. Changes are applied to a detached JSON
/// tree, validated, and deserialized before the caller swaps its live model.
/// Neither patches nor schemas resolve CLR type names or invoke editor methods.
/// </summary>
public static class JsonModelTools
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static readonly NullabilityInfoContext Nullability = new();
    private const int MaximumOperations = 1000;
    private const int MaximumDepth = 96;

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            MaxDepth = MaximumDepth,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options);

    public static T Deserialize<T>(JsonNode? value)
    {
        try
        {
            ValidateValue(typeof(T), value, "$", allowNull: false);
            return value.Deserialize<T>(Options) ?? throw Error("模型不能为空。");
        }
        catch (JsonModelException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new JsonModelException(JsonModelStageCode.Deserialize, "JSON 模型格式无效。", exception);
        }
    }

    /// <summary>
    /// Object fields merge recursively, arrays replace whole arrays, and null
    /// explicitly clears a nullable field. The supplied object is never mutated.
    /// Unknown patch fields fail rather than silently becoming extension data.
    /// </summary>
    public static T Merge<T>(T current, JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        ValidatePatch(typeof(T), patch, "$", 0);
        JsonObject detached = ToNode(current) as JsonObject ?? throw Error("只能合并对象模型。");
        MergeObject(detached, patch, typeof(T));
        return Deserialize<T>(detached);
    }

    public static T Merge<T>(T current, JsonElement patch) => Merge(current,
        JsonNode.Parse(patch.GetRawText()) as JsonObject ?? throw Error("patch 必须为 JSON 对象。"));

    /// <summary>
    /// Applies RFC 6902 add/remove/replace/test/copy/move with JSON Pointer paths.
    /// A failed test or invalid operation leaves the live input entirely intact.
    /// </summary>
    public static T Patch<T>(T current, JsonArray operations)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (operations.Count > MaximumOperations) throw Error("单次补丁操作过多。");
        JsonNode? detached = ToNode(current);
        foreach (JsonNode? item in operations)
        {
            if (item is not JsonObject operation) throw Error("每个补丁操作必须是对象。");
            string op = RequiredString(operation, "op");
            string path = RequiredString(operation, "path");
            string[] segments = ParsePointer(path);
            ValidatePath(typeof(T), segments);
            switch (op)
            {
                case "test":
                    RequireValue(operation);
                    if (!JsonNode.DeepEquals(GetAt(detached, segments), operation["value"]))
                        throw new JsonModelException(JsonModelStageCode.Patch, $"补丁前置条件不匹配：{path}");
                    break;
                case "add":
                case "replace":
                    RequireValue(operation);
                    SetAt(ref detached, segments, operation["value"]?.DeepClone(), replace: op == "replace");
                    break;
                case "remove":
                    RemoveAt(ref detached, segments);
                    break;
                case "copy":
                case "move":
                    string[] from = ParsePointer(RequiredString(operation, "from"));
                    ValidatePath(typeof(T), from);
                    if (op == "move" && segments.Length > from.Length && segments.Take(from.Length).SequenceEqual(from))
                        throw Error("不能将对象移动到它自己的子对象。");
                    JsonNode? source = GetAt(detached, from)?.DeepClone();
                    if (op == "move") RemoveAt(ref detached, from);
                    SetAt(ref detached, segments, source, replace: false);
                    break;
                default: throw Error($"不支持的补丁操作：{op}");
            }
        }
        return Deserialize<T>(detached);
    }

    public static JsonObject GetSchema<T>() => SchemaFor(typeof(T));

    /// <summary>Builds a complete recursive schema from serialized public properties.</summary>
    public static JsonObject SchemaFor(Type type)
    {
        var definitions = new JsonObject();
        JsonObject schema = SchemaNode(type, definitions, new HashSet<Type>());
        if (definitions.Count > 0) schema["$defs"] = definitions;
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        return schema;
    }

    private static JsonObject SchemaNode(Type type, JsonObject definitions, HashSet<Type> active)
    {
        Type? nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return new JsonObject { ["anyOf"] = new JsonArray(SchemaNode(nullable, definitions, active), new JsonObject { ["type"] = "null" }) };
        if (type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(object)) return new JsonObject();
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsEnum) return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()) };
        if (IsNumber(type)) return new JsonObject { ["type"] = IsInteger(type) ? "integer" : "number" };
        Type? dictionaryValue = DictionaryValue(type);
        if (dictionaryValue is not null)
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = SchemaNode(dictionaryValue, definitions, active) };
        Type? elementType = ElementType(type);
        if (elementType is not null) return new JsonObject { ["type"] = "array", ["items"] = SchemaNode(elementType, definitions, active) };
        string definitionName = type.Name;
        if (!definitions.ContainsKey(definitionName) && active.Add(type))
        {
            // Reserve the name before visiting children to support recursive models.
            definitions[definitionName] = new JsonObject();
            var properties = new JsonObject();
            foreach (PropertyInfo property in Properties(type))
            {
                JsonObject child = SchemaNode(property.PropertyType, definitions, active);
                if (AllowsNull(property) && Nullable.GetUnderlyingType(property.PropertyType) is null)
                    child = new JsonObject { ["anyOf"] = new JsonArray(child, new JsonObject { ["type"] = "null" }) };
                properties[JsonName(property)] = child;
            }
            definitions[definitionName] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = HasExtensionData(type) };
            active.Remove(type);
        }
        return new JsonObject { ["$ref"] = "#/$defs/" + definitionName };
    }

    private static void ValidatePatch(Type type, JsonObject patch, string path, int depth)
    {
        if (depth > MaximumDepth) throw Error("补丁嵌套过深。");
        type = Nullable.GetUnderlyingType(type) ?? type;
        Type? dictionaryValue = DictionaryValue(type);
        if (dictionaryValue is not null)
        {
            foreach ((string name, JsonNode? value) in patch)
                ValidateValue(dictionaryValue, value, path + "/" + name, allowNull: !dictionaryValue.IsValueType);
            return;
        }
        if (type == typeof(JsonElement) || type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type)) return;
        foreach ((string name, JsonNode? value) in patch)
        {
            PropertyInfo property = FindProperty(type, name) ?? throw Error($"未知字段：{path}/{name}");
            if (value is JsonObject child && IsObject(property.PropertyType))
                ValidatePatch(property.PropertyType, child, path + "/" + name, depth + 1);
            else ValidateValue(property.PropertyType, value, path + "/" + name, AllowsNull(property));
        }
    }

    private static void ValidateValue(Type type, JsonNode? value, string path, bool allowNull)
    {
        if (value is null)
        {
            if (type == typeof(JsonElement) || type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type)) return;
            if (!allowNull && Nullable.GetUnderlyingType(type) is null) throw Error($"字段不允许为空：{path}");
            return;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(JsonElement) || type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type)) return;
        Type? dictionaryValue = DictionaryValue(type);
        if (dictionaryValue is not null)
        {
            if (value is not JsonObject dictionary) throw Error($"字段必须为对象：{path}");
            foreach ((string key, JsonNode? child) in dictionary)
                ValidateValue(dictionaryValue, child, path + "/" + key, !dictionaryValue.IsValueType);
            return;
        }
        Type? elementType = ElementType(type);
        if (elementType is not null)
        {
            if (value is not JsonArray array) throw Error($"字段必须为数组：{path}");
            for (int index = 0; index < array.Count; index++)
                ValidateValue(elementType, array[index], path + "/" + index, allowNull: false);
            return;
        }
        if (type.IsEnum)
        {
            if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out string? name) ||
                !Enum.GetNames(type).Any(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw Error($"枚举值无效：{path}");
            return;
        }
        if (IsObject(type))
        {
            if (value is not JsonObject obj) throw Error($"字段必须为对象：{path}");
            foreach ((string name, JsonNode? child) in obj)
            {
                PropertyInfo? property = FindProperty(type, name);
                if (property is null)
                {
                    if (HasExtensionData(type)) continue;
                    throw Error($"未知字段：{path}/{name}");
                }
                ValidateValue(property.PropertyType, child, path + "/" + name, AllowsNull(property));
            }
        }
        try { value.Deserialize(type, Options); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
        { throw new JsonModelException(JsonModelStageCode.Validate, $"字段类型无效：{path}", exception); }
    }

    private static void MergeObject(JsonObject target, JsonObject patch, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        Type? dictionaryValue = DictionaryValue(type);
        foreach ((string name, JsonNode? value) in patch)
        {
            PropertyInfo? property = dictionaryValue is null ? FindProperty(type, name) : null;
            string actualName = property is null ? name : JsonName(property);
            Type childType = property?.PropertyType ?? dictionaryValue ?? typeof(JsonElement);
            if (value is JsonObject child && target[actualName] is JsonObject existing)
                MergeObject(existing, child, childType);
            else target[actualName] = value?.DeepClone();
        }
    }

    private static void ValidatePath(Type type, string[] segments)
    {
        for (int index = 0; index < segments.Length; index++)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(JsonElement) || type == typeof(object) || typeof(JsonNode).IsAssignableFrom(type)) return;
            Type? dictionaryValue = DictionaryValue(type);
            if (dictionaryValue is not null) { type = dictionaryValue; continue; }
            Type? elementType = ElementType(type);
            if (elementType is not null)
            {
                if (segments[index] != "-" && !int.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw Error("数组路径必须使用非负整数索引。");
                type = elementType;
                continue;
            }
            PropertyInfo? property = FindProperty(type, segments[index]);
            if (property is null) throw Error($"未知或不可写字段：{segments[index]}");
            // JSON Pointer is case-sensitive, unlike object merge patches.
            if (!StringComparer.Ordinal.Equals(JsonName(property), segments[index]))
                throw Error($"JSON Pointer 字段区分大小写，请使用：{JsonName(property)}");
            type = property.PropertyType;
        }
    }

    private static string[] ParsePointer(string pointer)
    {
        if (pointer.Length == 0) return [];
        if (!pointer.StartsWith('/')) throw Error("补丁路径必须为 JSON Pointer，以 / 开头。");
        string[] segments = pointer[1..].Split('/');
        if (segments.Length > MaximumDepth) throw Error("补丁路径过深。");
        for (int index = 0; index < segments.Length; index++)
        {
            string value = segments[index];
            for (int character = 0; character < value.Length; character++)
                if (value[character] == '~' && (character + 1 >= value.Length || value[++character] is not ('0' or '1')))
                    throw Error("JSON Pointer 转义无效。");
            segments[index] = value.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }
        return segments;
    }

    private static JsonNode? GetAt(JsonNode? root, string[] path)
    {
        JsonNode? node = root;
        foreach (string part in path)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out JsonNode? child)) node = child;
            else if (node is JsonArray array) node = array[ArrayIndex(part, array.Count, allowEnd: false)];
            else throw Error($"补丁路径不存在：{part}");
        }
        return node;
    }

    private static void SetAt(ref JsonNode? root, string[] path, JsonNode? value, bool replace)
    {
        if (path.Length == 0) { root = value; return; }
        JsonNode? parent = GetAt(root, path[..^1]);
        string last = path[^1];
        if (parent is JsonObject obj)
        {
            if (replace && !obj.ContainsKey(last)) throw Error($"替换路径不存在：{last}");
            obj[last] = value;
        }
        else if (parent is JsonArray array)
        {
            int index = last == "-" && !replace ? array.Count : ArrayIndex(last, array.Count, allowEnd: !replace);
            if (replace) array[index] = value;
            else array.Insert(index, value);
        }
        else throw Error("补丁父路径不是对象或数组。");
    }

    private static void RemoveAt(ref JsonNode? root, string[] path)
    {
        if (path.Length == 0) { root = null; return; }
        JsonNode? parent = GetAt(root, path[..^1]);
        string last = path[^1];
        if (parent is JsonObject obj)
        {
            if (!obj.Remove(last)) throw Error($"删除路径不存在：{last}");
        }
        else if (parent is JsonArray array) array.RemoveAt(ArrayIndex(last, array.Count, allowEnd: false));
        else throw Error("补丁父路径不是对象或数组。");
    }

    private static int ArrayIndex(string value, int count, bool allowEnd)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 ||
            index > count || (!allowEnd && index == count) || value.Length > 1 && value[0] == '0')
            throw Error($"数组索引无效：{value}");
        return index;
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetIndexParameters().Length == 0 && property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true &&
            property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition is not JsonIgnoreCondition.Always &&
            property.GetCustomAttribute<JsonExtensionDataAttribute>() is null);

    private static PropertyInfo? FindProperty(Type type, string name) => Properties(type)
        .FirstOrDefault(property => JsonName(property).Equals(name, StringComparison.OrdinalIgnoreCase));
    private static string JsonName(PropertyInfo property) => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    private static bool HasExtensionData(Type type) => type.GetProperties().Any(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null);
    private static bool AllowsNull(PropertyInfo property) => Nullable.GetUnderlyingType(property.PropertyType) is not null ||
        !property.PropertyType.IsValueType && Nullability.Create(property).WriteState != NullabilityState.NotNull;
    private static Type? DictionaryValue(Type type) => type.GetInterfaces().Append(type)
        .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) && candidate.GetGenericArguments()[0] == typeof(string))
        .Select(candidate => candidate.GetGenericArguments()[1]).FirstOrDefault();
    private static Type? ElementType(Type type) => type == typeof(string) ? null : type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type)
        .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        .Select(candidate => candidate.GetGenericArguments()[0]).FirstOrDefault();
    private static bool IsNumber(Type type) => IsInteger(type) || type == typeof(double) || type == typeof(float) || type == typeof(decimal);
    private static bool IsInteger(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
    private static bool IsObject(Type type) => !IsNumber(Nullable.GetUnderlyingType(type) ?? type) && type != typeof(bool) && !type.IsEnum &&
        type != typeof(string) && type != typeof(char) && type != typeof(Guid) && type != typeof(DateTime) && type != typeof(DateTimeOffset);
    private static string RequiredString(JsonObject obj, string name) => obj[name] is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null
        ? text : throw Error($"缺少字符串参数：{name}");
    private static void RequireValue(JsonObject operation)
    {
        if (!operation.ContainsKey("value")) throw Error("补丁操作缺少 value。");
    }
    private static JsonModelException Error(string message) => new(JsonModelStageCode.Validate, message);
}
