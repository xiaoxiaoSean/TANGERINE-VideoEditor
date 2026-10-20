# 内置纯音乐与 MCP

“内置音乐”作为第六个素材分类，索引为 5。原有媒体、文字、效果、转场、贴纸分类仍为 0–4。音乐条目使用自己的 `music.*` 稳定 ID，与时间线轨道、项目媒体及片段 ID 分开；音乐不是视觉预设，不传入 `catalog_schema`、`catalog_preview` 或效果参数接口。

## 离线目录与来源

`Editor/BuiltinMusicCatalog.cs` 读取随程序打包的 `Resources/BuiltinMusic/manifest.json`，列出 100 首纯音乐。每项登记标题、作者、风格、情绪、时长、本地音频与封面路径，以及来源 URL、许可 ID 和许可 URL。音频与封面预先准备并随应用发布，用户打开分类或试听时不在线下载、不生成音乐或封面。清单中的资源路径必须位于指定资源目录；目录缺失或清单无效以 `MUSIC` 阶段码报告。

当前目录要求登记 `CC0-1.0`。公开领域奉献的依据保留为具体曲目的来源与许可链接，不用“免版权”推断任何未登记音乐均可以分发。插入项目时保留这些元数据，方便用户追溯来源。

本次实际收录原作者 Rrrrrose 在 Loyalty Freak Music 官网以 Komiku、Loyalty Freak Music、Monplaisir 艺名发布的 100 首器乐，共 302.1 分钟，覆盖 14 种风格：电子节拍、器乐节奏蓝调、电子舞曲、合成器波、尤克里里、原声民谣、器乐摇滚、轻松治愈、悬疑氛围、氛围电子、低保真节拍、游戏配乐、原声吉他、吉他氛围。每首都保留完整音乐，最短超过 60 秒；没有以裁切、变速或重复曲目凑数量。音频约 695.4 MiB，100 张封面约 15.5 MiB，安装包会相应增大。

`tools/CollectBuiltinMusic.py` 负责开发时采集：保存逐专辑原始许可网页、作者 FAQ、CC0 全文及校验记录，按 MPEG 音频内容去重。统一移除 ID3 内嵌专辑图片，音乐帧不改变、不重编码；清单同时保存原始文件、打包文件和音频内容的 SHA-256。该脚本不会在用户打开软件时运行。

`tools/GenerateMusicArtwork.py` 根据风格离线绘制原创封面，每首的稳定 ID 决定其构图变化。PNG、`artwork.json` 和封面来源说明均放在 `Resources/BuiltinMusic/`，运行时仅加载图片，不需要 Python、Pillow、网络或图像生成服务。曲目原名与作者保持原始名称；图片是本软件的风格示意图。

阶段码按失败分支固定登记：`MUSIC0001` 清单读取或结构错误，`MUSIC0002` 曲目 ID 不存在，`MUSIC0003` 音频文件缺失或时长无效，`MUSIC0004` 试听解码或启动失败，`MUSIC0005` 封面加载失败，`MUSIC0006` 插入失败。`MessageTipGenerator` 将 MUSIC 组件归入渲染诊断，同时保留文件不存在、权限等异常类型的优先说明。

## MCP 命令

| 命令 | 参数 | 行为 |
| --- | --- | --- |
| `music_list` | `query?`、`genre?`、`mood?` | 只读筛选；返回 `total/count/tracks/genres/moods`，曲目含本地路径及完整作者、许可来源 |
| `music_preview` | `musicId` | 从随包音频开始试听，同时在主预览器显示曲目缩略图；不导入工程，返回 `requested:true` 及封面状态 |
| `music_stop` | 无 | 停止当前试听并释放它自己的播放器 |
| `music_add` | `musicId`、`trackId?`、`startSeconds?`、`sourceInSeconds?`、`durationSeconds?`、`volume?`、`fadeInSeconds?`、`fadeOutSeconds?`、`snap?` | 把音乐注册为普通媒体并插入音频轨道，操作可撤销 |
| `catalog_list` | `category:"music"` 或 `"all"` | 在既有目录入口发现音乐；筛选使用 `music_list` |
| `workspace_set` | `patch:{categoryIndex:5}` | 显示音乐分类；临时设置页不能成为持久分类 |

`music_list` 的 `genre`、`mood` 为不区分大小写的包含匹配，多个条件同时满足。`query` 匹配曲目 ID、标题、作者、风格、情绪和许可 ID。AI 应先读取目录返回的实际选项，再选择曲目。

`music_add.trackId` 只表示当前序列的目标音频轨道，不表示音乐曲目。未指定轨道时，优先当前选中的未锁定音频轨道，再用第一个未锁定音频轨道；都不存在时创建“音乐”轨道。源入点默认 0，持续时长默认曲目剩余时长，插入位置默认当前播放头。音量范围 0–4，默认 1。淡入、淡出各 0–1 秒，默认 0，且不能超过片段时长；通过已有音频滤镜参数实现，预览与导出一致。吸附默认开启，遵从全局及目标轨道开关；`snap:false` 保留指定时间。

插入前异步检查项目归档容量，完成后校验项目、修订与保存位置。注册和插入统一进入 `McpMutate`，失败时回滚。返回音乐 ID、媒体 ID、片段 ID、目标轨道 ID、实际时间和授权来源，便于继续使用普通 `clip_trim`、`clip_update`、音频处理及关键帧命令。

## 共享 UI 与状态归属

UI 插入和 MCP 使用 `EnsureBuiltinMusicAsset` 注册项目媒体，避免以曲目标题作为身份。音频文件以普通 `MediaAsset` 引用；保存 `.tvep` 时由既有项目归档流程把所用音频写入项目。媒体元数据保留 `BuiltinMusicId/Artist/Genre/Mood/SourceUrl/LicenseId/LicenseUrl`。

显式重新链接为其他文件时，清除原内置曲目的身份、自动风格标签与授权标记，防止新文件沿用旧封面和 CC0 说明。重新探测同一文件则保留这些来源信息。再次添加内置音乐按其真实安装资源路径去重，不能因为旧素材保留相同目录 ID 就误用已被替换的声音。

UI 与 MCP 的试听统一调用 `PreviewBuiltinMusic`，停止统一调用 `StopBuiltinMusicPreview`。每次试听有独立播放器，延迟解码事件先验证播放器引用，不能改变后来选择的音乐。试听、视觉示例和时间线播放相互清理，项目和序列切换也会停止旧试听。MCP `editor_get_state().preview.music` 提供曲目、播放状态、时间和时长，变化进入 `uiRevision`。`preview_pause` 面向时间线，音乐试听使用 `music_stop`。

试听时，主预览器使用预先打包的曲目缩略图作为画面。该封面有独立图层和异步请求身份；旧曲目解码完毕、失败或停止后的回调不能覆盖新选择。音乐封面跟随预览视图缩放，暂停试听时仍显示；结束试听、切换预览或开始时间线播放后恢复工程画面。封面不注册为工程素材、不修改序列和导出内容。

`editor_get_state().preview.mode` 返回 `music/catalog/timeline`，按实际监视器所有者区分音乐试听、视觉示例和工程预览。`preview.music` 额外提供 `artworkPath`、`artworkLoading`、`artworkVisible`、`artworkRequest`；可见状态需音乐仍拥有监视器、封面图层可见且图像非空。请求序号、加载与可见状态纳入 MCP 指纹，AI 可用 `editor_wait_for_change` 等待封面完成，不能只用工具已返回推断封面已经显示。`music_preview` 直接返回当前模式、封面路径及加载、可见状态供即时查看。

## 验证范围

本次完成源码、文档、音频采集与离线封面生成；按用户要求，没有运行测试、构建或启动编辑器。资源来源与采集记录以随资源清单及其对应说明为准，功能尚未经过运行时验证。
