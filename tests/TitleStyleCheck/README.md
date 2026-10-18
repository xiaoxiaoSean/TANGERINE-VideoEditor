# TitleStyleCheck

在 Windows / .NET 10 SDK 下从仓库根目录运行：

```powershell
dotnet run --project tests/TitleStyleCheck/TitleStyleCheck.csproj -p:UseAppHost=false
```

使用真实项目样式工厂及 WPF 透明 PNG 渲染器，验证横向滚动、持续缩放、动感标题的组合运动、四种缓动、安全边距、背景内边距/圆角/透明度及 JSON 参数保存。为素材库全部文字样式生成 PNG，并生成新增样式与滚动片尾等样式的九宫格静态图。

默认输出到被 Git 忽略的 `artifacts/title-style-check/`；可在 `--` 后提供输出目录。程序无外部测试框架依赖。`UseAppHost=false` 避免编辑器正在运行时因 EXE 被锁定导致构建复制失败。

这组检查验证参数与静态排版，动态 MP4 及项目预览/导出由 `RenderSmokeCheck` 验证。静态图不包含运动时刻，不作为完整动画的视觉验收证明。
