// 验证探针：启动 TaskbarStats，取窗口几何、抓屏差分，输出证据。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Probe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string cls, string win);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest,
        int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    private static int Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : "TaskbarStats.exe";
        int waitMs = args.Length > 1 ? int.Parse(args[1]) : 3200;

        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        RECT tr;
        GetWindowRect(tray, out tr);
        Console.WriteLine("taskbar rect = " + tr.Left + "," + tr.Top + "," + tr.Right + "," + tr.Bottom);

        int bandTop = tr.Top;
        int bandH = tr.Bottom - tr.Top;
        int bandW = 640;

        Bitmap before = Capture(0, bandTop - 4, bandW, bandH + 8);
        before.Save("probe_before.png", ImageFormat.Png);

        Process p = Process.Start(exe);
        Thread.Sleep(waitMs);
        Console.WriteLine("alive = " + (!p.HasExited));

        // 枚举属于该进程的顶层窗口
        List<IntPtr> wins = new List<IntPtr>();
        uint target = (uint)p.Id;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target) wins.Add(h);
            return true;
        }, IntPtr.Zero);

        foreach (IntPtr h in wins)
        {
            RECT r;
            GetWindowRect(h, out r);
            StringBuilder cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            int ex = GetWindowLong(h, -20);
            Console.WriteLine("hwnd=0x" + h.ToInt64().ToString("X") +
                " class=" + cls +
                " visible=" + IsWindowVisible(h) +
                " rect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom +
                " size=" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                " ex=0x" + ex.ToString("X"));
            Console.WriteLine("   layered=" + ((ex & 0x80000) != 0) +
                " toolwindow=" + ((ex & 0x80) != 0) +
                " noactivate=" + ((ex & 0x08000000) != 0) +
                " topmost=" + ((ex & 0x8) != 0));
        }

        Thread.Sleep(400);
        Bitmap after = Capture(0, bandTop - 4, bandW, bandH + 8);
        after.Save("probe_after.png", ImageFormat.Png);

        // 像素差分
        int w = after.Width, h2 = after.Height;
        int changed = 0, minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        bool hasAlphaIssue = false;
        for (int y = 0; y < h2; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int a = before.GetPixel(x, y).ToArgb();
                int b = after.GetPixel(x, y).ToArgb();
                if (a != b)
                {
                    changed++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if ((b >> 24) == 0) hasAlphaIssue = true;
                }
            }
        }
        Console.WriteLine("changed pixels = " + changed);
        if (changed > 0)
        {
            Console.WriteLine("diff bbox = x " + minX + ".." + maxX + "  y " + minY + ".." + maxY +
                "   (screen y " + (bandTop - 4 + minY) + ".." + (bandTop - 4 + maxY) + ")");
        }

        // 远端的对照条必须完全没变（证明没有背景色块）
        int ctrlChanged = 0;
        for (int y = 0; y < h2; y++)
            for (int x = 500; x < 640; x++)
                if (before.GetPixel(x, y).ToArgb() != after.GetPixel(x, y).ToArgb()) ctrlChanged++;
        Console.WriteLine("control band (x 500..640) changed = " + ctrlChanged);

        // 放大存图，便于人眼查看
        int cropW = Math.Min(w, Math.Max(200, maxX + 20));
        Bitmap crop = after.Clone(new Rectangle(0, 0, cropW, h2), after.PixelFormat);
        SaveScaled(crop, cropW * 3, h2 * 3, "probe_zoom.png");

        // 单独抓窗口自身内容（能看到文字渲染质量）
        foreach (IntPtr h in wins)
        {
            RECT r;
            GetWindowRect(h, out r);
            int ww = r.Right - r.Left, wh = r.Bottom - r.Top;
            if (ww <= 4 || wh <= 4) continue;
            Bitmap wb = new Bitmap(ww, wh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(wb))
            {
                IntPtr dc = g.GetHdc();
                PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
            }
            SaveScaled(wb, ww * 4, wh * 4, "probe_window.png");
            Console.WriteLine("window bitmap saved " + ww + "x" + wh);
            break;
        }

        p.Kill();
        p.WaitForExit();
        Console.WriteLine("killed");
        return 0;
    }

    private static Bitmap Capture(int x, int y, int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            IntPtr dst = g.GetHdc();
            IntPtr src = GetDC(IntPtr.Zero);
            BitBlt(dst, 0, 0, w, h, src, x, y, SRCCOPY | CAPTUREBLT);
            ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dst);
        }
        return bmp;
    }

    private static void SaveScaled(Bitmap src, int w, int h, string path)
    {
        using (Bitmap big = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, 0, 0, w, h);
            }
            big.Save(path, ImageFormat.Png);
        }
    }
}
