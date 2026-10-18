# MCP 本地通信

`TANGERINE-VideoEditor.Mcp` 是独立的标准输入/输出 MCP 服务，使用官方 C# SDK `ModelContextProtocol` 2.2.0。它不重新创建项目副本，而是把命令发给用户已经打开的编辑器实例。编辑器通过 `TANGERINE-VideoEditor.Automation` 提供的本地命名管道接收命令。

## 进程选择

先调用 `list_instances`，可看到每个实例的 PID、启动时间、项目名称、项目路径、项目修订号和实例标识。调用 `select_instance(processId)` 设置当前 MCP 会话的目标；也可以在每条编辑器命令中提供 `processId`。单次指定 PID 不改变会话选择。

实例启动后自动注册，无需用户打开连接对话框或切换前台。AI 仅通过 MCP 即可读取所有实例及各自的工程；未保存工程路径可为空。确认后直接调用 `editor_control_acquire({processId, ownerLabel})` 锁定目标，后续调用及释放均携带该 PID，完全不要求先 `select_instance` 或操作图形界面。MCP 初始化说明及 `list_instances` 的工具描述明确公开该流程。

只有一个实例时可自动使用该实例。多个实例时必须明确选择，不能猜测当前窗口。选定的进程退出后，命令返回错误，不会自动转到其他窗口；同一个 PID 被系统复用时，实例标识检查也会拒绝旧选择。

## 独占控制会话

选择进程后，AI 必须显式调用 `editor_control_acquire`，完成后调用 `editor_control_release`。除 `editor_control_acquire`、`editor_control_release`、`editor_control_status` 三个控制命令以外，所有编辑器命令（包括状态查询）都要求当前 MCP 会话已获得控制权。桥接服务不会自动获取控制权。

每个 stdio 连接生成独立、稳定的随机会话标识，并在内部 IPC 请求中自动附带；该字段不作为 MCP 工具参数公开，调用工具时不能覆盖其他连接的标识。选择另一个进程或在命令中指定 `processId` 不会改变当前连接的会话标识。不同 AI 连接不能相互释放控制权。

应用在空闲 60 秒后自动释放控制模式；正在执行的命令及后台作业不计入空闲。AI 应主动释放，不必等待超时。桥接服务正常执行托管关闭时，会尽力在 2 秒内释放这个连接曾获取的所有进程；客户端强制结束服务进程时依靠应用侧的空闲超时回收。

所有编辑器工具来自共享 `AutomationCatalog`，MCP 的 `tools/list` 同时公开其参数 JSON Schema。各命令的可选 `expectedRevision` 提供并发修改保护，避免 AI 根据过期的时间线覆盖用户的新修改。应用侧收到命令后在 WPF Dispatcher 上操作并完成相关刷新，再返回结果。

## 配置

MCP 客户端配置示例；`args` 中的路径应替换成实际构建或发布目录中的文件位置：

```json
{
  "mcpServers": {
    "tangerine-video-editor": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\mcp\\TANGERINE-VideoEditor.Mcp.dll"]
    }
  }
}
```

运行服务前打开编辑器。服务不强制打开、关闭或切换用户窗口。标准输出只传输 MCP JSON-RPC；诊断日志写入标准错误，以免损坏协议。

## 本地发现与生命周期

实例注册位于 `%LOCALAPPDATA%\TANGERINE-VideoEditor\mcp\instances`。每个窗口进程生成随机实例标识、随机管道名称及认证令牌，每秒原子更新注册信息。发现时同时验证 PID 和进程启动时间，并清理已经退出的进程记录。

命名管道使用 `PipeOptions.CurrentUserOnly`，仅供本机当前 Windows 用户使用，不开放 TCP 端口。认证令牌只在本地注册和管道握手中使用，`list_instances` 不公开令牌或管道名称。

每个连接只承载一条命令。UTF-8 JSON 使用换行界定，单条请求或响应最多 16 MiB。连接超时为 5 秒，一条命令的传输上限为 2 分钟；耗时导出应通过应用侧作业接口启动并查询进度。客户端取消或连接断开会传递取消信号给应用处理器，避免被放弃的请求继续占用交互队列。

## 验证

```powershell
dotnet run --project tests\AutomationTransportCheck\AutomationTransportCheck.csproj
```

测试会启动两个临时子进程，不操作用户项目；覆盖实际跨进程发现、命名管道认证、中文路径、修订冲突、取消传播、并发请求、正常退出与崩溃清理，以及官方 MCP SDK 客户端的 stdio 握手、完整工具列表和多实例路由。

附加 `-- --mcp "完整发布目录\mcp\TANGERINE-VideoEditor.Mcp.exe"` 可以针对实际随程序发布的 MCP 服务运行相同互操作验证。

本地通信阶段代码为 `MCPIP0001` 至 `MCPIP0009`，分别对应读取、消息大小、JSON 消息、认证、取消、命令处理、进程发现、实例身份和连接。MCP 桥接阶段代码为 `MCPBR0001` 至 `MCPBR0003`，分别对应进程选择、工具名称和参数处理。工具错误同时返回稳定的英文 `code` 键，方便客户端按错误类别重试；面向用户的说明和工具描述均为简体中文。
