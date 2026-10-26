# TANGERINE-VideoEditor AI 操作手册

适用版本：v1.0.0。本文面向通过 MCP 操作已打开编辑器的 AI，也说明源码测试与故障排查方法。用户当前指令优先于本文中的默认工作习惯。协议和参数以目标构建实际返回的 `tools/list`、`model_get_schema`、`catalog_schema` 为准；不要臆造工具名、参数名或预设 ID。

## 0. 先用已经连接的工具，不要重新开发连接层

用户让你编辑已打开的软件时，这是剪辑任务，不是服务开发任务。先搜索当前客户端已经提供的 MCP 工具（工具名可能带服务前缀），直接调用 `list_instances`。实例发现、锁定、实时刷新均由应用和随附 MCP 服务实现。

- **禁止为普通剪辑任务临时编写 Node.js / Python 桥接程序、JSON-RPC 转发器、HTTP / WebSocket 服务器或网页控制台。** 不需要安装 npm 包、建立项目或监听端口，更不需要重建本软件已有的服务。
- 已连接时直接调用工具，不重复启动第二套 MCP 服务，也不通过脚本模拟工具调用。下面的调用顺序是工具调用示意，不是让你写 JavaScript。
- 当前客户端没有列出这些工具时，先用客户端的工具搜索/发现功能检查。仍没有时，明确报告“当前客户端尚未连接 TANGERINE MCP”，指导用户按下一节配置**随应用提供的可执行文件**并刷新连接；不要把缺少客户端连接误报为软件没有 MCP，也不要自行写替代服务器。
- 源码开发、协议测试或用户明确要求修改连接层时，才使用本仓库已有的 .NET 项目和测试工具；这些不属于日常剪辑接管流程。

## 1. 选择正确操作方式

- 操作用户已经打开的应用：使用本软件 MCP，通过实际进程更新内存模型和实时界面。
- 修改或测试软件：使用源码和命令行，在项目默认 Debug 输出目录构建；若输出文件被运行中的程序锁定，保留该进程，待程序关闭后再构建。
- MCP 没有可用结构化入口时：先查询 `ui_list_commands`，仅使用白名单命令；不要假定 `ui_invoke` 能填完模态对话框。
- 不要通过直接修改磁盘上的 `.tvep` 假装修改了已打开的项目。离线修改后，应用仍然持有原来的内存状态。应使用 MCP，或在适当时机显式重新打开文件。
- 不要通过 shell、窗口消息或私有方法绕过 MCP 会话控制。本文中的原生消息仅用于源码自动化测试。

## 2. 连接与发现

用户打开包含 MCP 功能的构建后，实例自动注册。AI 直接通过 MCP 发现实例，不需要截图、点击、切换前台或打开「帮助 → AI / MCP 连接」。连接窗口只是供用户手动查看客户端配置的可选入口，不是发现或接管的前置条件。旧进程不会因重新编译自动获得新功能。构建和发布目录都包含主程序及 `mcp` 子目录。

MCP 客户端配置示例（路径替换为实际安装位置）：

```json
{
  "mcpServers": {
    "tangerine-video-editor": {
      "command": "C:\\安装目录\\mcp\\TANGERINE-VideoEditor.Mcp.exe",
      "args": []
    }
  }
}
```

仅有 DLL 时可使用 `command: "dotnet"`，把 MCP DLL 的绝对路径作为唯一启动参数。服务使用标准输入输出 JSON-RPC，标准错误用于日志，不配置 HTTP 端口。正常使用时由 MCP 客户端负责初始化和工具调用，AI 无需自行编写 SDK 客户端。仅开发集成客户端时使用标准 MCP SDK；不要把日志写到协议标准输出。

1. 调用 `list_instances({})`，读取 PID、项目名称、路径、启动时间及实例身份。
2. 根据工程名称与路径确认目标，可直接调用 `editor_control_acquire({"processId": 实际PID, "ownerLabel": "任务目的"})`；后续每条调用携带相同 `processId`。无需先调用 `select_instance`。也可以使用 `select_instance({"processId": 实际PID})` 设置本会话默认目标，然后省略后续 PID。
3. 多进程时必须明确选择，不猜测前台窗口。只有一个进程时桥接服务可以自动选择，但仍建议核对。
4. 每个编辑器工具也可携带 `processId` 临时指定目标；不会改变本会话的默认选择。实例退出或身份改变时重新发现，不把旧 PID 当作永久身份。

本机发现注册位于 `exe 同级的 data/mcp/instances`，使用当前 Windows 用户专用命名管道。不要把内部认证令牌、管道地址写入对用户的回复，也不要手工伪造注册文件。

未保存工程的 `projectPath` 可能为 null，工程仍可按 `projectName`、`processId`、`instanceId` 区分。注册自动更新，工程切换或另存为后重新 `list_instances` 获取最新信息；无需让用户报 PID 或工程路径。无法区分两个同名未保存工程时再向用户确认，不能随意操作其中一个。

按 PID 直接接管示意：

```text
list_instances({})
// 从返回的 instances 中选择实际工程及 PID，不使用示例占位值。
editor_control_acquire({processId: 目标PID, ownerLabel: "任务目的"})
editor_get_state({processId: 目标PID, includeProject: true})
...所有编辑和任务查询都携带相同 processId...
editor_control_release({processId: 目标PID})
```

## 3. 必须遵守的接管生命周期

完整顺序：发现 → 选择进程 → 申请控制 → 读取状态 → 执行与验证 → 等待任务收尾 → 保存（若任务需要）→ 主动释放。

```text
list_instances({})
select_instance({processId: 实际PID})
editor_control_acquire({ownerLabel: "当前操作目的"})
editor_get_state({includeProject: true})
...编辑、预览、导出与等待...
editor_control_release({})
```

上面是调用顺序示意，不是可直接执行的 JavaScript；ID、PID、修订号必须来自实际响应。

- 所有普通编辑器工具，包括只读的状态、模型、目录和任务查询，都要求先获得同一进程的控制权。
- 例外是桥接工具 `list_instances`、`select_instance`，以及 `editor_control_acquire`、`editor_control_release`、`editor_control_status`。
- 每个进程同时只有一个 MCP 会话拥有控制权。`ownerLabel` 只是说明，不是身份凭证。会话身份由桥接服务自动附带，不能冒充另一个 AI。
- 获取后顶部菜单隐藏，显示不可点击提示「正在通过MCP操作本程序」。用户的项目编辑、点击、键盘输入及关闭窗口受阻止。
- 用户仍可在轨道区通过纵向滚轮、横向滚轮、Shift＋滚轮浏览视图。浏览不修改项目，不续期 MCP 空闲计时；Ctrl＋滚轮缩放在接管期间不开放。
- 连续空闲 60 秒自动交还用户。正在执行的操作、等待变化、录音和后台任务不算空闲；任务完成后重新计算完整的空闲时间。
- `editor_control_status` 不续期；拥有者重复 `editor_control_acquire` 会续期。不要为无实际任务而长期占用用户界面。
- 任务仍执行或收尾时释放返回 `MCPCT0004`。等待完成，或取消并确认结束，再释放。不要杀死编辑器来解锁。
- 切换进程不自动释放原进程，也不自动取得新进程。尽量先完成并释放旧目标，再切换。
- 超时解锁后如还需操作，重新申请并重新读取最新状态。不要根据旧快照继续写入。
- 无论任务成功、失败还是用户取消，都应在适当的清理流程中释放自己的控制权。正常关闭桥接服务会尽力释放；强制终止只能依靠应用空闲回收，不能作为正常完成方式。

## 4. 响应、版本与实时刷新

编辑器响应包含 `success`、`data`、`revision` 或 `error`；MCP 工具错误还会标记 `isError`。不要只看调用是否返回。错误包含稳定的英文 `code`、面向用户的说明和 `stageCode`。

`editor_get_state` 的 `data` 包含进程、版本、项目路径、dirty、控制状态、项目模型（可省略）、选择、播放头、预览状态、撤销重做数量和任务。`revision` 表示项目编辑版本；`uiRevision` 用于等待界面状态变化。不要混用这两个版本。

需要写入时建议携带 `expectedRevision`，值使用最近成功响应返回的项目修订号。发生冲突应重新读取并重新定位目标，不能简单去掉保护后重试。每个版本仅属于它的目标进程。

编辑在目标窗口 Dispatcher 上执行，成功后刷新时间线、属性、素材库和预览请求。预览像素可能仍在异步合成，检查 `preview.rendering`、`pending`、`failed`；必要时使用 `preview_capture` 生成真实合成图片，不把刚发出刷新请求当成已经看过画面。

使用 `editor_wait_for_change({"afterUiRevision": 最近uiRevision, "timeoutMilliseconds": 30000})` 等待变化，返回后更新游标。超时是正常结果。避免毫秒级轮询或反复读取完整大项目；轻量状态可传 `includeProject:false`。

## 5. 参数与模型规则

- 首次使用工具先看工具 schema；复杂模型先调用 `model_get_schema`。
- 字段使用大小写敏感的 camelCase。枚举值以 schema 为准，不将中文界面标签随意翻译成内部 ID。
- 轨道、片段、效果、转场、素材等 ID 从当前状态读取；名称可以重复，不能代替 ID。
- 时间字段通常以秒为单位，关键帧时间为片段相对时间；坐标、比例和音频单位必须按对应模型或预设 schema 判断，不能统一当像素。
- `*_update` 的 `patch` 递归合并对象；数组整体替换；`null` 仅可清空允许为空的字段。只发送需要修改的字段，避免从旧快照整段覆盖数组。
- 未知字段、参数超界和非法引用会被拒绝。先验证再提交，失败不会留下半份模型更新。
- `model_get_schema` 支持 project、sequence、track、clip、text、motion、transform、mask、effect、transition、keyframe、media、marker、shape、subclip、bin、multicam、audio。
- `tools/list` 中的 `$defs` 还包含 exportSettings、分析设置等输入模型。下方附录集中列出这些模型，`$ref` 指向同名定义。
- `project_patch` 支持 RFC 6902 的 add/remove/replace/test/copy/move，最多 1,000 个操作，JSON Pointer 最深 96 层，`~` 与 `/` 分别转义为 `~0`、`~1`。优先使用按 ID 定位的专用工具；使用数组下标时增加 `test` 和 `expectedRevision`。

## 6. 常用工作流

### 6.1 轨道命名与吸附

读取真实轨道 ID 后：

```json
{
  "trackId": "实际轨道ID",
  "patch": { "name": "主画面", "isSnapping": true },
  "expectedRevision": 12
}
```

交给 `track_update`，替换占位 ID 和修订号。用户也可右键轨道名称或轨道空白区域重命名。片段移动可用 `clip_move` 的 `snap` 参数选择按目标轨道吸附；读取当前开关，不擅自改变用户设置。若要求精确定位，明确处理帧网格与吸附规则。

### 6.2 文字、效果与转场

1. `catalog_list` 发现预设稳定 ID，再 `catalog_schema` 查询参数、范围、默认值和选项。
2. `title_apply` 创建或应用文字/贴纸；`title_style_update` 修改字体、文字、描边、背景、阴影、发光、间距等；动画使用 `title_motion_update`。
3. `effect_add` 添加真实效果；更新使用返回的 effectId。数组顺序影响结果，通过 `effect_reorder` 显式调整。
4. `transition_add` 的 edge 为 `In` 或 `Out`，可作用于可见对象的开头、末尾及相邻对象边界。使用与名称对应的真实预设，不用替换名称假装实现其他转场。参数以该预设 schema 为准。
5. 用真实画面预览并验证导出；示例视频成功不保证复杂多图层组合无问题。

### 6.3 后台任务与导出

`export_start`、捕获、示例视频、部分分析、代理和字幕识别返回任务数据，其中 `jobId` 是后续查询依据。任务状态为 `running`、`completed`、`failed`、`cancelled`；进度为 0–1。`completed` 后读取 `result`，失败读取 `error` 和 `stageCode`。任务字段遵循实际返回对象，不把任务 ID 当成输出路径。

`export_start` 会自动在目标实例显示只读、非模态的视频导出进度窗口：准备序列和文字图形时显示准备状态，编码时显示百分比，任务成功、失败或取消后自动关闭。该窗口不解除 MCP 操作锁定，也不阻止用户在主窗口用滚轮浏览轨道。AI 不需要点击或操作此窗口；取消请调用 `jobs_cancel({jobId})`，继续等到最终状态再释放控制权。关闭进度窗口不会取消任务；不同任务的进度和窗口按各自 `jobId` 独立更新。进度窗口创建、归属或 UI 调度失败使用阶段码 `EXPRT0901`。

```text
project_validate({})
preview_capture({path: "绝对路径\\检查帧.png", timeSeconds: 2})
jobs_status({jobId: 捕获任务ID})
export_start({path: "绝对路径\\成片.mp4",
              settings: {width: 1280, height: 720, framesPerSecond: 24,
                         videoBitrateKbps: 6500, startSeconds: 0, endSeconds: 24}})
jobs_status({jobId: 导出任务ID})
...等待完成并验证文件...
project_save({})
editor_control_release({})
```

捕获和导出都需等待完成；不要并行发起互相冲突的写操作。`jobs_cancel` 仅发出取消请求，必须继续观察到任务结束后才能释放。使用逐步退避或等待变化监测，避免高频轮询。导出以任务启动时的项目快照为准，后续编辑不自动改变已启动的成片。

完成标准：工具成功、任务完成、文件存在且非空；重要导出还应完整解码、检查时长/尺寸/帧率/音频，并抽查关键位置画面。编码失败应排查和修复，不把拆成多个小片段当作完整项目已经可导出。用户取消宣传片等创作任务后，不继续创作，只把原项目作为获授权修复的回归样本。

### 6.4 音频、字幕、分析与交换

音频处理用 `audio_settings_get/update`，不手工猜测扩展数据键。分析先用 `media_analyze`，需要只读建议时采用支持的 `apply:false`，确认后再应用。变速通过 `remap_set` 或公开模型，核对源范围。

### 6.5 内置纯音乐

“内置音乐”区在原有分类后新增，`workspace_set({patch:{categoryIndex:5}})` 可显示；0 媒体、1 文字、2 效果、3 转场、4 贴纸保持不变。用 `music_list({query?,genre?,mood?})` 读取实际曲目，返回 `total/count/tracks/genres/moods`。每首曲目含 `id/title/artist/genre/mood/durationSeconds/audioPath/thumbnailPath/sourceUrl/licenseId/licenseUrl`；本地音频和封面随软件打包，试听、剪辑不需要联网下载。`catalog_list({category:"music"})` 返回相同曲目列表，`catalog_list({category:"all"})` 也包含 `music`。

`music_preview({musicId})` 试听时，曲目缩略图同步显示在主预览器中，不修改工程；返回 `requested:true` 表示已提交播放请求，实际解码仍可失败。返回的 `artworkPath/artworkLoading/artworkVisible` 表示当前封面路径与加载、显示状态。曲目 ID 必须来自当前实例目录，不把曲目 ID 当成时间线轨道 ID。试听与示例预览、时间线播放共用停止及归属清理规则，选择新对象或切换项目时旧请求失效。

`music_stop({})` 停止试听并释放播放器，主预览器恢复工程画面。`editor_get_state().preview.mode` 区分 `music`（音乐试听及其封面）、`catalog`（视觉示例生成或播放）、`timeline`（工程预览）。试听暂停后仍保留 `music` 模式和封面。`preview.music` 返回当前试听的 `musicId/playing/timeSeconds/durationSeconds` 及 `artworkPath/artworkLoading/artworkVisible/artworkRequest`；封面请求、加载和显示变化也会更新 `uiRevision`。封面仅用于试听展示，不会添加到时间线或导出视频。`preview_pause` 面向时间线，停止音乐试听请使用专用命令。

`music_add({musicId,trackId?,startSeconds?,sourceInSeconds?,durationSeconds?,volume?,fadeInSeconds?,fadeOutSeconds?,snap?})` 插入可撤销的音频片段。`trackId` 是目标时间线音频轨道；省略时使用当前或第一个未锁定音频轨道，无可用轨道则创建“音乐”轨道。`startSeconds` 默认当前播放头，`sourceInSeconds` 默认 0，`durationSeconds` 默认源曲剩余时长，不能超出曲目。`volume` 默认 1、范围 0–4；淡入淡出默认 0，各自范围 0–1 秒且不能超过片段时长。`snap` 默认 true，遵从全局和目标轨道吸附开关；需要精确时间时设为 false。返回音乐、素材、片段和目标轨道 ID，及实际插入时间和许可来源。

```text
music_list({genre:"钢琴"})
music_preview({musicId: 实际曲目ID})
music_add({musicId: 实际曲目ID, startSeconds:0, durationSeconds:30,
           volume:0.35, fadeInSeconds:1, fadeOutSeconds:1, snap:false})
project_save({})
```

插入后的音乐是普通音频素材与片段，可继续使用 `clip_trim`、`clip_update`、`audio_settings_update`、`keyframe_set`、`audio_remix`。保存 `.tvep` 时会打包所用音频；作者和许可来源保留在素材元数据中。目录标注的授权以每首曲目的 `licenseId/licenseUrl/sourceUrl` 为准，保留来源记录，不把免版税误解为未登记的第三方作品也可任意使用。原有 `catalog_schema/catalog_preview` 只处理视觉预设，音乐使用专用命令。

字幕识别依赖实际 whisper.cpp 可执行文件和本地模型；缺失时报告缺失，不能生成占位字幕冒充识别。录音开始/停止/取消对应独立工具，真实采集仅在用户要求录音时进行。

时间线交换支持 XML、EDL、OpenTimelineIO，AAF 导出是 AAF 的 XML 表示，不是二进制 AAF。不要承诺所有商业软件直接兼容。导入前核对是否覆盖项目；不擅自丢弃未保存编辑。

## 7. 文件与系统变更

新建、打开、关闭项目如有未保存修改，工具要求显式 `discardChanges`。只有用户任务已授权丢弃时才设置，优先保存或另存为。删除轨道、片段、素材属于项目编辑，不删除原始媒体文件。

使用 Windows 绝对路径，JSON 反斜杠要转义。输出应放到明确的工作目录，避免覆盖用户现有作品。默认打开方式修改使用 `system_association_set`，动作 register/setDefault/unregister/openSettings 影响系统配置，仅在用户要求时调用；项目文件图标按软件约定使用 Windows 默认白色文件图标。

## 8. 错误恢复

| 错误/阶段 | 处理 |
| --- | --- |
| no_instances / selection_required / instance_not_found | 重新发现并选取实际进程，不启动或关闭无关窗口 |
| revision_conflict / MCPRT0003 | 重读状态、重新定位 ID、重新计算补丁 |
| missing_session / MCPCT0001 | 通过正常 MCP 连接获取会话，不手填内部认证字段 |
| control_required / MCPCT0002 | 重新申请，成功后重读状态 |
| control_owned / MCPCT0003 | 不冒充其他会话、不抢占；告知当前占用并等待合法释放 |
| control_busy / MCPCT0004 | 等待操作/任务完成，或取消并确认结束；不要杀进程 |
| MCPBR0001–0003 | 检查进程选择、工具名称、参数与 schema |
| MCPIP0001–0009 | 检查连接、消息大小、JSON、身份及实例生命周期；不要无限重试 |
| MCPJS / 模型校验 | 按 schema 修正具体字段，保留用户已有模型 |
| EXPRT0004 | 检查编码器 stderr、退出码和导出诊断，修复再验证 |

编码失败诊断目录为 `exe 同级的 data/diagnostics/export`，记录参数、滤镜图与错误。`0xC0000005` 是编码器原生内存访问冲突；日志空白不能理解为成功。参见 [导出修复记录](dev_doc1/Export%20Reliability.md)。

本地 IPC 单条请求/响应上限 16 MiB，连接超时 5 秒，单命令传输上限 2 分钟。耗时操作应使用后台任务，而不是长时间占用同步请求。遇到响应大小问题使用轻量状态或按需查询，不反复发送同一个超大项目。

## 9. 源码验证与交付

遵循仓库 `rules.md`：中文界面和开发文档、解释性英文注释、比例布局、stagecode 异常。保留未提交修改，除非用户明确要求，不重置仓库、不自动提交、不替换或关闭用户正打开的应用。

每轮代码修改后按项目根 `AGENTS.md` 执行 `dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj --configuration Debug`，主程序输出固定在默认 `TANGERINE-VideoEditor/bin/Debug/net10.0-windows/`。不要用 `OutputPath`、`--output`、`-o` 或 `--artifacts-path` 重定向主程序，也不要用 Release 替代 Debug；若运行中的程序锁定文件，不要结束该进程。Debug 的 MCP 程序位于该目录的 `mcp/TANGERINE-VideoEditor.Mcp.exe`，操作手册为同目录 `toAI.md`。

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj --configuration Debug
dotnet run --project tests/TrackRenameCheck --configuration Debug
dotnet run --project tests/McpControlCheck --configuration Debug
dotnet run --project tests/RenderSmokeCheck --configuration Debug
dotnet run --project tests/ExportCoverageCheck --configuration Debug -- --features artifacts/export-audit/features
```

每轮代码修改后先按上述命令构建主项目；测试项目保持各自默认 Debug 输出，避免把主程序重定向到审计目录。不要并行构建共享 WPF obj，也不要在某检查 DLL 运行期间覆盖该 DLL。只运行与修改有关的必要检查，失败后查原因，不通过延长时限、删除断言或伪造输出掩盖缺陷。

相关检查：AutomationTransportCheck（标准 MCP/IPC）、McpLiveCheck（实时窗口）、McpControlCheck（接管与用户浏览）、McpFeaturesCheck（高级功能与真实媒体）、RenderSmokeCheck（预览与导出）、TransitionVisualCheck（转场图像与导出一致性）、ExportCoverageCheck（功能预览/导出/解码）、TrackRenameCheck（轨道命名与导航）。未验证真实硬件、麦克风或外部模型时明确说明，不声称全部环境通过。

交付报告说明修复内容、实际测试结果、输出位置和仍需重启新版本的事实。不要因源码构建成功就声称旧进程已更新，也不要在任务失败时声称功能全部可用。

## 10. 完整命令与输入模型附录

以下命令和模型从本版本 `AutomationCatalog` 生成，避免手抄遗漏。所有编辑器命令额外接受可选的 `processId`（正整数）和 `expectedRevision`（非负整数），这两个桥接参数不再在每条命令下重复列出。各块内 `required` 列出必填字段，其余字段可省略；可省略不等于可传 null。`$ref` 查最后的集中模型定义；运行时 `tools/list` 会提供对应 `$defs`。

桥接工具 `list_instances` 无参数；`select_instance` 必填 `processId`，正整数。它们不要求持有应用控制权。

<!-- GENERATED MCP CATALOG -->

本版本共 116 个 MCP 工具（含 2 个桥接工具），28 个集中输入模型。

### `editor_control_acquire`

申请独占 MCP 控制模式，AI 执行任意普通编辑器命令前必须先获取。同一进程仅一个 MCP 会话拥有控制权；当前拥有者重复获取会刷新空闲时间。连续空闲 60 秒自动释放，执行操作和后台任务期间暂停空闲计时。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "ownerLabel": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `editor_control_release`

完成操作后主动释放本会话的 MCP 控制模式。正在执行命令或后台任务时返回 MCPCT0004 busy，需等任务完成或取消后结束；不能释放其他 AI 会话持有的控制权。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `editor_control_status`

读取当前 MCP 控制模式、拥有者与空闲释放状态；未获取控制权时也可查询。查询状态不会续期空闲时间。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `editor_get_state`

读取当前应用、项目、选择、播放头、播放、任务和实时状态版本。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "includeProject": {
      "type": "boolean"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `editor_wait_for_change`

等待 uiRevision 变化或超时，读取鼠标编辑、播放、选择与 AI 操作后的最新状态，减少完整项目轮询。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "afterUiRevision": {
      "type": "integer",
      "minimum": 0
    },
    "timeoutMilliseconds": {
      "type": "integer",
      "minimum": 0,
      "maximum": 30000
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `editor_select`

选择轨道、素材或多个片段，并立即更新软件选中状态。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "trackId": {
      "type": "string"
    },
    "assetId": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `project_new`

新建项目；有未保存修改时需显式 discardChanges。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "discardChanges": {
      "type": "boolean"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `project_open`

从本地 .tvep 打开项目；有未保存修改时需显式 discardChanges。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "discardChanges": {
      "type": "boolean"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `project_save`

保存当前项目；path 可用于另存为，缺省使用当前项目路径。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `project_close`

关闭当前项目，创建空白项目；丢弃未保存修改需显式指定。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "discardChanges": {
      "type": "boolean"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `project_validate`

检查完整项目的结构、时间、素材引用、序列和效果参数。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `project_patch`

原子应用 RFC 6902 JSON Patch；支持完整项目的所有公开模型字段。请先读取模型和 revision，再用 expectedRevision 防止覆盖其他操作。失败不修改项目，成功可撤销。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "operations": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "op": {
            "type": "string",
            "enum": [
              "add",
              "remove",
              "replace",
              "move",
              "copy",
              "test"
            ]
          },
          "path": {
            "type": "string"
          },
          "from": {
            "type": "string"
          },
          "value": {}
        },
        "required": [
          "op",
          "path"
        ],
        "additionalProperties": false
      }
    }
  },
  "required": [
    "operations"
  ],
  "additionalProperties": false
}
```

### `model_get_schema`

读取实际模型的完整 JSON Schema，包含全部文字、动画、遮罩、效果、转场、音频和关键帧字段。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "model": {
      "type": "string",
      "enum": [
        "project",
        "sequence",
        "track",
        "clip",
        "text",
        "motion",
        "transform",
        "mask",
        "effect",
        "transition",
        "keyframe",
        "media",
        "marker",
        "shape",
        "subclip",
        "bin",
        "multicam",
        "audio"
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `history_undo`

撤销最近一次编辑，并同步时间线、属性和预览。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `history_redo`

重做最近一次撤销，并同步软件界面。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `sequence_list`

列出项目中的全部序列。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `sequence_create`

新建可编辑序列。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "width": {
      "type": "integer",
      "minimum": 16,
      "maximum": 16384
    },
    "height": {
      "type": "integer",
      "minimum": 16,
      "maximum": 16384
    },
    "framesPerSecond": {
      "type": "number",
      "minimum": 0.1,
      "maximum": 240
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `sequence_select`

切换当前编辑序列并刷新时间线和预览。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "sequenceId": {
      "type": "string"
    }
  },
  "required": [
    "sequenceId"
  ],
  "additionalProperties": false
}
```

### `sequence_update`

编辑序列的画布、帧率、色彩空间、工作区和背景等全部参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "sequenceId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/sequence"
    }
  },
  "required": [
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "sequence": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "workingColorSpace": {
          "type": "string"
        },
        "outputColorSpace": {
          "type": "string"
        },
        "backgroundColor": {
          "type": "string"
        },
        "width": {
          "type": "integer",
          "minimum": 16,
          "maximum": 16384
        },
        "height": {
          "type": "integer",
          "minimum": 16,
          "maximum": 16384
        },
        "framesPerSecond": {
          "type": "number",
          "minimum": 0.1,
          "maximum": 240
        },
        "audioSampleRate": {
          "type": "integer",
          "minimum": 8000,
          "maximum": 192000
        },
        "magneticTimeline": {
          "type": "boolean",
          "deprecated": true,
          "description": "旧工程兼容字段，不再控制行为；使用轨道 isSnapping 与工作区 snap。"
        },
        "playheadSeconds": {
          "type": "number",
          "minimum": 0
        },
        "workAreaStartSeconds": {
          "type": "number",
          "minimum": 0
        },
        "workAreaEndSeconds": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `sequence_remove`

从项目删除指定序列。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "sequenceId": {
      "type": "string"
    }
  },
  "required": [
    "sequenceId"
  ],
  "additionalProperties": false
}
```

### `sequence_nest`

把已有序列作为嵌套序列片段插入指定轨道。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "sequenceId": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "sequenceId",
    "trackId"
  ],
  "additionalProperties": false
}
```

### `media_list`

读取素材元数据、代理状态、素材箱和标签。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `media_import`

导入一个或多个本地媒体文件并探测实际元数据。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "paths": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "binId": {
      "type": "string"
    }
  },
  "required": [
    "paths"
  ],
  "additionalProperties": false
}
```

### `media_image_sequence`

发现首帧所在的连续图片序列，按帧率编码为视频并导入当前项目，返回后台任务 ID。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "firstFrame": {
      "type": "string"
    },
    "path": {
      "type": "string"
    },
    "framesPerSecond": {
      "type": "number",
      "minimum": 0.1,
      "maximum": 240
    },
    "name": {
      "type": "string"
    },
    "binId": {
      "type": "string"
    }
  },
  "required": [
    "firstFrame",
    "path"
  ],
  "additionalProperties": false
}
```

### `media_update`

编辑素材的名称、标签、色彩空间、入出点、标记和所有元数据字段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/media"
    }
  },
  "required": [
    "assetId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "media": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "sourcePath": {
          "type": "string"
        },
        "labelColor": {
          "type": "string"
        },
        "description": {
          "type": "string"
        },
        "inputColorSpace": {
          "type": "string"
        },
        "binId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "proxyPath": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "videoCodec": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "audioCodec": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "fileModifiedUtc": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "kind": {
          "type": "string",
          "enum": [
            "Video",
            "Audio",
            "Image"
          ]
        },
        "durationSeconds": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        },
        "width": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 1
            },
            {
              "type": "null"
            }
          ]
        },
        "height": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 1
            },
            {
              "type": "null"
            }
          ]
        },
        "framesPerSecond": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        },
        "hasAudio": {
          "type": "boolean"
        },
        "audioStreams": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/sourceAudioStream"
          }
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string"
          }
        },
        "metadata": {
          "type": "object",
          "additionalProperties": {
            "type": "string"
          }
        },
        "sourceInSeconds": {
          "type": "number",
          "minimum": 0
        },
        "sourceOutSeconds": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        },
        "markers": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/marker"
          }
        },
        "useProxy": {
          "type": "boolean"
        },
        "audioChannels": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 1
            },
            {
              "type": "null"
            }
          ]
        },
        "audioSampleRate": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 1
            },
            {
              "type": "null"
            }
          ]
        },
        "fileSizeBytes": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "sourceAudioStream": {
      "type": "object",
      "properties": {
        "index": {
          "type": "integer",
          "minimum": 0
        },
        "streamIndex": {
          "type": "integer",
          "minimum": 0
        },
        "codec": {
          "type": "string"
        },
        "channels": {
          "type": "integer",
          "minimum": 0
        },
        "sampleRate": {
          "type": "integer",
          "minimum": 0
        },
        "language": {
          "type": "string"
        },
        "title": {
          "type": "string"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "marker": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "notes": {
          "type": "string"
        },
        "color": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `media_remove`

从项目移除素材；removeClips 控制是否同时移除引用片段，不删除源文件。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "removeClips": {
      "type": "boolean"
    }
  },
  "required": [
    "assetId"
  ],
  "additionalProperties": false
}
```

### `media_relink`

把素材重新链接到实际存在的本地文件。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "path": {
      "type": "string"
    }
  },
  "required": [
    "assetId",
    "path"
  ],
  "additionalProperties": false
}
```

### `media_proxy`

附加或生成代理、切换代理使用状态，预览读取最新设置；generate=true 时必须提供输出 path。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "path": {
      "type": "string"
    },
    "useProxy": {
      "type": "boolean"
    },
    "generate": {
      "type": "boolean"
    }
  },
  "required": [
    "assetId"
  ],
  "additionalProperties": false
}
```

### `media_analyze`

分析实际媒体的响度、波形、场景切点或节奏；返回后台任务 ID。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "kind": {
      "type": "string",
      "enum": [
        "loudness",
        "waveform",
        "sceneCuts",
        "tempo"
      ]
    },
    "options": {
      "$ref": "#/$defs/analysisSettings"
    }
  },
  "required": [
    "assetId",
    "kind"
  ],
  "additionalProperties": false,
  "$defs": {
    "analysisSettings": {
      "type": "object",
      "properties": {
        "startSeconds": {
          "type": "number",
          "minimum": 0
        },
        "durationSeconds": {
          "type": "number",
          "minimum": 1e-06,
          "maximum": 600
        },
        "bins": {
          "type": "integer",
          "minimum": 8,
          "maximum": 2000
        },
        "threshold": {
          "type": "number",
          "minimum": 0.01,
          "maximum": 1
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `media_reframe`

采样片段实际画面，估算主体中心并生成可编辑位置关键帧；可先返回分析结果再决定应用。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "samples": {
      "type": "integer",
      "minimum": 2,
      "maximum": 61
    },
    "apply": {
      "type": "boolean"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `bin_create`

新建项目素材箱，可指定父素材箱。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "parentId": {
      "type": "string"
    }
  },
  "required": [
    "name"
  ],
  "additionalProperties": false
}
```

### `bin_update`

编辑素材箱名称或父级。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "binId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/bin"
    }
  },
  "required": [
    "binId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "bin": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "parentId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `bin_remove`

删除项目素材箱，保留源文件。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "binId": {
      "type": "string"
    }
  },
  "required": [
    "binId"
  ],
  "additionalProperties": false
}
```

### `subclip_create`

根据素材入出点创建可复用子片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "inSeconds": {
      "type": "number",
      "minimum": 0
    },
    "outSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "assetId",
    "inSeconds",
    "outSeconds"
  ],
  "additionalProperties": false
}
```

### `subclip_update`

编辑子片段的入出点、名称、备注和标签。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "subclipId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/subclip"
    }
  },
  "required": [
    "subclipId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "subclip": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "parentAssetId": {
          "type": "string"
        },
        "notes": {
          "type": "string"
        },
        "createdUtc": {
          "type": "string"
        },
        "inSeconds": {
          "type": "number",
          "minimum": 0
        },
        "outSeconds": {
          "type": "number",
          "minimum": 0
        },
        "tags": {
          "type": "array",
          "items": {
            "type": "string"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `subclip_remove`

删除可复用子片段定义。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "subclipId": {
      "type": "string"
    }
  },
  "required": [
    "subclipId"
  ],
  "additionalProperties": false
}
```

### `track_create`

创建视频或音频轨道。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "kind": {
      "type": "string",
      "enum": [
        "Video",
        "Audio"
      ]
    },
    "order": {
      "type": "integer",
      "minimum": 0
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `track_update`

编辑轨道名称、锁定、隐藏、静音、同步锁和独立轨道吸附开关。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/track"
    }
  },
  "required": [
    "trackId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "track": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "isLocked": {
          "type": "boolean"
        },
        "isHidden": {
          "type": "boolean"
        },
        "isMuted": {
          "type": "boolean"
        },
        "isSyncLocked": {
          "type": "boolean"
        },
        "isSnapping": {
          "type": "boolean",
          "description": "轨道吸附属性；总吸附 snap 开启时，编辑提交后自动闭合空隙。"
        },
        "kind": {
          "type": "string",
          "enum": [
            "Video",
            "Audio"
          ]
        },
        "order": {
          "type": "integer",
          "minimum": 0
        },
        "clips": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/clip"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "clip": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "blendMode": {
          "type": "string"
        },
        "frameInterpolation": {
          "type": "string"
        },
        "templateId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "mediaAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "nestedSequenceId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamAngleId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "groupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "linkGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "color": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedAudioClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedVideoClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamLocalStartSeconds": {
          "type": "number"
        },
        "startSeconds": {
          "type": "number"
        },
        "durationSeconds": {
          "type": "number"
        },
        "sourceInSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        },
        "volume": {
          "type": "number"
        },
        "opacity": {
          "type": "number"
        },
        "speedAdjustmentApplied": {
          "type": "boolean"
        },
        "isMuted": {
          "type": "boolean"
        },
        "isReversed": {
          "type": "boolean"
        },
        "audioDetached": {
          "type": "boolean"
        },
        "sourceAudioStreamIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 63
        },
        "stabilization": {
          "$ref": "#/$defs/stabilization"
        },
        "kind": {
          "type": "string",
          "enum": [
            "Video",
            "Audio",
            "Image",
            "Text",
            "Color",
            "Adjustment",
            "Sequence"
          ]
        },
        "mask": {
          "anyOf": [
            {
              "$ref": "#/$defs/mask"
            },
            {
              "type": "null"
            }
          ]
        },
        "transform": {
          "$ref": "#/$defs/transform"
        },
        "text": {
          "anyOf": [
            {
              "$ref": "#/$defs/text"
            },
            {
              "type": "null"
            }
          ]
        },
        "timeRemapPoints": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/remapPoint"
          }
        },
        "effects": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/effect"
          }
        },
        "transitions": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/transition"
          }
        },
        "keyframes": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/keyframe"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "stabilization": {
      "type": "object",
      "properties": {
        "enabled": {
          "type": "boolean"
        },
        "smoothing": {
          "type": "integer",
          "minimum": 1,
          "maximum": 120
        },
        "shakiness": {
          "type": "integer",
          "minimum": 1,
          "maximum": 10
        },
        "maxZoomPercent": {
          "type": "number",
          "minimum": 0,
          "maximum": 50
        },
        "originalAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "originalSourceIn": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "mask": {
      "type": "object",
      "properties": {
        "centerX": {
          "type": "number"
        },
        "centerY": {
          "type": "number"
        },
        "width": {
          "type": "number"
        },
        "height": {
          "type": "number"
        },
        "feather": {
          "type": "number"
        },
        "shape": {
          "type": "string",
          "enum": [
            "Rectangle",
            "Ellipse",
            "Path"
          ]
        },
        "invert": {
          "type": "boolean"
        },
        "points": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/point"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "point": {
      "type": "object",
      "properties": {
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transform": {
      "type": "object",
      "properties": {
        "positionX": {
          "type": "number"
        },
        "positionY": {
          "type": "number"
        },
        "scaleX": {
          "type": "number"
        },
        "scaleY": {
          "type": "number"
        },
        "rotationDegrees": {
          "type": "number"
        },
        "anchorX": {
          "type": "number"
        },
        "anchorY": {
          "type": "number"
        },
        "cropLeft": {
          "type": "number"
        },
        "cropTop": {
          "type": "number"
        },
        "cropRight": {
          "type": "number"
        },
        "cropBottom": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "text": {
      "type": "object",
      "properties": {
        "content": {
          "type": "string"
        },
        "fontFamily": {
          "type": "string"
        },
        "foregroundColor": {
          "type": "string"
        },
        "outlineColor": {
          "type": "string"
        },
        "shadowColor": {
          "type": "string"
        },
        "glowColor": {
          "type": "string"
        },
        "horizontalAlignment": {
          "type": "string"
        },
        "verticalAlignment": {
          "type": "string"
        },
        "fontSize": {
          "type": "number"
        },
        "backgroundPaddingX": {
          "type": "number"
        },
        "backgroundPaddingY": {
          "type": "number"
        },
        "backgroundCornerRadius": {
          "type": "number"
        },
        "backgroundOpacity": {
          "type": "number"
        },
        "horizontalInset": {
          "type": "number"
        },
        "verticalInset": {
          "type": "number"
        },
        "outlineThickness": {
          "type": "number"
        },
        "shadowBlur": {
          "type": "number"
        },
        "shadowOpacity": {
          "type": "number"
        },
        "shadowOffsetX": {
          "type": "number"
        },
        "shadowOffsetY": {
          "type": "number"
        },
        "glowRadius": {
          "type": "number"
        },
        "glowOpacity": {
          "type": "number"
        },
        "letterSpacing": {
          "type": "number"
        },
        "lineHeight": {
          "type": "number"
        },
        "bold": {
          "type": "boolean"
        },
        "italic": {
          "type": "boolean"
        },
        "underline": {
          "type": "boolean"
        },
        "outlineEnabled": {
          "type": "boolean"
        },
        "shadowEnabled": {
          "type": "boolean"
        },
        "backgroundColor": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "motion": {
          "anyOf": [
            {
              "$ref": "#/$defs/motion"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "motion": {
      "type": "object",
      "properties": {
        "delaySeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "secondsPerCharacter": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "countSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeInSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeOutSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popOvershoot": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomEndScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "creditsSpeed": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "decimals": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0,
              "maximum": 12
            },
            {
              "type": "null"
            }
          ]
        },
        "slideFrom": {
          "anyOf": [
            {
              "type": "string",
              "enum": [
                "Left",
                "Right",
                "Top",
                "Bottom"
              ]
            },
            {
              "type": "null"
            }
          ]
        },
        "easing": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDirection": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "remapPoint": {
      "type": "object",
      "properties": {
        "timeSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "effect": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "isEnabled": {
          "type": "boolean"
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transition": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "edge": {
          "type": "string",
          "enum": [
            "In",
            "Out"
          ]
        },
        "durationSeconds": {
          "type": "number",
          "minimum": 1e-06
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "keyframe": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "property": {
          "type": "string"
        },
        "interpolation": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number"
        },
        "value": {
          "type": "number"
        },
        "bezierOutTime": {
          "type": "number"
        },
        "bezierOutValue": {
          "type": "number"
        },
        "bezierInTime": {
          "type": "number"
        },
        "bezierInValue": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `track_remove`

删除轨道及其片段，可撤销。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackId": {
      "type": "string"
    }
  },
  "required": [
    "trackId"
  ],
  "additionalProperties": false
}
```

### `track_reorder`

按 trackIds 数组重新排列当前序列轨道。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    }
  },
  "required": [
    "trackIds"
  ],
  "additionalProperties": false
}
```

### `clip_create`

在轨道创建视频、音频、图片、文字、纯色、调整图层或嵌套序列片段；支持完整片段模型。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackId": {
      "type": "string"
    },
    "clip": {
      "$ref": "#/$defs/clip"
    }
  },
  "required": [
    "trackId",
    "clip"
  ],
  "additionalProperties": false,
  "$defs": {
    "clip": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "blendMode": {
          "type": "string"
        },
        "frameInterpolation": {
          "type": "string"
        },
        "templateId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "mediaAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "nestedSequenceId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamAngleId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "groupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "linkGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "color": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedAudioClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedVideoClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamLocalStartSeconds": {
          "type": "number"
        },
        "startSeconds": {
          "type": "number"
        },
        "durationSeconds": {
          "type": "number"
        },
        "sourceInSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        },
        "volume": {
          "type": "number"
        },
        "opacity": {
          "type": "number"
        },
        "speedAdjustmentApplied": {
          "type": "boolean"
        },
        "isMuted": {
          "type": "boolean"
        },
        "isReversed": {
          "type": "boolean"
        },
        "audioDetached": {
          "type": "boolean"
        },
        "sourceAudioStreamIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 63
        },
        "stabilization": {
          "$ref": "#/$defs/stabilization"
        },
        "kind": {
          "type": "string",
          "enum": [
            "Video",
            "Audio",
            "Image",
            "Text",
            "Color",
            "Adjustment",
            "Sequence"
          ]
        },
        "mask": {
          "anyOf": [
            {
              "$ref": "#/$defs/mask"
            },
            {
              "type": "null"
            }
          ]
        },
        "transform": {
          "$ref": "#/$defs/transform"
        },
        "text": {
          "anyOf": [
            {
              "$ref": "#/$defs/text"
            },
            {
              "type": "null"
            }
          ]
        },
        "timeRemapPoints": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/remapPoint"
          }
        },
        "effects": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/effect"
          }
        },
        "transitions": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/transition"
          }
        },
        "keyframes": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/keyframe"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "stabilization": {
      "type": "object",
      "properties": {
        "enabled": {
          "type": "boolean"
        },
        "smoothing": {
          "type": "integer",
          "minimum": 1,
          "maximum": 120
        },
        "shakiness": {
          "type": "integer",
          "minimum": 1,
          "maximum": 10
        },
        "maxZoomPercent": {
          "type": "number",
          "minimum": 0,
          "maximum": 50
        },
        "originalAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "originalSourceIn": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "mask": {
      "type": "object",
      "properties": {
        "centerX": {
          "type": "number"
        },
        "centerY": {
          "type": "number"
        },
        "width": {
          "type": "number"
        },
        "height": {
          "type": "number"
        },
        "feather": {
          "type": "number"
        },
        "shape": {
          "type": "string",
          "enum": [
            "Rectangle",
            "Ellipse",
            "Path"
          ]
        },
        "invert": {
          "type": "boolean"
        },
        "points": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/point"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "point": {
      "type": "object",
      "properties": {
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transform": {
      "type": "object",
      "properties": {
        "positionX": {
          "type": "number"
        },
        "positionY": {
          "type": "number"
        },
        "scaleX": {
          "type": "number"
        },
        "scaleY": {
          "type": "number"
        },
        "rotationDegrees": {
          "type": "number"
        },
        "anchorX": {
          "type": "number"
        },
        "anchorY": {
          "type": "number"
        },
        "cropLeft": {
          "type": "number"
        },
        "cropTop": {
          "type": "number"
        },
        "cropRight": {
          "type": "number"
        },
        "cropBottom": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "text": {
      "type": "object",
      "properties": {
        "content": {
          "type": "string"
        },
        "fontFamily": {
          "type": "string"
        },
        "foregroundColor": {
          "type": "string"
        },
        "outlineColor": {
          "type": "string"
        },
        "shadowColor": {
          "type": "string"
        },
        "glowColor": {
          "type": "string"
        },
        "horizontalAlignment": {
          "type": "string"
        },
        "verticalAlignment": {
          "type": "string"
        },
        "fontSize": {
          "type": "number"
        },
        "backgroundPaddingX": {
          "type": "number"
        },
        "backgroundPaddingY": {
          "type": "number"
        },
        "backgroundCornerRadius": {
          "type": "number"
        },
        "backgroundOpacity": {
          "type": "number"
        },
        "horizontalInset": {
          "type": "number"
        },
        "verticalInset": {
          "type": "number"
        },
        "outlineThickness": {
          "type": "number"
        },
        "shadowBlur": {
          "type": "number"
        },
        "shadowOpacity": {
          "type": "number"
        },
        "shadowOffsetX": {
          "type": "number"
        },
        "shadowOffsetY": {
          "type": "number"
        },
        "glowRadius": {
          "type": "number"
        },
        "glowOpacity": {
          "type": "number"
        },
        "letterSpacing": {
          "type": "number"
        },
        "lineHeight": {
          "type": "number"
        },
        "bold": {
          "type": "boolean"
        },
        "italic": {
          "type": "boolean"
        },
        "underline": {
          "type": "boolean"
        },
        "outlineEnabled": {
          "type": "boolean"
        },
        "shadowEnabled": {
          "type": "boolean"
        },
        "backgroundColor": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "motion": {
          "anyOf": [
            {
              "$ref": "#/$defs/motion"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "motion": {
      "type": "object",
      "properties": {
        "delaySeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "secondsPerCharacter": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "countSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeInSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeOutSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popOvershoot": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomEndScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "creditsSpeed": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "decimals": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0,
              "maximum": 12
            },
            {
              "type": "null"
            }
          ]
        },
        "slideFrom": {
          "anyOf": [
            {
              "type": "string",
              "enum": [
                "Left",
                "Right",
                "Top",
                "Bottom"
              ]
            },
            {
              "type": "null"
            }
          ]
        },
        "easing": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDirection": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "remapPoint": {
      "type": "object",
      "properties": {
        "timeSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "effect": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "isEnabled": {
          "type": "boolean"
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transition": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "edge": {
          "type": "string",
          "enum": [
            "In",
            "Out"
          ]
        },
        "durationSeconds": {
          "type": "number",
          "minimum": 1e-06
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "keyframe": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "property": {
          "type": "string"
        },
        "interpolation": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number"
        },
        "value": {
          "type": "number"
        },
        "bezierOutTime": {
          "type": "number"
        },
        "bezierOutValue": {
          "type": "number"
        },
        "bezierInTime": {
          "type": "number"
        },
        "bezierInValue": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `clip_update`

修改片段的全部公开参数，包括时间、变换、裁切、透明度、混合、遮罩、音量、速度、效果、转场和关键帧。对象递归合并，数组整体替换。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/clip"
    }
  },
  "required": [
    "clipId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "clip": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "blendMode": {
          "type": "string"
        },
        "frameInterpolation": {
          "type": "string"
        },
        "templateId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "mediaAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "nestedSequenceId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamAngleId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "groupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "linkGroupId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "color": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedAudioClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "detachedVideoClipId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "multicamLocalStartSeconds": {
          "type": "number"
        },
        "startSeconds": {
          "type": "number"
        },
        "durationSeconds": {
          "type": "number"
        },
        "sourceInSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        },
        "volume": {
          "type": "number"
        },
        "opacity": {
          "type": "number"
        },
        "speedAdjustmentApplied": {
          "type": "boolean"
        },
        "isMuted": {
          "type": "boolean"
        },
        "isReversed": {
          "type": "boolean"
        },
        "audioDetached": {
          "type": "boolean"
        },
        "sourceAudioStreamIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 63
        },
        "stabilization": {
          "$ref": "#/$defs/stabilization"
        },
        "kind": {
          "type": "string",
          "enum": [
            "Video",
            "Audio",
            "Image",
            "Text",
            "Color",
            "Adjustment",
            "Sequence"
          ]
        },
        "mask": {
          "anyOf": [
            {
              "$ref": "#/$defs/mask"
            },
            {
              "type": "null"
            }
          ]
        },
        "transform": {
          "$ref": "#/$defs/transform"
        },
        "text": {
          "anyOf": [
            {
              "$ref": "#/$defs/text"
            },
            {
              "type": "null"
            }
          ]
        },
        "timeRemapPoints": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/remapPoint"
          }
        },
        "effects": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/effect"
          }
        },
        "transitions": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/transition"
          }
        },
        "keyframes": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/keyframe"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "stabilization": {
      "type": "object",
      "properties": {
        "enabled": {
          "type": "boolean"
        },
        "smoothing": {
          "type": "integer",
          "minimum": 1,
          "maximum": 120
        },
        "shakiness": {
          "type": "integer",
          "minimum": 1,
          "maximum": 10
        },
        "maxZoomPercent": {
          "type": "number",
          "minimum": 0,
          "maximum": 50
        },
        "originalAssetId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "originalSourceIn": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "mask": {
      "type": "object",
      "properties": {
        "centerX": {
          "type": "number"
        },
        "centerY": {
          "type": "number"
        },
        "width": {
          "type": "number"
        },
        "height": {
          "type": "number"
        },
        "feather": {
          "type": "number"
        },
        "shape": {
          "type": "string",
          "enum": [
            "Rectangle",
            "Ellipse",
            "Path"
          ]
        },
        "invert": {
          "type": "boolean"
        },
        "points": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/point"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "point": {
      "type": "object",
      "properties": {
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transform": {
      "type": "object",
      "properties": {
        "positionX": {
          "type": "number"
        },
        "positionY": {
          "type": "number"
        },
        "scaleX": {
          "type": "number"
        },
        "scaleY": {
          "type": "number"
        },
        "rotationDegrees": {
          "type": "number"
        },
        "anchorX": {
          "type": "number"
        },
        "anchorY": {
          "type": "number"
        },
        "cropLeft": {
          "type": "number"
        },
        "cropTop": {
          "type": "number"
        },
        "cropRight": {
          "type": "number"
        },
        "cropBottom": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "text": {
      "type": "object",
      "properties": {
        "content": {
          "type": "string"
        },
        "fontFamily": {
          "type": "string"
        },
        "foregroundColor": {
          "type": "string"
        },
        "outlineColor": {
          "type": "string"
        },
        "shadowColor": {
          "type": "string"
        },
        "glowColor": {
          "type": "string"
        },
        "horizontalAlignment": {
          "type": "string"
        },
        "verticalAlignment": {
          "type": "string"
        },
        "fontSize": {
          "type": "number"
        },
        "backgroundPaddingX": {
          "type": "number"
        },
        "backgroundPaddingY": {
          "type": "number"
        },
        "backgroundCornerRadius": {
          "type": "number"
        },
        "backgroundOpacity": {
          "type": "number"
        },
        "horizontalInset": {
          "type": "number"
        },
        "verticalInset": {
          "type": "number"
        },
        "outlineThickness": {
          "type": "number"
        },
        "shadowBlur": {
          "type": "number"
        },
        "shadowOpacity": {
          "type": "number"
        },
        "shadowOffsetX": {
          "type": "number"
        },
        "shadowOffsetY": {
          "type": "number"
        },
        "glowRadius": {
          "type": "number"
        },
        "glowOpacity": {
          "type": "number"
        },
        "letterSpacing": {
          "type": "number"
        },
        "lineHeight": {
          "type": "number"
        },
        "bold": {
          "type": "boolean"
        },
        "italic": {
          "type": "boolean"
        },
        "underline": {
          "type": "boolean"
        },
        "outlineEnabled": {
          "type": "boolean"
        },
        "shadowEnabled": {
          "type": "boolean"
        },
        "backgroundColor": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "motion": {
          "anyOf": [
            {
              "$ref": "#/$defs/motion"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "motion": {
      "type": "object",
      "properties": {
        "delaySeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "secondsPerCharacter": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "countSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeInSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeOutSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popOvershoot": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomEndScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "creditsSpeed": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "decimals": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0,
              "maximum": 12
            },
            {
              "type": "null"
            }
          ]
        },
        "slideFrom": {
          "anyOf": [
            {
              "type": "string",
              "enum": [
                "Left",
                "Right",
                "Top",
                "Bottom"
              ]
            },
            {
              "type": "null"
            }
          ]
        },
        "easing": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDirection": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "remapPoint": {
      "type": "object",
      "properties": {
        "timeSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "effect": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "isEnabled": {
          "type": "boolean"
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "transition": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "edge": {
          "type": "string",
          "enum": [
            "In",
            "Out"
          ]
        },
        "durationSeconds": {
          "type": "number",
          "minimum": 1e-06
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "keyframe": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "property": {
          "type": "string"
        },
        "interpolation": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number"
        },
        "value": {
          "type": "number"
        },
        "bezierOutTime": {
          "type": "number"
        },
        "bezierOutValue": {
          "type": "number"
        },
        "bezierInTime": {
          "type": "number"
        },
        "bezierInValue": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `clip_remove`

删除一个或多个片段，可选波纹删除。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "ripple": {
      "type": "boolean"
    }
  },
  "required": [
    "clipIds"
  ],
  "additionalProperties": false
}
```

### `clip_move`

移动片段到指定时间或轨道，可按照目标轨道吸附规则定位。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "trackId": {
      "type": "string"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "snap": {
      "type": "boolean"
    }
  },
  "required": [
    "clipIds",
    "startSeconds"
  ],
  "additionalProperties": false
}
```

### `clip_trim`

直接设置片段的时间线起点、持续时长或源入点。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "sourceInSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `clip_split`

在指定序列时间分割片段，正确保留源时间、效果和关键帧。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "clipId",
    "timeSeconds"
  ],
  "additionalProperties": false
}
```

### `clip_freeze_frame`

捕获视频片段指定时间的真实源画面并创建静帧；默认插入 1 秒静帧，replace=true 则替换原片段范围。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "path": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "replace": {
      "type": "boolean"
    }
  },
  "required": [
    "clipId",
    "path"
  ],
  "additionalProperties": false
}
```

### `clip_duplicate`

复制多个片段，可指定相对位移和目标轨道。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "offsetSeconds": {
      "type": "number"
    },
    "trackId": {
      "type": "string"
    }
  },
  "required": [
    "clipIds"
  ],
  "additionalProperties": false
}
```

### `clip_group`

编组或取消编组指定片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "grouped": {
      "type": "boolean"
    }
  },
  "required": [
    "clipIds"
  ],
  "additionalProperties": false
}
```

### `clip_link`

链接或解除链接音视频片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "linked": {
      "type": "boolean"
    }
  },
  "required": [
    "clipIds"
  ],
  "additionalProperties": false
}
```

### `title_apply`

创建或应用内置文字或图形贴纸样式，样式、几何和动画参数仍可完整修改。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "presetId": {
      "type": "string"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "content": {
      "type": "string"
    }
  },
  "required": [
    "presetId"
  ],
  "additionalProperties": false
}
```

### `title_style_update`

编辑全部文字样式参数，包括字体、描边、阴影、发光、背景、字距、行距、对齐和安全区。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/text"
    }
  },
  "required": [
    "clipId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "text": {
      "type": "object",
      "properties": {
        "content": {
          "type": "string"
        },
        "fontFamily": {
          "type": "string"
        },
        "foregroundColor": {
          "type": "string"
        },
        "outlineColor": {
          "type": "string"
        },
        "shadowColor": {
          "type": "string"
        },
        "glowColor": {
          "type": "string"
        },
        "horizontalAlignment": {
          "type": "string"
        },
        "verticalAlignment": {
          "type": "string"
        },
        "fontSize": {
          "type": "number"
        },
        "backgroundPaddingX": {
          "type": "number"
        },
        "backgroundPaddingY": {
          "type": "number"
        },
        "backgroundCornerRadius": {
          "type": "number"
        },
        "backgroundOpacity": {
          "type": "number"
        },
        "horizontalInset": {
          "type": "number"
        },
        "verticalInset": {
          "type": "number"
        },
        "outlineThickness": {
          "type": "number"
        },
        "shadowBlur": {
          "type": "number"
        },
        "shadowOpacity": {
          "type": "number"
        },
        "shadowOffsetX": {
          "type": "number"
        },
        "shadowOffsetY": {
          "type": "number"
        },
        "glowRadius": {
          "type": "number"
        },
        "glowOpacity": {
          "type": "number"
        },
        "letterSpacing": {
          "type": "number"
        },
        "lineHeight": {
          "type": "number"
        },
        "bold": {
          "type": "boolean"
        },
        "italic": {
          "type": "boolean"
        },
        "underline": {
          "type": "boolean"
        },
        "outlineEnabled": {
          "type": "boolean"
        },
        "shadowEnabled": {
          "type": "boolean"
        },
        "backgroundColor": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "motion": {
          "anyOf": [
            {
              "$ref": "#/$defs/motion"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "motion": {
      "type": "object",
      "properties": {
        "delaySeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "secondsPerCharacter": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "countSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeInSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeOutSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popOvershoot": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomEndScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "creditsSpeed": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "decimals": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0,
              "maximum": 12
            },
            {
              "type": "null"
            }
          ]
        },
        "slideFrom": {
          "anyOf": [
            {
              "type": "string",
              "enum": [
                "Left",
                "Right",
                "Top",
                "Bottom"
              ]
            },
            {
              "type": "null"
            }
          ]
        },
        "easing": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDirection": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `title_motion_update`

编辑打字机、数字计数、淡入淡出、滑入、弹跳、滚动、缩放和缓动的全部动画参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/motion"
    }
  },
  "required": [
    "clipId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "motion": {
      "type": "object",
      "properties": {
        "delaySeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "secondsPerCharacter": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "countSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeInSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "fadeOutSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "slideDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "popOvershoot": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDistance": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomSeconds": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomStartScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "zoomEndScale": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "creditsSpeed": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "decimals": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 0,
              "maximum": 12
            },
            {
              "type": "null"
            }
          ]
        },
        "slideFrom": {
          "anyOf": [
            {
              "type": "string",
              "enum": [
                "Left",
                "Right",
                "Top",
                "Bottom"
              ]
            },
            {
              "type": "null"
            }
          ]
        },
        "easing": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        },
        "scrollDirection": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `shape_update`

编辑图形形状、尺寸比例、填充、描边和自定义顶点。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/shape"
    }
  },
  "required": [
    "clipId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "shape": {
      "type": "object",
      "properties": {
        "kind": {
          "type": "string"
        },
        "widthFraction": {
          "type": "number"
        },
        "heightFraction": {
          "type": "number"
        },
        "fillColor": {
          "type": "string"
        },
        "strokeColor": {
          "type": "string"
        },
        "strokeFraction": {
          "type": "number"
        },
        "points": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/point"
          }
        }
      },
      "required": [],
      "additionalProperties": false
    },
    "point": {
      "type": "object",
      "properties": {
        "x": {
          "type": "number"
        },
        "y": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `mask_track`

对片段遮罩执行真实图像位移跟踪，可向前或向后分析并按需生成关键帧。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "forward": {
      "type": "boolean"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    },
    "frames": {
      "type": "integer",
      "minimum": 1,
      "maximum": 300
    },
    "apply": {
      "type": "boolean"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `effect_add`

为片段添加内置视觉或音频效果；通过 catalog_schema 查询此效果全部参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "presetId": {
      "type": "string"
    },
    "parameters": {
      "type": "object",
      "additionalProperties": true
    }
  },
  "required": [
    "clipId",
    "presetId"
  ],
  "additionalProperties": false
}
```

### `effect_update`

修改已添加效果的启用状态、名称和全部参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "effectId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/effect"
    }
  },
  "required": [
    "clipId",
    "effectId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "effect": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "isEnabled": {
          "type": "boolean"
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `effect_remove`

移除一个效果。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "effectId": {
      "type": "string"
    }
  },
  "required": [
    "clipId",
    "effectId"
  ],
  "additionalProperties": false
}
```

### `effect_reorder`

更改片段效果的应用顺序。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "effectIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    }
  },
  "required": [
    "clipId",
    "effectIds"
  ],
  "additionalProperties": false
}
```

### `transition_add`

在任意可见片段的开头或末尾添加转场，相邻片段共用边界；所有参数可编辑。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "presetId": {
      "type": "string"
    },
    "edge": {
      "type": "string",
      "enum": [
        "In",
        "Out"
      ]
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "parameters": {
      "type": "object",
      "additionalProperties": true
    }
  },
  "required": [
    "clipId",
    "presetId"
  ],
  "additionalProperties": false
}
```

### `transition_update`

修改转场效果、应用边缘、时长和全部对应参数；预览和导出共享参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "transitionId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/transition"
    }
  },
  "required": [
    "clipId",
    "transitionId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "transition": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "presetId": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "edge": {
          "type": "string",
          "enum": [
            "In",
            "Out"
          ]
        },
        "durationSeconds": {
          "type": "number",
          "minimum": 1e-06
        },
        "parameters": {
          "type": "object",
          "additionalProperties": true
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `transition_remove`

移除指定转场。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "transitionId": {
      "type": "string"
    }
  },
  "required": [
    "clipId",
    "transitionId"
  ],
  "additionalProperties": false
}
```

### `keyframe_set`

添加或更新关键帧，支持线性、保持、缓动和完整贝塞尔控制点。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "keyframe": {
      "$ref": "#/$defs/keyframe"
    }
  },
  "required": [
    "clipId",
    "keyframe"
  ],
  "additionalProperties": false,
  "$defs": {
    "keyframe": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "property": {
          "type": "string"
        },
        "interpolation": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number"
        },
        "value": {
          "type": "number"
        },
        "bezierOutTime": {
          "type": "number"
        },
        "bezierOutValue": {
          "type": "number"
        },
        "bezierInTime": {
          "type": "number"
        },
        "bezierInValue": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `keyframe_remove`

删除关键帧。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "keyframeId": {
      "type": "string"
    }
  },
  "required": [
    "clipId",
    "keyframeId"
  ],
  "additionalProperties": false
}
```

### `remap_set`

设置完整变速曲线和帧插值策略，预览与导出使用同一时间映射。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "points": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/remapPoint"
      }
    },
    "frameInterpolation": {
      "type": "string",
      "enum": [
        "Sampling",
        "Blend",
        "OpticalFlow"
      ]
    }
  },
  "required": [
    "clipId",
    "points"
  ],
  "additionalProperties": false,
  "$defs": {
    "remapPoint": {
      "type": "object",
      "properties": {
        "timeSeconds": {
          "type": "number"
        },
        "speed": {
          "type": "number"
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `marker_create`

在序列时间创建带备注和颜色的标记。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    },
    "notes": {
      "type": "string"
    },
    "color": {
      "type": "string"
    }
  },
  "required": [
    "timeSeconds"
  ],
  "additionalProperties": false
}
```

### `marker_update`

修改标记的时间、名称、备注和颜色。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "markerId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/marker"
    }
  },
  "required": [
    "markerId",
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "marker": {
      "type": "object",
      "properties": {
        "id": {
          "type": "string"
        },
        "name": {
          "type": "string"
        },
        "notes": {
          "type": "string"
        },
        "color": {
          "type": "string"
        },
        "timeSeconds": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `marker_remove`

删除序列标记。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "markerId": {
      "type": "string"
    }
  },
  "required": [
    "markerId"
  ],
  "additionalProperties": false
}
```

### `preview_seek`

移动播放头并刷新实际预览画面。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "timeSeconds"
  ],
  "additionalProperties": false
}
```

### `preview_play`

以原速正向或反向播放当前序列；speed 为 1 正放、-1 倒放、0 暂停。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "speed": {
      "type": "integer",
      "enum": [
        -1,
        0,
        1
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `preview_pause`

暂停播放并保留当前播放头。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `preview_capture`

把指定序列时间的真实合成画面导出为图片。返回任务 ID，使用 jobs_status 跟踪进度。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    },
    "sequenceId": {
      "type": "string"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `scope_capture`

根据指定时间的真实合成画面导出波形、RGB 分量、矢量或直方图示波器图片。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "mode": {
      "type": "string",
      "enum": [
        "waveform",
        "parade",
        "vectorscope",
        "histogram"
      ]
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "path",
    "mode"
  ],
  "additionalProperties": false
}
```

### `catalog_list`

列出内置标题、特效、转场、贴纸和音乐及其稳定 ID；`category:"music"` 返回内置曲目，音乐试听和插入使用专用命令。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "category": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `catalog_schema`

查询一个内置预设的全部参数、默认值、范围和可选项。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "presetId": {
      "type": "string"
    }
  },
  "required": [
    "presetId"
  ],
  "additionalProperties": false
}
```

### `catalog_preview`

渲染或读取内置效果示例的 GPU 加速缓存；返回任务 ID。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "presetId": {
      "type": "string"
    }
  },
  "required": [
    "presetId"
  ],
  "additionalProperties": false
}
```

### `export_start`

按当前项目真实渲染管线异步导出视频，返回任务 ID，不阻塞界面。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "sequenceId": {
      "type": "string"
    },
    "settings": {
      "$ref": "#/$defs/exportSettings"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false,
  "$defs": {
    "exportSettings": {
      "type": "object",
      "properties": {
        "width": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 16,
              "maximum": 16384
            },
            {
              "type": "null"
            }
          ]
        },
        "height": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 16,
              "maximum": 16384
            },
            {
              "type": "null"
            }
          ]
        },
        "framesPerSecond": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0.1,
              "maximum": 240
            },
            {
              "type": "null"
            }
          ]
        },
        "videoBitrateKbps": {
          "anyOf": [
            {
              "type": "integer",
              "minimum": 1
            },
            {
              "type": "null"
            }
          ]
        },
        "startSeconds": {
          "type": "number",
          "minimum": 0
        },
        "endSeconds": {
          "anyOf": [
            {
              "type": "number",
              "minimum": 0
            },
            {
              "type": "null"
            }
          ]
        },
        "videoCodec": {
          "type": "string",
          "enum": [
            "H264",
            "HEVC",
            "AV1"
          ]
        },
        "bitDepth": {
          "type": "integer",
          "enum": [
            8,
            10
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `jobs_list`

读取所有导出、捕获、代理、识别和示例渲染任务及其进度。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `jobs_status`

读取指定后台任务的状态、进度、输出和分阶段错误。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "jobId": {
      "type": "string"
    }
  },
  "required": [
    "jobId"
  ],
  "additionalProperties": false
}
```

### `jobs_cancel`

取消指定后台任务并终止它拥有的外部处理进程。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "jobId": {
      "type": "string"
    }
  },
  "required": [
    "jobId"
  ],
  "additionalProperties": false
}
```

### `subtitle_list`

读取当前序列全部字幕或指定轨道的字幕。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackId": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `subtitle_import`

从 SRT 字幕文件导入实际文字片段，可设置轨道和时间偏移。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "offsetSeconds": {
      "type": "number"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `subtitle_export`

将当前序列或指定轨道的字幕导出为 SRT。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `subtitle_recognize`

使用本地 whisper.cpp 和模型识别真实带时间戳的字幕，并导入轨道；返回后台任务 ID。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "assetId": {
      "type": "string"
    },
    "modelPath": {
      "type": "string"
    },
    "whisperPath": {
      "type": "string"
    },
    "language": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "offsetSeconds": {
      "type": "number"
    }
  },
  "required": [
    "assetId",
    "modelPath"
  ],
  "additionalProperties": false
}
```

### `graphic_template_read`

读取本地图形模板的全部可编辑字段。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `graphic_template_save`

把指定文字或图形片段保存为可复用模板。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "clipId": {
      "type": "string"
    }
  },
  "required": [
    "path",
    "clipId"
  ],
  "additionalProperties": false
}
```

### `graphic_template_insert`

从本地图形模板创建可编辑片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `sequence_exchange_import`

从 XML、EDL 或 OpenTimelineIO 文件导入可编辑时间线。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "format": {
      "type": "string",
      "enum": [
        "xml",
        "edl",
        "otio"
      ]
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `sequence_exchange_export`

将当前时间线导出为 XML、EDL、OpenTimelineIO 或 AAF-XML；AAF-XML 是 XML 表示，并非二进制 AAF 文件。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "format": {
      "type": "string",
      "enum": [
        "xml",
        "edl",
        "otio",
        "aafXml"
      ]
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `audio_sync`

通过真实波形或时间码同步片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "referenceClipId": {
      "type": "string"
    },
    "movingClipId": {
      "type": "string"
    },
    "method": {
      "type": "string",
      "enum": [
        "Waveform",
        "Timecode"
      ]
    }
  },
  "required": [
    "referenceClipId",
    "movingClipId"
  ],
  "additionalProperties": false
}
```

### `audio_settings_get`

读取片段或轨道的完整音频设置；clipId 和 trackId 二选一。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `audio_settings_update`

编辑片段或轨道的增益、声像、通道、滤波、均衡、压缩、限制、降噪、回声和自动音乐闪避全部参数；clipId 和 trackId 二选一。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "trackId": {
      "type": "string"
    },
    "patch": {
      "$ref": "#/$defs/audio"
    }
  },
  "required": [
    "patch"
  ],
  "additionalProperties": false,
  "$defs": {
    "audio": {
      "type": "object",
      "properties": {
        "normalizeEnabled": {
          "type": "boolean"
        },
        "targetLufs": {
          "type": "number",
          "minimum": -36,
          "maximum": -5
        },
        "targetTruePeakDb": {
          "type": "number",
          "minimum": -9,
          "maximum": 0
        },
        "measuredLufs": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "measuredTruePeakDb": {
          "anyOf": [
            {
              "type": "number"
            },
            {
              "type": "null"
            }
          ]
        },
        "normalizationGainDb": {
          "type": "number",
          "minimum": -60,
          "maximum": 60
        },
        "category": {
          "type": "string",
          "enum": [
            "未分类",
            "对白",
            "音乐",
            "音效",
            "环境声"
          ]
        },
        "gain": {
          "type": "number",
          "minimum": 0,
          "maximum": 4
        },
        "pan": {
          "type": "number",
          "minimum": -1,
          "maximum": 1
        },
        "channelMode": {
          "type": "string",
          "enum": [
            "立体声",
            "单声道",
            "仅左声道",
            "仅右声道"
          ]
        },
        "highPassHz": {
          "type": "number",
          "minimum": 0,
          "maximum": 20000
        },
        "lowPassHz": {
          "type": "number",
          "minimum": 0,
          "maximum": 20000
        },
        "equalizerHz": {
          "type": "number",
          "minimum": 0,
          "maximum": 20000
        },
        "equalizerGainDb": {
          "type": "number",
          "minimum": -24,
          "maximum": 24
        },
        "compressorEnabled": {
          "type": "boolean"
        },
        "compressorThresholdDb": {
          "type": "number",
          "minimum": -60,
          "maximum": 0
        },
        "compressorRatio": {
          "type": "number",
          "minimum": 1,
          "maximum": 20
        },
        "limiterEnabled": {
          "type": "boolean"
        },
        "limiterCeiling": {
          "type": "number",
          "minimum": 0.05,
          "maximum": 1
        },
        "noiseReductionEnabled": {
          "type": "boolean"
        },
        "noiseReductionDb": {
          "type": "number",
          "minimum": 0,
          "maximum": 40
        },
        "echoEnabled": {
          "type": "boolean"
        },
        "echoDelayMs": {
          "type": "number",
          "minimum": 1,
          "maximum": 2000
        },
        "echoDecay": {
          "type": "number",
          "minimum": 0,
          "maximum": 0.9
        },
        "autoDuckingEnabled": {
          "type": "boolean"
        },
        "duckingGain": {
          "type": "number",
          "minimum": 0.05,
          "maximum": 1
        },
        "duckingFadeSeconds": {
          "type": "number",
          "minimum": 0,
          "maximum": 2
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `audio_source_streams`

读取所选媒体的全部源音频流及当前索引；通过clip_update的sourceAudioStreamIndex切换。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `audio_detach`

从视频创建可独立编辑的音频片段，原视频停止发声，可撤销。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `audio_attach`

重新附着分离音频；音画必须对齐，保留音频处理参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `audio_normalize`

测量所选源音轨R128响度并应用目标LUFS与真峰值约束；支持变速曲线，静音会明确报错。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "targetLufs": {
      "type": "number",
      "minimum": -36,
      "maximum": -5
    },
    "truePeakDb": {
      "type": "number",
      "minimum": -9,
      "maximum": 0
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `video_stabilize`

分析运动并生成无损防抖派生素材，自动接入预览、导出及项目保存；enabled=false恢复原视频。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "enabled": {
      "type": "boolean"
    },
    "smoothing": {
      "type": "integer",
      "minimum": 1,
      "maximum": 120
    },
    "shakiness": {
      "type": "integer",
      "minimum": 1,
      "maximum": 10
    },
    "maxZoomPercent": {
      "type": "number",
      "minimum": 0,
      "maximum": 50
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `audio_meter_get`

读取实际监听混音的左右峰值、削波保持与播放状态。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `audio_tempo`

分析片段真实音频的节奏和 BPM。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    }
  },
  "required": [
    "clipId"
  ],
  "additionalProperties": false
}
```

### `audio_remix`

根据 BPM 和目标时长生成可编辑音乐重混片段。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "targetDurationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "bpm": {
      "type": "number",
      "minimum": 0.1
    }
  },
  "required": [
    "clipId",
    "targetDurationSeconds",
    "bpm"
  ],
  "additionalProperties": false
}
```

### `multicam_list`

读取多机位组、同步方式、角度和源片段。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `multicam_create`

根据时间线、时间码或波形创建多机位组。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "name": {
      "type": "string"
    },
    "clipIds": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "syncMethod": {
      "type": "string",
      "enum": [
        "Timeline",
        "Timecode",
        "Waveform"
      ]
    }
  },
  "required": [
    "name",
    "clipIds"
  ],
  "additionalProperties": false
}
```

### `multicam_switch`

切换多机位片段角度，可在指定时间建立新的机位剪切。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "clipId": {
      "type": "string"
    },
    "angleId": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "clipId",
    "angleId"
  ],
  "additionalProperties": false
}
```

### `paper_edit`

按时间范围保留或删除内容，执行基于文本的纸面剪辑。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "trackId": {
      "type": "string"
    },
    "ranges": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/timeRange"
      }
    },
    "keepSelected": {
      "type": "boolean"
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "trackId",
    "ranges"
  ],
  "additionalProperties": false,
  "$defs": {
    "timeRange": {
      "type": "object",
      "properties": {
        "startSeconds": {
          "type": "number",
          "minimum": 0
        },
        "endSeconds": {
          "type": "number",
          "minimum": 0
        }
      },
      "required": [
        "startSeconds",
        "endSeconds"
      ],
      "additionalProperties": false
    }
  }
}
```

### `voiceover_devices`

列出当前可录音设备。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `voiceover_start`

在指定播放头和音轨开始真实录音。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "deviceId": {
      "type": "integer",
      "minimum": -1
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "trackId": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

### `voiceover_stop`

停止录音、保存 WAV，并按需导入和插入当前时间线。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    },
    "insert": {
      "type": "boolean"
    }
  },
  "required": [
    "path"
  ],
  "additionalProperties": false
}
```

### `voiceover_cancel`

取消当前录音。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `workspace_get`

读取当前工作区、时间线缩放和预览设置。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `workspace_set`

应用、保存、恢复或重置工作区；可编辑面板比例、缩放、预览、自动保存和自定义快捷键。shortcuts 中 null 删除指定自定义快捷键；restore 时不同时指定新参数。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": [
        "apply",
        "save",
        "restore",
        "reset"
      ]
    },
    "preset": {
      "type": "string",
      "enum": [
        "editing",
        "color",
        "audio"
      ]
    },
    "upperRatio": {
      "type": "number",
      "minimum": 0.15,
      "maximum": 0.85
    },
    "libraryRatio": {
      "type": "number",
      "minimum": 0.15,
      "maximum": 0.85
    },
    "zoom": {
      "type": "number",
      "minimum": 0.25,
      "maximum": 4
    },
    "previewViewZoom": {
      "type": "number",
      "minimum": 0.1,
      "maximum": 4
    },
    "mediaViewIndex": {
      "type": "integer",
      "minimum": 0,
      "maximum": 2
    },
    "shortcuts": {
      "type": "object",
      "additionalProperties": {
        "anyOf": [
          {
            "type": "string"
          },
          {
            "type": "null"
          }
        ]
      }
    },
    "patch": {
      "$ref": "#/$defs/workspace"
    }
  },
  "required": [],
  "additionalProperties": false,
  "$defs": {
    "workspace": {
      "type": "object",
      "properties": {
        "upperRatio": {
          "type": "number",
          "minimum": 0.15,
          "maximum": 0.85
        },
        "libraryRatio": {
          "type": "number",
          "minimum": 0.15,
          "maximum": 0.85
        },
        "zoom": {
          "type": "number",
          "minimum": 0.25,
          "maximum": 4
        },
        "previewViewZoom": {
          "type": "number",
          "minimum": 0.1,
          "maximum": 4
        },
        "mediaViewIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 2
        },
        "shortcuts": {
          "type": "object",
          "additionalProperties": {
            "anyOf": [
              {
                "type": "string"
              },
              {
                "type": "null"
              }
            ]
          }
        },
        "snap": {
          "type": "boolean"
        },
        "gpuMode": {
          "type": "string",
          "enum": [
            "自动（GPU 优先）",
            "CPU 软件渲染",
            "NVIDIA CUDA",
            "Intel QSV",
            "AMD AMF"
          ]
        },
        "autoSave": {
          "type": "boolean"
        },
        "previewQualityIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 2
        },
        "categoryIndex": {
          "type": "integer",
          "minimum": 0,
          "maximum": 5
        },
        "globalMute": {
          "type": "boolean"
        },
        "soloTrackId": {
          "anyOf": [
            {
              "type": "string"
            },
            {
              "type": "null"
            }
          ]
        }
      },
      "required": [],
      "additionalProperties": false
    }
  }
}
```

### `ui_list_commands`

列出软件可调用的已命名界面命令白名单。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `ui_invoke`

执行 ui_list_commands 返回的已命名界面命令；仅调用白名单，不执行代码。需要输入的功能优先使用结构化专用工具。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "command": {
      "type": "string"
    }
  },
  "required": [
    "command"
  ],
  "additionalProperties": false
}
```

### `system_association_get`

读取 .tvep 的默认打开方式和当前用户注册状态。

元数据：只读。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {},
  "required": [],
  "additionalProperties": false
}
```

### `system_association_set`

为当前用户注册、设置默认、取消注册或打开 Windows 默认应用设置；使用系统白色文件图标。

元数据：可改变状态或生成输出。只读工具同样需要控制权（控制状态查询除外）。

```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": [
        "register",
        "setDefault",
        "unregister",
        "openSettings"
      ]
    }
  },
  "required": [
    "action"
  ],
  "additionalProperties": false
}
```

### 集中模型定义

#### `transform`

```json
{
  "type": "object",
  "properties": {
    "positionX": {
      "type": "number"
    },
    "positionY": {
      "type": "number"
    },
    "scaleX": {
      "type": "number"
    },
    "scaleY": {
      "type": "number"
    },
    "rotationDegrees": {
      "type": "number"
    },
    "anchorX": {
      "type": "number"
    },
    "anchorY": {
      "type": "number"
    },
    "cropLeft": {
      "type": "number"
    },
    "cropTop": {
      "type": "number"
    },
    "cropRight": {
      "type": "number"
    },
    "cropBottom": {
      "type": "number"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `motion`

```json
{
  "type": "object",
  "properties": {
    "delaySeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "secondsPerCharacter": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "countSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "fadeInSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "fadeOutSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "slideSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "slideDistance": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "popSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "popStartScale": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "popOvershoot": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "scrollSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "scrollDistance": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "zoomSeconds": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "zoomStartScale": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "zoomEndScale": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "creditsSpeed": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "decimals": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 0,
          "maximum": 12
        },
        {
          "type": "null"
        }
      ]
    },
    "slideFrom": {
      "anyOf": [
        {
          "type": "string",
          "enum": [
            "Left",
            "Right",
            "Top",
            "Bottom"
          ]
        },
        {
          "type": "null"
        }
      ]
    },
    "easing": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "scrollDirection": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `text`

```json
{
  "type": "object",
  "properties": {
    "content": {
      "type": "string"
    },
    "fontFamily": {
      "type": "string"
    },
    "foregroundColor": {
      "type": "string"
    },
    "outlineColor": {
      "type": "string"
    },
    "shadowColor": {
      "type": "string"
    },
    "glowColor": {
      "type": "string"
    },
    "horizontalAlignment": {
      "type": "string"
    },
    "verticalAlignment": {
      "type": "string"
    },
    "fontSize": {
      "type": "number"
    },
    "backgroundPaddingX": {
      "type": "number"
    },
    "backgroundPaddingY": {
      "type": "number"
    },
    "backgroundCornerRadius": {
      "type": "number"
    },
    "backgroundOpacity": {
      "type": "number"
    },
    "horizontalInset": {
      "type": "number"
    },
    "verticalInset": {
      "type": "number"
    },
    "outlineThickness": {
      "type": "number"
    },
    "shadowBlur": {
      "type": "number"
    },
    "shadowOpacity": {
      "type": "number"
    },
    "shadowOffsetX": {
      "type": "number"
    },
    "shadowOffsetY": {
      "type": "number"
    },
    "glowRadius": {
      "type": "number"
    },
    "glowOpacity": {
      "type": "number"
    },
    "letterSpacing": {
      "type": "number"
    },
    "lineHeight": {
      "type": "number"
    },
    "bold": {
      "type": "boolean"
    },
    "italic": {
      "type": "boolean"
    },
    "underline": {
      "type": "boolean"
    },
    "outlineEnabled": {
      "type": "boolean"
    },
    "shadowEnabled": {
      "type": "boolean"
    },
    "backgroundColor": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "motion": {
      "anyOf": [
        {
          "$ref": "#/$defs/motion"
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `point`

```json
{
  "type": "object",
  "properties": {
    "x": {
      "type": "number"
    },
    "y": {
      "type": "number"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `mask`

```json
{
  "type": "object",
  "properties": {
    "centerX": {
      "type": "number"
    },
    "centerY": {
      "type": "number"
    },
    "width": {
      "type": "number"
    },
    "height": {
      "type": "number"
    },
    "feather": {
      "type": "number"
    },
    "shape": {
      "type": "string",
      "enum": [
        "Rectangle",
        "Ellipse",
        "Path"
      ]
    },
    "invert": {
      "type": "boolean"
    },
    "points": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/point"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `shape`

```json
{
  "type": "object",
  "properties": {
    "kind": {
      "type": "string"
    },
    "widthFraction": {
      "type": "number"
    },
    "heightFraction": {
      "type": "number"
    },
    "fillColor": {
      "type": "string"
    },
    "strokeColor": {
      "type": "string"
    },
    "strokeFraction": {
      "type": "number"
    },
    "points": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/point"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `effect`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "presetId": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "isEnabled": {
      "type": "boolean"
    },
    "parameters": {
      "type": "object",
      "additionalProperties": true
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `transition`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "presetId": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "edge": {
      "type": "string",
      "enum": [
        "In",
        "Out"
      ]
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06
    },
    "parameters": {
      "type": "object",
      "additionalProperties": true
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `keyframe`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "property": {
      "type": "string"
    },
    "interpolation": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number"
    },
    "value": {
      "type": "number"
    },
    "bezierOutTime": {
      "type": "number"
    },
    "bezierOutValue": {
      "type": "number"
    },
    "bezierInTime": {
      "type": "number"
    },
    "bezierInValue": {
      "type": "number"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `remapPoint`

```json
{
  "type": "object",
  "properties": {
    "timeSeconds": {
      "type": "number"
    },
    "speed": {
      "type": "number"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `clip`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "blendMode": {
      "type": "string"
    },
    "frameInterpolation": {
      "type": "string"
    },
    "templateId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "mediaAssetId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "nestedSequenceId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "multicamGroupId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "multicamAngleId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "groupId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "linkGroupId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "color": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "detachedAudioClipId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "detachedVideoClipId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "multicamLocalStartSeconds": {
      "type": "number"
    },
    "startSeconds": {
      "type": "number"
    },
    "durationSeconds": {
      "type": "number"
    },
    "sourceInSeconds": {
      "type": "number"
    },
    "speed": {
      "type": "number"
    },
    "volume": {
      "type": "number"
    },
    "opacity": {
      "type": "number"
    },
    "speedAdjustmentApplied": {
      "type": "boolean"
    },
    "isMuted": {
      "type": "boolean"
    },
    "isReversed": {
      "type": "boolean"
    },
    "audioDetached": {
      "type": "boolean"
    },
    "sourceAudioStreamIndex": {
      "type": "integer",
      "minimum": 0,
      "maximum": 63
    },
    "stabilization": {
      "$ref": "#/$defs/stabilization"
    },
    "kind": {
      "type": "string",
      "enum": [
        "Video",
        "Audio",
        "Image",
        "Text",
        "Color",
        "Adjustment",
        "Sequence"
      ]
    },
    "mask": {
      "anyOf": [
        {
          "$ref": "#/$defs/mask"
        },
        {
          "type": "null"
        }
      ]
    },
    "transform": {
      "$ref": "#/$defs/transform"
    },
    "text": {
      "anyOf": [
        {
          "$ref": "#/$defs/text"
        },
        {
          "type": "null"
        }
      ]
    },
    "timeRemapPoints": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/remapPoint"
      }
    },
    "effects": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/effect"
      }
    },
    "transitions": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/transition"
      }
    },
    "keyframes": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/keyframe"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `track`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "isLocked": {
      "type": "boolean"
    },
    "isHidden": {
      "type": "boolean"
    },
    "isMuted": {
      "type": "boolean"
    },
    "isSyncLocked": {
      "type": "boolean"
    },
    "isSnapping": {
      "type": "boolean",
      "description": "轨道吸附属性；总吸附 snap 开启时，编辑提交后自动闭合空隙。"
    },
    "kind": {
      "type": "string",
      "enum": [
        "Video",
        "Audio"
      ]
    },
    "order": {
      "type": "integer",
      "minimum": 0
    },
    "clips": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/clip"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `marker`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "notes": {
      "type": "string"
    },
    "color": {
      "type": "string"
    },
    "timeSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `sequence`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "workingColorSpace": {
      "type": "string"
    },
    "outputColorSpace": {
      "type": "string"
    },
    "backgroundColor": {
      "type": "string"
    },
    "width": {
      "type": "integer",
      "minimum": 16,
      "maximum": 16384
    },
    "height": {
      "type": "integer",
      "minimum": 16,
      "maximum": 16384
    },
    "framesPerSecond": {
      "type": "number",
      "minimum": 0.1,
      "maximum": 240
    },
    "audioSampleRate": {
      "type": "integer",
      "minimum": 8000,
      "maximum": 192000
    },
    "magneticTimeline": {
      "type": "boolean",
      "deprecated": true,
      "description": "旧工程兼容字段，不再控制行为；使用轨道 isSnapping 与工作区 snap。"
    },
    "playheadSeconds": {
      "type": "number",
      "minimum": 0
    },
    "workAreaStartSeconds": {
      "type": "number",
      "minimum": 0
    },
    "workAreaEndSeconds": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `media`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "sourcePath": {
      "type": "string"
    },
    "labelColor": {
      "type": "string"
    },
    "description": {
      "type": "string"
    },
    "inputColorSpace": {
      "type": "string"
    },
    "binId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "proxyPath": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "videoCodec": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "audioCodec": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "fileModifiedUtc": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "kind": {
      "type": "string",
      "enum": [
        "Video",
        "Audio",
        "Image"
      ]
    },
    "durationSeconds": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    },
    "width": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 1
        },
        {
          "type": "null"
        }
      ]
    },
    "height": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 1
        },
        {
          "type": "null"
        }
      ]
    },
    "framesPerSecond": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    },
    "hasAudio": {
      "type": "boolean"
    },
    "audioStreams": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/sourceAudioStream"
      }
    },
    "tags": {
      "type": "array",
      "items": {
        "type": "string"
      }
    },
    "metadata": {
      "type": "object",
      "additionalProperties": {
        "type": "string"
      }
    },
    "sourceInSeconds": {
      "type": "number",
      "minimum": 0
    },
    "sourceOutSeconds": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    },
    "markers": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/marker"
      }
    },
    "useProxy": {
      "type": "boolean"
    },
    "audioChannels": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 1
        },
        {
          "type": "null"
        }
      ]
    },
    "audioSampleRate": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 1
        },
        {
          "type": "null"
        }
      ]
    },
    "fileSizeBytes": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `bin`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "parentId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `subclip`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "parentAssetId": {
      "type": "string"
    },
    "notes": {
      "type": "string"
    },
    "createdUtc": {
      "type": "string"
    },
    "inSeconds": {
      "type": "number",
      "minimum": 0
    },
    "outSeconds": {
      "type": "number",
      "minimum": 0
    },
    "tags": {
      "type": "array",
      "items": {
        "type": "string"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `angle`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "sourceClip": {
      "$ref": "#/$defs/clip"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `multicam`

```json
{
  "type": "object",
  "properties": {
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "syncMethod": {
      "type": "string"
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 0
    },
    "angles": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/angle"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `sequenceWorkspace`

```json
{
  "type": "object",
  "properties": {
    "sequence": {
      "$ref": "#/$defs/sequence"
    },
    "tracks": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/track"
      }
    },
    "markers": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/marker"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `project`

```json
{
  "type": "object",
  "properties": {
    "format": {
      "type": "string"
    },
    "id": {
      "type": "string"
    },
    "name": {
      "type": "string"
    },
    "createdUtc": {
      "type": "string"
    },
    "modifiedUtc": {
      "type": "string"
    },
    "version": {
      "type": "integer",
      "minimum": 1
    },
    "sequence": {
      "$ref": "#/$defs/sequence"
    },
    "sequences": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/sequenceWorkspace"
      }
    },
    "bins": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/bin"
      }
    },
    "media": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/media"
      }
    },
    "subclips": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/subclip"
      }
    },
    "multicamGroups": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/multicam"
      }
    },
    "tracks": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/track"
      }
    },
    "markers": {
      "type": "array",
      "items": {
        "$ref": "#/$defs/marker"
      }
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `exportSettings`

```json
{
  "type": "object",
  "properties": {
    "width": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 16,
          "maximum": 16384
        },
        {
          "type": "null"
        }
      ]
    },
    "height": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 16,
          "maximum": 16384
        },
        {
          "type": "null"
        }
      ]
    },
    "framesPerSecond": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0.1,
          "maximum": 240
        },
        {
          "type": "null"
        }
      ]
    },
    "videoBitrateKbps": {
      "anyOf": [
        {
          "type": "integer",
          "minimum": 1
        },
        {
          "type": "null"
        }
      ]
    },
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "endSeconds": {
      "anyOf": [
        {
          "type": "number",
          "minimum": 0
        },
        {
          "type": "null"
        }
      ]
    },
    "videoCodec": {
      "type": "string",
      "enum": [
        "H264",
        "HEVC",
        "AV1"
      ]
    },
    "bitDepth": {
      "type": "integer",
      "enum": [
        8,
        10
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `analysisSettings`

```json
{
  "type": "object",
  "properties": {
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "durationSeconds": {
      "type": "number",
      "minimum": 1e-06,
      "maximum": 600
    },
    "bins": {
      "type": "integer",
      "minimum": 8,
      "maximum": 2000
    },
    "threshold": {
      "type": "number",
      "minimum": 0.01,
      "maximum": 1
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `workspace`

```json
{
  "type": "object",
  "properties": {
    "upperRatio": {
      "type": "number",
      "minimum": 0.15,
      "maximum": 0.85
    },
    "libraryRatio": {
      "type": "number",
      "minimum": 0.15,
      "maximum": 0.85
    },
    "zoom": {
      "type": "number",
      "minimum": 0.25,
      "maximum": 4
    },
    "previewViewZoom": {
      "type": "number",
      "minimum": 0.1,
      "maximum": 4
    },
    "mediaViewIndex": {
      "type": "integer",
      "minimum": 0,
      "maximum": 2
    },
    "shortcuts": {
      "type": "object",
      "additionalProperties": {
        "anyOf": [
          {
            "type": "string"
          },
          {
            "type": "null"
          }
        ]
      }
    },
    "snap": {
      "type": "boolean"
    },
    "gpuMode": {
      "type": "string",
      "enum": [
        "自动（GPU 优先）",
        "CPU 软件渲染",
        "NVIDIA CUDA",
        "Intel QSV",
        "AMD AMF"
      ]
    },
    "autoSave": {
      "type": "boolean"
    },
    "previewQualityIndex": {
      "type": "integer",
      "minimum": 0,
      "maximum": 2
    },
    "categoryIndex": {
      "type": "integer",
      "minimum": 0,
      "maximum": 5
    },
    "globalMute": {
      "type": "boolean"
    },
    "soloTrackId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `sourceAudioStream`

```json
{
  "type": "object",
  "properties": {
    "index": {
      "type": "integer",
      "minimum": 0
    },
    "streamIndex": {
      "type": "integer",
      "minimum": 0
    },
    "codec": {
      "type": "string"
    },
    "channels": {
      "type": "integer",
      "minimum": 0
    },
    "sampleRate": {
      "type": "integer",
      "minimum": 0
    },
    "language": {
      "type": "string"
    },
    "title": {
      "type": "string"
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `stabilization`

```json
{
  "type": "object",
  "properties": {
    "enabled": {
      "type": "boolean"
    },
    "smoothing": {
      "type": "integer",
      "minimum": 1,
      "maximum": 120
    },
    "shakiness": {
      "type": "integer",
      "minimum": 1,
      "maximum": 10
    },
    "maxZoomPercent": {
      "type": "number",
      "minimum": 0,
      "maximum": 50
    },
    "originalAssetId": {
      "anyOf": [
        {
          "type": "string"
        },
        {
          "type": "null"
        }
      ]
    },
    "originalSourceIn": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `audio`

```json
{
  "type": "object",
  "properties": {
    "normalizeEnabled": {
      "type": "boolean"
    },
    "targetLufs": {
      "type": "number",
      "minimum": -36,
      "maximum": -5
    },
    "targetTruePeakDb": {
      "type": "number",
      "minimum": -9,
      "maximum": 0
    },
    "measuredLufs": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "measuredTruePeakDb": {
      "anyOf": [
        {
          "type": "number"
        },
        {
          "type": "null"
        }
      ]
    },
    "normalizationGainDb": {
      "type": "number",
      "minimum": -60,
      "maximum": 60
    },
    "category": {
      "type": "string",
      "enum": [
        "未分类",
        "对白",
        "音乐",
        "音效",
        "环境声"
      ]
    },
    "gain": {
      "type": "number",
      "minimum": 0,
      "maximum": 4
    },
    "pan": {
      "type": "number",
      "minimum": -1,
      "maximum": 1
    },
    "channelMode": {
      "type": "string",
      "enum": [
        "立体声",
        "单声道",
        "仅左声道",
        "仅右声道"
      ]
    },
    "highPassHz": {
      "type": "number",
      "minimum": 0,
      "maximum": 20000
    },
    "lowPassHz": {
      "type": "number",
      "minimum": 0,
      "maximum": 20000
    },
    "equalizerHz": {
      "type": "number",
      "minimum": 0,
      "maximum": 20000
    },
    "equalizerGainDb": {
      "type": "number",
      "minimum": -24,
      "maximum": 24
    },
    "compressorEnabled": {
      "type": "boolean"
    },
    "compressorThresholdDb": {
      "type": "number",
      "minimum": -60,
      "maximum": 0
    },
    "compressorRatio": {
      "type": "number",
      "minimum": 1,
      "maximum": 20
    },
    "limiterEnabled": {
      "type": "boolean"
    },
    "limiterCeiling": {
      "type": "number",
      "minimum": 0.05,
      "maximum": 1
    },
    "noiseReductionEnabled": {
      "type": "boolean"
    },
    "noiseReductionDb": {
      "type": "number",
      "minimum": 0,
      "maximum": 40
    },
    "echoEnabled": {
      "type": "boolean"
    },
    "echoDelayMs": {
      "type": "number",
      "minimum": 1,
      "maximum": 2000
    },
    "echoDecay": {
      "type": "number",
      "minimum": 0,
      "maximum": 0.9
    },
    "autoDuckingEnabled": {
      "type": "boolean"
    },
    "duckingGain": {
      "type": "number",
      "minimum": 0.05,
      "maximum": 1
    },
    "duckingFadeSeconds": {
      "type": "number",
      "minimum": 0,
      "maximum": 2
    }
  },
  "required": [],
  "additionalProperties": false
}
```

#### `timeRange`

```json
{
  "type": "object",
  "properties": {
    "startSeconds": {
      "type": "number",
      "minimum": 0
    },
    "endSeconds": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": [
    "startSeconds",
    "endSeconds"
  ],
  "additionalProperties": false
}
```

## 视图与预览缓存（新增）

- `workspace_set` 的 `previewViewZoom`：0.1–4，只改变预览视图，不改变素材变换或导出；回位为 1。`mediaViewIndex`：0 列表、1 小缩略图、2 大缩略图。
- 三分区可拖动分隔条；MCP 用 `upperRatio`、`libraryRatio` 设置比例。效果分类统一包含滤镜与特效，目录预设 ID 保持不变；分类索引为媒体 0、文字 1、效果 2、转场 3、贴纸 4。
- 停止编辑约 2 秒后自动在本机生成整个工程的分段预览并拼接；编辑后按内容指纹复用未受影响段。缓存仅用于正向播放，正式导出仍使用项目原始数据；不要把预览缓存作为交付视频。取消或缓存失败不影响保存、普通预览或导出。
- 预览缩放与缩略图视图状态可经 `workspace_get` 读取，使用已有 `workspace_set` 操作即可，不需要启动任何新服务。


## Concat 对照新增工作流

- 源文件音轨：先 `audio_source_streams({clipId})` 获取实际流列表。`index` 是 FFmpeg 音频序号，`streamIndex` 是容器全局索引，不能混用。通过 `clip_update({clipId,patch:{sourceAudioStreamIndex:index}})` 切换，波形、监听和导出自动刷新。
- 分离 / 附着：`audio_detach({clipId})` 从视频建立独立声音对象，原视频不重复发声。`audio_attach({clipId})` 可以传原视频或分离声音 ID；分别修剪后若音画不对齐会明确报错，不会丢弃用户改动。
- 归一化：`audio_normalize({clipId,targetLufs:-14,truePeakDb:-1})` 真实测量所选流及片段区间，并保存音频参数；真峰值限制优先，可能无法完全达到目标 LUFS。支持速度曲线。使用音频参数更新的 normalizeEnabled=false 关闭；不要拿旧的简化响度分析替代真实归一化。
- 防抖：`video_stabilize({clipId,enabled:true,smoothing:30,shakiness:5,maxZoomPercent:10})` 两次真实运动处理，建立可打包的无损派生媒体。`enabled:false` 恢复原媒体。期间应等待工具返回，勿并发修改项目使分析结果失效；源文件不改写。
- 电平：`audio_meter_get({})` 读取真实监听 PCM 的声道峰值、削波保持及播放状态。它不是 LUFS 计量器。
- 轨道吸附：`track_update({trackId,patch:{isSnapping:true}})` 设置该轨道的吸附属性；`workspace_set({patch:{snap:true}})` 为总开关。两者开启后，移动、修剪、删除和新素材加入后的编辑提交自动闭合空隙。锁定、专用字幕、跨轨关联及多机位对象保留位置；重叠片段作为整体移动，保持转场关系。需要按绝对时间布置并保留空隙时，先关闭目标轨道的 isSnapping，或关闭总吸附。操作参数 snap:false 只关闭该次操作的边界吸附，不会取消开启中的轨道自动闭合。旧 sequence.magneticTimeline 仅兼容旧工程，不再控制独立模式；不要再用它开关吸附。
- 编码：`export_start` 的 settings 新增 videoCodec（H264、HEVC、AV1）和 bitDepth（8 或 10）；H264 当前只提供8位。10位为SDR高精度管线，不代表HDR；遇到仅支持8位的效果会明确拒绝该10位组合，可选8位或关闭对应效果后重试。
- 转场：有源余量时前镜头持续解码，与后镜头一起运动。转场区间在切点后，保持时间线时长；没有余量、未知源时长或前段使用速度曲线时回退尾帧保持，参数页明确提示。不要向用户把回退描述为连续运动双源转场。
- 快捷键：workspace_get 返回可重绑命令列表；workspace_set 的 shortcuts 支持逐帧、波纹、吸附和播放动作。“切换磁性模式”已从可选项删除；旧工作区的该名称只作为“切换吸附”的兼容别名。支持单键；文本输入时不执行剪辑命令，同一自定义手势不能重复分配。

仍使用已连接的 MCP 工具与现有 .NET 服务。新增功能不需要编写 Node 桥接、启动HTTP服务器或操作用户界面。

## StageCode 与过期操作

- `error.stageCode` 与后台任务的 `stageCode` 固定使用五个 ASCII 大写字母加四个数字。完整登记位于程序资源 `Resources/StageList.txt`，开发说明位于 `dev_doc1/StageCodeAndStateOwnership.md`。保留原始代码用于诊断，不将界面文字或枚举名称当成阶段码。
- `MCPRT0003` 表示工程版本已变化；先重新读取工程状态、对象 ID 与 revision，再决定是否重试。示例任务被新的选择、工程、序列或会话替代后，不应强行继续播放旧结果。
- `catalog_preview` 返回后台任务，必须通过 `jobs_status` 等待 `completed` 并检查错误；示例播放器打开或解码失败也不表示示例已成功播放。显式切换选择或工程会终止对应旧预览。
- 修改代码时，异步 UI 回写需要核对项目/序列、请求世代、对象引用及来源；不要仅凭相同文件名或对象 ID 接受旧结果。遵守用户当前的检查范围；用户要求不要测试时，不运行测试或启动程序；按项目 AGENTS.md 的明确要求，每轮代码修改后构建 Debug 并使用默认输出目录。

## 本地语音生成与专用轨道

继续使用已连接的 MCP 桥接和打开的编辑器实例。先发现实例与工程，再锁定目标实例；所有修改调用受锁定保护，结束任务后解锁。不要创建 Node/Python 桥接、HTTP 服务或自动操作用户界面。

语音工具使用本地 Qwen3-TTS ONNX。所有模型在根目录 `voice_models`（开发版为仓库根目录，安装版为主程序目录）。克隆音色存储于主程序 exe 同级的 `data/voices`，生成后的原始/处理音频进入工程媒体缓存并随 .tvep 打包。已生成工程预览和导出不需要模型或网络。

### 设备、模型与音色

可通过 `model_get_schema({model:"speech"})` 查询完整轨道数据；`speechSentence`、`speechParameters`、`speechVoice` 查询各自类型。命令参数使用公开可写子集，生成时长、媒体 ID 与指纹由软件维护。

| 命令 | 参数与行为 |
| --- | --- |
| `speech_devices` | 查询 `auto`、`cpu` 和真实 DXGI `EnumAdapters1` 枚举的 `dml:索引`；不要猜测显卡索引。列表优先排列 Intel、NVIDIA、AMD 物理设备，保留原始索引；自动模式取第一张显卡。返回专用与共享显存容量，二者参与完整 GPU 图的容量估算；不足时使用 GPU 编码预测器 + CPU talker/声码器。完整图失败或超时可先重试混合 GPU；仅自动模式可继续回退 CPU，指定显卡保持选择并返回失败。枚举可见不代表实际推理已验证。 |
| `speech_models` | 查询模型 ID、安装状态、路径以及根目录。 |
| `speech_model_download` | `modelId` 为 `custom-0.6b`、`base-0.6b`、`custom-1.7b`；显式下载并校验固定版本，返回任务。生成时不会自动下载。 |
| `speech_model_import` | `{modelId,path}`；将完整兼容 ONNX 模型复制到 voice_models，不覆盖正在使用的旧目录。 |
| `speech_voices` | 查询预设及克隆音色、语言。九种预设 ID 为 vivian、serena、uncle_fu、dylan、eric、ryan、aiden、ono_anna、sohee。 |
| `speech_clone_prompt` | `{language:"zh"}` 或 `"en"`，返回固定朗读示例 text、presetId、editable:false、instructions、coverage 与 readAloudField:"text"。当前版本 phonetic-zh-v3 / phonetic-en-v3，将发音覆盖自然编入词句。录音和导入音频都必须原样完整朗读 text，不要朗读 coverage、录音指引或另加拼音音节；不能替换为 AI 自编文案。另返回 importedAudioRequiresPreset:true、confirmationField:"confirmedPreset"。 |
| `speech_voice_create` | `{path,name,gender,language,presetId,confirmedPreset:true,referenceText?,startSeconds?,endSeconds?,outputPath?}`；先读取 `speech_clone_prompt`，确认导入的音频完整朗读该固定文案，再传回对应 presetId 并确认 confirmedPreset:true。gender 必须为 male 或 female；referenceText 可省略，如提供必须与固定 text 完全一致，不能留空或自编；参考音频选取 3～120 秒并保留完整文案，建立本地音色，outputPath 可输出 `.tvevcv`。支持 FFmpeg 可读取的常见音频/视频，自动转为 24 kHz 单声道参考音频；创建和导入拒绝同名，历史 `.tvevcv` 仍保留原始文本。 |
| `speech_voice_import` | `{path}`，导入 `.tvevcv` 克隆音色。 |
| `speech_voice_export` | `{voiceId,path}`，导出用户音色为 `.tvevcv`。 |
| `speech_voice_delete` | `{voiceId}`，删除克隆音色，不能删除内置音色；保留工程中已经生成的音频。 |
| `speech_voice_preview` | `{voiceId,text,device?}`，临时合成试听文字，不修改工程；返回 jobs，等待播放器打开后完成；可由 `jobs_cancel` 或 `speech_preview_stop` 取消。 |
| `speech_clone_record_start` | `{deviceId,gender,language}`；gender 必须为 male 或 female；设备 ID 使用 `voiceover_devices` 的录音设备查询结果，开始录制并返回该语言的固定示例。text 不可自定义，录音全文与示例绑定。 |
| `speech_clone_record_stop` | `{name,path}`；停止录音、创建音色并输出 `.tvevcv`，返回任务。使用开始录音时冻结的示例全文，不提供转写或裁剪覆盖参数；未完整朗读应取消后重录。 |
| `speech_clone_record_cancel` | 取消当前克隆录音。 |

克隆文件是版本化清单与参考音频的便携音色包，不是任意 .pth/.pt 模型权重。预设音色使用 CustomVoice，可选 0.6B/1.7B；录音克隆使用 0.6B Base。无 speech tokenizer 的 Base 使用说话人嵌入，带该组件的兼容模型可使用录音文字进行 ICL。

### 创建、进度与试听

`speech_generate` 的 `text` 与 `sentences` 必须且只能提供一个。默认参数为 `voiceId:"vivian"`、`language:"auto"`、`rate:1`、`pitch:0`、`gap:0.2`；语言使用 `auto/zh/en/ja/ko/de/fr/ru/pt/es/it`，语速 0.5～2 倍、音调 ±12 半音，音调与语速独立。默认从当前播放头开始；可提供 `startSeconds`、`device` 和 `modelSize:"0.6b"|"1.7b"`。

可选 `captions:{"wordByWord":false}`：直接使用语音轨已保存的分句文字和音频实际时长，统一放在随语音移动的专用字幕轨道；不调用语音识别或对齐模型。不提供 `captions` 则不生成字幕。逐字输出可改为 `captions:{"language":"zh","alignmentModel":"本地对齐模型路径","wordByWord":true,"device":"cpu"}`，仅对已知原文做强制对齐；`language:"auto"` 根据句子语言/原文选本地对齐模型，无须识别模型。`speech_track_update` 可以新增或修改字幕，`captions:null` 删除关联字幕轨；返回结果包含 `captionTrackId`。字幕样式及逐字时间可在字幕轨道中继续调整。

```json
{
  "text": "你好，欢迎来到 Tangerine。[s1000]Hello, welcome back!",
  "startSeconds": 0,
  "device": "auto",
  "modelSize": "0.6b",
  "defaults": {"voiceId":"vivian","language":"auto","rate":1,"pitch":0,"gap":0.2}
}
```

长操作返回现有 jobs 任务；使用 `jobs_status({jobId})` 等待 completed，检查 failed/cancelled 及 stageCode。`jobs_cancel({jobId})` 取消。界面显示准备、每句推理和收尾进度；排队不代表卡死，不要因此重复提交相同请求。任务执行时不因一分钟空闲而交还操作权；不要在工作尚未结束时解锁。取消、生成失败或位置冲突均保留原有效轨道，不留下半条结果。按逻辑处理器数安排并发：worker 上限为 `clamp(ProcessorCount / 4, 1, 4)`，0.6B 最多使用该数量，1.7B 最多两句；不再设置 18 GiB 空闲内存门槛。每进程共用 ORT 线程池并分配 CPU 线程预算，单句重生成可获得完整预算；不同显卡的独立会话可并行，同一张 DirectML 显卡仍串行。设备查询独立；成功后的空闲会话两分钟回收。

协议读取、错误管道排空和执行超时监控使用独立线程。每张图准确报告 CPU/GPU 加载、预填充、预测、解码的开始与完成，重复消息不能掩盖卡死。加载四分钟、预填充三分钟、预测/解码两分钟无进展时结束对应工作进程；音频转换五分钟总期限。完整 GPU 图无进展先重试混合模式；只有 auto 可在混合 GPU 失败后改用 CPU。CPU 阶段故障不归因于 GPU。混合/CPU 偏好按设备和模型保留，后续句子及回收后新进程不重复尝试已失败路径。并行句子最终仍按原句顺序排布，提交前统一检查工程代次及位置约束。

排查停滞可只读 `exe 同级的 data/Logs/speech-worker.log` 和 `.previous`，对比 PID、stage、noProgress、累计 cpu 秒及 private 字节；日志不含生成文本或录音。不要凭总占用率断定完成或失败，也不要自动改为网络服务。用户要求离线时不下载依赖；代码修改后使用 `dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj --configuration Debug` 构建到默认目录。2026-10-26 的 SIMD、按需投影、原生 KV 复用和独立监控修改仅通过静态审查与编译，不能声称模型质量、驱动稳定性或速度已实测。

工作进程每 15 秒采样一次 CPU 累计时间：增量达到 0.05 秒时记录 `running-cpu-progress` 并重置无进展计时；低于该值时记录 `running-no-cpu-progress`，计时继续累计。前者说明该进程仍在消耗 CPU，不代表 GPU 利用率或音频阶段已完成。声码器开始和完成事件带耗时与帧数；FFmpeg 变速/移调用 `-progress` 更新音频处理百分比，并在同一日志中记录 `audio-convert-start`、每 15 秒的 `audio-convert-running`、`audio-convert-complete`、`audio-convert-failure` 或 `audio-convert-timeout`，字段包括 PID、已处理音频秒数、CPU 增量、私有内存和输出字节数。排查接近完成时卡住的问题时，要结合 `stage`、`noProgress`、累计 `cpu`、`cpuDelta`、`private`、`outSeconds` 和 `outputBytes` 判断。当前日志仍不能证明之前观察到的末段停滞根因。

GPU 图失败会另外记录 `graph-diagnostic`，保留故障图名称、异常类型与经脱敏的原生错误详情；`GPU_FULL_TO_HYBRID` 表示完整语言模型失败但仍尝试显卡编码预测器，`GPU_HYBRID_TO_CPU` 才表示自动模式转用 CPU。不要把前者解释为驱动未安装或整张显卡不可用。截图 VOICE0007 若包含旧 `.wav.part-<GUID>.wav` 超长路径，是已修复的转换暂存命名回归，需使用短暂存文件的新构建。Debug 产物不再包含 `dev_doc1`，开发文档在源码根目录阅读。

`speech_tracks` 返回专用轨道 ID、设置与句子稳定 ID。`speech_preview({trackId,sentenceId})` 试听生成句子，`speech_preview_stop` 停止并恢复工程预览；`editor_get_state.preview.mode` 为 `speech`。语音、内置音乐、示例共用独占播放所有权，新的试听会撤销旧请求，不需要操作播放按钮。

### 编辑与位置约束

| 命令 | 参数与行为 |
| --- | --- |
| `speech_track_update` | `{trackId,defaults?,captions?,device?,modelSize?,startSeconds?}`；defaults 为局部补丁，仅重新生成受影响的句子；captions 为完整字幕设置或 null。 |
| `speech_sentence_update` | `{trackId,sentenceId,patch}`；可修改 text、voiceId、language、rate、pitch、gap、parts、anchor、start、end，不能伪造生成时长或媒体 ID。 |
| `speech_sentence_regenerate` | `{trackId,sentenceId}`，强制重新生成本句。 |
| `speech_sentence_reorder` | `{trackId,sentenceIds}`，数组必须包含该轨道全部句子且不重复。 |
| `speech_sentence_remove` | `{trackId,sentenceId}`，删除一句并重排；最后一句请删除整条轨道。 |
| `speech_track_remove` | `{trackId}`，删除整个语音轨道，可撤销。 |

`anchor` 为 `Auto`（按上一句及 gap 顺排）、`Start`（固定 start）、`End`（固定 end，由实际时长反推起点）、`Window`（固定 start/end，由实际音频时长自动求语速）。Window 模式不手填 rate；求得语速超出 0.5～2 倍、出现负起点或撞上固定句时报 VOICE0004，旧结果不被覆盖。修改文字或语速后，后续 Auto 句重排，固定句不移动。生成时长来自真实处理后的 PCM，不得只改 durationSeconds。

```json
{
  "trackId":"读取到的语音轨道ID",
  "sentenceId":"读取到的句子ID",
  "patch":{"anchor":"Window","start":3,"end":7,"pitch":2}
}
```

句内混合语言默认交给模型处理，也可在 `parts` 提供顺序片段，例如 `[{"text":"你好，","language":"zh"},{"text":"welcome back","language":"en"}]`；所有片段拼接必须等于句子 text。后续修改文字而不提供 parts 时清除旧语言片段，以免标注指向旧文字。可空覆盖字段设为 null 表示恢复轨道默认值。

专用语音轨道默认关闭吸附并保留停顿；不接收外部素材，禁止普通分割、修剪、跨轨移动。使用专用命令修改内容；`track_update` 仅用于命名、锁定、静音等普通轨道属性，先解锁再修改。通用 `project_patch` 不能删除、替换或伪造语音轨道。保存工程及撤销重做保留原始和处理后的音频媒体引用。

### 一次性文字标记

完整说明见根目录 `voice_make.md`。`speech_split({text,gapSeconds?})` 可预览自动分句计划；它不播放、不生成文件。

- `[s2000]` 在轨道留下两秒空隙；句中停顿把该句拆成同组音频对象，停顿不是编码进音频的静音。
- `[sps200]你好[spe200]` 以 200% 初始语速生成包围文字；50～200%，开始结束数值必须一致、成对且不嵌套。标记只在新建任务首次生成时执行一次，成功后消耗到原始音频基线，编辑和单句重生成不重新解释原标记。
- `speech_split` 返回包含 ID 与生成计划的内部对象。若将它用于 `speech_generate.sentences`，只传递公开句子字段（text、parts、gap、anchor、start、end 及参数覆盖），不要直接回传内部 ID/媒体/指纹字段。parts.initialRate 仅用于首次生成：原分句结果 hasInitialRate 为 false 时省略 initialRate，以继承句子/全局语速；为 true 时才传递，显式 initialRate:1 表示首次按 100% 生成。后续 sentence_update 使用 rate，parts 中不要设置 initialRate。

错误代码：VOICE0001 参数/标记；VOICE0002 设备/工作进程；VOICE0003 推理；VOICE0004 位置/专用轨道；VOICE0005 模型；VOICE0006 录音/音色包；VOICE0007 音频处理；VOICE0008 文件/UI；VOICE0009 过期生成请求。先检查任务状态与当前轨道版本，再决定是否修改参数重试。
