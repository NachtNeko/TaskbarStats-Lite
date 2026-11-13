// TaskbarStats - Win11 任务栏 CPU / RAM / GPU 实时监视
//
// 极简风格：无窗口边框、无背景色块，只有文字/图形直接嵌在任务栏上。
//
// 编译（无需安装任何 SDK，用 Windows 自带 csc）：
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ ^
//     /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll TaskbarStats.cs
//
// 实现要点：
//   1. 分层窗口 (WS_EX_LAYERED) + UpdateLayeredWindow 逐像素 alpha —— 真正透明，
//      不会出现黑底方块，抗锯齿边缘也干净。
//   2. CPU 用 GetSystemTimes 差分，RAM 用 GlobalMemoryStatusEx，GPU 用 PDH 通配符查
//      \GPU Engine(*)\Utilization Percentage，一次收集拿到全部实例，约 1-4ms。
//   3. 显示样式三种：数字百分比 / 环形进度 / 长条进度，右键菜单切换。
//   4. 窗口是 WS_EX_TOOLWINDOW，不占任务栏按钮、不出现在 Alt+Tab。
//   5. 每秒重读任务栏矩形，任务栏高度/位置/DPI/浅色深色主题变化都能跟上。
//
// 语言版本限制：Windows 自带 csc 只支持 C# 5，所以本文件不用插值字符串等新语法。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TaskbarStats
{
    /// <summary>占用率显示样式。</summary>
    internal enum GaugeStyle
    {
        Number = 0,
        Ring = 1,
        Bar = 2
    }

    /// <summary>
    /// 只在 /define:TSB_TEST 时切换成独立实例名与独立配置，便于和已运行的正式实例并存做验证。
    /// 正式编译（不带该宏）行为完全不变。
    /// </summary>
    internal static class Build
    {
#if TSB_TEST
        public const string InstanceName = @"Local\TaskbarStats.TestInstance";
        public const string SettingsFolder = "TaskbarStatsTest";
        public static readonly bool PortableSettings = true;
#else
        public const string InstanceName = @"Local\TaskbarStats.SingleInstance";
        public const string SettingsFolder = "TaskbarStats";
        public static readonly bool PortableSettings = false;
#endif
    }

    internal static class Native
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int ULW_ALPHA = 0x02;

        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;

        public const uint PDH_FMT_DOUBLE = 0x00000200;
        public const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int Cx, Cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = 64;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        // PDH_FMT_COUNTERVALUE_ITEM_W:
        //   { LPCWSTR szName; PDH_FMT_COUNTERVALUE FmtValue; }
        // PDH_FMT_COUNTERVALUE = { DWORD CStatus; union { ...; double doubleValue; ... } }
        // 布局（x86/x64 同为 24 字节）：szName @0，CStatus @8，doubleValue @16
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PDH_ITEM
        {
            [FieldOffset(0)] public IntPtr szName;
            [FieldOffset(8)] public uint cStatus;
            [FieldOffset(16)] public double doubleValue;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out ulong lpIdleTime,
            out ulong lpKernelTime, out ulong lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // ---- PDH：GPU 引擎占用率 ----
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhAddCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll")]
        public static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
            ref uint bufferSize, ref uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll")]
        public static extern uint PdhCloseQuery(IntPtr query);
    }

    internal sealed class Widget : Form
    {
        private const int MetricCount = 3;
        private const int MetricCpu = 0;
        private const int MetricRam = 1;
        private const int MetricGpu = 2;

        private static readonly string[] Labels = { "CPU", "RAM", "GPU" };
        private const string GpuCounterPath = @"\GPU Engine(*)\Utilization Percentage";

        // ---- 采样状态 ----
        private ulong prevIdle, prevKernel, prevUser;
        private bool havePrev;
        private readonly double[] values = new double[MetricCount];
        private readonly bool[] valid = new bool[MetricCount];

        // ---- GPU (PDH) ----
        private bool gpuQueryOpen;
        private IntPtr gpuQuery = IntPtr.Zero;
        private IntPtr gpuCounter = IntPtr.Zero;
        private IntPtr gpuBuffer = IntPtr.Zero;
        private uint gpuBufferSize;
        private readonly Dictionary<string, double> engineTotals =
            new Dictionary<string, double>(StringComparer.Ordinal);

        // ---- 显示样式 ----
        private GaugeStyle style = GaugeStyle.Number;

        // ---- 绘制 ----
        private Bitmap canvas;
        private Bitmap textMask;
        private Font font;
        private int dpi = 96;
        private int winW, winH;
        private float padX, gap, bigGap, labelW, gaugeW, emPx, lineH, textTop;
        private int dpiOverride;      // 仅自检用：强制按指定 DPI 渲染，便于对比不同缩放下的清晰度
        private bool lightTaskbar;

        // ---- 窗口位置状态 ----
        private int winX, winY;
        private int offsetX;
        private Rectangle trayRect;
        private bool hasTray;

        // ---- 交互 ----
        private bool dragging;
        private bool windowShown = true;
        private int dragStartCursorX;
        private int dragOriginOffset;

        // ---- 循环 ----
        private System.Windows.Forms.Timer timer;
        private int tickCount;
        private readonly bool headless;

        private const int ThemePollEveryTicks = 5;  // 每 5 秒查一次浅/深色

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED
                            | Native.WS_EX_TOOLWINDOW
                            | Native.WS_EX_NOACTIVATE
                            | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        public Widget() : this(false)
        {
        }

        public Widget(bool headlessMode)
        {
            headless = headlessMode;

            SetStyle(ControlStyles.UserPaint
                   | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            Text = "TaskbarStats";

            LoadSettings();

            // 先给出一个初始占位，窗口在 UpdateLayeredWindow 之前不会显示，所以不会闪黑框
            winW = 10;
            winH = 10;
            Bounds = new Rectangle(0, 0, winW, winH);

            lightTaskbar = ReadSystemUsesLightTheme();

            if (!headless)
            {
                Show();
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, winW, winH,
                    Native.SWP_NOACTIVATE);
            }

            InitGpu();
            RebuildLayout(true);
            Sample();

            if (headless) return;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += OnTick;
            timer.Start();
        }

        // ------------------------------------------------------------------
        // 每秒一次：采样 -> 必要时重建布局 -> 定位 -> 重绘
        // ------------------------------------------------------------------
        private void OnTick(object sender, EventArgs e)
        {
            tickCount++;

            if (tickCount % ThemePollEveryTicks == 0)
            {
                bool light = ReadSystemUsesLightTheme();
                if (light != lightTaskbar)
                {
                    lightTaskbar = light;
                    RebuildLayout(true);
                }
            }

            Sample();

            RefreshTrayRect();

            if (!hasTray)
            {
                // 任务栏被自动隐藏：把浮窗收起来，别孤零零挂在桌面上
                if (windowShown)
                {
                    Hide();
                    windowShown = false;
                }
                return;
            }

            if (!windowShown)
            {
                Show();
                windowShown = true;
            }

            if (winH != trayRect.Height)
            {
                RebuildLayout(true);
            }
            else
            {
                Reposition();
                Render();
            }
        }

        private void Sample()
        {
            // --- CPU ---
            ulong idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                if (havePrev)
                {
                    ulong dIdle = idle - prevIdle;
                    ulong dTotal = (kernel - prevKernel) + (user - prevUser);
                    if (dTotal > 0)
                    {
                        double busy = 1.0 - ((double)dIdle / (double)dTotal);
                        values[MetricCpu] = ClampPercent(busy * 100.0);
                        valid[MetricCpu] = true;
                    }
                }
                prevIdle = idle;
                prevKernel = kernel;
                prevUser = user;
                havePrev = true;
            }

            // --- RAM ---
            Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(ms))
            {
                values[MetricRam] = ms.dwMemoryLoad;
                valid[MetricRam] = true;
            }

            // --- GPU ---
            SampleGpu();
        }

        private static double ClampPercent(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        // ------------------------------------------------------------------
        // GPU：PDH 通配符查所有 GPU 引擎实例，按引擎类型分别求和后取最大值
        // （与任务管理器「GPU 利用率」的口径基本一致）
        // ------------------------------------------------------------------
        private void InitGpu()
        {
            try
            {
                IntPtr q, c;
                uint rc = Native.PdhOpenQueryW(null, IntPtr.Zero, out q);
                if (rc != 0) return;

                rc = Native.PdhAddEnglishCounterW(q, GpuCounterPath, IntPtr.Zero, out c);
                if (rc != 0)
                {
                    rc = Native.PdhAddCounterW(q, GpuCounterPath, IntPtr.Zero, out c);
                }
                if (rc != 0)
                {
                    Native.PdhCloseQuery(q);
                    return;
                }

                gpuQuery = q;
                gpuCounter = c;
                gpuQueryOpen = true;

                // 先收一次，避免第一次读数无效
                Native.PdhCollectQueryData(gpuQuery);
            }
            catch (Exception)
            {
                // pdh.dll 不可用（极老系统）时静默降级
                gpuQueryOpen = false;
            }
        }

        private void SampleGpu()
        {
            if (!gpuQueryOpen) return;

            try
            {
                if (Native.PdhCollectQueryData(gpuQuery) != 0) return;

                uint size = 0, count = 0;
                uint rc = Native.PdhGetFormattedCounterArrayW(gpuCounter, Native.PDH_FMT_DOUBLE,
                    ref size, ref count, IntPtr.Zero);
                if (rc != Native.PDH_MORE_DATA || size == 0 || count == 0) return;

                if (gpuBuffer == IntPtr.Zero || gpuBufferSize < size)
                {
                    if (gpuBuffer != IntPtr.Zero) Marshal.FreeHGlobal(gpuBuffer);
                    gpuBuffer = Marshal.AllocHGlobal((int)size);
                    gpuBufferSize = size;
                }

                uint size2 = size, count2 = count;
                rc = Native.PdhGetFormattedCounterArrayW(gpuCounter, Native.PDH_FMT_DOUBLE,
                    ref size2, ref count2, gpuBuffer);
                if (rc != 0) return;

                int stride = Marshal.SizeOf(typeof(Native.PDH_ITEM));
                int items = (int)Math.Min(count2, size2 / (uint)stride);

                engineTotals.Clear();
                for (int i = 0; i < items; i++)
                {
                    IntPtr p = new IntPtr(gpuBuffer.ToInt64() + (long)i * stride);
                    Native.PDH_ITEM item = (Native.PDH_ITEM)Marshal.PtrToStructure(p, typeof(Native.PDH_ITEM));
                    if (item.szName == IntPtr.Zero) continue;
                    if (item.cStatus != 0) continue;      // PDH_CSTATUS_VALID_DATA == 0

                    string name = Marshal.PtrToStringUni(item.szName);
                    if (name == null) continue;

                    int idx = name.LastIndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
                    string engine = idx >= 0 ? name.Substring(idx + 9) : "Other";

                    double acc;
                    if (!engineTotals.TryGetValue(engine, out acc)) acc = 0;
                    engineTotals[engine] = acc + item.doubleValue;
                }

                if (engineTotals.Count == 0) return;

                double max = 0;
                foreach (KeyValuePair<string, double> kv in engineTotals)
                {
                    if (kv.Value > max) max = kv.Value;
                }

                values[MetricGpu] = ClampPercent(max);
                valid[MetricGpu] = true;
            }
            catch (Exception)
            {
                // 单次读数失败不影响其它指标
            }
        }

        // ------------------------------------------------------------------
        // 布局 / 字体 / 画布
        // ------------------------------------------------------------------
        private void RebuildLayout(bool resizeWindow)
        {
            if (!hasTray) RefreshTrayRect();

            dpi = 96;
            try
            {
                if (headless)
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    {
                        int d0 = (int)Math.Round(g.DpiX);
                        if (d0 > 0) dpi = d0;
                    }
                }
                else
                {
                    int d = Native.GetDpiForWindow(Handle);
                    if (d > 0) dpi = d;
                }
            }
            catch (Exception) { }

            if (dpiOverride > 0) dpi = dpiOverride;

            float scale = dpi / 96f;

            if (font != null) { font.Dispose(); font = null; }
            font = new Font(PickFontFamily(), 9f, FontStyle.Regular, GraphicsUnit.Point);

            padX = 8f * scale;
            gap = 4f * scale;
            bigGap = 11f * scale;

            float measuredLabel, measuredValue, heightLocal;
            using (Bitmap probe = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
            {
                probe.SetResolution(dpi, dpi);
                using (Graphics g = Graphics.FromImage(probe))
                {
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (StringFormat sf = TypographicFormat())
                    {
                        measuredLabel = 0;
                        for (int i = 0; i < MetricCount; i++)
                        {
                            float w = g.MeasureString(Labels[i], font, PointF.Empty, sf).Width;
                            if (w > measuredLabel) measuredLabel = w;
                        }
                        measuredValue = g.MeasureString("100%", font, PointF.Empty, sf).Width;
                        heightLocal = g.MeasureString("Ag100%", font, PointF.Empty, sf).Height;
                    }
                    lineH = font.GetHeight(g);
                }
            }

            emPx = heightLocal;
            labelW = (float)Math.Ceiling(measuredLabel);

            switch (style)
            {
                case GaugeStyle.Ring:
                    gaugeW = (float)Math.Round(emPx);
                    if (gaugeW < 10f) gaugeW = 10f;
                    break;
                case GaugeStyle.Bar:
                    gaugeW = (float)Math.Round(emPx * 2.0f);
                    if (gaugeW < 24f) gaugeW = 24f;
                    break;
                default:
                    gaugeW = (float)Math.Ceiling(measuredValue) + 1f;
                    break;
            }

            int contentW = (int)Math.Ceiling(
                padX * 2 + MetricCount * labelW + MetricCount * gap + MetricCount * gaugeW
                + (MetricCount - 1) * bigGap);
            int contentH = hasTray ? trayRect.Height : Math.Max((int)Math.Ceiling(heightLocal) + 8, 32);

            if (contentW < 16) contentW = 16;
            if (canvas != null && (canvas.Width != contentW || canvas.Height != contentH))
            {
                canvas.Dispose();
                canvas = null;
            }
            winW = contentW;
            winH = contentH;

            // 文字整体对齐到整数像素：小字号下 0.5px 的偏移就是"发虚"的元凶
            textTop = (float)Math.Round((winH - lineH) / 2f);

            if (resizeWindow)
            {
                Reposition();
            }
            Render();
        }

        private static string PickFontFamily()
        {
            string[] candidates = { "Segoe UI", "Tahoma" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    using (FontFamily ff = new FontFamily(candidates[i])) { }
                    return candidates[i];
                }
                catch (Exception) { }
            }
            return SystemFonts.DefaultFont.FontFamily.Name;
        }

        private static StringFormat TypographicFormat()
        {
            StringFormat sf = StringFormat.GenericTypographic;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            sf.FormatFlags |= StringFormatFlags.NoClip;
            return sf;
        }

        private void Render()
        {
            if (winW <= 0 || winH <= 0) return;

            if (canvas == null)
            {
                canvas = new Bitmap(winW, winH, PixelFormat.Format32bppArgb);
                canvas.SetResolution(dpi, dpi);
            }

            Color ink = lightTaskbar
                ? Color.FromArgb(235, 24, 24, 28)
                : Color.FromArgb(242, 255, 255, 255);

            using (Graphics g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.CompositingMode = CompositingMode.SourceOver;

                // 先算出每个单元格的位置，两趟绘制共用（形状一趟、文字一趟）
                float[] labelX = new float[MetricCount];
                float[] gaugeX = new float[MetricCount];
                float x = padX;
                for (int i = 0; i < MetricCount; i++)
                {
                    labelX[i] = x;
                    x += labelW + gap;
                    gaugeX[i] = x;
                    x += gaugeW;
                    if (i < MetricCount - 1) x += bigGap;
                }

                // 第一趟：只在画布上画环形/长条（矢量图形，抗锯齿即可）
                if (style != GaugeStyle.Number)
                {
                    for (int i = 0; i < MetricCount; i++)
                    {
                        if (style == GaugeStyle.Ring) DrawRing(g, ink, gaugeX[i], gaugeW, values[i], valid[i]);
                        else DrawBar(g, ink, gaugeX[i], gaugeW, values[i], valid[i]);
                    }
                }
            }

            // 第二趟：文字走"ClearType 遮罩 -> alpha"通道，小字号下比灰阶抗锯齿实得多
            RenderTextMask(ink);

            Premultiply(canvas);
            PushToWindow();
        }

        // ------------------------------------------------------------------
        // 小字号清晰度的关键：
        //   透明画布上无法直接用 ClearType（它要求不透明底色）。做法是
        //   1) 在纯黑不透明位图上用 ClearTypeGridFit 画白字 —— 拿到微软的
        //      次像素定位 + hinting + gamma 校正结果；
        //   2) 取每个像素亮度作为覆盖率 alpha，丢掉 ClearType 的子像素彩色；
        //   3) 用真正要用的 ink 颜色按这个 alpha 写回透明画布。
        //   12px 这种小字号下，比 GDI+ 的灰阶 AntiAliasGridFit 明显更锐。
        // ------------------------------------------------------------------
        private void RenderTextMask(Color ink)
        {
            if (textMask == null || textMask.Width != winW || textMask.Height != winH)
            {
                if (textMask != null) textMask.Dispose();
                textMask = new Bitmap(winW, winH, PixelFormat.Format32bppRgb);
                textMask.SetResolution(dpi, dpi);
            }

            float[] labelX = new float[MetricCount];
            float[] gaugeX = new float[MetricCount];
            float x = padX;
            for (int i = 0; i < MetricCount; i++)
            {
                labelX[i] = x;
                x += labelW + gap;
                gaugeX[i] = x;
                x += gaugeW;
                if (i < MetricCount - 1) x += bigGap;
            }

            using (Graphics tg = Graphics.FromImage(textMask))
            {
                tg.Clear(Color.Black);
                tg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                tg.TextContrast = 5;
                tg.SmoothingMode = SmoothingMode.None;
                tg.PixelOffsetMode = PixelOffsetMode.None;

                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    for (int i = 0; i < MetricCount; i++)
                    {
                        DrawText(tg, white, Labels[i], labelX[i], labelW);
                        if (style == GaugeStyle.Number)
                        {
                            DrawText(tg, white, FormatPercent(values[i], valid[i]), gaugeX[i], gaugeW);
                        }
                    }
                }
            }

            CompositeMaskAsAlpha(ink);
        }

        private void CompositeMaskAsAlpha(Color ink)
        {
            Rectangle rect = new Rectangle(0, 0, winW, winH);
            BitmapData md = textMask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            BitmapData cd = canvas.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int[] maskRow = new int[winW];
                int[] canvasRow = new int[winW];
                int rgb = (ink.R << 16) | (ink.G << 8) | ink.B;

                for (int y = 0; y < winH; y++)
                {
                    IntPtr mSrc = new IntPtr(md.Scan0.ToInt64() + (long)y * md.Stride);
                    IntPtr cSrc = new IntPtr(cd.Scan0.ToInt64() + (long)y * cd.Stride);
                    Marshal.Copy(mSrc, maskRow, 0, winW);
                    Marshal.Copy(cSrc, canvasRow, 0, winW);

                    for (int x = 0; x < winW; x++)
                    {
                        int v = maskRow[x];
                        int r = (v >> 16) & 0xFF;
                        int g = (v >> 8) & 0xFF;
                        int b = v & 0xFF;
                        int luma = (r * 30 + g * 59 + b * 11) / 100;
                        if (luma <= 2) continue;              // 没有字迹，保留画布原有像素
                        if (luma > 255) luma = 255;
                        int a = luma * ink.A / 255;
                        canvasRow[x] = (a << 24) | rgb;
                    }

                    Marshal.Copy(canvasRow, 0, cSrc, winW);
                }
            }
            finally
            {
                textMask.UnlockBits(md);
                canvas.UnlockBits(cd);
            }
        }

        private static string FormatPercent(double value, bool ok)
        {
            if (!ok) return "--%";
            int v = (int)Math.Round(value);
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            return v.ToString(CultureInfo.InvariantCulture) + "%";
        }

        private void DrawText(Graphics g, Brush brush, string text, float x, float w)
        {
            using (StringFormat sf = TypographicFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;   // 自己按整数像素定位，Center 会引入 .5 偏移
                RectangleF box = new RectangleF(x, textTop, w, lineH);
                g.DrawString(text, font, brush, box, sf);
            }
        }

        // 环形进度：细描边圆环 + 低透明度轨道，从 12 点方向顺时针填充
        private void DrawRing(Graphics g, Color ink, float x, float d, double value, bool ok)
        {
            float stroke = Math.Max(2f, d * 0.115f);
            float inset = stroke / 2f;
            RectangleF rect = new RectangleF(
                x + inset,
                (winH - d) / 2f + inset,
                d - stroke,
                d - stroke);

            Color track = Color.FromArgb((int)(ink.A * 0.22f), ink);
            using (Pen trackPen = new Pen(track, stroke))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                g.DrawEllipse(trackPen, rect);
            }

            if (!ok || value <= 0.5) return;

            double threshold = 360.0 * value / 100.0;
            if (threshold > 359.5) threshold = 359.5;

            using (Pen valuePen = new Pen(ink, stroke))
            {
                valuePen.StartCap = LineCap.Round;
                valuePen.EndCap = LineCap.Round;
                g.DrawArc(valuePen, rect, -90f, (float)threshold);
            }
        }

        // 长条进度：两端圆头的胶囊形，低透明度轨道 + 实心填充
        private void DrawBar(Graphics g, Color ink, float x, float w, double value, bool ok)
        {
            float thickness = Math.Max(3f, w * 0.11f);
            float cy = winH / 2f;
            float inset = thickness / 2f;
            float left = x + inset;
            float right = x + w - inset;
            if (right <= left) return;

            Color track = Color.FromArgb((int)(ink.A * 0.22f), ink);
            using (Pen trackPen = new Pen(track, thickness))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                g.DrawLine(trackPen, left, cy, right, cy);
            }

            if (!ok || value <= 0.5) return;

            float span = (right - left) * (float)(value / 100.0);
            if (span < 0.6f) span = 0.6f;

            using (Pen valuePen = new Pen(ink, thickness))
            {
                valuePen.StartCap = LineCap.Round;
                valuePen.EndCap = LineCap.Round;
                g.DrawLine(valuePen, left, cy, left + span, cy);
            }
        }

        // ------------------------------------------------------------------
        // 定位
        // ------------------------------------------------------------------
        private void RefreshTrayRect()
        {
            IntPtr hTray = Native.FindWindow("Shell_TrayWnd", null);
            hasTray = false;
            trayRect = Rectangle.Empty;
            if (hTray == IntPtr.Zero) return;
            if (!Native.IsWindowVisible(hTray)) return;

            Native.RECT r;
            if (!Native.GetWindowRect(hTray, out r)) return;

            Rectangle rc = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rc.Width <= 0 || rc.Height <= 0) return;

            // 自动隐藏时任务栏会滑出屏幕，这时候就别硬贴在边上
            Rectangle screen = Screen.FromHandle(hTray).Bounds;
            bool horizontal = rc.Width >= rc.Height;
            if (horizontal)
            {
                if (rc.Top >= screen.Bottom - 2) return;   // 底部任务栏被隐藏
                if (rc.Bottom <= screen.Top + 2) return;   // 顶部任务栏被隐藏
            }
            else
            {
                if (rc.Left >= screen.Right - 2) return;
                if (rc.Right <= screen.Left + 2) return;
            }

            hasTray = true;
            trayRect = rc;
        }

        private void Reposition()
        {
            if (!hasTray) return;

            bool horizontal = trayRect.Width >= trayRect.Height;

            if (horizontal)
            {
                winX = trayRect.Left + offsetX;
                winY = trayRect.Top + (trayRect.Height - winH) / 2;
            }
            else
            {
                // 任务栏被钉在屏幕左/右侧（需要第三方工具才有的效果）的兜底排布
                winX = trayRect.Left + (trayRect.Width - winW) / 2;
                winY = trayRect.Top + offsetX;
            }

            ClampToTray(ref winX, ref winY, horizontal);
        }

        private void ClampToTray(ref int x, ref int y, bool horizontal)
        {
            if (horizontal)
            {
                int minX = trayRect.Left;
                int maxX = trayRect.Right - winW;
                if (maxX < minX) maxX = minX;
                if (x < minX) x = minX;
                if (x > maxX) x = maxX;
            }
            else
            {
                int minY = trayRect.Top;
                int maxY = trayRect.Bottom - winH;
                if (maxY < minY) maxY = minY;
                if (y < minY) y = minY;
                if (y > maxY) y = maxY;
            }
        }

        // ------------------------------------------------------------------
        // 把 ARGB 位图逐像素贴到分层窗口上（这一步是"没有黑框"的关键）
        // ------------------------------------------------------------------
        private void PushToWindow()
        {
            if (headless) return;
            if (canvas == null) return;
            if (!IsHandleCreated) return;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, winX, winY, winW, winH,
                Native.SWP_NOACTIVATE);

            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hBmp = canvas.GetHbitmap(Color.FromArgb(0));
                hOld = Native.SelectObject(memDc, hBmp);

                Native.POINT dst = new Native.POINT();
                dst.X = winX;
                dst.Y = winY;

                Native.SIZE size = new Native.SIZE();
                size.Cx = winW;
                size.Cy = winH;

                Native.POINT src = new Native.POINT();
                src.X = 0;
                src.Y = 0;

                Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                blend.BlendOp = 0;                    // AC_SRC_OVER
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;                // AC_SRC_ALPHA

                bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                if (!ok)
                {
                    NoteError("UpdateLayeredWindow failed, Win32Error=" + Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (hOld != IntPtr.Zero) Native.SelectObject(memDc, hOld);
                if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
                if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // UpdateLayeredWindow 要求预乘 alpha，GDI+ 给的是直通 alpha，这里转一下，
        // 否则文字抗锯齿边缘会出现一圈毛边。
        private static void Premultiply(Bitmap bmp)
        {
            Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int count = data.Stride / 4 * bmp.Height;
                int[] buffer = new int[count];
                Marshal.Copy(data.Scan0, buffer, 0, count);

                for (int i = 0; i < count; i++)
                {
                    int v = buffer[i];
                    int a = (v >> 24) & 0xFF;
                    if (a == 255) continue;
                    if (a == 0) { buffer[i] = 0; continue; }
                    int r = (((v >> 16) & 0xFF) * a) / 255;
                    int g = (((v >> 8) & 0xFF) * a) / 255;
                    int b = ((v & 0xFF) * a) / 255;
                    buffer[i] = (a << 24) | (r << 16) | (g << 8) | b;
                }

                Marshal.Copy(buffer, 0, data.Scan0, count);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        // ------------------------------------------------------------------
        // 鼠标：左键拖动改位置，右键菜单
        // ------------------------------------------------------------------
        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)Native.MA_NOACTIVATE;
                    return;

                case Native.WM_LBUTTONDOWN:
                    {
                        Native.POINT p;
                        if (Native.GetCursorPos(out p))
                        {
                            dragging = true;
                            dragStartCursorX = p.X;
                            dragOriginOffset = offsetX;
                        }
                        Cursor = Cursors.SizeWE;
                        return;
                    }

                case Native.WM_MOUSEMOVE:
                    if (dragging)
                    {
                        Native.POINT p;
                        if (Native.GetCursorPos(out p))
                        {
                            offsetX = dragOriginOffset + (p.X - dragStartCursorX);
                            Reposition();
                            PushToWindow();
                        }
                        return;
                    }
                    break;

                case Native.WM_LBUTTONUP:
                    if (dragging)
                    {
                        dragging = false;
                        Cursor = Cursors.Default;
                        SaveSettings();
                        return;
                    }
                    break;

                case Native.WM_RBUTTONUP:
                    ShowMenu();
                    return;
            }

            base.WndProc(ref m);
        }

        private void ShowMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Font = SystemFonts.MenuFont;

            // --- 显示样式 子菜单 ---
            ToolStripMenuItem styleMenu = new ToolStripMenuItem("显示样式");
            AddStyleItem(styleMenu, GaugeStyle.Number, "数字百分比");
            AddStyleItem(styleMenu, GaugeStyle.Ring, "环形进度条");
            AddStyleItem(styleMenu, GaugeStyle.Bar, "长条进度条");
            menu.Items.Add(styleMenu);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem auto = new ToolStripMenuItem("开机自动启动");
            auto.Checked = AutoStartEnabled();
            auto.Click += delegate
            {
                SetAutoStart(!AutoStartEnabled());
            };
            menu.Items.Add(auto);

            ToolStripMenuItem reset = new ToolStripMenuItem("重置到最左侧");
            reset.Click += delegate
            {
                offsetX = 0;
                SaveSettings();
                Reposition();
                PushToWindow();
            };
            menu.Items.Add(reset);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { Close(); };
            menu.Items.Add(exit);

            Native.POINT p;
            if (Native.GetCursorPos(out p))
            {
                menu.Show(p.X, p.Y);
            }
        }

        private void AddStyleItem(ToolStripMenuItem parent, GaugeStyle value, string text)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = (style == value);
            item.Click += delegate { ApplyStyle(value); };
            parent.DropDownItems.Add(item);
        }

        private void ApplyStyle(GaugeStyle value)
        {
            if (style == value) return;
            style = value;
            SaveSettings();
            RebuildLayout(true);
        }

        // ------------------------------------------------------------------
        // 设置持久化
        // ------------------------------------------------------------------
        private static string SettingsPath()
        {
            string dir;
            if (Build.PortableSettings)
            {
                // 测试构建：配置放在 exe 旁边，便于脚本化验证
                dir = Path.GetDirectoryName(Application.ExecutablePath);
            }
            else
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Build.SettingsFolder);
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "settings.ini");
        }

        private void LoadSettings()
        {
            offsetX = 0;
            style = GaugeStyle.Number;
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string key = line.Substring(0, idx).Trim();
                    string val = line.Substring(idx + 1).Trim();

                    if (key.Equals("offsetX", StringComparison.OrdinalIgnoreCase))
                    {
                        int n;
                        if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        {
                            offsetX = n;
                        }
                    }
                    else if (key.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("ring", StringComparison.OrdinalIgnoreCase)) style = GaugeStyle.Ring;
                        else if (val.Equals("bar", StringComparison.OrdinalIgnoreCase)) style = GaugeStyle.Bar;
                        else style = GaugeStyle.Number;
                    }
                }
            }
            catch (Exception) { }
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("offsetX=" + offsetX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("style=" + StyleKey(style));
                File.WriteAllText(SettingsPath(), sb.ToString());
            }
            catch (Exception) { }
        }

        private static string StyleKey(GaugeStyle s)
        {
            if (s == GaugeStyle.Ring) return "ring";
            if (s == GaugeStyle.Bar) return "bar";
            return "number";
        }

        private static void NoteError(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarStats");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "error.log");
                if (File.Exists(path) && new FileInfo(path).Length > 65536) return;
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 主题 & 开机启动
        // ------------------------------------------------------------------
        private static bool ReadSystemUsesLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key == null) return false;
                    object v = key.GetValue("SystemUsesLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string StartupShortcutPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "TaskbarStats.lnk");
        }

        private static bool AutoStartEnabled()
        {
            try { return File.Exists(StartupShortcutPath()); }
            catch (Exception) { return false; }
        }

        private static void SetAutoStart(bool enable)
        {
            try
            {
                string link = StartupShortcutPath();
                if (!enable)
                {
                    if (File.Exists(link)) File.Delete(link);
                    return;
                }

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type st = shortcut.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Application.ExecutablePath });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "任务栏 CPU / RAM / GPU 实时监视" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 离线自检：把真正送进分层窗口的那张位图导出，
        // 三种样式 × 深/浅两种底色拼成一张对照图，方便人眼确认。
        // ------------------------------------------------------------------
        public void DumpPreview(string basePath)
        {
            // PDH 是速率计数器，两次收集要间隔够久才有有效差值
            Sample();
            Thread.Sleep(1100);
            Sample();
            Thread.Sleep(1100);
            Sample();

            GaugeStyle original = style;
            bool originalLight = lightTaskbar;

            // 先量出三种样式里最宽的那个，保证每行左对齐
            int maxW = 0;
            List<Size> sizes = new List<Size>();
            for (int s = 0; s < 3; s++)
            {
                style = (GaugeStyle)s;
                RebuildLayout(true);
                sizes.Add(new Size(winW, winH));
                if (winW > maxW) maxW = winW;
            }

            int scale = 4;
            int gap = 10 * scale;
            int rowH = 0;
            for (int i = 0; i < sizes.Count; i++) if (sizes[i].Height > rowH) rowH = sizes[i].Height;

            int sheetW = maxW * scale * 2 + gap;
            int sheetH = rowH * scale * 3;

            try
            {
                using (Bitmap sheet = new Bitmap(sheetW, sheetH, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(sheet))
                    {
                        g.Clear(Color.FromArgb(255, 92, 92, 96));
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;

                        for (int s = 0; s < 3; s++)
                        {
                            style = (GaugeStyle)s;
                            lightTaskbar = false;
                            RebuildLayout(true);
                            Render();
                            using (Bitmap dark = (Bitmap)canvas.Clone())
                            {
                                lightTaskbar = true;
                                Render();
                                using (Bitmap light = (Bitmap)canvas.Clone())
                                {
                                    int y = s * rowH * scale;
                                    using (Bitmap d = Composite(dark, Color.FromArgb(255, 28, 28, 30), maxW, rowH, scale))
                                    using (Bitmap l = Composite(light, Color.FromArgb(255, 243, 243, 243), maxW, rowH, scale))
                                    {
                                        g.DrawImageUnscaled(d, 0, y);
                                        g.DrawImageUnscaled(l, maxW * scale + gap, y);
                                    }
                                }
                            }
                        }
                    }
                    sheet.Save(basePath + "_preview.png", ImageFormat.Png);
                }
            }
            finally
            {
                style = original;
                lightTaskbar = originalLight;
                RebuildLayout(true);
            }

            if (canvas != null) canvas.Save(basePath + "_alpha.png", ImageFormat.Png);

            StringBuilder info = new StringBuilder();
            for (int s = 0; s < 3; s++)
            {
                GaugeStyle st = (GaugeStyle)s;
                style = st;
                RebuildLayout(true);
                info.AppendLine(StyleKey(st) + ": canvas=" + winW + "x" + winH +
                                "  coverage=" + InkCoverage(canvas).ToString("0.0", CultureInfo.InvariantCulture) + "%");
            }
            style = original;
            RebuildLayout(true);

            info.AppendLine("dpi = " + dpi);
            info.AppendLine("font = " + font.Name);
            info.AppendLine("cpu = " + values[MetricCpu].ToString("0.#", CultureInfo.InvariantCulture));
            info.AppendLine("ram = " + values[MetricRam].ToString("0.#", CultureInfo.InvariantCulture));
            info.AppendLine("gpu = " + (valid[MetricGpu] ? values[MetricGpu].ToString("0.#", CultureInfo.InvariantCulture) : "n/a"));
            info.AppendLine("gpu pdh open = " + gpuQueryOpen);
            info.AppendLine("gpu engines seen = " + engineTotals.Count);
            info.AppendLine("light taskbar = " + lightTaskbar);

            Console.WriteLine(info.ToString());
            try { File.WriteAllText(basePath + "_info.txt", info.ToString()); }
            catch (Exception) { }
        }

        private static double InkCoverage(Bitmap bmp)
        {
            int opaque = 0, total = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    total++;
                    if (bmp.GetPixel(x, y).A > 8) opaque++;
                }
            }
            return total == 0 ? 0 : 100.0 * opaque / total;
        }

        private Bitmap Composite(Bitmap src, Color background, int targetW, int targetH, int scale)
        {
            Bitmap bmp = new Bitmap(targetW * scale, targetH * scale, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(background);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceOver;
                // 按自然尺寸放大，不要拉伸到 targetW
                g.DrawImage(src, new Rectangle(0, 0, src.Width * scale, src.Height * scale));
            }
            return bmp;
        }

        public void SetDpiOverride(int value)
        {
            dpiOverride = value;
        }

        // ------------------------------------------------------------------
        // 诊断模式：把影响清晰度的关键参数全部打印出来，便于在出问题的机器上取证
        // ------------------------------------------------------------------
        public void DumpInfo(string basePath)
        {
            Sample();
            RebuildLayout(true);

            int awareness = -1;
            int systemDpi = 0;
            try
            {
                awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext());
                systemDpi = Native.GetDpiForSystem();
            }
            catch (Exception) { }

            string awarenessText;
            if (awareness == 2) awarenessText = "PerMonitorAware";
            else if (awareness == 1) awarenessText = "SystemAware";
            else if (awareness == 0) awarenessText = "Unaware  (会被系统拉伸 => 字会发虚)";
            else awarenessText = "unknown(" + awareness + ")";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("os            = " + Environment.OSVersion.VersionString);
            sb.AppendLine("64bit process = " + (IntPtr.Size == 8));
            sb.AppendLine("dpi (window)  = " + dpi + "    scale = " + (dpi / 96.0).ToString("0.###", CultureInfo.InvariantCulture) + "x");
            sb.AppendLine("dpi (system)  = " + systemDpi);
            sb.AppendLine("dpi awareness = " + awarenessText);
            sb.AppendLine("taskbar rect  = " + (hasTray ? trayRect.ToString() : "<none>"));
            sb.AppendLine("taskbar height= " + (hasTray ? trayRect.Height.ToString(CultureInfo.InvariantCulture) : "n/a"));
            sb.AppendLine("font          = " + font.Name + "  " + font.SizeInPoints.ToString("0.#", CultureInfo.InvariantCulture) + "pt  emPx=" + emPx.ToString("0.##", CultureInfo.InvariantCulture) + "  lineH=" + lineH.ToString("0.##", CultureInfo.InvariantCulture));
            sb.AppendLine("textTop       = " + textTop.ToString("0.##", CultureInfo.InvariantCulture) + "   (整数才代表基线对齐到像素)");
            sb.AppendLine("padX/gap/bigGap = " + padX.ToString("0.##", CultureInfo.InvariantCulture) + " / " + gap.ToString("0.##", CultureInfo.InvariantCulture) + " / " + bigGap.ToString("0.##", CultureInfo.InvariantCulture));

            for (int s = 0; s < 3; s++)
            {
                style = (GaugeStyle)s;
                RebuildLayout(true);
                sb.AppendLine(StyleKey((GaugeStyle)s) + " canvas  = " + winW + "x" + winH + "   仪表宽度=" + gaugeW.ToString("0.##", CultureInfo.InvariantCulture));
            }
            style = GaugeStyle.Number;
            RebuildLayout(true);

            sb.AppendLine("cpu/ram/gpu   = " + values[0].ToString("0.#", CultureInfo.InvariantCulture) + " / " + values[1].ToString("0.#", CultureInfo.InvariantCulture) + " / " + (valid[2] ? values[2].ToString("0.#", CultureInfo.InvariantCulture) : "n/a"));

            Console.WriteLine(sb.ToString());
            try { File.WriteAllText(basePath + "_diag.txt", sb.ToString()); }
            catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
                if (canvas != null) { canvas.Dispose(); canvas = null; }
                if (textMask != null) { textMask.Dispose(); textMask = null; }
                if (font != null) { font.Dispose(); font = null; }

                if (gpuQueryOpen)
                {
                    try { Native.PdhCloseQuery(gpuQuery); }
                    catch (Exception) { }
                    gpuQueryOpen = false;
                    gpuQuery = IntPtr.Zero;
                    gpuCounter = IntPtr.Zero;
                }
                if (gpuBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(gpuBuffer);
                    gpuBuffer = IntPtr.Zero;
                }
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (Exception) { }

            if (args != null && args.Length > 0 && (args[0] == "--dump" || args[0] == "--info"))
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                string basePath = args.Length > 1 ? args[1] : "canvas";
                int forcedDpi = 0;
                if (args.Length > 2) int.TryParse(args[2], out forcedDpi);

                using (Widget probe = new Widget(true))
                {
                    probe.SetDpiOverride(forcedDpi);
                    if (args[0] == "--info") probe.DumpInfo(basePath);
                    else probe.DumpPreview(basePath);
                }
                return;
            }

            bool createdNew;
            using (Mutex single = new Mutex(true, Build.InstanceName, out createdNew))
            {
                if (!createdNew) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    // 不让一个偶发异常把常驻进程干掉
                };
                AppDomain.CurrentDomain.UnhandledException += delegate { };

                Application.Run(new Widget());
            }
        }
    }
}
