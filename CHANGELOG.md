# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 规范，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.1.0] - 2026-11-12

### 新增

- **三种占用率显示样式**，右键菜单 `显示样式` 切换，选择写入 `settings.ini`
  - `number` 数字百分比（默认，与 1.0 外观一致）
  - `ring` 环形进度条：细描边圆环，12 点起顺时针填充，22% 透明度的轨道表示满量程
  - `bar` 长条进度条：两端圆头胶囊，同色轨道 + 实心填充
  - 切换后窗口宽度立即重算，无需重启
- **GPU 占用率**：PDH 通配符查询 `\GPU Engine(*)\Utilization Percentage`，
  一次收集全部实例约 1–4ms；按引擎类型分别求和后取最大值，口径与任务管理器基本一致
- 指标标签统一为 `CPU` / `RAM` / `GPU`（原为「内存」），字体改用 Segoe UI，
  与 Win11 任务栏排版更一致
- `--dump` 自检升级为三种样式 × 深/浅主题的对照图，并输出各样式画布尺寸与墨水覆盖率

### 变更

- 布局改为按「标签 + 仪表」单元格循环生成，指标数量由常量 `MetricCount` 驱动
- 环形/长条绘制开启抗锯齿，圆头端点；数值为 0 时只画轨道
- GPU 不可用时（无计数器 / 首次采样）显示 `--%`，不再显示 `0%` 误导

### 修复

- PDH 结构体 `PDH_FMT_COUNTERVALUE_ITEM_W` 在 x64 上应为 24 字节
  （`szName` @0、`CStatus` @8、`doubleValue` @16），按 16 字节解析会读越界
- 离线自检的对照图改为按行铺满，不再出现灰色缝隙

## [1.0.0] - 2026-11-11

### 新增

- 任务栏最左侧常驻显示 CPU 与内存占用，每秒刷新
- 分层窗口 + `UpdateLayeredWindow` 逐像素 alpha，无边框、无背景色块
- CPU 采样基于 `GetSystemTimes` 差分，内存基于 `GlobalMemoryStatusEx`，不走 WMI
- 自动跟随任务栏矩形、高度、DPI 缩放与深浅色主题
- 左键拖动调整水平位置，位置持久化到 `%APPDATA%\TaskbarStats\settings.ini`
- 右键菜单：开机自动启动 / 重置到最左侧 / 退出
- 单实例互斥锁，避免重复启动
- 任务栏自动隐藏时同步收起浮窗
- `--dump` 离线自检模式，导出真实渲染位图与画布指标
- `build.ps1` 构建脚本：`-Run` / `-Startup` / `-Probe`
- GitHub Actions 工作流，在 `windows-latest` 上编译并上传产物

### 技术说明

- 目标运行时 .NET Framework 4.x，用系统自带 csc（**C# 5**）编译，零 SDK 依赖
- 进程声明 Per-Monitor-V2 DPI 感知
- GDI+ 输出直通 alpha，贴图前手动做预乘，消除抗锯齿边缘色晕

[1.1.0]: https://example.invalid/TaskbarStats/releases/tag/v1.1.0
[1.0.0]: https://example.invalid/TaskbarStats/releases/tag/v1.0.0
