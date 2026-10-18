# Version and Project File Associations

## 版本显示

参照 `TANGERINE-PhotoViewer`，应用版本由项目文件 `TANGERINE-VideoEditor.csproj` 的 `<Version>1.0.0</Version>` 统一维护。`AppVersion` 从本程序集版本读取前三段，显示为 `v1.0.0`，避免测试宿主或外部启动器影响版本来源。

主窗口标题、底部状态栏和“帮助 → 关于 TANGERINE”使用同一版本。关于窗口采用可选择、复制的只读文本，与 PhotoViewer 的版本展示方式一致；窗口宽高根据工作区比例计算。

## .tvep 打开方式

参照 `TANGERINE-ZIP` 的默认应用注册、核验和取消注册服务，新增“为系统做设置 → 默认打开方式设置”。仅支持 `.tvep` 项目，窗口提供以下操作：

- 使用本应用：注册处理程序，尝试设置当前用户默认打开方式，再查询 Windows Shell 的实际结果。
- 仅注册打开方式：将本程序加入可选打开程序，保留当前默认选择。
- 打开 Windows 默认应用设置：注册本程序后跳转 Windows 设置，由用户选择打开方式。
- 取消设置并删除注册：先清除属于本程序的显式选择，再撤销处理程序与应用注册。
- 刷新状态：查询实际默认程序，返回窗口时也自动刷新。

所有注册操作在后台任务执行，运行期间禁止关闭窗口，确保写入完成或回滚。启动软件本身不会注册或修改默认应用。

## 注册结构

全部写入当前用户 `HKCU`，不申请管理员权限，不修改媒体文件格式。

| 位置 | 内容 |
| --- | --- |
| `Software/Classes/TangerineVideoEditor.Project.tvep` | 项目类型说明、默认图标、打开命令 |
| `Software/Classes/.tvep/OpenWithProgids` | 本程序 ProgID 候选 |
| `Software/TangerineVideoEditor/DefaultApps/Capabilities` | 应用名称、描述、图标和 `.tvep` 能力声明 |
| `Software/RegisteredApplications` | `TANGERINE VideoEditor` 的能力路径 |

打开命令为 `"<实际 EXE 路径>" "%1"`，支持程序或项目路径中的空格和中文。程序路径使用 `Environment.ProcessPath`，支持重命名和单文件发布；禁止将 `dotnet.exe` 注册为处理程序。

项目文件的 `DefaultIcon` 固定为 `"<Windows 系统目录>/shell32.dll",0`，使用 Windows 的默认白色文件图标。只在本程序 ProgID 设置图标，不覆盖扩展名层级，不生成自定义图标文件。

## 默认选择与取消注册

沿用 ZIP 的经典 `UserChoice`（SFTA）和 `UserChoiceLatest` 检测及校验逻辑。新式哈希使用随程序集嵌入的 x64 计算组件，源代码、构建脚本和 MIT 许可证保留在 `SystemIntegration/Native`，保留上游导出符号以便复核。非 x64 平台可使用 Windows 设置入口。

Windows 对默认关联的保护机制可能拒绝自动设置，因此不能把注册表写入成功等同于设置成功。服务查询有效 ProgID 核验结果，失败尝试恢复原关联，使用 `DEFAS`、`SFTAS`、`LUCAS`、`UNRAS`、`DAPWN` stagecode 报告具体阶段。

注册写入带有原值日志，任一步失败按逆序恢复原值及原注册表类型。注册和取消注册使用同一个当前用户互斥锁。取消注册只处理本程序的 ProgID，保留其他应用的选择、候选值及未知能力声明；本程序选择无法清除时保留处理程序，避免指向不存在的打开命令。

## 关联启动

`App.OnStartup` 接收 Explorer 传来的完整项目路径，在主窗口加载后调用 `OpenProjectPathAsync`。该方法与“打开项目”对话框共用同一异步加载、项目状态替换和错误处理路径。只有成功读取项目后才替换当前工程；大写 `.TVEP` 同样接受。

## 验证

`tests/SystemIntegrationCheck` 对真实服务使用隔离注册表根 `HKCU/Software/TangerineVideoEditor.Tests/<随机标识>`，验证写入计划、白色图标、中文路径引号、回滚、所有权保护、取消注册幂等性和两种显式选择清理。测试不更改真实 `.tvep` 默认应用，也不触发 Shell 关联通知。

同时检查程序集及文件版本、原生资源与许可证，并在 STA 调度器上检查窗口布局和真实项目文件加载。
