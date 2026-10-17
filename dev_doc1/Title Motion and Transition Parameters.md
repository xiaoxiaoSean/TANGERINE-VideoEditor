# 动画文字与转场参数

## 目标
动画文字（打字机、数字滚动、淡入淡出、滑入、弹跳）和转场的时间、距离、强度等数值都由用户设置，不写死在代码里。预览与导出读取同一份参数，同一帧在两边的结果一致。

## 数据
- `ClipText.Motion`（`TitleMotion`，`Project/ProjectDocument.cs`）：所有字段可空，空表示使用内置默认值，所以旧项目打开后效果不变。
  - 通用：`DelaySeconds`（开始延迟）。
  - 打字机：`SecondsPerCharacter`（秒/字）。未设置时沿用旧规则：整句固定 1.8 秒打完。
  - 数字滚动：`CountSeconds`（0 或空 = 整个片段）、`Decimals`（空 = 跟随输入的数字）。
  - 淡入淡出：`FadeInSeconds`、`FadeOutSeconds`；两者之和超过片段长度时按比例缩短。
  - 滑入：`SlideSeconds`、`SlideDistance`（占画面宽或高的比例）、`SlideFrom`（Left/Right/Top/Bottom）。
  - 弹跳：`PopSeconds`、`PopStartScale`、`PopOvershoot`。
- `ClipTransition.Parameters`：`strength`（强度，1 = 100%）、`turns`（旋转圈数）。未设置时与改动前的数值相同。

## 代码位置
- `Editor/DynamicTitlePreset.cs`：读取参数并做范围限制；预览（WPF）和导出（FFmpeg 表达式）共用。垂直滑入通过 `VerticalOffset` / `FfmpegVerticalOffset` 接入预览、图形贴纸叠加层和导出。
- `Editor/TransitionSettings.cs`：转场参数的读取、写入与取值范围。`CompositeFrameRenderer`（静帧合成）和 `TimelineExporter`（视频导出）都从这里取值。
- `MainWindow.TitleMotion.cs`：文字设置页中的“动画参数”分组。
- `MainWindow.TransitionParameters.cs`：转场设置页和片段“效果控件”页共用的参数输入框。
- 新建的打字机标题会带上默认 0.1 秒/字（`DynamicTitlePreset.DefaultMotionFor`）。

## 界面行为
- 所有设置页（`OpenSettingsCategory`）自带纵向滚动条；序列索引自带虚拟化列表，不再外套滚动。
- 输入越界时拒绝并在状态栏提示允许范围（阶段代码 `TEXT-MOTION-RANGE`、`VIS-TRANSITION-PARAM`），输入框恢复为上一次有效值。
- 回车或失去焦点时提交；文字页同一次编辑只记一次撤销，转场参数每次提交各记一次。
- 打字机页会显示“全部显示约需 X 秒 / 片段长 Y 秒”，不够时用橙色提示。
- 文字页“恢复默认动画参数”会重建页面。

## 转场可调项
| 转场 | 可调参数 |
| --- | --- |
| 所有转场 | 时长（秒） |
| 放大 / 缩小切换 | 缩放幅度 |
| 模糊切换 | 模糊程度 |
| 闪光 / 漏光 | 光效亮度 |
| 翻页 / 故障 / 胶片 | 卷曲或抖动幅度 |
| 旋转切换 | 旋转圈数 |

擦除、滑动、推入、圆形/菱形/分屏、闪黑、闪白、淡入淡出、交叉溶解只有时长，因为它们的形状由类型决定。

## 已知限制
- 转场的缓动曲线、闪黑/闪白的颜色暂不可调。
- 内置示例预览（`CatalogPreviewService`）使用默认参数，不反映用户设置的打字速度。
