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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex,
        System.Text.StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const int UOI_NAME = 2;
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>
    /// True when Windows is showing the lock screen / secure desktop (Logon UI).
    /// PulsesDesk cannot type/send input there - Windows blocks injection into that desktop.
    /// </summary>
    internal static bool IsScreenLocked()
    {
        IntPtr desk = IntPtr.Zero;
        try
        {
            desk = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (desk == IntPtr.Zero) return true;   // secure desktop active, cannot open it
            var sb = new System.Text.StringBuilder(64);
            if (GetUserObjectInformation(desk, UOI_NAME, sb, sb.Capacity * 2, out _))
            {
                var name = sb.ToString().Trim();
                return name.Length > 0 && !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (desk != IntPtr.Zero) CloseDesktop(desk);
        }
    }

    /// <summary>
    /// Keeps the display/session awake while the admin is in control, so the laptop does not
    /// fall asleep or auto-lock in the middle of a support session. State is per thread.
    /// </summary>
    internal static void KeepAwake(bool on)
    {
        try
        {
            SetThreadExecutionState(ES_CONTINUOUS | (on ? ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : 0));
        }
        catch { }
    }

    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    // ---- fast screen copy (GDI StretchBlt: capture + downscale in one step) ----
    internal const int HALFTONE = 4;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr hDc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hDc, IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    internal static extern int SetStretchBltMode(IntPtr hDc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool StretchBlt(IntPtr hDcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hDcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr hDcDest, int xDest, int yDest, int w, int h,
        IntPtr hDcSrc, int xSrc, int ySrc, uint rop);

    internal const uint SRCCOPY = 0x00CC0020;

    // ---- cursor drawing (BitBlt never captures the mouse pointer) ----
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    internal static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
        int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    internal const int CURSOR_SHOWING = 0x00000001;
    internal const uint DI_NORMAL = 0x0003;
    internal const int SM_CXCURSOR = 17;
    internal const int SM_CYCURSOR = 18;

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
