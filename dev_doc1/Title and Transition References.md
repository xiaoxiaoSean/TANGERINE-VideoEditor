# Title and Transition References

## 目标与范围

用户反馈的“旋转开场”“轻微抖动”与实际画面不符，属于效果语义错误。FFmpeg 退出码为零只能说明视频可编码，不能证明效果名称与视觉行为一致。修复应改变实际几何、遮罩或颜色计算，并把示例、时间线预览和导出接到同一套实现。

本轮参考 Adobe Premiere、Kdenlive、Shotcut 的公开官方帮助，独立实现通用文字排版和转场行为。参考功能和参数不等于移植厂商源码、模板、字体或商业效果素材；界面不应声称已经移植 Movavi 的专有效果包。以下“建议参数”是本软件的实现要求，不能作为已经完成的功能清单。

检索日期：2026-10-18（Asia/Shanghai）。下列正文页面已通过 HTTP 实际获取并核对。

## 已核对的官方来源

| 软件 | 官方页面 | 事实与参考点 |
| --- | --- | --- |
| Premiere | [List of Video transitions](https://helpx.adobe.com/premiere/desktop/add-video-effects/effects-and-transitions-library/list-of-video-transitions.html) | 官方分类包含 Dissolve、Iris、Page Peel、Slide、Wipe、Zoom；圆形/菱形光圈、推移、滑动、百叶窗、时钟擦除是不同种类。 |
| Premiere | [Change transition settings](https://helpx.adobe.com/premiere/desktop/add-video-effects/apply-video-transitions/change-transition-settings.html) | 明确列出边缘方向、开始/结束完成度、边框宽度/颜色、反向、边缘平滑质量及效果专属参数。并非所有转场都适用全部字段。 |
| Premiere | [Style parameters when applying from the style browser](https://helpx.adobe.com/premiere/desktop/add-text-images/stylize-text/style-parameters-when-applying-from-style-browser.html) | 样式包含字体、字体样式、字距、行距、填充、描边、背景、阴影等；文档明确区分正文层和字幕可用属性。 |
| Kdenlive | [Transitions](https://docs.kdenlive.org/en/compositing/transitions.html) | 擦除基本由灰度图驱动；允许自定义 PGM 遮罩。灰度出现顺序和反向会改变转场过程。 |
| Kdenlive | [Wipe](https://docs.kdenlive.org/en/compositing/transitions/wipe.html) | 有遮罩类型、柔和度、invert、reverse；柔和度零意味着清晰边缘。类型包括 Bar、Barn Door、Checkerboard、Clock、Iris Circle、Radial、Spiral、Horizontal Blinds 等。 |
| Kdenlive | [Slide](https://docs.kdenlive.org/en/compositing/transitions/slide.html) | 有起始位置、结束位置、透明度；默认从左侧画外滑入并覆盖另一段，位置组合决定真实方向。 |
| Kdenlive | [Title Clips](https://docs.kdenlive.org/en/titles_and_graphics/titles/titles.html) | 标题可以组合文字、矩形、圆形、图片，制作片头、下三分之一、标注；支持模板和滚动文字。 |
| Kdenlive | [Text](https://docs.kdenlive.org/en/titles_and_graphics/titles/title_text.html) | 文本具有字体/字重/字号/斜体/下划线、纯色/渐变、描边宽度/颜色、阴影模糊/颜色/X/Y、对齐、字间距、行间距。打字机可按字/词/行展开，步长范围 1–240 帧、波动 0–20、随机种子 0–100。 |
| Kdenlive | [Title Scrolling](https://docs.kdenlive.org/en/titles_and_graphics/graphics_and_animations/title_scrolling.html) | 开始视口与结束视口决定滚动方向；动画时长决定速度，片段长度与动画长度并非同一概念。 |
| Shotcut | [User Guide](https://forum.shotcut.org/t/user-guide-table-of-contents/43285) | 从 Shotcut 官网 How To 页面进入的官方文档索引，包含转场、文字、打字机、关键帧等主题。 |
| Shotcut | [Changing the Style of Transition](https://forum.shotcut.org/t/changing-the-style-of-video-transition/12959/1) | 提供 Dissolve、Horizontal/Vertical Wipe、Iris、Barn Door、Custom；invert 改方向，softness 改边缘。文档明确 wipe 只改变两幅图的混合区域，不使原图移动或变形。 |
| Shotcut | [Text: Rich Video Filter](https://forum.shotcut.org/t/text-rich-video-filter/21039/1) | 包含可复用预设、位置、矩形大小、背景色及透明度、溢出显示/裁切。 |
| Shotcut | [Text: Typewriter Video Filter](https://forum.shotcut.org/t/text-typewriter-video-filter/50224/1) | 打字机包含逐字/逐词/逐行、每步帧数、速度波动、光标显示规则/形状/闪烁速度，不能仅靠普通淡入冒充。 |

## 转场行为与参数矩阵

通用参数应覆盖时长、缓动、开始/结束完成度。方向、边缘柔和度等只展示给真正使用它们的效果；一个参数必须同时改变示例、预览和导出的实际画面。

| 效果族 | 用户可观察的行为 | 建议公开参数 |
| --- | --- | --- |
| 交叉溶解 | 两幅画面透明度连续交接，无几何位移 | 时长、缓动、开始/结束完成度 |
| 淡入黑/白/自定义颜色 | 中点明确出现指定颜色 | 时长、过渡颜色、颜色停留比例、缓动 |
| 直线/斜线擦除 | 原图保持静止，边界横向/纵向/斜向扫过 | 时长、方向/角度、柔和度、反向、边框宽度/颜色 |
| 滑入覆盖 | 新图移动进入，旧图保持原位 | 时长、起点/终点、方向、透明度、缓动 |
| 推移 | 两幅画面共同移动，拼接边界保持连续 | 时长、方向、缓动 |
| 圆形/菱形/矩形光圈 | 指定形状遮罩从中心展开或收拢 | 时长、中心 X/Y、形状、反向、柔和度 |
| 双门/分屏展开 | 对称的两个边界从中间展开或合拢 | 时长、横/纵方向、中心位置、反向、柔和度 |
| 百叶窗 | 多条平行遮罩同步展开，可看见重复条带 | 时长、横/纵方向、条带数、反向、柔和度 |
| 棋盘格 | 二维格子依次或交替切换 | 时长、行/列数、顺序/反向、柔和度 |
| 时钟/径向擦除 | 围绕一个中心按角度揭示新画面 | 时长、中心 X/Y、起始角度、顺/逆时针、柔和度 |
| 旋转开场 | 新画面围绕指定枢轴旋转进入，角度随时间改变 | 时长、方向、圈数/角度、中心 X/Y、起始缩放、缓动 |
| 轻微抖动 | 转场期间有连续正负位移/旋转，首尾回到稳定位置 | 时长、X/Y 幅度、频率、旋转幅度、随机种子、边缘填充方式 |
| 缩放切换 | 画面局部随缩放真实放大/缩小 | 时长、缩放量、中心 X/Y、缓动 |
| 模糊交接 | 中间阶段的边缘细节真实减少，首尾清晰 | 时长、模糊半径、方向/类型、缓动 |
| 闪光/漏光 | 颜色光层具有独立空间分布或时间包络 | 时长、颜色、强度、位置/方向、衰减 |
| 翻页 | 可见翻折的页面或透视边界，旧画面被翻走 | 时长、翻页角、卷曲量、阴影强度、方向；只有实际支持该几何时才使用“翻页”命名 |

上述参数是跨软件参考后的本软件设计，不表示源软件对每个效果都提供同名字段，也不表示本轮已经实现整张表。

## 文字样式与参数矩阵

文字预设应保存一组可编辑样式，而不是给同一默认文字换名称。可基于下列风格组织预设：纯色字幕、粗描边字幕、阴影标题、渐变片头、下三分之一信息条、半透明背景字幕、片尾滚动、打字机、滑入标题、弹跳标题。风格命名应描述实际排版或运动。

| 参数组 | 需要读取和保存的字段 | 预览/导出要求 |
| --- | --- | --- |
| 内容与排版 | 文字、字体、字重、字号、斜体、下划线、对齐、字距、行距 | 相同换行、基线与锚点 |
| 填充 | 纯色/渐变、渐变两端颜色、角度、不透明度 | 文字轮廓内绘制，随字形而非全画面渐变 |
| 描边 | 启用、宽度、颜色 | 必须沿字形路径绘制，宽度零无描边 |
| 阴影 | 启用、颜色、X/Y 偏移、模糊、不透明度 | 透明背景、预览/导出相同阴影边界 |
| 背景框 | 颜色、不透明度、内边距、圆角 | 包围文字内容，透明度零时消失 |
| 布局 | 位置、锚点、宽度、缩放、旋转 | 使用画面比例储存，不依赖固定预览控件像素 |
| 进出场 | 延迟、进场/退场时长、方向、距离、缓动、缩放/弹性幅度 | 片段本地时间驱动，拖动播放头可确定性重现 |
| 打字机 | 每字时间或帧步长、逐字/词/行、时间波动、种子、光标显示/形状/闪烁 | 中文按字素簇，随机种子稳定，导出不得重新随机 |
| 滚动片尾 | 方向、速度/时长、开始/结束位置、停留 | 最后一行能按设置滚出；动画长度与片段长度有明确关系 |

## 必须执行的验收

1. 示例使用不对称测试素材：A/B 字母、四角标记、网格、不同颜色。纯色卡无法证明旋转、推移或抖动成立。
2. 在 0%、25%、50%、75%、100% 抽帧；首尾正确，旋转具有角度变化，抖动有交替位移，百叶窗有多条边界。
3. 对同一素材/参数/时间点比较示例、时间线合成帧和导出帧。应使用相同算法和参数，不给示例单独做近似。
4. 参数做最小值、默认值、最大值检查；至少一个有效中间帧必须随参数改变，避免只有输入框而渲染忽略参数。
5. 遍历所有目录预设，检查未识别 ID 不得悄悄退化成通用溶解。使用 stagecode 报告明确错误。
6. 参数保存/重开/撤销后结果保持一致；旧项目缺字段时使用有文档的默认值。
7. FFmpeg 4.4 的兼容验证与视觉验收分别记录，不把“进程成功”写成“全部效果视觉一致”。

## 实施记录约束

实际新增或修复的效果、参数和测试结果应在对应实现文档中登记。本参考文档只保存官方查证、设计矩阵和验收准则，避免将建议清单误报为已完成移植。
