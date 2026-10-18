namespace TANGERINE_VideoEditor.Editor;

/// <summary>
/// Describes an item that can be dragged from the object browser onto the timeline.
/// IDs are stable because project files persist them independently of the display language.
/// </summary>
public sealed record BuiltinPreset(string Id, string Name, string Group, string Description, string AccentHex);

/// <summary>
/// The initial offline preset library. Each preset is immediately available without
/// downloading an asset. The editor can map IDs to richer renderers in later releases.
/// </summary>
public static class BuiltinCatalog
{
    public static IReadOnlyList<BuiltinPreset> Titles { get; } =
    [
        new("title.basic", "基础文字", "基础", "简洁的居中文字", "#8B8CF7"),
        new("title.subtitle", "标准字幕", "字幕", "适合对白的底部字幕", "#8B8CF7"),
        new("title.caption-bar", "字幕条", "字幕", "带背景的字幕条", "#8B8CF7"),
        new("title.lower-third", "人物介绍", "片头片尾", "姓名和说明文字", "#8B8CF7"),
        new("title.opening", "大标题", "片头片尾", "醒目的片头文字", "#8B8CF7"),
        new("title.ending", "片尾字幕", "片头片尾", "结束画面和署名", "#8B8CF7"),
        new("title.typewriter", "打字机", "动态", "逐字出现的文字", "#8B8CF7"),
        new("title.fade", "淡入文字", "动态", "柔和淡入淡出", "#8B8CF7"),
        new("title.slide", "滑入文字", "动态", "从画面边缘滑入", "#8B8CF7"),
        new("title.pop", "弹跳文字", "动态", "快速弹出的文字", "#8B8CF7"),
        new("title.glow", "霓虹文字", "风格", "发光描边文字", "#8B8CF7"),
        new("title.outline", "描边文字", "风格", "带清晰描边的文字", "#8B8CF7"),
        new("title.shadow", "阴影文字", "风格", "带柔和投影的文字", "#8B8CF7"),
        new("title.quote", "引用卡片", "风格", "引言和署名布局", "#8B8CF7"),
        new("title.counter", "数字计数", "信息", "用于数据和倒计时", "#8B8CF7"),
        new("title.date", "日期地点", "信息", "时间地点信息布局", "#8B8CF7"),
        new("title.modern", "现代标题", "现代", "干净的无衬线标题与轻微阴影", "#8B8CF7"),
        new("title.social", "社交媒体标题", "社交", "适合短视频的醒目标题条", "#8B8CF7"),
        new("title.handwritten", "手写标题", "手写", "斜体手写感标题", "#8B8CF7"),
        new("title.callout", "标注底板", "标注", "带底色和内边距的重点标注", "#8B8CF7"),
        new("title.kinetic", "动感标题", "动态", "弹出并滑入的短视频标题", "#8B8CF7"),
        new("title.credits", "滚动片尾", "片头片尾", "适合人员名单的片尾样式", "#8B8CF7"),
        new("title.clean-lower-third", "简洁滑入姓名条", "信息条", "安全边距、底板与从侧边滑入的姓名信息", "#8B8CF7"),
        new("title.box-reveal", "圆角底板淡入", "底板", "带内边距和圆角的文字底板，整体淡入淡出", "#8B8CF7"),
        new("title.outline-pop", "粗描边弹跳标题", "社交", "大字描边、对比色和可调回弹缩放", "#8B8CF7"),
        new("title.crawl", "横向滚动字幕", "滚动", "字幕按设定方向和距离持续横向滚动", "#8B8CF7"),
        new("title.zoom", "缓慢缩放标题", "动态", "从起始比例连续缩放至结束比例", "#8B8CF7"),
    ];

    public static IReadOnlyList<BuiltinPreset> Effects { get; } =
    [
        new("effect.brightness", "亮度", "基础调节", "调整画面亮度", "#F1AE69"),
        new("effect.contrast", "对比度", "基础调节", "调整明暗对比", "#F1AE69"),
        new("effect.saturation", "饱和度", "基础调节", "调整色彩浓淡", "#F1AE69"),
        new("effect.temperature", "色温", "基础调节", "调整冷暖色调", "#F1AE69"),
        new("effect.tint", "色调", "基础调节", "调整绿色与洋红色", "#F1AE69"),
        new("effect.exposure", "曝光", "基础调节", "调整整体曝光", "#F1AE69"),
        new("effect.highlights", "高光", "基础调节", "调整亮部细节", "#F1AE69"),
        new("effect.shadows", "阴影", "基础调节", "调整暗部细节", "#F1AE69"),
        new("effect.vignette", "暗角", "光影", "柔化画面边缘", "#F1AE69"),
        new("effect.glow", "柔光", "光影", "添加柔和光晕", "#F1AE69"),
        new("effect.blur", "模糊", "模糊", "模糊画面", "#F1AE69"),
        new("effect.motion-blur", "运动模糊", "模糊", "增强运动感", "#F1AE69"),
        new("effect.sharpen", "锐化", "细节", "增强边缘清晰度", "#F1AE69"),
        new("effect.grain", "胶片颗粒", "纹理", "胶片质感颗粒", "#F1AE69"),
        new("effect.noise", "噪点", "纹理", "数字噪点纹理", "#F1AE69"),
        new("effect.black-white", "黑白", "风格", "转换为黑白影像", "#F1AE69"),
        new("effect.sepia", "复古褐色", "风格", "怀旧暖褐色调", "#F1AE69"),
        new("effect.invert", "反相", "风格", "反转画面颜色", "#F1AE69"),
        new("effect.posterize", "色阶分离", "风格", "减少颜色层次", "#F1AE69"),
        new("effect.chromatic", "色差", "故障", "彩色通道偏移", "#F1AE69"),
        new("effect.glitch", "故障闪烁", "故障", "数字故障风格", "#F1AE69"),
        new("effect.mosaic", "马赛克", "遮挡", "像素化遮挡", "#F1AE69"),
        new("effect.mirror", "镜像", "变换", "水平镜像画面", "#F1AE69"),
        new("effect.kaleidoscope", "万花筒", "变换", "对称复制画面", "#F1AE69"),
        new("effect.key", "色度键控", "合成", "按颜色去除背景", "#F1AE69"),
        new("effect.curves", "色彩曲线", "调色", "调整暗部、中间调与高光", "#F1AE69"),
        new("effect.wheels", "色轮", "调色", "分别调整暗部与亮部的色彩", "#F1AE69"),
        new("effect.hsl", "局部选色", "调色", "选定色相范围并调整颜色", "#F1AE69"),
        new("effect.lut", "3D LUT", "调色", "载入本地 .cube 调色查找表", "#F1AE69"),
        new("effect.vintage", "复古胶片", "复古", "暖色、颗粒和轻微褪色的胶片感", "#F1AE69"),
        new("effect.cinematic", "电影感", "电影调色", "压低饱和度并提升电影对比度", "#F1AE69"),
        new("effect.dream", "梦幻柔光", "柔光", "柔焦与高光光晕", "#F1AE69"),
        new("effect.color-pop", "色彩突出", "色彩", "增强主体色彩的鲜艳度", "#F1AE69"),
        new("effect.teal-orange", "青橙电影", "电影调色", "青色阴影与橙色高光", "#F1AE69"),
        new("effect.soft-focus", "柔焦", "人像", "适合人像的轻柔模糊", "#F1AE69"),
    ];

    public static IReadOnlyList<BuiltinPreset> Transitions { get; } =
    [
        new("transition.fade", "淡入淡出", "基础", "透明度平滑过渡", "#6CC7B8"),
        new("transition.crossfade", "交叉溶解", "基础", "两段画面柔和融合", "#6CC7B8"),
        new("transition.dip-black", "闪黑", "基础", "经黑场切换", "#6CC7B8"),
        new("transition.dip-white", "闪白", "基础", "经白场切换", "#6CC7B8"),
        new("transition.wipe-left", "向左擦除", "擦除", "从右向左揭示画面", "#6CC7B8"),
        new("transition.wipe-right", "向右擦除", "擦除", "从左向右揭示画面", "#6CC7B8"),
        new("transition.wipe-up", "向上擦除", "擦除", "从下向上揭示画面", "#6CC7B8"),
        new("transition.wipe-down", "向下擦除", "擦除", "从上向下揭示画面", "#6CC7B8"),
        new("transition.slide-left", "左滑", "滑动", "画面向左滑动", "#6CC7B8"),
        new("transition.slide-right", "右滑", "滑动", "画面向右滑动", "#6CC7B8"),
        new("transition.push-up", "上推", "滑动", "新画面向上推入", "#6CC7B8"),
        new("transition.push-down", "下推", "滑动", "新画面向下推入", "#6CC7B8"),
        new("transition.zoom-in", "放大切换", "缩放", "从中心放大切换", "#6CC7B8"),
        new("transition.zoom-out", "缩小切换", "缩放", "向中心缩小切换", "#6CC7B8"),
        new("transition.spin", "旋转切换", "旋转", "旋转画面完成切换", "#6CC7B8"),
        new("transition.blur", "模糊切换", "模糊", "经过模糊画面切换", "#6CC7B8"),
        new("transition.flash", "闪光", "光效", "亮光闪烁切换", "#6CC7B8"),
        new("transition.light-leak", "漏光", "光效", "胶片漏光风格", "#6CC7B8"),
        new("transition.circle", "圆形展开", "形状", "圆形遮罩展开", "#6CC7B8"),
        new("transition.diamond", "菱形展开", "形状", "菱形遮罩展开", "#6CC7B8"),
        new("transition.split", "分屏切换", "形状", "分屏揭示画面", "#6CC7B8"),
        new("transition.page", "二维折页", "风格", "带边缘卷曲、阴影和透视感的二维折页", "#6CC7B8"),
        new("transition.glitch", "故障切换", "风格", "数字故障画面切换", "#6CC7B8"),
        new("transition.film", "胶片切换", "风格", "胶片帧过渡", "#6CC7B8"),
        new("transition.dissolve", "柔和溶解", "溶解", "比普通淡化更柔和的片段衔接", "#6CC7B8"),
        new("transition.circle-open", "椭圆光圈展开", "形状", "按画幅比例向外展开的椭圆光圈", "#6CC7B8"),
        new("transition.zoom", "动态缩放", "缩放", "缩放并淡入下一段画面", "#6CC7B8"),
        new("transition.rotate", "旋转开场", "旋转", "旋转并淡入下一段画面", "#6CC7B8"),
        new("transition.shake", "轻微抖动", "抖动", "带轻微抖动的电影切换", "#6CC7B8"),
        new("transition.blur-dissolve", "模糊溶解", "模糊", "先模糊再完成片段衔接", "#6CC7B8"),
        new("transition.pixelize", "像素块溶解", "像素", "像素块逐渐缩小并溶解至清晰画面", "#6CC7B8"),
        new("transition.radial", "时钟扫描", "擦除", "围绕指定中心按角度扫描展开", "#6CC7B8"),
        new("transition.blinds", "百叶窗展开", "擦除", "多条水平条带同时揭示下一画面", "#6CC7B8"),
        new("transition.checker", "棋盘格展开", "形状", "分组交错揭示棋盘网格", "#6CC7B8"),
        new("transition.diagonal", "斜向擦除", "擦除", "以可调斜率的斜线揭示画面", "#6CC7B8"),
        new("transition.luma", "亮度遮罩溶解", "溶解", "按下一画面的亮度分布逐渐揭示", "#6CC7B8"),
    ];

    public static IReadOnlyList<BuiltinPreset> Stickers { get; } =
    [
        new("sticker.rectangle", "矩形", "基础图形", "可调填充与描边的矩形", "#E5A35A"),
        new("sticker.triangle", "三角形", "基础图形", "可调填充与描边的三角形", "#E5A35A"),
        new("sticker.pentagon", "五边形", "基础图形", "可调填充与描边的五边形", "#E5A35A"),
        new("sticker.hexagon", "六边形", "基础图形", "可调填充与描边的六边形", "#E5A35A"),
        new("sticker.decagon", "十边形", "基础图形", "可调填充与描边的十边形", "#E5A35A"),
        new("sticker.circle", "圆形", "基础图形", "可调填充与描边的圆形", "#E5A35A"),
        new("sticker.ellipse", "椭圆形", "基础图形", "可调填充与描边的椭圆形", "#E5A35A"),
        new("sticker.parallelogram", "平行四边形", "基础图形", "可调填充与描边的平行四边形", "#E5A35A"),
        new("sticker.quadrilateral", "任意四边形", "基础图形", "可编辑四个顶点的图形", "#E5A35A"),
    ];

    public static IReadOnlyList<BuiltinPreset> Filters { get; } =
    [
        new("effect.brightness", "亮度", "滤镜调节", "调整画面亮度", "#9B8CFF"),
        new("effect.contrast", "对比度", "滤镜调节", "调整明暗对比", "#9B8CFF"),
        new("effect.saturation", "饱和度", "滤镜调节", "调整色彩浓淡", "#9B8CFF"),
        new("effect.temperature", "色温", "滤镜调节", "调整冷暖色调", "#9B8CFF"),
        new("effect.exposure", "曝光", "滤镜调节", "调整整体曝光", "#9B8CFF"),
        new("effect.shadows", "阴影", "滤镜调节", "调整暗部细节", "#9B8CFF"),
        new("effect.highlights", "高光", "滤镜调节", "调整亮部细节", "#9B8CFF"),
        new("effect.vignette", "暗角", "滤镜调节", "柔化画面边缘", "#9B8CFF"),
        new("effect.blur", "模糊", "滤镜调节", "调整画面模糊度", "#9B8CFF"),
        new("effect.sharpen", "锐化", "滤镜调节", "增强边缘清晰度", "#9B8CFF"),
        new("effect.black-white", "黑白", "滤镜风格", "转换为黑白影像", "#9B8CFF"),
        new("effect.sepia", "复古褐色", "滤镜风格", "怀旧暖褐色调", "#9B8CFF"),
        new("effect.vintage", "复古胶片", "复古", "暖色、颗粒和轻微褪色的胶片感", "#9B8CFF"),
        new("effect.cinematic", "电影感", "电影调色", "压低饱和度并提升电影对比度", "#9B8CFF"),
        new("effect.dream", "梦幻柔光", "柔光", "柔焦与高光光晕", "#9B8CFF"),
        new("effect.teal-orange", "青橙电影", "电影调色", "青色阴影与橙色高光", "#9B8CFF"),
        new("effect.soft-focus", "柔焦", "人像", "适合人像的轻柔模糊", "#9B8CFF"),
    ];
}
