# TANGERINE-VideoEditor AI 操作手册

适用版本：v1.0.0。本文面向通过 MCP 操作已打开编辑器的 AI，也说明源码测试与故障排查方法。用户当前指令优先于本文中的默认工作习惯。协议和参数以目标构建实际返回的 `tools/list`、`model_get_schema`、`catalog_schema` 为准；不要臆造工具名、参数名或预设 ID。

## 1. 选择正确操作方式

- 操作用户已经打开的应用：使用本软件 MCP，通过实际进程更新内存模型和实时界面。
- 修改或测试软件：使用源码和命令行，在独立输出目录构建；不要覆盖仍在运行的程序文件或擅自结束用户进程。
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

仅有 DLL 时可使用 `command: "dotnet"`，把 MCP DLL 的绝对路径作为唯一启动参数。服务使用标准输入输出 JSON-RPC，标准错误用于日志，不配置 HTTP 端口。客户端应使用标准 MCP SDK 完成初始化及工具调用；不要把日志写到协议标准输出。

1. 调用 `list_instances({})`，读取 PID、项目名称、路径、启动时间及实例身份。
2. 根据工程名称与路径确认目标，可直接调用 `editor_control_acquire({"processId": 实际PID, "ownerLabel": "任务目的"})`；后续每条调用携带相同 `processId`。无需先调用 `select_instance`。也可以使用 `select_instance({"processId": 实际PID})` 设置本会话默认目标，然后省略后续 PID。
3. 多进程时必须明确选择，不猜测前台窗口。只有一个进程时桥接服务可以自动选择，但仍建议核对。
4. 每个编辑器工具也可携带 `processId` 临时指定目标；不会改变本会话的默认选择。实例退出或身份改变时重新发现，不把旧 PID 当作永久身份。

本机发现注册位于 `%LOCALAPPDATA%\TANGERINE-VideoEditor\mcp\instances`，使用当前 Windows 用户专用命名管道。不要把内部认证令牌、管道地址写入对用户的回复，也不要手工伪造注册文件。

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

编码失败诊断目录为 `%LOCALAPPDATA%\TANGERINE-VideoEditor\diagnostics\export`，记录参数、滤镜图与错误。`0xC0000005` 是编码器原生内存访问冲突；日志空白不能理解为成功。参见 [导出修复记录](dev_doc1/Export%20Reliability.md)。

本地 IPC 单条请求/响应上限 16 MiB，连接超时 5 秒，单命令传输上限 2 分钟。耗时操作应使用后台任务，而不是长时间占用同步请求。遇到响应大小问题使用轻量状态或按需查询，不反复发送同一个超大项目。

## 9. 源码验证与交付

遵循仓库 `rules.md`：中文界面和开发文档、解释性英文注释、比例布局、stagecode 异常。保留未提交修改，除非用户明确要求，不重置仓库、不自动提交、不替换或关闭用户正打开的应用。

用户明确要求更新 Debug 且 Debug 程序已关闭时，使用 `dotnet build TANGERINE-VideoEditor.slnx -c Debug` 编译到默认 `TANGERINE-VideoEditor/bin/Debug/net10.0-windows`，不要只交付独立审计目录或 Release。Debug 的 MCP 程序位于该目录的 `mcp/TANGERINE-VideoEditor.Mcp.exe`，操作手册为同目录 `toAI.md`。

```powershell
dotnet build TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false
dotnet run --project tests/TrackRenameCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false
dotnet run --project tests/McpControlCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false
dotnet run --project tests/RenderSmokeCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false
dotnet run --project tests/ExportCoverageCheck -p:OutputPath=bin/ExportAudit/ -p:UseAppHost=false -p:BuildProjectReferences=false -- --features artifacts/export-audit/features
dotnet publish TANGERINE-VideoEditor/TANGERINE-VideoEditor.csproj -c Release -o artifacts/verified-editor
```

先构建主项目，再使用同名输出目录运行检查。不要并行构建共享 WPF obj，也不要在某检查 DLL 运行期间覆盖该 DLL；需要并行时使用独立输出目录并正常构建引用。只运行与修改有关的必要检查，失败后查原因，不通过延长时限、删除断言或伪造输出掩盖缺陷。

相关检查：AutomationTransportCheck（标准 MCP/IPC）、McpLiveCheck（实时窗口）、McpControlCheck（接管与用户浏览）、McpFeaturesCheck（高级功能与真实媒体）、RenderSmokeCheck（预览与导出）、TransitionVisualCheck（转场图像与导出一致性）、ExportCoverageCheck（功能预览/导出/解码）、TrackRenameCheck（轨道命名与导航）。未验证真实硬件、麦克风或外部模型时明确说明，不声称全部环境通过。

交付报告说明修复内容、实际测试结果、输出位置和仍需重启新版本的事实。不要因源码构建成功就声称旧进程已更新，也不要在任务失败时声称功能全部可用。

## 10. 完整命令与输入模型附录

以下命令和模型从本版本 `AutomationCatalog` 生成，避免手抄遗漏。所有编辑器命令额外接受可选的 `processId`（正整数）和 `expectedRevision`（非负整数），这两个桥接参数不再在每条命令下重复列出。各块内 `required` 列出必填字段，其余字段可省略；可省略不等于可传 null。`$ref` 查最后的集中模型定义；运行时 `tools/list` 会提供对应 `$defs`。

桥接工具 `list_instances` 无参数；`select_instance` 必填 `processId`，正整数。它们不要求持有应用控制权。

<!-- GENERATED MCP CATALOG -->

本版本共 110 个 MCP 工具（含 2 个桥接工具），26 个集中输入模型。

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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
}
```

### `sequence_remove`

从项目删除指定序列。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
}
```

### `media_remove`

从项目移除素材；removeClips 控制是否同时移除引用片段，不删除源文件。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
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
  "additionalProperties": false
}
```

### `bin_remove`

删除项目素材箱，保留源文件。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
}
```

### `subclip_remove`

删除可复用子片段定义。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
}
```

### `track_remove`

删除轨道及其片段，可撤销。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
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
  "additionalProperties": false
}
```

### `clip_remove`

删除一个或多个片段，可选波纹删除。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
      "minimum": 1E-06
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
      "minimum": 1E-06
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
      "minimum": 1E-06
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
  "additionalProperties": false
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
  "additionalProperties": false
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
  "additionalProperties": false
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
  "additionalProperties": false
}
```

### `effect_remove`

移除一个效果。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
      "minimum": 1E-06
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
  "additionalProperties": false
}
```

### `transition_remove`

移除指定转场。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
}
```

### `keyframe_remove`

删除关键帧。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
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
  "additionalProperties": false
}
```

### `marker_remove`

删除序列标记。

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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

列出内置标题、特效、转场及其稳定预设 ID。

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
  "additionalProperties": false
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
      "minimum": 1E-06
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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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
  "additionalProperties": false
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
  "additionalProperties": false
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

元数据：可改变状态或生成输出；涉及删除、覆盖或完整补丁。只读工具同样需要控制权（控制状态查询除外）。

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

这些字段是实际可序列化模型或工具输入字段。这里只定义类型、选项和声明范围，业务逻辑还会校验引用、时间关系、文件和预设参数。修改模型时遵循补丁规则，不将所有可选字段强行补零。

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
      "minimum": 1E-06,
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

#### `audio`

```json
{
  "type": "object",
  "properties": {
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
      "type": "boolean"
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
      "minimum": 1E-06
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

