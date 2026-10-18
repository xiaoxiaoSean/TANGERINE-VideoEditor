# MCP 命令与编辑模型

本软件通过独立的标准输入输出 MCP 服务连接已经打开的编辑器窗口。每个窗口独立发布实例信息，AI 可以按进程 ID 选择窗口。服务不通过修改磁盘上的项目文件模拟界面编辑，而是把命令交给目标窗口，在 UI Dispatcher 中更新真实内存模型，并刷新时间线、素材库、属性和预览。

## 实例与实时状态

先调用 `list_instances`，根据返回的 `processId`、`instanceId`、项目名称和路径确认目标，然后调用 `select_instance`。也可以在任意编辑器工具中显式传入 `processId`，临时覆盖当前选择。仅当一个实例存在时，服务才自动选择。多实例不会猜测目标。

选择目标进程后，AI 必须先调用 `editor_control_acquire` 申请 MCP 控制模式，再读取和操作编辑器；操作结束后调用 `editor_control_release` 主动释放。每个应用进程只允许一个 MCP 会话持有控制权。除申请、释放、查询这三个控制命令外，所有编辑器命令，包括读取状态、查询模型和等待变化，都必须由持有控制权的同一会话调用。MCP 的工具列表与桥接服务的实例列表、进程选择属于连接元数据，不要求控制权。`ownerLabel` 是可选的显示名称，认证使用桥接服务自动提供的独立会话身份，不能用名称冒充另一个会话。

控制模式在连续空闲 60 秒后自动释放。正在执行命令、等待变化、导出、渲染、分析等后台任务时暂停空闲计时；命令或任务结束后重新开始计算 60 秒。`editor_control_status` 只查询而不续期，当前拥有者重复调用 `editor_control_acquire` 会刷新空闲时间。正在执行命令或任务时主动释放会返回 `MCPCT0004` 忙碌错误；必须等任务结束，或取消并确认结束，再释放。客户端切换目标进程不会自动释放原进程的控制权，应先完成并释放原目标。

每次响应包含当前状态版本 `revision`。编辑前先读取 `editor_get_state`，写入时附带 `expectedRevision` 可以拒绝过期修改，避免 AI 覆盖鼠标或另一个 AI 的最新编辑。版本属于具体窗口，不能跨窗口复用。实例身份还用于避免进程 ID 重用导致误操作。

短操作在返回之前完成模型编辑与界面刷新。导出、示例视频、代理生成、字幕识别、媒体分析等耗时工作返回任务 ID，通过 `jobs_list`、`jobs_status`、`jobs_cancel` 查询或取消。任务应捕获启动时的项目快照；异步结果写回时需要检查原项目身份，不能污染后来打开的项目。

## 命令矩阵

具体输入字段以 MCP `tools/list` 和 `model_get_schema` 返回的 schema 为准。`AutomationCatalog` 是共享目录，MCP 桥接服务从此目录生成工具列表，避免另外维护一份名称映射。

| 功能 | 命令 |
| --- | --- |
| MCP 控制模式 | `editor_control_acquire`、`editor_control_release`、`editor_control_status` |
| 进程与状态 | `list_instances`、`select_instance`、`editor_get_state`、`editor_wait_for_change`、`editor_select` |
| 项目 | `project_new`、`project_open`、`project_save`、`project_close`、`project_validate`、`project_patch` |
| 模型与历史 | `model_get_schema`、`history_undo`、`history_redo` |
| 序列 | `sequence_list`、`sequence_create`、`sequence_select`、`sequence_update`、`sequence_remove`、`sequence_nest` |
| 素材 | `media_list`、`media_import`、`media_image_sequence`、`media_update`、`media_remove`、`media_relink`、`media_proxy`、`media_analyze`、`media_reframe` |
| 素材箱与子片段 | `bin_create`、`bin_update`、`bin_remove`、`subclip_create`、`subclip_update`、`subclip_remove` |
| 轨道 | `track_create`、`track_update`、`track_remove`、`track_reorder` |
| 片段编辑 | `clip_create`、`clip_update`、`clip_remove`、`clip_move`、`clip_trim`、`clip_split`、`clip_freeze_frame`、`clip_duplicate`、`clip_group`、`clip_link` |
| 文字与图形 | `title_apply`、`title_style_update`、`title_motion_update`、`shape_update`、`mask_track` |
| 效果 | `effect_add`、`effect_update`、`effect_remove`、`effect_reorder` |
| 转场 | `transition_add`、`transition_update`、`transition_remove` |
| 动画与时间 | `keyframe_set`、`keyframe_remove`、`remap_set` |
| 标记 | `marker_create`、`marker_update`、`marker_remove` |
| 播放与画面 | `preview_seek`、`preview_play`、`preview_pause`、`preview_capture`、`scope_capture` |
| 预设目录 | `catalog_list`、`catalog_schema`、`catalog_preview` |
| 视频导出与任务 | `export_start`、`jobs_list`、`jobs_status`、`jobs_cancel` |
| 字幕 | `subtitle_list`、`subtitle_import`、`subtitle_export`、`subtitle_recognize` |
| 图形模板 | `graphic_template_read`、`graphic_template_save`、`graphic_template_insert` |
| 时间线交换 | `sequence_exchange_import`、`sequence_exchange_export` |
| 音频设置、同步与音乐 | `audio_settings_get`、`audio_settings_update`、`audio_sync`、`audio_tempo`、`audio_remix` |
| 多机位 | `multicam_list`、`multicam_create`、`multicam_switch` |
| 基于文本的剪辑 | `paper_edit` |
| 录音 | `voiceover_devices`、`voiceover_start`、`voiceover_stop`、`voiceover_cancel` |
| 工作区与命令 | `workspace_get`、`workspace_set`、`ui_list_commands`、`ui_invoke` |
| 项目默认打开方式 | `system_association_get`、`system_association_set` |

## 全参数编辑

`model_get_schema` 从实际公开模型反射生成 JSON Schema，只暴露可序列化且可读写的公开属性，不公开私有方法和内部运行状态。它不接受 CLR 类型名称、不执行脚本，也不允许反射调用任意方法。

`clip_update` 和各类 `*_update` 接收 `patch` 对象：对象字段递归合并，数组整体替换，`null` 显式清空可空属性。拼写错误和未知补丁字段会报错。原模型会先转换为独立 JSON 树，只有完整反序列化与业务验证成功后才替换真实模型。验证失败不会留下部分修改。

以下公开参数全部有结构化入口：

- 文字：内容、字体、字号、粗斜体、下划线、颜色、背景、内边距、圆角、透明度、安全区、描边、阴影、发光、字距、行距、对齐。
- 标题动画：延迟、打字速度、计数时间和小数位、淡入淡出时长、滑入方向和距离、弹跳时长和起始缩放、缓动、横向滚动、缩放起终值、片尾滚动速度。
- 变换与合成：位置、双轴缩放、旋转、锚点、四边裁切、透明度、混合模式、遮罩几何、羽化、反转和自由路径顶点。
- 转场：预设、In/Out、时长、方向、强度、中心、羽化、旋转、频率、模糊、条带数等；实际字段和边界由 `catalog_schema` 返回，不同效果可以拥有不同参数。
- 关键帧：属性、片段相对时间、值、插值方式及完整贝塞尔控制点。变速曲线通过 `remap_set` 或完整片段模型编辑。
- 轨道：类型、顺序、锁定、隐藏、静音、同步锁和轨道自己的吸附开关。
- 素材：标签、描述、素材箱、源入出点、标记、代理、输入色彩空间和全部公开元数据。
- 音频：片段或轨道的分类、增益、声像、通道、高低通、均衡、压缩、限制、降噪、回声与音乐自动闪避。此类设置保存在项目扩展数据中，通过专门的音频工具读写，不要求 AI 操作内部扩展键。

### 简单编辑示例

先获取 MCP 控制权，再用 `editor_get_state` 找到目标片段 ID，然后修改它的文字与样式：

```json
{
  "clipId": "从状态读取的片段ID",
  "patch": {
    "content": "新的标题",
    "fontSize": 80,
    "outlineEnabled": true,
    "outlineThickness": 3,
    "motion": { "slideSeconds": 0.7, "slideFrom": "Left" }
  },
  "expectedRevision": 12
}
```

以上参数交给 `title_style_update`。不要把示例里的占位 ID 直接发给工具。

### 完整项目补丁

`project_patch` 接受 RFC 6902 的 `add`、`remove`、`replace`、`test`、`copy`、`move`。路径使用 JSON Pointer，字段采用大小写敏感的 camelCase；`/` 和 `~` 必须分别转义为 `~1`、`~0`。单次最多 1,000 个操作，路径最多 96 层。一整组操作成功后才写回，任何一步失败都会保留原项目。

```json
{
  "operations": [
    { "op": "test", "path": "/tracks/0/id", "value": "已读取的轨道ID" },
    { "op": "replace", "path": "/tracks/0/isSnapping", "value": true }
  ],
  "expectedRevision": 12
}
```

能用 `track_update`、`clip_update` 等 ID 定位工具时优先使用它们。数组索引仅属于当前快照；使用完整项目补丁时，`test` 与 `expectedRevision` 能防止索引变化造成误改。

## 外部工具与边界

工作区通过 `workspace_set` 完整配置，`action` 为 `apply`、`save`、`restore` 或 `reset`。`upperRatio`、`libraryRatio` 使用 0.15–0.85 的比例，`zoom` 使用 0.25–4。剪辑、调色和音频预设分别为 `editing`、`color`、`audio`。参数也可以放进 `patch`，同一字段不能在顶层和 `patch` 重复出现。恢复保存布局时不能同时附带新参数。

`shortcuts` 按软件实际命令名称设置：`分割片段`、`删除片段`、`复制片段`、`剪切片段`、`粘贴片段`、`冻结帧`。例如给分割片段设置 `Ctrl+Alt+F8`，值为 `null` 会删除对应自定义绑定。按键使用现有快捷键解析规则，必须包含 Ctrl、Alt、Shift 中至少一个修饰键；不同自定义命令不能占用同一组合。布局和快捷键通过同一工作区设置文件原子保存，错误返回 MCP 分阶段错误，不会弹出阻塞 AI 的模态错误窗口。

媒体渲染和分析使用软件已有 FFmpeg 管线。语音识别需要用户配置真实 whisper.cpp 可执行文件与本地模型，工具返回实际时间戳字幕，不使用占位识别结果。音频设备来自当前 Windows 录音设备列表。

自动回归已覆盖录音状态与错误返回；没有启动真实麦克风采集，也未在缺少本地 Whisper 模型时声称完成语音识别验证。

自动重构图与遮罩跟踪使用已有的本地图像分析算法，产生普通可编辑关键帧；`apply: false` 可以只读取分析结果。示波器根据实际合成画面输出图片。时间线交换支持 XML、EDL、OpenTimelineIO，另外支持导出 AAF 的 XML 表示；AAF-XML 并非二进制 AAF，不应声称任意商业软件都能直接读取。

`ui_invoke` 只允许软件公布的已命名白名单命令，可用于打开对应设置面板；需要复杂输入的编辑操作使用上述结构化工具完成。工具不提供任意 shell、任意代码执行或任意私有方法调用。删除素材、片段或轨道属于项目编辑，不删除用户的原媒体文件。
