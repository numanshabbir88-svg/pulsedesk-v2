using System.Runtime.InteropServices;

namespace PulseDesk.Agent;

/// <summary>Win32 helpers used by the screen collector.</summary>
internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int nIndex);

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    internal static int IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = 8 };
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)((GetTickCount() - info.dwTime) / 1000);
    }

    internal static (string process, string title) Foreground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return ("", "");
        int len = GetWindowTextLength(h);
        var sb = new System.Text.StringBuilder(Math.Max(4, len + 2));
        GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        string proc = "";
        try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { }
        return (proc, sb.ToString());
    }
}
