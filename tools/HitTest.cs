// 命中测试探针：验证分层窗口哪些像素能被鼠标"点到"。
// 用 WindowFromPoint 询问系统"这个屏幕点会发给哪个窗口"，和浮窗句柄比对。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class HitTest
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    private static int Main(string[] args)
    {
        string exe = args.Length > 0 ? args[0] : "TaskbarStats.exe";
        int wait = args.Length > 1 ? int.Parse(args[1]) : 2600;

        Process p = Process.Start(exe);
        Thread.Sleep(wait);
        if (p.HasExited) { Console.WriteLine("EXITED code=" + p.ExitCode); return 1; }

        IntPtr widget = IntPtr.Zero;
        uint target = (uint)p.Id;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target)
            {
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x80000) != 0) { widget = h; return false; }
            }
            return true;
        }, IntPtr.Zero);

        if (widget == IntPtr.Zero) { Console.WriteLine("找不到分层窗口"); p.Kill(); return 1; }

        RECT r;
        GetWindowRect(widget, out r);
        int w = r.Right - r.Left, h2 = r.Bottom - r.Top;
        Console.WriteLine("窗口 rect = " + r.Left + "," + r.Top + "  " + w + "x" + h2);

        // 采样点：四角(一定是空白)、正中、字形行附近
        POINT[] pts = new POINT[]
        {
            new POINT { X = r.Left + 2,        Y = r.Top + 2 },          // 左上角空白
            new POINT { X = r.Left + 2,        Y = r.Top + h2 / 2 },     // 左边空白
            new POINT { X = r.Right - 3,       Y = r.Top + h2 / 2 },     // 右边空白
            new POINT { X = r.Left + w / 2,    Y = r.Top + h2 / 2 },     // 正中心
            new POINT { X = r.Left + w / 4,    Y = r.Top + h2 / 2 },     // 四分之一处
            new POINT { X = r.Left + w / 2,    Y = r.Top + 2 },          // 顶部中间
        };
        string[] names = { "左上角", "左边中", "右边中", "正中心", "四分之一", "顶部中间" };

        int hit = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            IntPtr under = WindowFromPoint(pts[i]);
            // 取顶层祖先，便于判断是不是任务栏
            IntPtr top = GetAncestor(under, 2 /*GA_ROOT*/);
            StringBuilder cls = new StringBuilder(256);
            GetClassNameW(top, cls, 256);

            bool isOurs = (under == widget) || (top == widget);
            if (isOurs) hit++;
            Console.WriteLine(string.Format("  {0,-9} ({1,4},{2,3})  ->  {3}  [{4}]",
                names[i], pts[i].X, pts[i].Y, isOurs ? "我们的窗口" : "别的东西", cls));
        }

        Console.WriteLine("命中 " + hit + " / " + pts.Length);
        Console.WriteLine(hit == pts.Length ? "RESULT: PASS 整块矩形都可点击" : "RESULT: FAIL 有区域穿透到下层");

        p.Kill();
        p.WaitForExit();
        return 0;
    }
}
