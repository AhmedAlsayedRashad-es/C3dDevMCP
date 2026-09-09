using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace C3dMCP.Server.Core;

/// <summary>Screenshots of the Civil3D windows (PrintWindow, so no focus change) and the
/// ESC-only-if-foreground rule lifted from the old shim.</summary>
public static class Recovery
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    public static List<string> Screenshot(int pid)
    {
        var files = new List<string>();
        Directory.CreateDirectory(Paths.Screenshots);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var windows = new List<(IntPtr h, string title)>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                windows.Add((h, sb.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        int n = 0;
        foreach (var (h, title) in windows)
        {
            if (!GetWindowRect(h, out var r)) continue;
            int w = r.R - r.L, ht = r.B - r.T;
            if (w < 60 || ht < 60) continue;
            try
            {
                using var bmp = new Bitmap(w, ht);
                using var g = Graphics.FromImage(bmp);
                var dc = g.GetHdc();
                PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
                var safe = new string(title.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').Take(30).ToArray());
                var file = Path.Combine(Paths.Screenshots, $"c3dshot-{stamp}-{n++}-{safe}.png");
                bmp.Save(file, ImageFormat.Png);
                files.Add(file);
            }
            catch { }
        }
        return files;
    }

    /// <summary>Type ESC ESC into Civil3D only when it already is the foreground window (never
    /// into another app). Returns true when sent.</summary>
    public static bool SendEscIfForeground(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            SetForegroundWindow(p.MainWindowHandle);
            Thread.Sleep(300);
            GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
            if (fg != pid) return false;
            const byte VK_ESCAPE = 0x1B; const uint KEYUP = 2;
            for (int i = 0; i < 2; i++) { keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero); keybd_event(VK_ESCAPE, 0, KEYUP, UIntPtr.Zero); Thread.Sleep(120); }
            return true;
        }
        catch { return false; }
    }
}
