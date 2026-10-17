# 紧急修复清单

## 问题1: Tab切换不及时
**位置**: 所有 `_selectedClip = clip;` 赋值后
**修复**: 立即调用 `ShowSelectedObjectSettings();`

需要修复的位置：
- MainWindow.Timeline.cs:678 (右键菜单)
- MainWindow.Timeline.cs:1306 (拖拽导入)
- MainWindow.Timeline.cs:1490 (未知位置)

## 问题2: 图形顶点不可编辑
**已创建**: Controls/ShapeVertexEditor.cs
**需要集成**: MainWindow.Text.cs 中添加形状编辑UI

## 问题3: UI空白控件
**需要检查**: MainWindow.xaml 中的空白Border/Grid元素
**修复**: 移除或隐藏未使用的占位符

## 问题4: 示例视频缺失
**已创建**: Editor/CatalogPreviewService.cs
**需要**: 确保在特效/转场Tab中调用预览方法
**示例图片**: samples/image1.jpg, samples/image2.jpg (自动生成)

---

## 立即执行的修复步骤

### Step 1: 修复Tab立即切换
在每个 _selectedClip 赋值后添加：
```csharp
_selectedClip = clip;
_selectedTrack = track;
ShowSelectedObjectSettings(); // 立即更新UI
DrawTimeline();
```

### Step 2: 集成顶点编辑器
在 MainWindow.Text.cs 的图形编辑方法中：
```csharp
var vertexEditor = new ShapeVertexEditor();
vertexEditor.SetShapeType(shape.Kind);
vertexEditor.VerticesChanged += (s, vertices) => {
    // 更新 shape 顶点
    MarkProjectDirty();
};
panel.Children.Add(vertexEditor);
```

### Step 3: 移除UI空白元素
检查并移除：
- MainWindow.xaml 中未绑定的 Border
- 中间空白的 Grid
- 上方空白的控件

### Step 4: 启用预览播放
在特效/转场Tab的点击事件中：
```csharp
effectItem.MouseLeftButtonDown += async (_, _) => {
    await CatalogPreviewService.PlayEffectPreviewAsync(
        effectId, 
        ffmpegPath, 
        videoPath => PreviewPlayer.Source = new Uri(videoPath));
};
```

---

## 优先级
1. ✅ Tab立即切换 (高)
2. ✅ 图形顶点编辑 (高)
3. ⚠️ UI空白修复 (中)
4. ⚠️ 预览播放集成 (中)
