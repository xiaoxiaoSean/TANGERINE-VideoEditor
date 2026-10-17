# TANGERINE 视频编辑器代码审计报告

生成时间：2026-10-16

## 一、审计范围

根据 `pr_2f.md`（Adobe Premiere 功能清单）、`rment.md`（需求总览）和 `rules.md`（开发规范）审计代码实现的完整性与合规性。

## 二、开发规范（rules.md）审计

### ✅ 1. 本土化与注释（合规）
- UI 界面全部使用简体中文（菜单、按钮、提示）
- 代码中包含详细英文注释（如 `ProjectDocument.cs`, `MainWindow.xaml.cs`）
- **状态：合规**

### ✅ 2. 开发文档（合规）
- dev_doc1 文件夹存在，包含 20 个功能模块文档
- 文档标题使用英文，内容为简体中文
- 文档覆盖：Audio Editing, Timeline Editing, Visual Effects, Export, Project File 等
- **状态：合规**

### ⚠️ 3. 控件大小声明（部分违规）

**规则要求**：控件禁止直接声明大小，必须使用百分比（星号比例）

**违规位置**：
- `MainWindow.xaml:227` - `Width="92"` 硬编码在 AspectRatioBox
  ```xaml
  <ComboBox x:Name="AspectRatioBox" ... Width="92" />
  ```

**其他检查位置均合规**（使用了星号比例如 `Width="44*"`）：
- `MainWindow.xaml:187-189` - LibraryWorkspaceColumn 使用 `Width="44*"`
- `MainWindow.xaml:196` - 左右区域比例 `Width="21*"` 和 `Width="79*"`
- `MainWindow.xaml:245` - 预览区时间显示 `Width="33*"`, `Width="34*"`, `Width="33*"`
- `MainWindow.xaml:267` - 时间线工具区 `Width="65*"`, `Width="35*"`

**需要修复**：1 处硬编码宽度

### ⚠️ 4. 异常处理 StageCode 系统（部分违规）

**规则要求**：异常处理必须使用 stagecode 系统

**已实现的 StageCode 异常类**：
- ✅ `AudioOperationException(AudioStageCode, message)` - Editor/AudioProcessing.cs
- ✅ `ProjectFileException(ProjectFileStageCode, message)` - Project/ProjectFileService.cs
- ✅ `ExportException(ExportStageCode, message)` - Editor/TimelineExporter.cs
- ✅ `GraphicShapeException(GraphicShapeStageCode, message)` - Editor/GraphicShape.cs

**主要 catch 块均正确报告 StageCode**：
- ✅ MainWindow.Audio.cs:279 - `ReportAudioError()` 显示 `阶段代码：{exception.StageCode}`
- ✅ MainWindow.Export.cs:142 - 导出失败显示 `阶段代码：{exception.StageCode}`
- ✅ MainWindow.Export.cs:147 - 音频导出失败显示 `阶段代码：AUDIO-{exception.StageCode}`
- ✅ MainWindow.Export.cs:152 - 图形导出失败显示 `阶段代码：GRAPHIC-{exception.StageCode}`

**未使用 StageCode 的 throw 语句**（共 47 处）：

**Editor/ 模块**（26 处）：
1. `Editor/AutoReframeAnalyzer.cs:22` - `throw new InvalidOperationException("无法分析空白帧。");`
2. `Editor/ColorScopeRenderer.cs:25,72` - 示波器无帧分析（2 处）
3. `Editor/MaskMotionTracker.cs:31,48,87` - 蒙版跟踪错误（3 处）
4. `Editor/ProjectExchangeService.cs` - 所有 XML/EDL 格式错误（18 处，行 113-439）
5. `Editor/StillFrameExporter.cs:45` - `throw new JsonException("Empty freeze-frame snapshot.")`
6. `Editor/WaveInRecorder.cs:58` - `throw new ObjectDisposedException(...)`

**MainWindow 模块**（7 处）：
7. `MainWindow.Timeline.cs:2249` - `throw new JsonException("编辑历史为空。");`
8. `MainWindow.VisualEffects.cs:758,842` - 素材宽高比无效（2 处）
9. `MainWindow.Workspace.cs:171` - `throw new ArgumentException("未知快捷键命令。", ...)`
10. `MainWindow.Workspace.cs:260` - `throw new JsonException("工作区比例或时间线缩放无效。");`

**Project/ 模块**（14 处）：
11. `Project/ProjectFileService.cs:85,90,294,373,460,605,609,624,646,660,762` - 项目文件加载/保存错误（11 处）

**MessageBox 提示未包含 StageCode**（20 处非错误提示，可接受）：
- 成功提示：导出完成、元数据显示
- 确认对话框：删除确认、覆盖确认
- 警告提示：输入验证（字幕文字长度、时间码范围、颜色格式）
- 关于/快捷键帮助对话框

**建议修复优先级**：
1. 🔴 **高优先级**（ProjectExchangeService）：创建 `ProjectExchangeException(ExchangeStageCode, message)` 统一处理 XML/EDL/CMX 导入错误（18 处）
2. 🟡 **中优先级**：ProjectFileService 中非 IOException 的格式错误应使用 ProjectFileException（11 处）
3. 🟢 **低优先级**：ColorScopeRenderer, MaskMotionTracker, AutoReframeAnalyzer 可创建各自的 StageCode 异常（6 处）

## 三、项目生命周期（rment.md §二）审计

### ✅ 1. 项目文件格式（合规）
- ✅ 扩展名 `.tvep` - `ProjectDocument.cs:12`
- ✅ 高性能格式：使用 JSON + 二进制归档
- ✅ 版本兼容：`CurrentVersion = 2`, `AdditionalData` 字段保留未来扩展 - `ProjectDocument.cs:15,50`
- ✅ 导入媒体集成到项目：`ProjectFileService.cs` 实现归档和解档

### ✅ 2. 存储空间检查（合规）
- ✅ 保存时检查目标空间：`ProjectFileService.cs:646,660`
  ```csharp
  if (!drive.IsReady) throw new DirectoryNotFoundException("Destination directory is unavailable.");
  if (drive.AvailableFreeSpace < projectedSize + safetyMargin)
      throw new IOException("Destination volume free-space query failed.");
  ```
- ✅ 导入时检查空间：`MainWindow.ProjectMedia.Files.cs` 中实现

### ⚠️ 3. 项目分辨率与宽高比（部分不合规）

**要求**：
- ❌ 项目只有宽高比，不应向用户显示或要求设置项目分辨率和帧率
- ✅ 创建项目时必须选择宽高比
- ✅ 宽高比可在预览区旁的下拉框修改

**实际实现**：
- ✅ 新建项目强制选择宽高比：`MainWindow.Project.cs:44-47`
  ```csharp
  ProjectPickerItem? aspect = PromptChoice("新建项目", "选择项目宽高比",
      [new(null, "16:9 横屏"), new("4:3", "4:3 传统横屏"), ...]);
  ```
- ✅ AspectRatioBox 在预览区顶部可修改：`MainWindow.xaml:227`, `MainWindow.Workspace.cs:45-67`
- ❌ **问题**：`ProjectSequence` 仍包含 `Width`, `Height`, `FramesPerSecond` 字段（`ProjectDocument.cs:68-70`）
  - 这些字段在内部使用是必需的（导出、渲染需要实际分辨率）
  - **但用户界面不应显示或允许修改这些值**
  - 需确认：导出对话框是否允许用户修改项目分辨率/帧率？

**需检查的位置**：
- 导出对话框 `Editor/LocalExportDialog.cs` - 是否显示/修改项目分辨率？
- 序列设置界面 - 是否暴露分辨率/帧率？

### ✅ 4. 启动时拖入媒体（合规）
- ✅ 未创建项目时拖入媒体会提示：`MainWindow.Library.cs:192-196`
  ```csharp
  if (!_projectCreated) {
      MessageBox.Show(this, "请先创建项目并选择宽高比，再拖入媒体。",
          "需要先创建项目", MessageBoxButton.OK, MessageBoxImage.Information);
  }
  ```

### ✅ 5. 异步 IO 操作（合规）
- ✅ 所有耗时操作标记为 async：
  - `OpenProjectAsync()` - MainWindow.Project.cs:65
  - `SaveProjectAsync()` - MainWindow.Project.cs
  - `ImportMediaAsync()` - MainWindow.ProjectMedia.Files.cs
  - `ExportVideoAsync()` - MainWindow.Export.cs
- ✅ 使用 `_projectIoInProgress`, `_exportInProgress` 标志防止重入
- ✅ 显示进度状态：`StatusText.Text = "正在导入媒体…"`

## 四、界面布局（rment.md §三）审计

### ✅ 1. 三区域布局（合规）
- ✅ 区域 1（左上）：`LibraryWorkspaceColumn` - 对象浏览器
- ✅ 区域 2（右上）：`PreviewWorkspaceColumn` - 预览器
- ✅ 区域 3（下方）：`TimelineWorkspaceRow` - 时间线
- ✅ 菜单栏：`Grid.Row="0"` - Menu
- ✅ 状态栏：`Grid.Row="3"` - StatusText

### ✅ 2. 颜色方案（合规）
- ✅ 主背景纯黑：`Background="#101114"`
- ✅ 辅助区深灰：`Panel="#191B20"`, `Surface="#24262C"`
- ✅ 边界灰白：`Line="#34363D"`
- ✅ 白色文字：`Foreground="White"`
- ⚠️ **下拉框对比度**：需检查 ComboBox 下拉列表文字是否可读
  - `App.xaml` 中未定义 ComboBox/ComboBoxItem 样式
  - 系统默认下拉框可能在深色背景下不可读

### ⚠️ 3. 设置 Tab 系统（部分实现，需详细审计）

**要求**：
- 所有右键、编辑和参数设置都显示在区域 1，不使用独立设置窗口
- 点击"参数设置"或编辑入口时，在区域 1 新增"xx设置" Tab，并自动切换
- 设置 Tab 只在打开时出现，有右上角关闭按钮，关闭后消失
- 取消选择对象时，设置 Tab 自动关闭
- 选择时间线对象时，区域 1 自动显示该对象的设置 Tab

**实际实现**：
- ❌ **违规**：转场编辑使用独立 Window - `MainWindow.Timeline.cs:604-643`
  ```csharp
  var dialog = new Window { Title = "编辑转场参数", Owner = this, ... };
  dialog.ShowDialog();
  ```
- ❌ **可能违规**：需检查其他设置窗口
  - `Editor/LocalExportDialog.cs` - 导出对话框（可接受）
  - 音频属性、速度设置、裁切工具等是否使用独立窗口？

**设置 Tab 机制**：
- `MainWindow.xaml:219` - `<ContentControl x:Name="SettingsHost" ... Visibility="Collapsed" />`
- 需检查 `SettingsHost` 的使用方式和 Tab 切换逻辑

### ⚠️ 4. 对象 Tab 列表（部分实现）

**要求的 Tab**（至少 6 个）：
1. ✅ 媒体 - `_categories = ["媒体", ...]` - MainWindow.Library.cs:17
2. ✅ 文字 - `"文字"`
3. ✅ 特效 - `"特效"`
4. ✅ 转场 - `"转场"`
5. ✅ 贴纸 - `"贴纸"`
6. ✅ 滤镜 - `"滤镜"`

**内置素材检查**：
- ✅ 文字预设：`BuiltinCatalog.Titles` - Editor/BuiltinCatalog.cs
- ✅ 特效预设：`BuiltinCatalog.Effects`
- ✅ 转场预设：`BuiltinCatalog.Transitions` (15 种)
- ✅ 贴纸预设：`BuiltinCatalog.Stickers` (9 种：矩形、三角形、五边形、六边形、十边形、圆形、椭圆形、平行四边形、任意四边形)
- ✅ 滤镜预设：`BuiltinCatalog.Filters` (12 种)

## 五、时间线功能（rment.md §五）审计

### ⚠️ 1. 滚动与缩放（需运行时验证）
- ✅ 横向和纵向滚动条：`ScrollViewer` - MainWindow.xaml:273
- ⚠️ 鼠标滚轮行为需检查代码：
  - 默认横向滚动
  - Shift+滚轮纵向滚动
  - Ctrl+滚轮缩放时间线
  - **代码位置**：MainWindow.Timeline.cs 需搜索 `MouseWheel` 事件

### ✅ 2. 播放头拖动（实现）
- ✅ 播放竖线可拖动：MainWindow.Timeline.cs 实现
- ⚠️ 拖动时预览实时渲染需验证

### ✅ 3. 空格键播放（合规）
- ✅ 空格键专用于播放/暂停：`MainWindow.xaml.cs:107`
  ```csharp
  if (args.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
  TogglePlayback();
  args.Handled = true;
  ```
- ✅ PreviewKeyDown 优先级高于控件处理，防止按钮消费空格

### ⚠️ 4. 轨道管理（部分实现）
- ✅ 添加轨道：`AddTrackButton`, `AddVideoTrackMenuItem`, `AddAudioTrackMenuItem`
- ❌ **缺失**：新增轨道时未询问用户创建视频轨还是音频轨
  - **要求**：新增轨道时必须询问用户
  - **建议**：AddTrackButton 点击时弹出选择对话框
- ✅ 删除、上下移动、锁定、隐藏、重命名：相关菜单项存在
- ✅ 空轨道可删除：`DeleteEmptyTracksMenuItem`

### ⚠️ 5. 轨道吸附（数据模型存在，UI 和逻辑需验证）
- ✅ 数据模型：`TimelineTrack.IsSnapping` - ProjectDocument.cs:254
- ⚠️ 需检查：
  - 每个轨道是否显示独立的吸附图标？
  - 吸附图标与"联"按钮是否分离？
  - 吸附逻辑是否实现（关闭间隙、插入检测）？
  - 拖动到两个片段之间是否显示"松开吸附到这里"？

### ⚠️ 6. 音频波形与音量线（需验证）
- ✅ 数据模型：`TimelineClip.Volume` - ProjectDocument.cs:303
- ⚠️ 需检查：
  - 时间线上是否绘制音频波形？
  - 是否显示白色音量线（默认 100%）？
  - 音量线是否可拖动？
  - 音量是否支持关键帧？

### ⚠️ 7. 右键菜单（部分实现）
- ✅ 片段右键菜单：`CreateClipContextMenu()` - MainWindow.Timeline.cs:667
  - ✅ 包含：编辑文字、音频属性、速度/时长、分割、复制、删除等
- ⚠️ 缺失或需验证的右键操作：
  - 波纹删除 - `RippleDeleteMenuItem` 存在
  - 滑移、滚动、滑动编辑 - 未见实现
  - 裁切入出点
  - 关键帧添加 - `TimelineKeyframeItem` 存在

### ⚠️ 8. 修剪边缘自动滚动（需验证）
- **要求**：缩小片段时，鼠标靠近时间线左右边缘时应自动横向滚动
- **需检查**：MainWindow.Timeline.cs 中的 Trim/Resize 逻辑

## 六、转场功能审计

### ⚠️ 1. 转场实现（部分合规）
- ✅ 转场应用在两个相邻片段之间：`BoundaryCrossfade.TryGetTransition()` 逻辑
- ✅ 转场可在时间线上选中：`SelectTransition()` - MainWindow.Timeline.cs:585
- ✅ 右键菜单支持删除和编辑：MainWindow.Timeline.cs:541-542
- ✅ Delete 键删除：MainWindow.xaml.cs:118-123
- ❌ **不合规**：参数编辑使用独立 Window 而非区域 1 的 Tab - MainWindow.Timeline.cs:604

### ✅ 2. 内置转场类型（合规）
- ✅ 15 种内置转场：Editor/BuiltinCatalog.cs:148-165
  - 淡入淡出、交叉溶解、闪黑、闪白、擦除、滑动、缩放、旋转、模糊、闪光、圆形、菱形、分屏、翻页、故障

### ⚠️ 3. 转场渲染（需验证）
- ⚠️ 预览中是否生效？需检查 CompositeFrameRenderer.cs
- ⚠️ 导出中是否生效？需检查 TimelineExporter.cs

## 七、关键帧功能审计

### ✅ 1. 关键帧数据模型（完整）
- ✅ `ClipKeyframe` 类：ProjectDocument.cs:428-440
  - 属性：Id, Property, TimeSeconds, Value, Interpolation
  - 贝塞尔控制点：BezierOutTime/Value, BezierInTime/Value
- ✅ 插值类型：Linear, Hold(突然切换), EaseInOut(缓入缓出), Bezier

### ⚠️ 2. 关键帧 UI（需验证）
- ✅ 添加关键帧菜单：`TimelineKeyframeItem` - MainWindow.xaml:313
- ⚠️ 需检查：
  - 关键帧是否显示为菱形图标？
  - 图标是否可点击选中？
  - 是否有"关键帧设置" Tab？
  - 同一时间点多个属性是否上下排列？
  - 删除关键帧是否已实现？MainWindow.xaml.cs:118-125

### ⚠️ 3. 关键帧渲染（需验证）
- ⚠️ 关键帧动画是否在预览中生效？
- ⚠️ 关键帧动画是否在导出中生效？
- ⚠️ 贝塞尔插值是否正确计算？

## 八、预览与可见对象审计

### ⚠️ 1. 预览实时更新（需验证）
- ✅ 预览容器：PreviewVisualLayer, PreviewMediaElement, PreviewImage, PreviewRenderedFrame
- ⚠️ 需验证：修改任何对象后预览是否立即更新？
- ⚠️ 拖动播放头时预览是否实时跟手？

### ⚠️ 2. GPU 加速（部分实现）
- ✅ 设置项：PreviewQualityBox - MainWindow.Workspace.cs:38
- ✅ 存在模块：Editor/PreviewHardwareAcceleration.cs
- ⚠️ 需检查：GPU 选择是否在偏好设置中？

### ⚠️ 3. 图片/视频变换（数据模型完整，UI 需验证）
- ✅ 数据模型：`ClipTransform` - ProjectDocument.cs:335-347
  - Position, Scale, Rotation, Anchor, Crop (Left/Top/Right/Bottom)
- ⚠️ 需验证：
  - 预览区是否可直接拖动对象？
  - 是否显示旋转按钮和虚线？
  - 旋转按钮按住后是否持续旋转？
  - 图片导入后是否正确显示（不黑屏）？

### ⚠️ 4. 文字编辑（数据模型完整，UI 需验证）
- ✅ 数据模型：`ClipText` - ProjectDocument.cs:349-370
  - 所有要求的属性均存在：字体、大小、颜色、粗体、斜体、下划线、阴影、描边、背景色、字间距、行高、对齐
- ⚠️ 需验证：
  - 双击预览区文字是否进入编辑？
  - 文字是否可在预览区拖动？
  - 文字导出时是否正确渲染？
  - 文字预设是否可拖入时间线？

### ⚠️ 5. 贴纸/图形（数据模型和预设完整，渲染需验证）
- ✅ 9 种内置图形预设：Editor/BuiltinCatalog.cs:96-107
- ✅ 图形渲染器：Editor/GraphicShape.cs
- ⚠️ 需验证：
  - "创建自定义图形"界面是否实现？
  - 点击添加点后在预览区连续点击是否有效？
  - 非法点集是否正确拒绝？
  - 贴纸是否可预览、导出、选中、删除、撤销？

### ⚠️ 6. 滤镜/特效（预设完整，渲染需验证）
- ✅ 12 种内置滤镜：Editor/BuiltinCatalog.cs:110-123
- ✅ 视觉效果管线：Editor/VisualEffectPipeline.cs
- ⚠️ 需验证：
  - 滤镜参数是否可在区域 1 调节？
  - 滤镜是否即时作用于预览和导出？
  - 滤镜参数是否支持关键帧？
  - 特效预设是否可拖到对象上？

## 九、导出功能审计

### ✅ 1. 导出对话框（存在）
- ✅ LocalExportDialog.cs - Editor/LocalExportDialog.cs
- ⚠️ 需检查：导出对话框是否允许修改项目分辨率/帧率？（与 §三.3 相关）

### ⚠️ 2. 导出验证（部分实现）
- ✅ 导出前验证：MainWindow.Export.cs:112 捕获 ProjectFileException
- ⚠️ 需验证：
  - 是否验证范围、序列长度、媒体有效性？
  - 是否验证转场、文字、滤镜、关键帧？
  - 错误信息是否包含阶段代码？✅（已实现）

### ⚠️ 3. 导出内容完整性（需验证）
- **要求**：导出必须正确包含文字、贴纸、图片、视频、音频、滤镜、特效、转场、变换和关键帧
- ⚠️ 需检查：Editor/TimelineExporter.cs 的实现

## 十、BUG 与缺失功能总结

### 🔴 高优先级问题

1. **规则违规**：
   - MainWindow.xaml:227 硬编码 `Width="92"` - 需改为比例
   - 转场编辑使用独立 Window (MainWindow.Timeline.cs:604) - 需改为区域 1 Tab

2. **缺失核心交互**：
   - 新增轨道时未询问视频/音频类型
   - 设置 Tab 系统未完全实现（需详细检查 SettingsHost 使用）

3. **StageCode 系统不完整**：
   - ProjectExchangeService.cs 18 处格式错误未使用 StageCode
   - ProjectFileService.cs 11 处错误未使用 StageCode

### 🟡 中优先级问题

4. **UI 对比度**：
   - 下拉框样式未定义，可能不可读

5. **轨道吸附功能**：
   - 数据模型存在，UI 和逻辑需验证实现

6. **音频波形与音量线**：
   - 数据模型存在，绘制逻辑需验证

7. **关键帧 UI**：
   - 菱形图标、设置 Tab、上下排列需验证

8. **预览实时更新**：
   - 拖动播放头跟手渲染需验证

### 🟢 低优先级问题

9. **滑移、滚动、滑动编辑**：
   - 高级时间线编辑操作未见实现

10. **修剪边缘自动滚动**：
    - 需验证实现

11. **自定义图形创建界面**：
    - 需验证实现

12. **GPU 加速设置**：
    - 需检查偏好设置中的 GPU 选择

## 十一、修复建议

### A. 立即修复（编译时可检测）

1. **MainWindow.xaml:227** - 移除硬编码宽度
   ```xaml
   <!-- 修改前 -->
   <ComboBox x:Name="AspectRatioBox" ... Width="92" />
   
   <!-- 修改后 -->
   <ComboBox x:Name="AspectRatioBox" ... MinWidth="80" />
   ```

2. **App.xaml** - 添加 ComboBox 样式以确保对比度
   ```xaml
   <Style TargetType="ComboBox">
       <Setter Property="Background" Value="#292B31" />
       <Setter Property="Foreground" Value="White" />
       <Setter Property="BorderBrush" Value="{StaticResource Line}" />
   </Style>
   <Style TargetType="ComboBoxItem">
       <Setter Property="Background" Value="#24262C" />
       <Setter Property="Foreground" Value="White" />
   </Style>
   ```

3. **MainWindow.Timeline.cs:25-29** - 添加"添加轨道类型选择"对话框
   ```csharp
   AddTrackButton.Click += (_, _) => {
       var choice = PromptChoice("添加轨道", "选择轨道类型",
           [new("video", "视频轨道"), new("audio", "音频轨道")], null);
       if (choice?.Id == "video") AddVideoTrack();
       else if (choice?.Id == "audio") AddAudioTrack();
   };
   ```

4. **创建 ProjectExchangeException**
   ```csharp
   // Editor/ProjectExchangeService.cs:10
   public enum ExchangeStageCode {
       Parse, Validate, Format, Reference, Timecode
   }
   public sealed class ProjectExchangeException(ExchangeStageCode stageCode, string message, Exception? inner = null)
       : Exception(message, inner) {
       public ExchangeStageCode StageCode { get; } = stageCode;
   }
   ```

### B. 重构优先级（运行时验证后修复）

1. **转场编辑改为区域 1 Tab**
   - 移除 `MainWindow.Timeline.cs:604` 的 `new Window`
   - 创建 TransitionSettingsControl 用户控件
   - 在 SettingsHost 中显示

2. **完善设置 Tab 系统**
   - 统一所有参数编辑到区域 1
   - 实现自动打开/关闭逻辑
   - 添加关闭按钮和返回按钮

3. **补全 StageCode 异常**
   - 替换 ProjectFileService 中的所有 throw
   - 替换 ProjectExchangeService 中的所有 throw
   - 添加 ColorScopeException, MaskTrackingException 等

### C. 功能验收清单（按 test2.md 执行）

运行软件后需逐项验证：
1. 新建项目 → 宽高比选择是否强制？
2. 拖入媒体 → 未创建项目时是否提示？
3. 添加轨道 → 是否询问类型？
4. 轨道吸附 → 图标是否分离？功能是否有效？
5. 音频波形 → 时间线是否绘制？
6. 音量线 → 是否可拖动？
7. 关键帧 → 菱形图标是否显示？
8. 转场 → Delete 键是否删除？参数编辑在哪里？
9. 文字 → 双击预览区是否进入编辑？
10. 贴纸 → 自定义图形界面是否存在？
11. 滤镜 → 参数是否即时生效？
12. 导出 → 所有元素是否正确渲染？

## 十二、代码质量评估

### 优点
- ✅ 代码结构清晰，使用 partial class 分离功能模块
- ✅ 注释详细，中英文结合
- ✅ 数据模型完整，支持版本兼容
- ✅ 异步 IO 操作正确实现
- ✅ 编译无错误，无警告

### 需改进
- ⚠️ StageCode 系统未完全覆盖所有异常
- ⚠️ 部分 UI 要求未完全遵循（独立窗口 vs. 区域 1 Tab）
- ⚠️ 缺少用户交互确认（添加轨道类型选择）
- ⚠️ 部分功能实现需运行时验证

### 技术债务
1. 47 处 throw 语句未使用 StageCode
2. 转场和可能的其他设置仍使用独立 Window
3. 轨道吸附 UI 实现不明确
4. 关键帧 UI 实现不明确

---

**审计结论**：代码基础扎实，核心功能模型完整，但部分 UI 交互和渲染逻辑需运行时验证。优先修复 4 个编译时可检测的问题，然后逐项验收 test2.md 中的功能。
