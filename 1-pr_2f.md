# Adobe Premiere 离线功能与特性清单

> 范围：桌面版 Adobe Premiere（旧称 Premiere Pro），核对至 **2026 年 10 月 16 日**。正式版以 26.5 的功能和 26.5.2 的修复记录为基准；可离线运行的 Beta 功能也列入。这里的“离线”指软件已安装、账户已完成所需激活、源媒体与需要的本机模型／语言包已预先取得后，编辑与本地导出期间无需连接互联网或调用云服务、第三方服务。本文按工作流汇总能力，不把每个菜单项、效果预设或编解码器都当作独立功能。下文的“原理”是便于理解的工作机制说明，不表示 Adobe 公开了内部实现代码。

## 如何阅读

- **功能**：软件能完成的任务或提供的编辑能力，例如关键帧、代理剪辑、自动字幕。
- **特性**：用户怎样使用这些能力，以及软件在交互、组织和工作流上的表现，例如把媒体拖进轨道、吸附对齐、非破坏性编辑。
- **片段**是对源媒体某一时间范围的引用；**序列**是按时间和轨道组织片段、图形及效果的编辑结果；**轨道**是承载这些对象的时间轴层。
- 表内介绍的是能力层级。具体效果、转场、相机格式和输出格式持续变化；核对单项兼容性时使用文末官方索引。离线语音识别、AI 搜索和对象蒙版等功能应在断网前准备好相应本机资源。文末附界面截图和第三方实践文章作为阅读资料；它们不是功能依赖。

## 一、功能清单

下表逐项回答“是什么、能干什么、以什么形态使用、原理是什么”。操作路径是典型方式，具体菜单名称可能因版本及界面语言不同而变化。

### 1. 项目、素材与序列

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 项目管理 | 保存素材引用、序列和效果参数的工程容器 | 组织和继续编辑一部作品 | 新建 `.prproj`，在“项目”面板管理内容并保存工程 | 工程记录编辑指令和源文件路径，成片在导出时才计算生成 |
| 媒体导入 | 将视频、音频、图片、图像序列等登记到工程 | 汇集拍摄素材与设计资产 | 使用“导入”界面、菜单或把文件拖入项目面板 | 解析媒体格式、时长、帧率、音轨和元数据，建立对源文件的引用 |
| 摄像机原生格式与混合素材 | 直接读取受支持的摄像机编码、RAW、Log、静帧和音频 | 混剪不同相机与录音设备素材，减少预转码 | 在媒体浏览器或导入界面选文件；按需检查 RAW 解码、帧率与色彩解释 | 解码器读取封装、编码和元数据；能否导入还取决于具体编码、平台及版本，不应只凭扩展名判断 |
| 导入时复制、转码与代理 | 在素材进入项目时执行媒体准备任务 | 统一存放拍摄卡内容、建立中间编码或代理 | 在导入／摄取设置中选复制、转码、创建代理或复制并创建代理 | Premiere 将摄取任务交给 Media Encoder 执行，完成后把本地结果文件与项目条目关联 |
| 图像序列与分层设计稿 | 将编号静帧视作连续画面，并读取 PSD、AI 等设计资产 | 使用逐帧动画、片头图形与可编辑设计层 | 导入连续编号图片为图像序列；导入分层 PSD 时选合并或保留图层 | 连续文件按指定帧率映射为一个时间对象；分层文件可生成独立素材或序列 |
| 素材箱与标签 | 项目面板中的层级分组及视觉分类 | 按场景、机位、日期、用途整理素材 | 新建素材箱、移动片段、设置标签颜色 | 组织信息存放于工程中，不改变源媒体内容 |
| 自由布局与缩略图浏览 | 在项目面板按视觉关系摆放与预览素材 | 做选片墙、比较机位和构建故事板 | 切换列表、图标或 Freeform 视图，调整缩略图和布局 | 视图布局是素材的组织元数据，不改变时间轴或媒体内容 |
| 元数据与搜索 | 读取并管理文件名、时间码、摄像机信息等 | 快速筛选素材、追踪来源 | 在项目面板显示列、搜索、筛选和编辑元数据 | 利用已解析的媒体属性及可写元数据建立检索条件 |
| AI 媒体智能搜索 | 按画面内容、语音、相似视觉或标记寻找本地素材 | 从大量镜头中找动作、对象、替代镜头和对应音效 | 在 Search 面板输入描述、选相似画面或搜索音频与标记 | 本机模型分析媒体，并把检索信息存入本地媒体缓存；视觉语义搜索当前只支持英文，结果仍需人工挑选 |
| 源监视器与入出点 | 在进入时间轴前预览和选定源素材区间 | 快速挑选可用镜头 | 双击素材，在源监视器播放并标记 In/Out | 时间轴保存源文件及源时间范围的引用，而非复制文件片段 |
| 子片段与精选范围 | 将源素材的一段保存为可复用的项目条目 | 从长采访或长镜头中整理备选段落 | 给源素材设入出点，创建子片段并命名 | 子片段记录受限的源时间范围及元数据，不产生新的媒体文件 |
| 序列与序列预设 | 决定成片时间基准和画面规格的编辑容器 | 制作横屏、竖屏、不同帧率和分辨率的视频 | 新建序列、选预设，或由素材创建序列 | 序列设置定义时间码、帧率、画幅、像素比例、音频采样等输出上下文 |
| 时间码与画面叠加信息 | 显示源／序列时间码和自定义监看信息 | 对点同步、记录反馈、核对来源 | 在监视器与时间轴查看时间码；设置元数据叠加、导出烧录信息 | 将媒体时间基准与当前播放位置换算显示，不改变源内容 |
| 多序列与嵌套序列 | 一个工程内有多个时间轴，且序列可作为片段放入另一个序列 | 分章节、复用片头、统一调整复杂段落 | 在项目面板打开多个序列，或将一个序列拖入另一序列 | 外层序列把内层序列当作可渲染的时间对象，编辑仍保留在内层 |
| 项目模板与多项目打开 | 复用工程结构，或同时访问多个项目 | 标准化素材箱、序列和交付设置；跨项目搬运内容 | 基于模板建项目，同时打开项目并按需启用项目锁 | 模板复制既有项目结构；多个项目各自保存编辑状态和媒体引用 |
| 标记 | 附着在片段或序列时间点上的注释 | 标识节拍、待修改处、章节和本地审片备注 | 在播放头位置添加标记，填写名称、颜色和备注 | 把带时间码的元数据关联到源片段或序列 |
| 代理剪辑 | 为高码率素材建立较轻的替代文件 | 在较慢的电脑上流畅剪辑 | 创建或附加代理，切换代理显示；导出时可选源文件或代理 | 代理与原片通过媒体关联及时间码对应，编辑决定仍指向同一源时间范围 |
| 媒体重链接与整合 | 修复素材路径或收集工程所用文件 | 工程搬家、交接、归档 | 找到离线媒体并重新链接；使用项目管理器整理文件 | 用路径和媒体属性重新匹配引用，必要时复制或转码所用素材 |

### 2. 时间轴剪辑与时间处理

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 多轨道时间轴 | 按时间排列的视频、音频、图形和字幕层 | 叠画面、配乐、配音和多层合成 | 把片段拖到 V/A 轨，移动、叠放、锁定或隐藏轨道 | 同一时刻从可见视频层及可听音轨组合出最终帧与声音 |
| 插入与覆盖编辑 | 把素材放入时间轴的两种基本方式 | 插入并推开后续内容，或覆盖现有范围 | 源监视器按钮、快捷键、拖拽落点 | 插入改变后续片段的时间位置；覆盖替换目标轨指定时间区间 |
| 三点／四点编辑与源修补 | 用源和序列入出点精确指定插入范围与轨道 | 快速把选定镜头安放到指定时段 | 在源／节目监视器设置入出点，选源修补及目标轨，再执行插入或覆盖 | 由已指定的三个或四个时间点求出片段时长与落点，并把源视频／音频映射到目标轨 |
| 提取、提升与替换 | 对选中时间段执行删除或换镜头 | 去掉镜头并选择保留空位、闭合间隙或保持时长换素材 | 使用 Lift／Extract、替换片段等命令 | 对时间轴区间及后续对象执行不同的位移与源引用替换规则 |
| 剪切与剃刀 | 在时间点把一个时间轴片段分成数段 | 去掉失误、改节奏、分别加效果 | 剃刀工具或“添加编辑点”快捷键 | 分割的是源范围引用，源媒体文件保持完整 |
| 修剪 | 改变片段在时间轴使用的源范围 | 缩短镜头、调整切点、控制节奏 | 拖动片段边缘或在修剪监视器精调 | 调整源入出点与时间轴边界，不重写源视频 |
| 波纹修剪／删除 | 修剪后自动消除或产生时间轴位移 | 快速删去空白并保持整体连贯 | 波纹编辑工具、波纹删除命令 | 改变一个边界后，系统把受影响的后续片段整体平移 |
| 滚动编辑 | 同时调整相邻两段的交界 | 改变两镜头各自时长但保持总时长 | 滚动编辑工具拖动切点 | 一段的出点与下一段的入点同步反向移动 |
| 滑移与滑动编辑 | 分别调整片段内部取景范围，或移动片段位置并调邻段 | 保持时长寻找更好的动作点，或重排切点 | Slip／Slide 工具拖动片段 | 滑移改变本段源入出点；滑动移动本段并补偿两侧邻段边界 |
| 修剪模式与 J/K/L 回放 | 在双画面修剪视图中以播放速度控制切点 | 细调动作匹配、对白接续和镜头节奏 | 进入 Trim mode，查看切点两侧画面，使用 J/K/L 倒放、暂停、正放或动态修剪 | 预览邻接画面并按修剪方向修改边界；修剪方式决定总时长是否变化 |
| J-cut／L-cut 分离音画切点 | 让下一镜头声音先到或上一镜头声音延后 | 使对话和场景转换自然衔接 | 取消链接或独立修剪音频／视频边缘 | 同一源片段的音频与视频使用不同的序列入出点，但仍可保持同步参考 |
| 轨道目标与同步锁定 | 控制命令作用轨道和跨轨联动 | 精确粘贴、导航、修剪，防止音画错位 | 点击轨道头部 V/A 目标、同步锁定、轨道锁 | 通过轨道状态决定编辑操作影响哪些时间轴对象 |
| 链接、编组与取消链接 | 管理视频和音频片段的选择关系 | 一起移动同步音画，或独立修音 | 选中片段后链接／取消链接／编组 | 为多个时间轴对象保存关联，选择和移动时按关联传播 |
| 速度／持续时间 | 改变片段播放速率与长度 | 快放、慢放、倒放、定格 | 右键“速度／持续时间”，或拖动速率调整带 | 把序列时间重新映射到源媒体时间，必要时重复、丢弃或合成帧 |
| 时间重映射 | 让速度随时间变化 | 速度坡道、动作强调 | 在片段上显示速度橡皮筋并设置速度关键点 | 通过分段变化的时间映射曲线决定每个输出时刻读取哪个源时刻 |
| 光流与帧插值 | 为变速片段生成中间视觉状态 | 减少慢动作卡顿 | 在速度处理或导出设置中选择帧采样、帧混合或光流 | 帧采样复用源帧；帧混合叠加相邻帧；光流估计运动以合成过渡帧 |
| 定格帧与素材解释 | 冻结画面，或重新解释源帧率、场序与像素比例 | 做画面停顿、修正错误的媒体解释、把高帧率素材用于慢动作 | 使用帧保持、时间重映射冻结帧；在“解释素材”中修改属性 | 定格重复指定源帧；素材解释改变媒体如何映射到序列时间与画面空间 |
| 多机位剪辑 | 将多个同步机位作为一组切换 | 访谈、演出、活动的多角度编辑 | 创建多机位源序列，同步音频／时间码，播放时点选机位 | 各机位共享时间基准，切机位实质是写入某时间段所引用的角度 |
| 场景编辑检测 | 从已合成的视频识别原有剪切点 | 重新拆分成片、定位镜头边界并加标记 | 对时间轴片段运行 Scene Edit Detection，选择切分、建子片段或标记 | 比较相邻帧视觉变化以估计剪切位置；溶解或强运动可能需要人工检查 |
| 匹配帧与源定位 | 从时间轴当前画面找回对应的原始源帧 | 核对镜头来源、重选前后余量或替换机位 | 在播放头所在片段执行 Match Frame，并在源监视器查看该帧 | 把序列位置反算到片段所引用的源文件和源时间码 |
| Sequence Index 序列索引 | 以可搜索的列表查看一个序列中的片段 | 定位长时间轴上的镜头、图形和问题区间 | 打开 Sequence Index，按名称等信息搜索并跳到结果 | 从序列编辑对象建立可导航的表格视图，不改变时间轴内容 |
| 基于文本的剪辑 | 用语音转录文字驱动粗剪 | 从长采访中快速选取语句、去停顿 | 在“文本”面板选择、复制、粘贴或删除词句 | 词语与源媒体时间戳对应，文本编辑被转换为时间轴片段选择与剪切 |
| Paper Edit | 把所选转录文字组织成序列的编辑方式 | 先按内容构建故事，再细修画面 | 在文本面板选择多段转录并生成序列 | 使用文本片段的源时间区间构造连续的序列编辑指令 |

### 3. 运动、效果与合成

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 关键帧 | 在指定时间为某个属性记录一个值的控制点 | 做位移、缩放、旋转、透明度、音量、效果强度等随时间变化的动画 | 在“效果控件”面板启用属性秒表，在不同时间点设值；也可在时间轴橡皮筋上加点、拖动关键帧，调整插值与贝塞尔手柄 | 系统按时间读取关键帧值，并在两点间以保持、线性或曲线方式插值；例如 0 秒缩放 100%、2 秒缩放 150%，中间帧自动计算过渡值 |
| 运动与变换 | 控制片段的位置、缩放、旋转、锚点等 | 构图、推拉镜头、画中画、竖屏适配 | 选择片段，在效果控件或节目监视器操作边框与数值 | 对每一输出帧应用几何变换；锚点决定缩放和旋转的参考中心 |
| 不透明度与混合模式 | 调整上层画面的透明程度及与下层的像素混合方法 | 淡入淡出、双重曝光、光效叠加 | 修改片段不透明度、橡皮筋或混合模式 | 按 alpha 值及所选混合公式合成上下图层像素 |
| 视频效果 | 对像素或片段属性施加处理器 | 模糊、锐化、扭曲、降噪、风格化等 | 从“效果”面板拖至片段，在效果控件中调参数 | 渲染时依效果顺序处理输入帧，多数参数可被关键帧驱动 |
| 效果堆栈、源效果与预设 | 管理多个效果的顺序，并把常用参数组合复用 | 批量统一视觉处理、从源片段继承通用修正 | 在效果控件中排序、启停、复制粘贴效果；保存或应用预设；按需在源片段层级加效果 | 效果按栈顺序计算；源效果先作用于该源的实例，片段效果再作用于时间轴实例 |
| 视频转场 | 处理两个镜头交界处的视觉过渡 | 溶解、擦除、缩放等 | 把转场拖到切点，设置时长与对齐方式 | 在过渡区间同时取用两段素材的可用帧，再按转场规则合成 |
| 转场手柄与 Morph Cut | 在时间轴直接调整转场范围，或平滑采访中的跳切 | 快速控制溶解长度、弱化同机位人物跳切 | 拖动片段上的转场手柄；在相邻人像镜头之间应用 Morph Cut | 转场手柄改变取用源帧的重叠区；Morph Cut 分析两端人脸与背景以合成过渡帧，效果受姿态和背景变化影响 |
| 蒙版 | 用形状或路径限定效果作用区域 | 局部调色、局部模糊、人物突出、遮挡 | 在效果控件中画椭圆、矩形或钢笔路径，调羽化、扩展并跟踪 | 蒙版形成随时间变化的黑白权重图，决定每个像素被效果影响的程度 |
| 运动跟踪／AI 对象蒙版 | 让蒙版跟随画面中的对象 | 给移动人脸打码、只调整运动主体 | 创建形状蒙版后跟踪；或在预先下载本机模型后使用 Object Mask 工具点选人物／物体 | 在本机逐帧估计对象位置、形状或运动，更新蒙版路径；复杂遮挡可能需人工修正 |
| 色度键控与合成 | 按颜色范围去除背景并叠加新画面 | 绿幕抠像、虚拟背景 | 对前景片段应用 Ultra Key 等效果，选取键色并细调边缘 | 比较像素颜色与键色的差异，生成透明度遮罩，再与下层画面合成 |
| 调整图层 | 对其覆盖范围下方的画面统一施加效果 | 统一调色、加全局风格或模糊 | 新建调整图层放在上方轨道并添加效果 | 渲染时先合成下方层，再以调整图层效果处理该合成结果 |
| Warp Stabilizer 防抖 | 分析镜头晃动并稳定运动 | 改善手持素材、减轻微抖 | 对片段添加 Warp Stabilizer，选择稳定程度及裁切方式 | 估计画面运动轨迹并对各帧施加反向变换，通常需要缩放或裁切边缘 |
| 自动重构图 | 改变序列画幅时自动跟随主要对象 | 从横屏版本制作竖屏、方屏版本 | 对序列应用 Auto Reframe 并检查关键帧 | 分析画面显著主体的位置，生成裁切窗口随时间移动的路径 |
| 360°／180° VR 编辑 | 解释、预览并导出全景或立体视频 | 编辑沉浸式素材并保持正确投影与空间方向 | 设置 VR 属性，在源／节目监视器用 VR Viewer 观察，应用沉浸式效果并勾选 VR 导出信息 | 按投影、视角和立体布局解释画面；导出时附带供播放器识别的 VR 元数据 |

### 4. 调色与画面监看

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| Lumetri Color | 集中完成基础校正和创意调色的面板／效果 | 调曝光、白平衡、对比、饱和度、曲线、色轮等 | 在 Lumetri 面板调节片段或调整图层 | 依所设参数对像素亮度与颜色作有序变换，实时生成预览 |
| 自动校色与比较视图 | 给素材建立初步色彩平衡并对照参考镜头 | 快速起调、匹配相邻镜头 | 使用 Auto Color／Color Match；在 Comparison View 查看当前帧与参考帧 | 分析亮度与颜色分布提出校正参数，编辑者再按内容修正；比较视图并列显示两帧 |
| LUT 应用 | 使用颜色查找表映射输入颜色到输出颜色 | 还原 Log 素材、统一视觉风格 | 在输入 LUT 或创意 Look 中载入、调强度 | 查找表依据预设映射关系转换颜色值；需匹配素材和工作色彩空间 |
| 色彩管理 | 为素材、序列和显示设备设定色彩空间及转换 | 混剪 Log、RAW、HDR 和 SDR 素材 | 在序列与 Lumetri 色彩设置中指定或覆盖输入／工作／输出色彩空间 | 根据色彩元数据及配置进行色域、传递函数和色调映射转换 |
| 色彩匹配与局部选色 | 比较镜头并针对颜色范围调整 | 统一不同机位肤色、环境色 | 使用 Color Match、HSL Secondary、曲线和色轮 | 根据参考镜头或色相／饱和度／亮度范围求调整，再局部应用 |
| 视频示波器 | 用客观图形显示亮度与颜色分布 | 判断曝光、色偏、过曝和播出安全范围 | 打开 Lumetri Scopes，选择波形、矢量示波器等 | 对当前视频帧像素取样并映射到亮度、色度等坐标，而非只依赖显示器观感 |
| HDR 监看与交付 | 在受支持的显示环境中制作 HLG／PQ 等高动态范围内容 | 保留高光与宽色域信息，制作 HDR 与 SDR 版本 | 设置序列输出色彩空间、监看设备与 HDR 导出参数，检查示波器及成片 | 将素材转换到序列工作空间和目标输出空间；显示链与文件元数据都需匹配才能正确呈现 |

### 5. 音频处理

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 音频轨道与声道 | 在时间轴安排单声道、立体声或多声道音频 | 分开对白、音乐、环境声和效果声 | 将音频放到 A 轨，设置声道映射和轨道类型 | 各轨按时间混合，按声道配置输出到相应通道 |
| 源声道映射与子混音 | 将录音文件声道分派到剪辑轨，并把多条轨道汇入子混音 | 拆分多麦克风采访、统一处理对白或音乐组 | 在修改音频声道中设映射；创建 Submix 并指定轨道输出 | 源声道先映射为片段声道，轨道信号再经路由进入子混音与主输出 |
| 音量与声像自动化 | 让响度和左右声场随时间变化 | 淡入淡出、对白避让、声音移动 | 拖动音量橡皮筋、添加关键帧，或在混音器中调整推子／声像 | 对音频采样乘以随时间变化的增益，再按声像分配到输出声道 |
| 音频增益、响度匹配与测量 | 调整输入增益并检查整体响度 | 统一不同录音电平、符合交付响度要求 | 设置 Audio Gain、在基本声音中 Auto-Match，使用 Loudness Meter 和音频表监看 | 增益改变电平；响度算法按时间统计感知能量，并给出调整或测量结果 |
| 基本声音面板 | 按对白、音乐、音效、环境声分类处理 | 快速统一响度、降噪、做音乐闪避 | 给片段指定音频类型，调对应控件 | 把较复杂的音频处理链及参数包装成按类型的工作流 |
| 自动闪避与对白修复 | 让背景音乐为对白让位，并减轻噪声／混响 | 提升解说可懂度、减少逐段手绘音量线 | 在基本声音面板启用 Ducking、降噪、去混响并检查参数 | 分析对话区间生成音乐音量关键帧；修复处理按信号特征抑制噪声与房间反射 |
| 增强语音 | 用 AI 改善录音中的讲话清晰度 | 降低背景噪声、突出人声 | 在基本声音面板或相关语音控件中启用并调强度 | 模型区分语音与噪声成分，重建或强调语音；强度过高可能带来伪影 |
| 音频效果 | 压缩器、均衡器、限幅器、混响等处理 | 控制动态、修音色、造空间感 | 在片段或轨道效果槽中添加并调参数 | 按滤波、动态处理等信号算法逐采样或逐块处理声音 |
| 音轨混音器 | 轨道级音量、声像、效果与路由界面 | 统一控制整条轨道并完成混音 | 使用推子、旋钮、插入效果和自动化模式 | 先汇总轨道内片段，再应用轨道处理与总线混合 |
| Remix 音乐重定时 | 自动把音乐重排到指定时长 | 让配乐贴合剪辑而不过度拉伸 | 对音乐片段启用 Remix 并拖到目标长度 | 分析节奏和重复结构，在合适处剪接音乐片段以匹配时长 |
| 音频转场与交叉淡化 | 在片段边界平滑连接声音 | 消除突兀切口、制作音乐及环境声过渡 | 对音频切点应用 Constant Power 等交叉淡化或单侧淡入淡出 | 在过渡区间用增益曲线混合相邻音频采样，避免电平骤变 |
| 画外音录制 | 在时间轴上直接录人声 | 旁白、临时配音 | 在目标音轨的录音控件中选择麦克风并录制 | 录入声卡采样形成音频文件，并与播放头时间位置对齐 |
| 音频同步 | 将外录音与摄影机画面匹配 | 双系统录音、访谈同步 | 选择片段后按音频波形或时间码同步 | 比较波形共同特征或相同时间码，求得音视频时间偏移 |
| 批量静音／哔声处理 | 按识别出的词句对多处对白消音或加哔声 | 快速处理敏感词和待删语句 | 结合文本搜索与音频清理命令，预览并修正命中范围 | 把词语时间戳映射到音频时间区间，在相应区间应用静音或替换声音 |

### 6. 文字、字幕与图形

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 文字与形状图形 | 可在画面上编辑的标题、字幕条、图形对象 | 片头、姓名条、信息图 | 文字工具在节目监视器点击输入，调整字体、填充、描边、阴影和形状 | 图形对象作为透明图层在序列时间范围内栅格化并与视频合成 |
| 文字样式、对齐与响应式图形 | 管理标题层级、布局约束与可伸缩的片头片尾 | 统一系列节目包装、替换文字后保持版式 | 在属性面板设置样式、对齐／分布、文本框与响应式时间区域 | 样式复用排版参数；响应式时间区域在改变图形时长时保留指定入场／出场动画 |
| 图形动画 | 让标题或形状的属性随时间变化 | 文字入场、强调、下三分之一动画 | 对位置、不透明度等加关键帧，或应用动画预设／转场 | 图形参数按时间插值，每帧重新排版或绘制 |
| 动态图形模板 | 可复用且暴露可编辑参数的图形模板 | 批量保持品牌样式、快速替换文本 | 导入并使用 `.mogrt`，在“属性／基本图形”中填内容 | 模板封装动画与可编辑控件，实例只保存用户改动的参数 |
| 模板媒体替换与数据驱动图形 | 把图片／视频占位符或数据字段交给剪辑者替换 | 大批量制作同一包装的不同人物、数字和品牌版本 | 在 MOGRT 中替换媒体或更新暴露的数据控件 | 模板保留动效与版式，替换的媒体和数据被传入模板参数重新渲染 |
| 语音转文字 | 自动把对白转换为带时间信息的文本 | 搜索采访、文本剪辑、制作字幕 | 在文本面板转录序列或导入时自动转录；断网前安装所需语言包 | 本机语音识别模型把音频解码为词语及时间戳，结果可人工修订 |
| 自动字幕与字幕编辑 | 将转录分段成可显示的字幕块 | 生成可读字幕、修改断句与样式 | 从转录创建字幕轨，改文本、时长、位置与样式 | 依语音时间戳生成字幕片段，并按轨道时间渲染或导出为旁挂文件 |
| 逐词字幕 | 每次显示一个随语音同步的词 | 制作节奏较快的社交短视频字幕 | 从转录创建字幕时选择 Single Word 布局，再统一设置字幕样式 | 按词语时间戳生成一个词一个片段的字幕轨，仍可逐条校对 |
| 字幕导入、格式输出与烧录 | 将已有字幕文件放进时间轴，或输出字幕与成片 | 本地制作、校对和交付有字幕的视频 | 导入本地 SRT 等受支持文件；在导出时选择旁挂字幕、支持的嵌入格式或烧录 | 以时间码对齐字幕片段；旁挂文件保存文本与时间码，烧录则把字形合成进视频帧 |

### 7. 本地导出、工程交接与应用联动

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理 |
|---|---|---|---|---|
| 导出媒体 | 将序列渲染、编码为可播放文件 | 交付母版或平台视频 | 打开“导出”，选择格式、预设、分辨率、码率和范围 | 解码各源媒体、合成效果与音频，再按编码器和封装格式写出文件 |
| 快速导出与自定义预设 | 用简化入口输出常用文件，或保存复杂交付参数 | 快速审片、重复交付固定规范 | 使用 Quick Export；在导出设置或预设管理器保存格式、码率、音频及字幕配置 | 预设复用编码参数，导出时按当前序列与设置生成具体输出任务 |
| 导出预设与批量队列 | 保存输出参数并将多个任务排队 | 快速产出多平台版本 | 选择预设，发送到 Adobe Media Encoder 队列 | 各队列任务共享工程数据但使用自己的编码与目标设置 |
| 硬件加速与智能渲染 | 借助受支持的 GPU／编解码硬件或复用可兼容的源媒体数据 | 提升播放与导出效率、减少不必要的重编码 | 在播放引擎、解码／编码与导出设置中检查硬件选项和支持格式 | 硬件单元处理可加速的编解码或效果；智能渲染只在源与输出规格满足匹配条件时复制兼容数据 |
| 帧／静帧导出 | 把某一时间点的画面输出为图片 | 缩略图、审稿截图 | 在节目监视器“导出帧”，设置格式和路径 | 渲染播放头所在的最终合成帧并编码为图像 |
| 多画幅本地导出 | 依据交付场景制作横屏、竖屏、方屏等文件版本 | 在本地交付不同画幅和码率的成片 | 使用序列副本、自动重构图和导出预设 | 同一源编辑在不同序列规格下重排、裁切并分别编码为本地文件 |
| Productions 多项目工程 | 将大型工作拆成多个相互引用的本地项目 | 分场景、分集管理长片工程 | 在本地磁盘上创建 Production，按模块建立多个项目 | 多个 `.prproj` 保持独立并引用本地媒体；无需使用云端项目服务 |
| Dynamic Link | 与 After Effects 等应用共享合成的链接 | 使用复杂动效且无需每次手动导出中间片 | 将 AE 合成链接到 Premiere 序列 | Premiere 请求关联应用渲染合成帧，源合成变动后可更新预览 |
| Audition／Photoshop／Illustrator 联动 | 将声音与图形资产送入专业应用处理或读入其源文件 | 精修混音、编辑分层图片与矢量图 | 发送序列到 Audition；导入 PSD／AI；按支持的工作流重新载入修改 | 交换媒体、时间轴或应用专用链接；往返时各应用只保留共同支持的参数 |
| 项目交换格式 | 与其他剪辑／声音系统交换时间轴文件 | 本地调色、声音后期和归档交接 | 按各格式受支持的方向交换 OTIO、AAF、EDL、Final Cut Pro XML 等本地文件；[OMF 仅导出](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/export-omf-files-for-pro-tools.html)，不能导入 Premiere | 把剪辑点、片段引用及可表达的元数据序列化；复杂效果未必能完整往返 |

### 离线 Beta 能力

| 功能 | 是什么 | 可以干什么 | 具体使用形态 | 工作原理与离线依据 |
|---|---|---|---|---|
| Color Mode（Beta 27.0） | 独立于 Edit 模式的调色工作界面 | 在 Clip Grid 中逐镜头校正，并对片段、组或序列设置调色操作 | 在 Premiere (beta) 顶部切换 Color，使用 Color Controls 与专用监看布局 | 调色参数在本机项目与像素渲染链中生效；Adobe 的该工作流未列云处理步骤，26.x 正式版尚不能完整读取其新效果 |
| Enhance Audio：V1 本机模式（Beta 27.0） | 把混合录音分离为对白、音乐、环境声等层并使用本机增强语音模型 | 分别调节或静音各层，必要时拆成独立时间轴片段 | 在属性面板的 Enhance Audio 中明确选择 **Enhance Speech V1**，再调各层音量 | Adobe 明确说明声源分离在本机进行，V1 模式也完全在本机运行；其他模型模式不适用于本清单 |
| 新音频拉伸模式（Beta 27.0） | 对对白或音乐提供不同质量的变速／保音高算法 | 拉长或缩短音频片段时减轻变调及伪影 | 在 Speed/Duration 中选择 Legacy、Medium 或 High Quality | Medium／High 在本机渲染拉伸后的音频；Adobe 描述了本地渲染流程，但未单独发布断网实测声明 |
| Audio Leveling（Beta 27.0） | 分析对白并自动写入音量关键帧 | 均衡忽大忽小的讲话电平 | 在基本声音面板的 Loudness 区对对白片段启用，再手动修改生成的关键帧 | 分析结果成为片段上的本地可编辑关键帧；Adobe 未单独发布断网实测声明 |

## 二、特性清单

这些条目侧重“使用体验和工作方式”，可用于界面设计或需求拆解。

| 特性 | 界面／操作中的具体表现 | 带来的效果与边界 |
|---|---|---|
| 直接拖拽媒体进轨道 | 从项目面板把视频、音频或图片拖到时间轴目标轨道；也可将项目素材拖到空时间轴以创建序列 | 所见即所得地决定落点、轨道和叠放关系；最终位置受轨道状态、拖拽方式和序列设置影响 |
| 拖拽效果与转场 | 把效果拖到片段，把转场拖到切点 | 直接把处理器绑定到目标对象；转场需要足够的素材余量才能完整展开 |
| 时间轴吸附 | 移动或修剪时靠近切点、标记、播放头会自动对齐 | 降低留缝和错位概率，可按需开关 |
| 源监视器与节目监视器并列 | 一个看原始素材，一个看当前序列合成结果 | 方便挑源入出点，并立即检查剪辑上下文 |
| 非破坏性编辑 | 裁切、效果、关键帧通常保存为工程指令 | 原文件保持不变；要共享工程还需附上源素材或正确重链接 |
| 可视化轨道层级 | 视频轨上下叠放，音频轨独立排列 | 一眼看出覆盖、画中画和音画同步关系；轨道多时需管理可见性 |
| 源修补与目标轨分开控制 | 在时间轴左侧分别指定源素材的音视频入口和编辑命令的目标轨 | 精确控制三点编辑、粘贴、导航与音画落点 |
| 时间轴橡皮筋 | 片段上直接显示音量、不透明度或速度曲线 | 不离开时间轴即可拖拽设值或加关键帧 |
| 关键帧双入口 | 效果控件面板可精确输入数值，时间轴可直接拖动关键点 | 同一动画可用参数方式精调，也可按节奏快速调整 |
| 原位预览与实时回放 | 在节目监视器播放叠加效果后的结果 | 快速发现节奏与合成问题；性能取决于硬件、编解码器和效果复杂度 |
| 对照监看、辅助线与安全区 | 在监视器比较源／成片、参考帧，显示导引线与安全范围 | 帮助调色、对齐标题与检查平台裁切；监视叠加层不自动进入最终画面 |
| 播放分辨率可调 | 降低预览分辨率或先渲染预览文件 | 提升编辑流畅度；预览清晰度不等于最终导出质量 |
| 渲染并替换 | 把计算量大的片段临时变为已渲染媒体，必要时恢复原始编辑 | 改善复杂合成回放与部分交接兼容性；应核对缓存文件及可恢复性 |
| 代理与原片切换 | 一键在轻量代理和高质量源媒体之间切换查看 | 剪辑操作保持对应关系，便于兼顾流畅度与质量检查 |
| 轨道锁定、隐藏与静音 | 在轨道头部控制是否可改、可见、可听 | 保护已完成部分，隔离画面或声音问题 |
| 全局静音与逐轨独奏 | 可暂时关掉整个序列声音，或只听选中的音轨 | 在检查画面、对白或配乐时快速隔离声音；不改片段音量参数 |
| 可自定义工作区 | 拖放面板、更改布局，保存剪辑／调色／音频等布局 | 让当前任务所需面板保持可见 |
| 快捷键可自定义 | 将常用编辑命令绑定到键盘 | 提升重复操作速度，便于沿用个人或团队习惯 |
| 撤销／重做与历史 | 连续回退或重做近期编辑步骤 | 方便试错；历史容量和跨会话保留方式受应用设置影响 |
| 多选、复制粘贴属性 | 对多个片段批量应用或复制效果参数 | 减少逐片设置；仍需核对不同素材上的视觉结果 |
| 上下文属性面板 | 选中视频、音频、文字或图形时显示相应常用控件 | 减少在多个面板间寻找参数；深入处理仍可进入专用面板 |
| 面板内搜索与素材智能搜索 | 在工程、效果、文字或 Search 面板按名称、转录或语义查找 | 缩短大量素材的定位时间；AI 搜索结果需要人工复核 |
| 自动保存与恢复 | 周期性保存工程副本 | 降低崩溃损失，仍应管理素材备份和版本 |
| 多格式混合时间轴 | 在同一序列放入不同分辨率、帧率、摄像机格式的素材 | 减少预转码步骤；时间基准和色彩空间差异需检查 |
| 动态音频波形 | 时间轴波形可反映片段音量调整并随缩放更新 | 更直观地看出电平变化与切点；最终响度仍须用仪表核查 |
| 键盘与辅助功能 | 提供可定制键盘导航及受支持的屏幕阅读器／放大器支持 | 便于高频操作和辅助技术使用；具体覆盖范围依平台和版本而定 |
| 本机 Adobe 应用联动 | 可调用已安装在本机的 After Effects、Audition 和 Media Encoder | 减少手动生成中间文件；可用能力受本机安装版本及兼容性影响 |
| 本机 AI 与传统剪辑并存 | 本地转录、媒体搜索、自动重构图和对象蒙版与轨道编辑并列 | 断网前准备好模型与语言包后，可在同一工程内继续使用自动分析和手工调整 |

## 三、界面详解：从素材到成片

Premiere 的主界面围绕 **Import → Edit → Export** 三个阶段组织。Import 用于选本地文件与决定摄取方式；Edit 是多面板剪辑工作区；Export 用于选择成片范围、格式、音视频设置和本地文件位置。面板可停靠、分组、放大与保存为工作区，因此实际布局可与下述默认布局不同。下表按“看见什么、怎样操作、影响什么”解释界面，而不是把一个按钮当作一种新功能。参见 Adobe 的[工作区说明](https://helpx.adobe.com/premiere/desktop/get-started/tour-the-workspace/what-are-workspaces.html)与[源／节目监视器说明](https://helpx.adobe.com/premiere/desktop/get-started/source-and-program-monitor-adjustments/about-source-monitor-and-program-monitor.html)。

| 区域 | 主要可见内容 | 典型操作与反馈 | 实际改变的数据 |
|---|---|---|---|
| Import 界面与媒体浏览器 | 本机文件夹、媒体缩略图、目标项目／素材箱及摄取选项 | 选择文件后可先预览，再决定导入、复制、转码或建代理；进度面板显示处理状态 | 向项目添加媒体引用，并可能在指定本地路径创建副本、转码文件或代理 |
| 项目面板（Project） | 素材箱、片段、序列、标签、缩略图、元数据列及离线状态 | 切换列表／图标／Freeform，按名称或属性筛选，双击素材送入源监视器 | 修改项目结构、名称、标记、标签或媒体关联；不移动时间轴上的既有切点 |
| Search 面板 | 文字查询、语义候选、音频／标记命中与过滤条件 | 用英文描述视觉内容、点相似镜头、跳到命中时间点；检查候选是否准确 | 搜索本机分析缓存与项目元数据；通常不修改媒体或序列 |
| Sequence Index | 当前序列片段的表格列表、名称和可搜索定位结果 | 在长时间轴中查找对象并跳到它的时间位置 | 只改变当前查看与播放头导航，不改片段 |
| 源监视器（Source） | 单一源片段、源时间码、播放控制和入出点 | 用 J/K/L 浏览，按 I/O 圈定可用范围；拖视频图标或音频图标可只送对应媒体 | 改变将被插入的源时间范围；源文件仍保持完整 |
| 节目监视器（Program） | 当前序列的合成画面、序列时间码、播放与画面操作控件 | 按播放头检查成片；直接拖动画面对象、看安全区或参考画面 | 对所选片段／图形写入位置、缩放等参数，或只改变监看辅助显示 |
| 时间轴（Timeline） | 上下叠放的 V 轨、分开的 A 轨、时间标尺、播放头、片段边界、波形与效果徽章 | 拖动、剃刀切分、吸附、修剪和加关键帧；片段移动后的时长与空隙立即可见 | 改写片段在序列中的位置、源入出点、轨道、转场和参数，保存为编辑指令 |
| 时间轴轨道头 | 源修补、轨道目标、锁、同步锁、视频可见性、音轨静音／独奏等开关 | 编辑前确认蓝色源／目标标识和锁定状态；发现音画错轨时检查此处 | 决定后续命令触及哪条轨、哪些片段可移动或参与回放 |
| 属性面板与效果控件 | 当前对象的常用属性、效果栈、秒表、数值、关键帧时间线和插值 | 选片段后改数值或直接拖画面；启用秒表、移动播放头并设下一数值 | 写入片段／图形属性与随时间变化的参数；效果栈顺序影响结果 |
| 文本、图形与字幕面板 | 转录词句、说话人、字幕块、图形层与样式 | 搜词跳时间点，删停顿、从转录建序列，创建逐词或分句字幕并校对 | 将文字时间戳转换为剪辑选择或字幕片段；样式决定文字绘制方式 |
| Lumetri Color 与 Scopes | 曝光、色温、曲线、色轮、HSL 二级调色和波形／矢量示波器 | 选镜头调参数，对照参考帧并看亮度／色度范围；确认序列色彩空间 | 对图像像素施加色彩变换；示波器仅测量，不改画面 |
| 基本声音、混音器与音频表 | 对白／音乐类别、修复与闪避控件、轨道推子、路由、电平／响度显示 | 给片段分类、调增益与效果，监听并查看峰值及响度 | 改变片段与轨道的增益、效果、声道和输出路由；仪表仅读数 |
| Export 界面 | 文件名、位置、格式、预设、范围、视频／音频／字幕设置与预览摘要 | 选择本地媒体文件作为目的地，核对源与输出参数，直接导出或交给本机 Media Encoder 排队 | 读取现有序列并生成新的本地视频、音频、图片或字幕文件 |

### 关键面板与控件的用法

**Import 与项目面板。** Import 的任务是把本地媒体加入项目，而项目面板负责长期整理。导入时先核对文件路径、素材目标箱和是否需要复制／代理；若只做快速剪辑，可直接保留源文件引用。项目面板的 **List** 视图适合核查帧率、媒体起止时间和状态，**Icon** 视图适合扫缩略图，**Freeform** 视图适合把候选镜头按场景自由排放。项目条目是媒体和序列的入口，移到素材箱或改标签并不会自动改变它已在时间轴中的位置。参见[项目面板自定义](https://helpx.adobe.com/premiere/desktop/get-started/customize-the-project-panel/customization-options-for-the-project-panel.html)。

**双监视器与轨道头。** 源监视器的时间码属于当前源片段，节目监视器的时间码属于当前序列；同一个画面出现在两处时，数值也可能不同。按 `I`／`O` 标记源入出点后，还要看时间轴左侧的 **Source Patching**，它决定源视频和源音频各进入哪条轨；**Track Targeting** 决定许多粘贴、导航与编辑命令作用的轨道。随后用插入或覆盖编辑把选区放到播放头附近：插入推开后续内容，覆盖保持后续内容位置。时间轴底部的缩放和滚动只改变观察范围，不改变剪辑时长。参见[源修补](https://helpx.adobe.com/premiere/desktop/edit-projects/intro-to-editing/add-media-to-the-timeline-using-source-patching.html)与[轨道目标](https://helpx.adobe.com/premiere/desktop/edit-projects/intro-to-editing/work-with-clips-on-the-timeline-using-track-targeting.html)。

**属性面板与效果控件。** 属性面板随着当前选中的视频、音频、文字或字幕改变，便于直接修改常用属性；效果控件则呈现所选片段的效果栈、关键帧时间线和更细的参数。一个常用动作是先在节目监视器拖动标题位置，再在属性面板输入精确数值；如果希望标题逐渐移动，就在效果控件中启用位置秒表、设起点和终点。标准效果的先后顺序可能影响结果，复制粘贴属性时应检查接收片段的尺寸、颜色空间与原片差异。参见[属性面板](https://helpx.adobe.com/premiere/desktop/add-text-images/stylize-text/about-properties-panel.html)与[效果控件](https://helpx.adobe.com/premiere/desktop/add-video-effects/apply-video-effects/about-effect-controls-panel.html)。

**文字、音频和调色面板。** 文本面板的 **Transcript** 是与语音时间点关联的转录，**Captions** 是独立的字幕轨内容，**Graphics** 便于查找与修改序列内图形文字；三者的数据用途不同。音频的 **Essential Sound** 按对白、音乐、音效、环境声提供任务化控件，**Audio Clip Mixer** 调当前片段，**Audio Track Mixer** 调整条轨及其路由，音频表用于发现峰值和响度问题。正式版 26.x 在 **Lumetri Color** 中调基础校正、曲线、色轮与 HSL，**Lumetri Scopes** 用波形、RGB 分量和矢量图检查结果；Beta Color Mode 则提供 Color Monitor、Clip Grid、Color Controls 与 Video Scopes 的独立布局。参见[基本声音面板](https://helpx.adobe.com/premiere/desktop/add-audio-effects/adjust-volume-and-levels/audio-editing-with-essential-sound-panel.html)、[音轨混音器](https://helpx.adobe.com/premiere/desktop/add-audio-effects/advanced-audio-techniques/about-audio-track-mixer.html)、[Lumetri 工作流](https://helpx.adobe.com/premiere/desktop/correct-color/color-correction-fundamentals/color-correction-workflow.html)与[Beta Color Mode](https://helpx.adobe.com/premiere/desktop/correct-color/color-mode-fundamentals/color-mode-basics.html)。

**Export。** 左侧只启用本地 **Media File** 目的地，中央先核对文件名、路径、格式和预设，再展开 **Video／Audio／Captions** 核对画幅、帧率、色彩、码率、声道布局和字幕方式。右侧预览与源／输出摘要帮助发现分辨率或音频设置误差。按 Export 在本机编码，或把任务加入已安装的 Media Encoder 本地队列；后者适合多版本批量编码。参见[本地导出](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/export-video-and-audio-files.html)。

### 一个离线剪辑在界面中的流转

1. 在 **Import** 选本地相机文件，按需创建本地代理；导入后，项目面板出现媒体条目，源文件仍留在原路径。
2. 在**项目面板**按场景建素材箱、查看帧率和音轨；在 **Search** 中查本地镜头，或双击到**源监视器**人工选入出点。
3. 把源监视器选区通过**源修补**送到目标 V/A 轨；在**时间轴**确定切点，再用波纹、滚动或滑移精修。**节目监视器**显示当前序列的合成结果。
4. 在**属性／效果控件**修正位置、缩放、防抖与关键帧；在 **Lumetri** 校正素材色彩，并用 **Scopes** 检查曝光和色偏。
5. 在**文本面板**使用已安装语言包生成转录，按词剪掉停顿并创建字幕；在**基本声音**中处理对白，调整配乐与轨道响度。
6. 在 **Export** 核对序列范围、画幅、帧率、色彩空间、码率、声道和字幕处理方式，导出到本地磁盘并复看文件。

## 四、原理详解：Premiere 怎样生成一帧画面和一段声音

下面描述可用于理解功能关系的**概念流程**。Premiere 的实际缓存、GPU 调度及各效果内部算法会因版本、媒体格式和硬件而变化；Adobe 未公开完整实现。关于非破坏性工程、代理、时间轴、色彩与导出，可对照[项目说明](https://helpx.adobe.com/premiere/desktop/organize-media/create-projects/start-projects.html)、[代理工作流](https://helpx.adobe.com/premiere/desktop/organize-media/ingest-proxy-workflow/ingest-and-proxy-workflow.html)和[导出设置](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/overview-of-export-settings.html)。

```text
本地媒体文件 → 解码／代理与媒体缓存 → 项目中的媒体引用
             → 序列中的片段、源入出点和轨道位置
             → 图像变换、效果、蒙版、转场、色彩处理与图层合成
             → 每一帧视频 + 逐轨混合的音频 + 字幕
             → 预览缓存或本地编码文件
```

### 1. 项目保存的是编辑决定

`.prproj` 主要保存媒体路径、素材箱、序列、剪辑点、效果参数、关键帧、标记等。把一段采访剪成十个片段，通常是建立十个指向源文件不同时间范围的对象，而不是把原文件物理切成十份。因此改切点、撤销和重新排列都很快；源媒体搬走后，工程仍可打开，但相应条目会离线，需要重链接。项目管理器可收集实际使用的媒体以便本地交接。代理、预览文件和媒体缓存是不同的辅助文件，不等同于工程本身。

### 2. 时间轴把“序列时间”映射到“源时间”

序列有自己的帧率和时间码。第 `n` 帧的名义序列时刻可写作 `t = n / F_seq`，其中 `F_seq` 是序列每秒帧数。普通速度片段把这一时刻映射到源片段的入点加经过时长；以 2 倍速度播放时，序列中过去 1 秒大约对应源文件前进 2 秒。变速、倒放和时间重映射会改变这条映射曲线。插入编辑改变后续对象在**序列时间**的位置，覆盖编辑主要替换指定区间；滑移编辑只改所引用的**源时间**。不同帧率素材放到同一序列时，Premiere 需要按序列的出帧时刻选择、混合或合成源帧。时间码的显示格式只影响编号方式；29.97 fps 的 drop-frame 时间码跳过的是某些**号码**，不是画面帧。参见[序列设置](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/sequence-settings-reference.html)、[时间码格式](https://helpx.adobe.com/premiere/desktop/organize-media/apply-labeling/choose-timecode-display-format.html)与[时间插值](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-speed/apply-time-interpolation-methods-to-adjust-clip-speed.html)。

### 3. 解码、代理与预览各解决不同问题

播放时软件先按源格式解码所需画面／音频，读取媒体缓存中的索引、波形或缩略图，并应用时间轴处理。高码率、复杂压缩或多机位素材可能使解码跟不上播放；**代理**是与原片对应的低负担替代媒体，切回原片后剪辑决定仍对应同一源时间范围。**预览渲染**把已计算的序列区间暂存为文件，减少重复计算；改变相关效果后预览可能失效。**Render and Replace** 则把指定片段临时替换为已渲染媒体，保留恢复原始编辑的路径。降低播放分辨率只影响监看负担，不自动降低最终输出规格。参见[代理导出说明](https://helpx.adobe.com/premiere/desktop/organize-media/ingest-proxy-workflow/export-proxies.html)。

### 4. 本机分析把媒体变成可检索、可编辑的数据

媒体智能搜索在本机分析画面和音频，建立与片段时间位置关联的索引；搜索词经过同样的本机语义处理后与索引比对，返回候选区间，而不是替用户作最终选片决定。分析数据通常进入媒体缓存，也可按支持的方式保存为随媒体移动的 `.prmi` 附带数据。语音转文字使用预先安装的语言包，把声音转为带词语时间戳的文本；对象蒙版使用预先下载的模型识别主体，跟踪步骤再沿时间更新蒙版。此类分析数据可以重新生成，但重分析要花时间，搬工程时应确认缓存或附带数据位置。参见[媒体智能搜索](https://helpx.adobe.com/premiere/desktop/organize-media/file-organization/media-intelligence-and-search-panel.html)与[分析元数据管理](https://helpx.adobe.com/premiere/desktop/organize-media/file-organization/manage-media-intelligence-metadata.html)。

### 5. 单帧画面来自多层处理与合成

对某一输出帧，Premiere 找出这个时刻可见的片段，从各自源文件取得对应帧，应用尺寸／位置／旋转、片段效果、蒙版与不透明度，再按 V 轨层级合成。位于较高视频轨的非透明区域通常盖住下层。转场在一段时间内同时读取切点两侧可用源帧，并计算过渡结果；没有足够素材余量时，转场范围会受限。调整图层把效果施加到其覆盖的下方合成画面。关键帧为参数提供随时间变化的值，线性、保持或贝塞尔插值决定两点之间每帧使用的参数。蒙版则为每个像素提供效果权重，跟踪工具使权重区域随对象移动。具体效果的处理次序以实际效果控件与预览为准。参见[合成概览](https://helpx.adobe.com/premiere/desktop/add-video-effects/work-with-composites/compositing-overview.html)与[关键帧说明](https://helpx.adobe.com/premiere/desktop/add-video-effects/control-effects-and-transitions-using-keyframes/about-keyframes.html)。

### 6. 色彩处理需对应输入、工作和输出空间

相机文件可能是 Rec. 709、Log、RAW 或 HDR。色彩管理先根据文件元数据或人工覆盖的设置解释素材，再变换到序列工作空间；Lumetri 的曝光、曲线、色轮、HSL Secondary 或 LUT 在已选流程中调整像素；导出时再变换到目标输出色彩空间。把不匹配的 LUT 当作“万能调色”可能导致偏色或高光损失。波形图和矢量示波器是当前帧的数值视图，能帮助判断曝光与饱和度，但不能替代正确配置的显示器。Beta Color Mode 重新组织了这些调色操作和面板，26.x 的 Lumetri 效果与 Beta 新效果应分别按其版本检查。参见[色彩管理选项](https://helpx.adobe.com/premiere/desktop/correct-color/set-up-color-management/color-management-options.html)。

### 7. 音频以采样、声道和路由形成最终混音

源音频有自己的采样率与声道布局。Premiere 在序列时间上对齐音频，把源声道映射到片段和轨道，经过片段增益、音量关键帧与音频效果，再由轨道混音器汇入子混音和主输出。交叉淡化在两个片段交界处改变相邻声音的增益权重；自动闪避根据对白位置生成配乐音量变化；Remix 通过选择合适的音乐片段边界重排结构。峰值电平显示瞬时幅度，响度仪表统计一段时间的感知电平，二者用于不同的检查。Beta Audio Leveling 写出可编辑关键帧，和压缩器持续处理采样的方式不同。参见[音频编辑概念](https://helpx.adobe.com/premiere/desktop/add-audio-effects/basic-audio-editing/audio-editing-concepts.html)、[子混音](https://helpx.adobe.com/premiere/desktop/add-audio-effects/advanced-audio-techniques/create-a-submix.html)与[响度仪表](https://helpx.adobe.com/premiere/desktop/add-audio-effects/apply-audio-effects/about-loudness-meter.html)。

### 8. 转录、字幕与导出是三层不同数据

本机语音识别把音频转换为带时间戳的**转录文本**。基于文本的剪辑通过时间戳把选中的词句换算为源片段范围，再写入时间轴；这一步改变剪辑决定。**字幕轨**则保存要显示的文本、起止时间与样式，通常不改变原有切点。导出时可以把字幕烧录进视频帧，或在受支持格式中生成独立字幕文件。最终导出会逐帧解码、合成、混音，再由编码器按所选格式、码率、分辨率与声道布局写到本地；能否使用硬件编码或智能渲染取决于硬件和源／目标格式是否满足条件。参见[文字剪辑](https://helpx.adobe.com/premiere/desktop/edit-projects/edit-video-using-text-based-editing/overview-of-text-based-editing.html)、[字幕概览](https://helpx.adobe.com/premiere/desktop/add-text-images/insert-captions/about-captions.html)与[智能渲染](https://helpx.adobe.com/premiere/desktop/render-and-export/render-sequences-for-playback/smart-rendering-supported-formats.html)。

## 五、关键帧的完整示例

**场景：让一个视频片段在 2 秒内缓慢放大。**

1. 将片段放到视频轨，选中它，打开“效果控件”。
2. 在片段起始处找到“运动 → 缩放”，开启秒表，设为 **100%**。此时生成第一个关键帧。
3. 把播放头移到该片段内的第 2 秒，把缩放设为 **150%**。此时生成第二个关键帧。
4. 播放时，Premiere 在两关键帧之间计算每帧缩放值；如果用线性插值，放大速度大体均匀。如果改为缓入缓出／贝塞尔曲线，起止速度会更平顺。
5. 若希望放大中心落在人物脸部，可同时修改锚点或位置，并分别为这些属性加关键帧。时间轴上的关键点控制“何时”，属性数值控制“到哪里／变成多少”，插值方式控制“中间怎样变化”。

关键帧不是一种独立的视觉特效，而是**驱动参数随时间变化的机制**。它可用于视频效果，也可用于音量等音频参数。只有一个关键帧时，该属性通常只是保持某个值；至少两个不同时间和值的关键帧才会形成可见或可听的变化。

## 六、离线边界与版本核对

- **运行条件**：Premiere 和需要的本机 Adobe 应用、媒体、字体、LUT、MOGRT、代理、AI 模型及转录语言包应提前安装或下载。Creative Cloud 的首次安装、激活和后续订阅验证属于软件使用前提，不是表内编辑功能。离线转录的依据见 Adobe 的[语音转文字说明](https://www.adobe.com/products/premiere/speech-to-text.html)；AI 媒体搜索的分析与缓存明确在本机进行，见[媒体智能搜索说明](https://helpx.adobe.com/premiere/desktop/organize-media/file-organization/search-for-media-using-ai-powered-media-intelligence.html)。
- **AI 条目**：AI 对象蒙版首次使用时需先下载模型，之后模型在本机使用，见[对象蒙版说明](https://helpx.adobe.com/premiere/desktop/add-video-effects/work-with-masks/object-masking.html)及[Adobe 发布文章](https://blog.adobe.com/en/publish/2026/01/20/new-ai-powered-video-editing-tools-premiere-major-motion-design-upgrades-after-effects)。正式版的 Enhance Speech 在本机运行，见[Adobe 生态文章](https://blog.adobe.com/en/publish/2024/02/22/enhance-your-video-editing-workflows-power-adobe-video-ecosystem)；Beta 的 Enhance Audio 必须选择 **V1 本机模式**，Adobe 在[Beta 公告](https://community.adobe.com/announcements-732/now-in-beta-enhance-audio-in-premiere-1640126)中明确区分了本机与云端模型。
- **版本状态**：截至本文核对日期，Adobe 的[桌面版更新记录](https://helpx.adobe.com/premiere/desktop/whats-new/release-notes.html)列出 26.5 的功能更新及 26.5.2 的修复。上表的 Color Mode 与三项音频新能力属于 Beta 27.0；音频拉伸和 Audio Leveling 的官方介绍描述本机渲染／关键帧流程，但没有单独的断网测试声明，详见[音频拉伸公告](https://community.adobe.com/announcements-732/now-in-beta-new-audio-stretching-modes-in-premiere-1638934)与[Audio Leveling 公告](https://community.adobe.com/announcements-732/now-in-beta-audio-leveling-in-premiere-1640991)。
- **资料边界**：各表的“工作原理”是依据可见工作流总结的产品级解释。实际效果、格式、GPU 加速和画质取决于硬件与版本；逐项兼容性应核对[受支持的导入格式](https://helpx.adobe.com/premiere/desktop/organize-media/import-files/supported-file-formats.html)、[导出格式](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/supported-export-file-formats.html)及[效果与转场清单](https://helpx.adobe.com/premiere/desktop/add-video-effects/effects-and-transitions-library/list-of-effects-and-transitions.html)。

### 按章节查官方说明

| 章节 | 官方资料 |
|---|---|
| 项目与素材 | [帮助中心完整索引](https://helpx.adobe.com/premiere/desktop.html)、[摄取与代理](https://helpx.adobe.com/premiere/desktop/organize-media/ingest-proxy-workflow/ingest-and-proxy-workflow.html)、[项目整合与归档](https://helpx.adobe.com/premiere/desktop/organize-media/create-projects/consolidate-and-archive-projects.html) |
| 时间轴 | [时间轴导航](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/navigation-controls-in-the-timeline.html)、[修剪模式](https://helpx.adobe.com/premiere/desktop/edit-projects/trim-clips/about-trim-mode.html)、[基于文本的剪辑](https://helpx.adobe.com/premiere/desktop/edit-projects/edit-video-using-text-based-editing/overview-of-text-based-editing.html)、[场景编辑检测](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/detect-edit-points-using-scene-edit-detection.html)、[序列索引](https://helpx.adobe.com/premiere/desktop/organize-media/file-organization/navigate-timelines-with-sequence-index.html) |
| 效果与合成 | [关键帧](https://helpx.adobe.com/premiere/desktop/add-video-effects/control-effects-and-transitions-using-keyframes/about-keyframes.html)、[防抖](https://helpx.adobe.com/premiere/desktop/add-video-effects/commonly-used-effects/stabilize-shaky-footage-using-warp-stabilizer.html)、[VR 编辑](https://helpx.adobe.com/premiere/desktop/edit-projects/edit-vr-content/vr-editing-in-premiere.html) |
| 调色 | [色彩管理](https://helpx.adobe.com/premiere/desktop/correct-color/set-up-color-management/color-management-options.html)、[色轮](https://helpx.adobe.com/premiere/desktop/correct-color/add-color-effects/correct-color-using-color-wheel.html)、[Color Mode（Beta）](https://helpx.adobe.com/premiere/desktop/correct-color/color-mode-fundamentals/color-mode-basics.html) |
| 音频 | [基本声音面板](https://helpx.adobe.com/premiere/desktop/add-audio-effects/adjust-volume-and-levels/audio-editing-with-essential-sound-panel.html)、[Remix](https://helpx.adobe.com/premiere/desktop/add-audio-effects/apply-audio-effects/remix-audio-in-premiere.html)、[响度仪表](https://helpx.adobe.com/premiere/desktop/add-audio-effects/apply-audio-effects/about-loudness-meter.html)、[全局静音](https://helpx.adobe.com/premiere/desktop/add-audio-effects/adjust-volume-and-levels/global-mute.html) |
| 图形与字幕 | [动态图形模板](https://helpx.adobe.com/premiere/desktop/add-text-images/use-motion-graphics-templates/about-motion-graphics-templates.html)、[创建字幕](https://helpx.adobe.com/premiere/desktop/add-text-images/insert-captions/create-captions.html)、[逐词字幕](https://helpx.adobe.com/premiere/desktop/add-text-images/insert-captions/create-single-word-captions.html) |
| 本地导出与交接 | [导出视频](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/export-video.html)、[智能渲染格式](https://helpx.adobe.com/premiere/desktop/render-and-export/render-sequences-for-playback/smart-rendering-supported-formats.html)、[Productions](https://helpx.adobe.com/premiere/desktop/collaborate-with-others/collaborate-using-productions/about-productions.html) |

## 七、界面截图

下列三张图取自 Adobe 帮助页，保留原图及其标注，便于对照本文的轨道、关键帧和调色概念。帮助页的截图可能与 26.5 的局部界面不同。来源及署名按 Adobe 的[截图使用准则](https://www.adobe.com/cc-shared/assets/pdf/legal/documents/adobe-trademark-guidelines.pdf)标示：Adobe product screenshot(s) reprinted with permission from Adobe.

**多轨时间轴**：可见 V/A 轨、片段、播放头与时间标尺。[截图来源：Adobe 时间轴导航](https://helpx.adobe.com/premiere/desktop/edit-projects/change-clip-sequence/navigation-controls-in-the-timeline.html)。

![Premiere 多轨时间轴界面](premiere_screenshots/timeline.jpg)

**关键帧**：效果控件中的缩放秒表、数值和关键点，可对照上文的 2 秒放大示例理解控件位置。[截图来源：Adobe 添加关键帧](https://helpx.adobe.com/premiere/desktop/add-video-effects/control-effects-and-transitions-using-keyframes/add-keyframes.html)。

![Premiere 效果控件中的缩放关键帧](premiere_screenshots/keyframes.jpg)

**调色色轮**：阴影、中间调和高光分别调整。[截图来源：Adobe 色轮调色](https://helpx.adobe.com/premiere/desktop/correct-color/add-color-effects/correct-color-using-color-wheel.html)。

![Premiere Lumetri 色轮](premiere_screenshots/color_wheels.jpg)

其他可查看的界面图见 Adobe 的[文字剪辑概览](https://helpx.adobe.com/premiere/desktop/edit-projects/edit-video-using-text-based-editing/overview-of-text-based-editing.html)、[字幕创建](https://helpx.adobe.com/premiere/desktop/add-text-images/insert-captions/create-captions.html)和[导出设置](https://helpx.adobe.com/premiere/desktop/render-and-export/export-files/export-video.html)。这些来源页含完整截图及操作步骤。

## 八、第三方文档与实践文章

下列资料用于对照操作和实际体验；文中软件版本可能早于 26.5，功能可用性以第六节的 Adobe 官方资料为准。阅读资料需要联网，但本文列出的软件操作不依赖这些网站。

| 资料 | 适合核对的离线工作流 |
|---|---|
| [威斯康星大学 Premiere 基础手册](https://sts.doit.wisc.edu/training-materials/manual/premiere-video-editing-basics-with-adobe-premiere/) | 工作区、素材导入、时间轴和本地导出；页面有完整界面截图 |
| [PremierePro.net 多机位技巧](https://premierepro.net/multicam/) | 多机位同步、机位音频选择与切换组织 |
| [Motion Array 速度坡道教程](https://motionarray.com/learn/premiere-pro/premiere-pro-speed-ramping-tutorial/) | 速度重映射、关键帧与平滑变速的实际步骤 |
| [Larry Jordan：Premiere 2025 降噪测试](https://larryjordan.com/articles/how-to-minimize-audio-background-noise-in-adobe-premiere-pro-2025/) | 对白修复、增强语音与原始录音的效果比较 |
| [Larry Jordan：对象蒙版跨软件比较](https://larryjordan.com/articles/compare-ai-assisted-masking-in-final-cut-premiere-resolve/) | AI 对象蒙版的选择精度、边缘与跟踪局限 |
| [Puget Systems：Premiere 硬件建议](https://www.pugetsystems.com/solutions/video-editing/adobe-premiere-pro/hardware-recommendations) | 本机 CPU、GPU、内存与编解码性能的选择 |
