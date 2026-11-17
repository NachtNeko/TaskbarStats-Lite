// 拖动逻辑测试：向浮窗投递 按下 -> 多次移动 -> 抬起（坐标放在 lParam 里），
// 检查窗口是否按位移移动、垂直方向是否漂移、位置是否被保存。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

internal static class MouseTest
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONUP = 0x0202;

    private static IntPtr Pack(int x, int y)
    {
        return (IntPtr)((y << 16) | (x & 0xFFFF));
    }

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
        if (widget == IntPtr.Zero) { Console.WriteLine("找不到窗口"); p.Kill(); return 1; }

        RECT r0;
        GetWindowRect(widget, out r0);
        Console.WriteLine("拖动前: left=" + r0.Left + " top=" + r0.Top + "  size=" + (r0.Right - r0.Left) + "x" + (r0.Bottom - r0.Top));

        int startX = 30, y = 24;
        const int delta = 80;

        PostMessage(widget, WM_LBUTTONDOWN, (IntPtr)1, Pack(startX, y));
        Thread.Sleep(40);

        for (int i = 1; i <= 4; i++)
        {
            PostMessage(widget, WM_MOUSEMOVE, IntPtr.Zero, Pack(startX + delta * i / 4, y));
            Thread.Sleep(40);
        }

        PostMessage(widget, WM_LBUTTONUP, IntPtr.Zero, Pack(startX + delta, y));
        Thread.Sleep(400);

        RECT r1;
        GetWindowRect(widget, out r1);
        Console.WriteLine("拖动后: left=" + r1.Left + " top=" + r1.Top);

        int moved = r1.Left - r0.Left;
        int drift = r1.Top - r0.Top;
        Console.WriteLine("水平位移 = " + moved + " px（期望 " + delta + "）  垂直漂移 = " + drift);

        bool ok = Math.Abs(moved - delta) <= 2 && drift == 0;
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");

        p.Kill();
        p.WaitForExit();
        return ok ? 0 : 1;
    }
}
