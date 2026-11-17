# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.0] - 2026-11-17

### 修复 (Fixed)

- **浮窗大部分区域收不到鼠标**：分层窗口只把「非透明像素」的鼠标事件交给窗口，而字形覆盖率只有约 5%，
  点文字周围的空白处时事件直接穿透到任务栏 —— 表现为「左键很难拖住、右键老是弹出任务栏菜单」。
  现在整个矩形额外铺一层 `alpha=1`（肉眼不可见）的底色，命中率由实测 **1/6 提升到 6/6**。
- **拖动卡顿**：拖动过程中改为只调用 `SetWindowPos` 移动窗口，不再逐帧重建 DC 并重贴位图；
  同时用 `SetCapture` 捕获鼠标，光标甩出窗口也能继续跟随。
- **开机自启无反馈**：原实现把所有异常静默吞掉，点了也不知道是否生效。

### 新增 (Added)

- 右键菜单「开机自动启动」直接显示真实状态：已开启 / 已关闭 / 已开启但指向其他程序。
- 切换后回读磁盘真实状态并弹窗回报结果，失败时显示剥离 COM 包装后的真实原因。
- 新增 `--autostart status|on|off` 命令行查询与切换，不弹窗、不依赖 GUI，便于排查。
- 新增 `tools/HitTest.cs`（鼠标命中探针）与 `tools/MouseTest.cs`（拖动探针）。

### 技术说明 (Technical Notes)

- 铺底用的是 `alpha=1`，视觉上不可分辨；墨水覆盖率实测不变（4.8% / 6.8% / 6.0%）。

## [1.0.0] - 2026-11-14

### 特性 (Features)

- **三种显示样式**：支持数字百分比、环形进度条、长条进度条，右键菜单随时切换并持久化。
- **GPU 占用率监控**：通过 Windows 自带的 PDH 性能计数器读取 `GPU Engine`，跨厂商通用，无需安装驱动 SDK。
- **极致的性能与体积**：12 秒常驻实测累计消耗 0.02 秒 CPU（约 0.17% 单核），编译产物仅 ~27KB。
- **真正的透明渲染**：分层窗口 + `UpdateLayeredWindow` 逐像素 alpha，非色键抠图，无黑底、无毛边。
- **不打扰的交互**：不占任务栏按钮、不进 Alt+Tab、点击不抢焦点，支持左键拖动调整位置。
- **全面适配**：自动跟随任务栏高度与位置，自适应系统深浅色主题。
- **零依赖构建**：编译仅用 Windows 自带 csc，不装 SDK；运行仅需 .NET Framework 4.x。

### 修复 (Fixed)

- **小字号下文字发虚**：改用「ClearType 遮罩转 alpha」技术，在不透明黑底上用 `ClearTypeGridFit` 画字拿到次像素定位与 hinting，取亮度作覆盖率 alpha，再用真正的 ink 颜色写回透明画布，小字号明显变实。
- **高 DPI 模糊**：内嵌 `src/app.manifest` 声明 `permonitorv2,permonitor`，从根上避免「整块位图被系统拉伸」导致发虚；同时 `PixelOffsetMode` 由 `Half` 改为 `None`，字形不再跨像素采样。
- **GPU 内存越界**：修正 PDH 结构体 `PDH_FMT_COUNTERVALUE_ITEM_W` 在 x64 上的 24 字节对齐问题，避免读越界。
- **文字基线偏移**：文字基线改为对齐整数像素（`Math.Round((winH - lineH) / 2)`），不再依赖 `LineAlignment.Center` 引入的 0.5px 偏移。

### 开发与工具 (Dev & Tools)

- 新增 `--info` 诊断模式：输出 DPI、缩放比例、DPI 感知状态、任务栏矩形等，用于取证排错。
- 新增 `--dump <base> [dpi]` 离线自检模式：支持导出三种样式 × 深/浅主题的对照图及画布指标。
- 新增 `build.ps1` 构建脚本（`-Run` / `-Startup` / `-Probe`）。
- 新增 GitHub Actions 工作流，在 `windows-latest` 上自动编译并自检。

### 技术说明 (Technical Notes)

- 目标运行时 .NET Framework 4.x，用系统自带 csc（**C# 5**）编译，零 SDK 依赖。
- GDI+ 输出直通 alpha，贴图前手动做预乘，消除抗锯齿边缘色晕。
- 环形/长条与文字分两趟绘制，文字统一走遮罩通道。

[1.1.0]: https://github.com/NachtNeko/TaskbarStats-Lite/releases/tag/v1.1.0
[1.0.0]: https://github.com/NachtNeko/TaskbarStats-Lite/releases/tag/v1.0.0