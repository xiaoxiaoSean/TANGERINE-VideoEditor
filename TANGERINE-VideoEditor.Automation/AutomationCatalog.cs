using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tangerine.VideoEditor.Automation;

public sealed record AutomationCommandDefinition(string Name, string Description, JsonObject InputSchema,
    bool ReadOnly = false, bool Destructive = false);

/// <summary>
/// Public automation contract shared by the stdio MCP bridge and the running
/// editor. Tools describe structured edits rather than opaque menu invocations.
/// Model schemas deliberately enumerate title, mask, transition and keyframe
/// settings so a client can discover every editable serialized parameter.
/// </summary>
public static class AutomationCatalog
{
    public static IReadOnlyDictionary<string, JsonObject> Models { get; } = CreateModels();
    public static IReadOnlyList<AutomationCommandDefinition> Commands { get; } = CreateCommands();
    private static readonly IReadOnlyDictionary<string, AutomationCommandDefinition> ByName =
        Commands.ToDictionary(command => command.Name, StringComparer.Ordinal);

    public static AutomationCommandDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// Applies the supported public schema vocabulary before dispatch. This
    /// catches missing required fields and misspelled arguments even when a
    /// client bypasses its own MCP schema checks. Business validation still
    /// runs in the editor transaction after the structured edit is applied.
    /// </summary>
    public static void ValidateArguments(string name, JsonObject arguments)
    {
        AutomationCommandDefinition command = Find(name) ?? throw new JsonModelException(JsonModelStageCode.Validate, "不支持的命令：" + name);
        ValidateArgumentNode(command.InputSchema, arguments, command.InputSchema, "$", 0);
    }

    private static void ValidateArgumentNode(JsonObject schema, JsonNode? value, JsonObject root, string path, int depth)
    {
        if (depth > 96) throw ArgumentError(path, "参数嵌套过深");
        if (schema["$ref"] is JsonValue reference)
        {
            string name = reference.GetValue<string>();
            if (!name.StartsWith("#/$defs/", StringComparison.Ordinal) || root["$defs"]?[name[8..]] is not JsonObject definition)
                throw ArgumentError(path, "模型定义无效");
            ValidateArgumentNode(definition, value, root, path, depth + 1);
            return;
        }
        if (schema["anyOf"] is JsonArray alternatives)
        {
            foreach (JsonNode? alternative in alternatives)
            {
                try { ValidateArgumentNode(alternative!.AsObject(), value, root, path, depth + 1); return; }
                catch (JsonModelException) { }
            }
            throw ArgumentError(path, "参数类型或取值无效");
        }
        JsonElement scalar = JsonSerializer.SerializeToElement(value);
        string? type = schema["type"]?.GetValue<string>();
        bool matches = type switch
        {
            null => true,
            "null" => value is null,
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => scalar.ValueKind == JsonValueKind.String,
            "boolean" => scalar.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => scalar.ValueKind == JsonValueKind.Number && scalar.TryGetDouble(out double number) && double.IsFinite(number),
            "integer" => scalar.ValueKind == JsonValueKind.Number && scalar.TryGetDecimal(out decimal integer) && integer == decimal.Truncate(integer),
            _ => false
        };
        if (!matches) throw ArgumentError(path, "参数类型无效，应为 " + type);
        if (schema["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, value)))
            throw ArgumentError(path, "参数不在允许的选项中");
        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (JsonNode? field in required)
                    if (!obj.ContainsKey(field!.GetValue<string>())) throw ArgumentError(path + "/" + field.GetValue<string>(), "缺少必填参数");
            JsonObject properties = schema["properties"] as JsonObject ?? new();
            foreach ((string key, JsonNode? child) in obj)
            {
                if (properties[key] is JsonObject property)
                    ValidateArgumentNode(property, child, root, path + "/" + key, depth + 1);
                else if (schema["additionalProperties"] is JsonObject extra)
                    ValidateArgumentNode(extra, child, root, path + "/" + key, depth + 1);
                else if (schema["additionalProperties"] is JsonValue allowed && allowed.TryGetValue<bool>(out bool permit) && !permit)
                    throw ArgumentError(path + "/" + key, "未知参数");
            }
        }
        if (value is JsonArray array && schema["items"] is JsonObject itemSchema)
            for (int index = 0; index < array.Count; index++)
                ValidateArgumentNode(itemSchema, array[index], root, path + "/" + index, depth + 1);
        if (scalar.ValueKind == JsonValueKind.Number && scalar.TryGetDouble(out double numeric))
        {
            if (schema["minimum"] is JsonValue minimum && numeric < minimum.GetValue<double>()) throw ArgumentError(path, "参数低于允许的最小值");
            if (schema["maximum"] is JsonValue maximum && numeric > maximum.GetValue<double>()) throw ArgumentError(path, "参数超过允许的最大值");
        }
    }

    private static JsonModelException ArgumentError(string path, string message) => new(JsonModelStageCode.Validate, message + "：" + path);

    private static IReadOnlyList<AutomationCommandDefinition> CreateCommands() =>
    [
        C("editor_control_acquire", "申请独占 MCP 控制模式，AI 执行任意普通编辑器命令前必须先获取。同一进程仅一个 MCP 会话拥有控制权；当前拥有者重复获取会刷新空闲时间。连续空闲 60 秒自动释放，执行操作和后台任务期间暂停空闲计时。", false, false, F("ownerLabel", S())),
        C("editor_control_release", "完成操作后主动释放本会话的 MCP 控制模式。正在执行命令或后台任务时返回 MCPCT0004 busy，需等任务完成或取消后结束；不能释放其他 AI 会话持有的控制权。"),
        C("editor_control_status", "读取当前 MCP 控制模式、拥有者与空闲释放状态；未获取控制权时也可查询。查询状态不会续期空闲时间。", true),
        C("editor_get_state", "读取当前应用、项目、选择、播放头、播放、任务和实时状态版本。", true, false, F("includeProject", B())),
        C("editor_wait_for_change", "等待 uiRevision 变化或超时，读取鼠标编辑、播放、选择与 AI 操作后的最新状态，减少完整项目轮询。", true, false, F("afterUiRevision", I(0)), F("timeoutMilliseconds", I(0, 30000))),
        C("editor_select", "选择轨道、素材或多个片段，并立即更新软件选中状态。", false, false, F("clipIds", IDs()), F("trackId", S()), F("assetId", S())),
        C("project_new", "新建项目；有未保存修改时需显式 discardChanges。", false, true, F("name", S()), F("discardChanges", B())),
        C("project_open", "从本地 .tvep 打开项目；有未保存修改时需显式 discardChanges。", false, true, F("path", S(), true), F("discardChanges", B())),
        C("project_save", "保存当前项目；path 可用于另存为，缺省使用当前项目路径。", false, false, F("path", S())),
        C("project_close", "关闭当前项目，创建空白项目；丢弃未保存修改需显式指定。", false, true, F("discardChanges", B())),
        C("project_validate", "检查完整项目的结构、时间、素材引用、序列和效果参数。", true),
        C("project_patch", "原子应用 RFC 6902 JSON Patch；支持完整项目的所有公开模型字段。请先读取模型和 revision，再用 expectedRevision 防止覆盖其他操作。失败不修改项目，成功可撤销。", false, true, F("operations", A(PatchOperation()), true)),
        C("model_get_schema", "读取实际模型的完整 JSON Schema，包含全部文字、动画、遮罩、效果、转场、音频和关键帧字段。", true, false, F("model", E("project", "sequence", "track", "clip", "text", "motion", "transform", "mask", "effect", "transition", "keyframe", "media", "marker", "shape", "subclip", "bin", "multicam", "audio"))),
        C("history_undo", "撤销最近一次编辑，并同步时间线、属性和预览。"),
        C("history_redo", "重做最近一次撤销，并同步软件界面。"),
        C("sequence_list", "列出项目中的全部序列。", true),
        C("sequence_create", "新建可编辑序列。", false, false, F("name", S()), F("width", I(16, 16384)), F("height", I(16, 16384)), F("framesPerSecond", N(.1, 240))),
        C("sequence_select", "切换当前编辑序列并刷新时间线和预览。", false, false, F("sequenceId", S(), true)),
        C("sequence_update", "编辑序列的画布、帧率、色彩空间、工作区和背景等全部参数。", false, false, F("sequenceId", S()), F("patch", R("sequence"), true)),
        C("sequence_remove", "从项目删除指定序列。", false, true, F("sequenceId", S(), true)),
        C("sequence_nest", "把已有序列作为嵌套序列片段插入指定轨道。", false, false, F("sequenceId", S(), true), F("trackId", S(), true), F("startSeconds", N(0))),
        C("media_list", "读取素材元数据、代理状态、素材箱和标签。", true),
        C("media_import", "导入一个或多个本地媒体文件并探测实际元数据。", false, false, F("paths", IDs(), true), F("binId", S())),
        C("media_image_sequence", "发现首帧所在的连续图片序列，按帧率编码为视频并导入当前项目，返回后台任务 ID。", false, false, F("firstFrame", S(), true), F("path", S(), true), F("framesPerSecond", N(.1, 240)), F("name", S()), F("binId", S())),
        C("media_update", "编辑素材的名称、标签、色彩空间、入出点、标记和所有元数据字段。", false, false, F("assetId", S(), true), F("patch", R("media"), true)),
        C("media_remove", "从项目移除素材；removeClips 控制是否同时移除引用片段，不删除源文件。", false, true, F("assetId", S(), true), F("removeClips", B())),
        C("media_relink", "把素材重新链接到实际存在的本地文件。", false, false, F("assetId", S(), true), F("path", S(), true)),
        C("media_proxy", "附加或生成代理、切换代理使用状态，预览读取最新设置；generate=true 时必须提供输出 path。", false, false, F("assetId", S(), true), F("path", S()), F("useProxy", B()), F("generate", B())),
        C("media_analyze", "分析实际媒体的响度、波形、场景切点或节奏；返回后台任务 ID。", true, false, F("assetId", S(), true), F("kind", E("loudness", "waveform", "sceneCuts", "tempo"), true), F("options", R("analysisSettings"))),
        C("media_reframe", "采样片段实际画面，估算主体中心并生成可编辑位置关键帧；可先返回分析结果再决定应用。", false, false, F("clipId", S(), true), F("samples", I(2, 61)), F("apply", B())),
        C("bin_create", "新建项目素材箱，可指定父素材箱。", false, false, F("name", S(), true), F("parentId", S())),
        C("bin_update", "编辑素材箱名称或父级。", false, false, F("binId", S(), true), F("patch", R("bin"), true)),
        C("bin_remove", "删除项目素材箱，保留源文件。", false, true, F("binId", S(), true)),
        C("subclip_create", "根据素材入出点创建可复用子片段。", false, false, F("assetId", S(), true), F("name", S()), F("inSeconds", N(0), true), F("outSeconds", N(0), true)),
        C("subclip_update", "编辑子片段的入出点、名称、备注和标签。", false, false, F("subclipId", S(), true), F("patch", R("subclip"), true)),
        C("subclip_remove", "删除可复用子片段定义。", false, true, F("subclipId", S(), true)),
        C("track_create", "创建视频或音频轨道。", false, false, F("name", S()), F("kind", E("Video", "Audio")), F("order", I(0))),
        C("track_update", "编辑轨道名称、锁定、隐藏、静音、同步锁和独立轨道吸附开关。", false, false, F("trackId", S(), true), F("patch", R("track"), true)),
        C("track_remove", "删除轨道及其片段，可撤销。", false, true, F("trackId", S(), true)),
        C("track_reorder", "按 trackIds 数组重新排列当前序列轨道。", false, false, F("trackIds", IDs(), true)),
        C("clip_create", "在轨道创建视频、音频、图片、文字、纯色、调整图层或嵌套序列片段；支持完整片段模型。", false, false, F("trackId", S(), true), F("clip", R("clip"), true)),
        C("clip_update", "修改片段的全部公开参数，包括时间、变换、裁切、透明度、混合、遮罩、音量、速度、效果、转场和关键帧。对象递归合并，数组整体替换。", false, false, F("clipId", S(), true), F("patch", R("clip"), true)),
        C("clip_remove", "删除一个或多个片段，可选波纹删除。", false, true, F("clipIds", IDs(), true), F("ripple", B())),
        C("clip_move", "移动片段到指定时间或轨道，可按照目标轨道吸附规则定位。", false, false, F("clipIds", IDs(), true), F("trackId", S()), F("startSeconds", N(0), true), F("snap", B())),
        C("clip_trim", "直接设置片段的时间线起点、持续时长或源入点。", false, false, F("clipId", S(), true), F("startSeconds", N(0)), F("durationSeconds", N(.000001)), F("sourceInSeconds", N(0))),
        C("clip_split", "在指定序列时间分割片段，正确保留源时间、效果和关键帧。", false, false, F("clipId", S(), true), F("timeSeconds", N(0), true)),
        C("clip_freeze_frame", "捕获视频片段指定时间的真实源画面并创建静帧；默认插入 1 秒静帧，replace=true 则替换原片段范围。", false, false, F("clipId", S(), true), F("path", S(), true), F("timeSeconds", N(0)), F("durationSeconds", N(.000001)), F("replace", B())),
        C("clip_duplicate", "复制多个片段，可指定相对位移和目标轨道。", false, false, F("clipIds", IDs(), true), F("offsetSeconds", N()), F("trackId", S())),
        C("clip_group", "编组或取消编组指定片段。", false, false, F("clipIds", IDs(), true), F("grouped", B())),
        C("clip_link", "链接或解除链接音视频片段。", false, false, F("clipIds", IDs(), true), F("linked", B())),
        C("title_apply", "创建或应用内置文字或图形贴纸样式，样式、几何和动画参数仍可完整修改。", false, false, F("clipId", S()), F("trackId", S()), F("presetId", S(), true), F("startSeconds", N(0)), F("durationSeconds", N(.000001)), F("content", S())),
        C("title_style_update", "编辑全部文字样式参数，包括字体、描边、阴影、发光、背景、字距、行距、对齐和安全区。", false, false, F("clipId", S(), true), F("patch", R("text"), true)),
        C("title_motion_update", "编辑打字机、数字计数、淡入淡出、滑入、弹跳、滚动、缩放和缓动的全部动画参数。", false, false, F("clipId", S(), true), F("patch", R("motion"), true)),
        C("shape_update", "编辑图形形状、尺寸比例、填充、描边和自定义顶点。", false, false, F("clipId", S(), true), F("patch", R("shape"), true)),
        C("mask_track", "对片段遮罩执行真实图像位移跟踪，可向前或向后分析并按需生成关键帧。", false, false, F("clipId", S(), true), F("forward", B()), F("timeSeconds", N(0)), F("frames", I(1, 300)), F("apply", B())),
        C("effect_add", "为片段添加内置视觉或音频效果；通过 catalog_schema 查询此效果全部参数。", false, false, F("clipId", S(), true), F("presetId", S(), true), F("parameters", O())),
        C("effect_update", "修改已添加效果的启用状态、名称和全部参数。", false, false, F("clipId", S(), true), F("effectId", S(), true), F("patch", R("effect"), true)),
        C("effect_remove", "移除一个效果。", false, true, F("clipId", S(), true), F("effectId", S(), true)),
        C("effect_reorder", "更改片段效果的应用顺序。", false, false, F("clipId", S(), true), F("effectIds", IDs(), true)),
        C("transition_add", "在任意可见片段的开头或末尾添加转场，相邻片段共用边界；所有参数可编辑。", false, false, F("clipId", S(), true), F("presetId", S(), true), F("edge", E("In", "Out")), F("durationSeconds", N(.000001)), F("parameters", O())),
        C("transition_update", "修改转场效果、应用边缘、时长和全部对应参数；预览和导出共享参数。", false, false, F("clipId", S(), true), F("transitionId", S(), true), F("patch", R("transition"), true)),
        C("transition_remove", "移除指定转场。", false, true, F("clipId", S(), true), F("transitionId", S(), true)),
        C("keyframe_set", "添加或更新关键帧，支持线性、保持、缓动和完整贝塞尔控制点。", false, false, F("clipId", S(), true), F("keyframe", R("keyframe"), true)),
        C("keyframe_remove", "删除关键帧。", false, true, F("clipId", S(), true), F("keyframeId", S(), true)),
        C("remap_set", "设置完整变速曲线和帧插值策略，预览与导出使用同一时间映射。", false, false, F("clipId", S(), true), F("points", A(R("remapPoint")), true), F("frameInterpolation", E("Sampling", "Blend", "OpticalFlow"))),
        C("marker_create", "在序列时间创建带备注和颜色的标记。", false, false, F("name", S()), F("timeSeconds", N(0), true), F("notes", S()), F("color", S())),
        C("marker_update", "修改标记的时间、名称、备注和颜色。", false, false, F("markerId", S(), true), F("patch", R("marker"), true)),
        C("marker_remove", "删除序列标记。", false, true, F("markerId", S(), true)),
        C("preview_seek", "移动播放头并刷新实际预览画面。", false, false, F("timeSeconds", N(0), true)),
        C("preview_play", "以原速正向或反向播放当前序列；speed 为 1 正放、-1 倒放、0 暂停。", false, false, F("speed", new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(-1, 0, 1) })),
        C("preview_pause", "暂停播放并保留当前播放头。"),
        C("preview_capture", "把指定序列时间的真实合成画面导出为图片。返回任务 ID，使用 jobs_status 跟踪进度。", false, false, F("path", S(), true), F("timeSeconds", N(0)), F("sequenceId", S())),
        C("scope_capture", "根据指定时间的真实合成画面导出波形、RGB 分量、矢量或直方图示波器图片。", false, false, F("path", S(), true), F("mode", E("waveform", "parade", "vectorscope", "histogram"), true), F("timeSeconds", N(0))),
        C("catalog_list", "列出内置标题、特效、转场及其稳定预设 ID。", true, false, F("category", S())),
        C("catalog_schema", "查询一个内置预设的全部参数、默认值、范围和可选项。", true, false, F("presetId", S(), true)),
        C("catalog_preview", "渲染或读取内置效果示例的 GPU 加速缓存；返回任务 ID。", false, false, F("presetId", S(), true)),
        C("export_start", "按当前项目真实渲染管线异步导出视频，返回任务 ID，不阻塞界面。", false, false, F("path", S(), true), F("sequenceId", S()), F("settings", R("exportSettings"))),
        C("jobs_list", "读取所有导出、捕获、代理、识别和示例渲染任务及其进度。", true),
        C("jobs_status", "读取指定后台任务的状态、进度、输出和分阶段错误。", true, false, F("jobId", S(), true)),
        C("jobs_cancel", "取消指定后台任务并终止它拥有的外部处理进程。", false, false, F("jobId", S(), true)),
        C("subtitle_list", "读取当前序列全部字幕或指定轨道的字幕。", true, false, F("trackId", S())),
        C("subtitle_import", "从 SRT 字幕文件导入实际文字片段，可设置轨道和时间偏移。", false, false, F("path", S(), true), F("trackId", S()), F("offsetSeconds", N())),
        C("subtitle_export", "将当前序列或指定轨道的字幕导出为 SRT。", false, false, F("path", S(), true), F("trackId", S())),
        C("subtitle_recognize", "使用本地 whisper.cpp 和模型识别真实带时间戳的字幕，并导入轨道；返回后台任务 ID。", false, false, F("assetId", S(), true), F("modelPath", S(), true), F("whisperPath", S()), F("language", S()), F("trackId", S()), F("offsetSeconds", N())),
        C("graphic_template_read", "读取本地图形模板的全部可编辑字段。", true, false, F("path", S(), true)),
        C("graphic_template_save", "把指定文字或图形片段保存为可复用模板。", false, false, F("path", S(), true), F("clipId", S(), true)),
        C("graphic_template_insert", "从本地图形模板创建可编辑片段。", false, false, F("path", S(), true), F("trackId", S()), F("startSeconds", N(0))),
        C("sequence_exchange_import", "从 XML、EDL 或 OpenTimelineIO 文件导入可编辑时间线。", false, false, F("path", S(), true), F("format", E("xml", "edl", "otio"))),
        C("sequence_exchange_export", "将当前时间线导出为 XML、EDL、OpenTimelineIO 或 AAF-XML；AAF-XML 是 XML 表示，并非二进制 AAF 文件。", false, false, F("path", S(), true), F("format", E("xml", "edl", "otio", "aafXml"))),
        C("audio_sync", "通过真实波形或时间码同步片段。", false, false, F("referenceClipId", S(), true), F("movingClipId", S(), true), F("method", E("Waveform", "Timecode"))),
        C("audio_settings_get", "读取片段或轨道的完整音频设置；clipId 和 trackId 二选一。", true, false, F("clipId", S()), F("trackId", S())),
        C("audio_settings_update", "编辑片段或轨道的增益、声像、通道、滤波、均衡、压缩、限制、降噪、回声和自动音乐闪避全部参数；clipId 和 trackId 二选一。", false, false, F("clipId", S()), F("trackId", S()), F("patch", R("audio"), true)),
        C("audio_source_streams", "读取所选媒体的全部源音频流及当前索引；通过clip_update的sourceAudioStreamIndex切换。", false, false, F("clipId", S(), true)),
        C("audio_detach", "从视频创建可独立编辑的音频片段，原视频停止发声，可撤销。", false, false, F("clipId", S(), true)),
        C("audio_attach", "重新附着分离音频；音画必须对齐，保留音频处理参数。", false, false, F("clipId", S(), true)),
        C("audio_normalize", "测量所选源音轨R128响度并应用目标LUFS与真峰值约束；支持变速曲线，静音会明确报错。", false, false, F("clipId", S(), true), F("targetLufs", N(-36, -5)), F("truePeakDb", N(-9, 0))),
        C("video_stabilize", "分析运动并生成无损防抖派生素材，自动接入预览、导出及项目保存；enabled=false恢复原视频。", false, false, F("clipId", S(), true), F("enabled", B()), F("smoothing", I(1, 120)), F("shakiness", I(1, 10)), F("maxZoomPercent", N(0, 50))),
        C("audio_meter_get", "读取实际监听混音的左右峰值、削波保持与播放状态。", true),
        C("audio_tempo", "分析片段真实音频的节奏和 BPM。", true, false, F("clipId", S(), true)),
        C("audio_remix", "根据 BPM 和目标时长生成可编辑音乐重混片段。", false, false, F("clipId", S(), true), F("targetDurationSeconds", N(.000001), true), F("bpm", N(.1), true)),
        C("multicam_list", "读取多机位组、同步方式、角度和源片段。", true),
        C("multicam_create", "根据时间线、时间码或波形创建多机位组。", false, false, F("name", S(), true), F("clipIds", IDs(), true), F("syncMethod", E("Timeline", "Timecode", "Waveform"))),
        C("multicam_switch", "切换多机位片段角度，可在指定时间建立新的机位剪切。", false, false, F("clipId", S(), true), F("angleId", S(), true), F("timeSeconds", N(0))),
        C("paper_edit", "按时间范围保留或删除内容，执行基于文本的纸面剪辑。", false, true, F("trackId", S(), true), F("ranges", A(R("timeRange")), true), F("keepSelected", B()), F("startSeconds", N(0))),
        C("voiceover_devices", "列出当前可录音设备。", true),
        C("voiceover_start", "在指定播放头和音轨开始真实录音。", false, false, F("deviceId", I(-1)), F("startSeconds", N(0)), F("trackId", S())),
        C("voiceover_stop", "停止录音、保存 WAV，并按需导入和插入当前时间线。", false, false, F("path", S(), true), F("insert", B())),
        C("voiceover_cancel", "取消当前录音。"),
        C("workspace_get", "读取当前工作区、时间线缩放和预览设置。", true),
        C("workspace_set", "应用、保存、恢复或重置工作区；可编辑面板比例、缩放、预览、自动保存和自定义快捷键。shortcuts 中 null 删除指定自定义快捷键；restore 时不同时指定新参数。", false, false, F("action", E("apply", "save", "restore", "reset")), F("preset", E("editing", "color", "audio")), F("upperRatio", N(.15, .85)), F("libraryRatio", N(.15, .85)), F("zoom", N(.25, 4)), F("previewViewZoom", N(.1, 4)), F("mediaViewIndex", I(0, 2)), F("shortcuts", O(Nullable(S()))), F("patch", R("workspace"))),
        C("ui_list_commands", "列出软件可调用的已命名界面命令白名单。", true),
        C("ui_invoke", "执行 ui_list_commands 返回的已命名界面命令；仅调用白名单，不执行代码。需要输入的功能优先使用结构化专用工具。", false, false, F("command", S(), true)),
        C("system_association_get", "读取 .tvep 的默认打开方式和当前用户注册状态。", true),
        C("system_association_set", "为当前用户注册、设置默认、取消注册或打开 Windows 默认应用设置；使用系统白色文件图标。", false, true, F("action", E("register", "setDefault", "unregister", "openSettings"), true))
    ];

    private static IReadOnlyDictionary<string, JsonObject> CreateModels()
    {
        var models = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        models["transform"] = Fields(
            Numbers("positionX", "positionY", "scaleX", "scaleY", "rotationDegrees", "anchorX", "anchorY", "cropLeft", "cropTop", "cropRight", "cropBottom"));
        models["motion"] = Fields(
            NullableNumbers("delaySeconds", "secondsPerCharacter", "countSeconds", "fadeInSeconds", "fadeOutSeconds", "slideSeconds", "slideDistance", "popSeconds", "popStartScale", "popOvershoot", "scrollSeconds", "scrollDistance", "zoomSeconds", "zoomStartScale", "zoomEndScale", "creditsSpeed")
            .Concat([F("decimals", Nullable(I(0, 12))), F("slideFrom", Nullable(E("Left", "Right", "Top", "Bottom"))), F("easing", Nullable(S())), F("scrollDirection", Nullable(S()))]));
        models["text"] = Fields(
            Strings("content", "fontFamily", "foregroundColor", "outlineColor", "shadowColor", "glowColor", "horizontalAlignment", "verticalAlignment")
            .Concat(Numbers("fontSize", "backgroundPaddingX", "backgroundPaddingY", "backgroundCornerRadius", "backgroundOpacity", "horizontalInset", "verticalInset", "outlineThickness", "shadowBlur", "shadowOpacity", "shadowOffsetX", "shadowOffsetY", "glowRadius", "glowOpacity", "letterSpacing", "lineHeight"))
            .Concat(Bools("bold", "italic", "underline", "outlineEnabled", "shadowEnabled"))
            .Concat([F("backgroundColor", Nullable(S())), F("motion", Nullable(R("motion")))]));
        models["point"] = Fields(Numbers("x", "y"));
        models["mask"] = Fields(Numbers("centerX", "centerY", "width", "height", "feather")
            .Concat([F("shape", E("Rectangle", "Ellipse", "Path")), F("invert", B()), F("points", A(R("point")))]));
        models["shape"] = Fields([F("kind", S()), F("widthFraction", N()), F("heightFraction", N()), F("fillColor", S()), F("strokeColor", S()), F("strokeFraction", N()), F("points", A(R("point")))]);
        models["effect"] = Fields([F("id", S()), F("presetId", S()), F("name", S()), F("isEnabled", B()), F("parameters", O())]);
        models["transition"] = Fields([F("id", S()), F("presetId", S()), F("name", S()), F("edge", E("In", "Out")), F("durationSeconds", N(.000001)), F("parameters", O())]);
        models["keyframe"] = Fields(Strings("id", "property", "interpolation")
            .Concat(Numbers("timeSeconds", "value", "bezierOutTime", "bezierOutValue", "bezierInTime", "bezierInValue")));
        models["remapPoint"] = Fields(Numbers("timeSeconds", "speed"));
        models["clip"] = Fields(Strings("id", "name", "blendMode", "frameInterpolation")
            .Concat(NullableStrings("templateId", "mediaAssetId", "nestedSequenceId", "multicamGroupId", "multicamAngleId", "groupId", "linkGroupId", "color", "detachedAudioClipId", "detachedVideoClipId"))
            .Concat(Numbers("multicamLocalStartSeconds", "startSeconds", "durationSeconds", "sourceInSeconds", "speed", "volume", "opacity"))
            .Concat(Bools("speedAdjustmentApplied", "isMuted", "isReversed", "audioDetached"))
            .Concat([F("sourceAudioStreamIndex", I(0, 63)), F("stabilization", R("stabilization")), F("kind", E("Video", "Audio", "Image", "Text", "Color", "Adjustment", "Sequence")), F("mask", Nullable(R("mask"))), F("transform", R("transform")), F("text", Nullable(R("text"))), F("timeRemapPoints", A(R("remapPoint"))), F("effects", A(R("effect"))), F("transitions", A(R("transition"))), F("keyframes", A(R("keyframe")))]));
        models["track"] = Fields(Strings("id", "name").Concat(Bools("isLocked", "isHidden", "isMuted", "isSyncLocked", "isSnapping"))
            .Concat([F("kind", E("Video", "Audio")), F("order", I(0)), F("clips", A(R("clip")))]));
        models["marker"] = Fields(Strings("id", "name", "notes", "color").Concat([F("timeSeconds", N(0))]));
        models["sequence"] = Fields(Strings("id", "name", "workingColorSpace", "outputColorSpace", "backgroundColor")
            .Concat([F("width", I(16, 16384)), F("height", I(16, 16384)), F("framesPerSecond", N(.1, 240)), F("audioSampleRate", I(8000, 192000)), F("magneticTimeline", B()), F("playheadSeconds", N(0)), F("workAreaStartSeconds", N(0)), F("workAreaEndSeconds", Nullable(N(0)))]));
        models["media"] = Fields(Strings("id", "name", "sourcePath", "labelColor", "description", "inputColorSpace")
            .Concat(NullableStrings("binId", "proxyPath", "videoCodec", "audioCodec", "fileModifiedUtc"))
            .Concat([F("kind", E("Video", "Audio", "Image")), F("durationSeconds", Nullable(N(0))), F("width", Nullable(I(1))), F("height", Nullable(I(1))), F("framesPerSecond", Nullable(N(0))), F("hasAudio", B()), F("audioStreams", A(R("sourceAudioStream"))), F("tags", IDs()), F("metadata", O(S())), F("sourceInSeconds", N(0)), F("sourceOutSeconds", Nullable(N(0))), F("markers", A(R("marker"))), F("useProxy", B()), F("audioChannels", Nullable(I(1))), F("audioSampleRate", Nullable(I(1))), F("fileSizeBytes", Nullable(I(0)))]));
        models["bin"] = Fields([F("id", S()), F("name", S()), F("parentId", Nullable(S()))]);
        models["subclip"] = Fields(Strings("id", "name", "parentAssetId", "notes", "createdUtc")
            .Concat([F("inSeconds", N(0)), F("outSeconds", N(0)), F("tags", IDs())]));
        models["angle"] = Fields([F("id", S()), F("name", S()), F("sourceClip", R("clip"))]);
        models["multicam"] = Fields(Strings("id", "name", "syncMethod").Concat([F("durationSeconds", N(0)), F("angles", A(R("angle")))]));
        models["sequenceWorkspace"] = Fields([F("sequence", R("sequence")), F("tracks", A(R("track"))), F("markers", A(R("marker")))]);
        models["project"] = Fields(Strings("format", "id", "name", "createdUtc", "modifiedUtc")
            .Concat([F("version", I(1)), F("sequence", R("sequence")), F("sequences", A(R("sequenceWorkspace"))), F("bins", A(R("bin"))), F("media", A(R("media"))), F("subclips", A(R("subclip"))), F("multicamGroups", A(R("multicam"))), F("tracks", A(R("track"))), F("markers", A(R("marker")))]));
        models["exportSettings"] = Fields([F("width", Nullable(I(16, 16384))), F("height", Nullable(I(16, 16384))), F("framesPerSecond", Nullable(N(.1, 240))), F("videoBitrateKbps", Nullable(I(1))), F("startSeconds", N(0)), F("endSeconds", Nullable(N(0))), F("videoCodec", E("H264", "HEVC", "AV1")), F("bitDepth", new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(8, 10) })]);
        models["analysisSettings"] = Fields([F("startSeconds", N(0)), F("durationSeconds", N(.000001, 600)), F("bins", I(8, 2000)), F("threshold", N(.01, 1))]);
        models["workspace"] = Fields([F("upperRatio", N(.15, .85)), F("libraryRatio", N(.15, .85)), F("zoom", N(.25, 4)), F("previewViewZoom", N(.1, 4)), F("mediaViewIndex", I(0, 2)), F("shortcuts", O(Nullable(S()))), F("snap", B()), F("gpuMode", E("自动（GPU 优先）", "CPU 软件渲染", "NVIDIA CUDA", "Intel QSV", "AMD AMF")), F("autoSave", B()), F("previewQualityIndex", I(0, 2)), F("categoryIndex", I(0, 4)), F("globalMute", B()), F("soloTrackId", Nullable(S()))]);
        models["sourceAudioStream"] = Fields([F("index", I(0)), F("streamIndex", I(0)), F("codec", S()), F("channels", I(0)), F("sampleRate", I(0)), F("language", S()), F("title", S())]);
        models["stabilization"] = Fields([F("enabled", B()), F("smoothing", I(1, 120)), F("shakiness", I(1, 10)), F("maxZoomPercent", N(0, 50)), F("originalAssetId", Nullable(S())), F("originalSourceIn", N(0))]);
        models["audio"] = Fields([F("normalizeEnabled", B()), F("targetLufs", N(-36, -5)), F("targetTruePeakDb", N(-9, 0)), F("measuredLufs", Nullable(N())), F("measuredTruePeakDb", Nullable(N())), F("normalizationGainDb", N(-60, 60)),F("category", E("未分类", "对白", "音乐", "音效", "环境声")), F("gain", N(0, 4)), F("pan", N(-1, 1)), F("channelMode", E("立体声", "单声道", "仅左声道", "仅右声道")), F("highPassHz", N(0, 20000)), F("lowPassHz", N(0, 20000)), F("equalizerHz", N(0, 20000)), F("equalizerGainDb", N(-24, 24)), F("compressorEnabled", B()), F("compressorThresholdDb", N(-60, 0)), F("compressorRatio", N(1, 20)), F("limiterEnabled", B()), F("limiterCeiling", N(.05, 1)), F("noiseReductionEnabled", B()), F("noiseReductionDb", N(0, 40)), F("echoEnabled", B()), F("echoDelayMs", N(1, 2000)), F("echoDecay", N(0, .9)), F("autoDuckingEnabled", B()), F("duckingGain", N(.05, 1)), F("duckingFadeSeconds", N(0, 2))]);
        models["timeRange"] = Fields([F("startSeconds", N(0), true), F("endSeconds", N(0), true)]);
        return models;
    }

    private static AutomationCommandDefinition C(string name, string description, bool readOnly = false, bool destructive = false,
        params (string Name, JsonObject Schema, bool Required)[] fields)
    {
        JsonObject schema = Fields(fields);
        // Build a self-contained schema containing only transitively referenced
        // definitions, keeping tools/list bounded without losing typed fields.
        var definitions = new JsonObject();
        AddDefinitions(schema, definitions);
        if (definitions.Count > 0) schema["$defs"] = definitions;
        return new(name, description, schema, readOnly, destructive);
    }

    private static void AddDefinitions(JsonNode node, JsonObject definitions)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out string? path) && path.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                string name = path[8..];
                if (!definitions.ContainsKey(name))
                {
                    JsonObject model = (JsonObject)Models[name].DeepClone();
                    definitions[name] = model;
                    AddDefinitions(model, definitions);
                }
            }
            foreach ((string key, JsonNode? child) in obj)
                if (key != "$defs" && child is not null) AddDefinitions(child, definitions);
        }
        else if (node is JsonArray array)
            foreach (JsonNode? child in array) if (child is not null) AddDefinitions(child, definitions);
    }

    private static JsonObject PatchOperation() => Fields([F("op", E("add", "remove", "replace", "move", "copy", "test"), true), F("path", S(), true), F("from", S()), F("value", new JsonObject())]);
    private static (string Name, JsonObject Schema, bool Required) F(string name, JsonObject schema, bool required = false) => (name, schema, required);
    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> Strings(params string[] names) => names.Select(name => F(name, S()));
    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> NullableStrings(params string[] names) => names.Select(name => F(name, Nullable(S())));
    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> Numbers(params string[] names) => names.Select(name => F(name, N()));
    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> NullableNumbers(params string[] names) => names.Select(name => F(name, Nullable(N())));
    private static IEnumerable<(string Name, JsonObject Schema, bool Required)> Bools(params string[] names) => names.Select(name => F(name, B()));
    private static JsonObject Fields(IEnumerable<(string Name, JsonObject Schema, bool Required)> fields)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach ((string name, JsonObject schema, bool mandatory) in fields)
        {
            properties[name] = schema;
            if (mandatory) required.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }
    private static JsonObject S() => new() { ["type"] = "string" };
    private static JsonObject B() => new() { ["type"] = "boolean" };
    private static JsonObject N(double? minimum = null, double? maximum = null)
    {
        var schema = new JsonObject { ["type"] = "number" };
        if (minimum is not null) schema["minimum"] = minimum.Value;
        if (maximum is not null) schema["maximum"] = maximum.Value;
        return schema;
    }
    private static JsonObject I(double? minimum = null, double? maximum = null) { JsonObject schema = N(minimum, maximum); schema["type"] = "integer"; return schema; }
    private static JsonObject E(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject A(JsonObject item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject IDs() => A(S());
    private static JsonObject O(JsonObject? value = null) => new() { ["type"] = "object", ["additionalProperties"] = (JsonNode?)value ?? JsonValue.Create(true) };
    private static JsonObject R(string name) => new() { ["$ref"] = "#/$defs/" + name };
    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
}

/// <summary>Compatibility name for hosts that describe the registry as a tool catalog.</summary>
public static class AutomationToolCatalog
{
    public static IReadOnlyList<AutomationCommandDefinition> Commands => AutomationCatalog.Commands;
    public static AutomationCommandDefinition? Find(string name) => AutomationCatalog.Find(name);
}
