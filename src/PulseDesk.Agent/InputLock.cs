using System.Runtime.InteropServices;

namespace PulseDesk.Agent;

/// <summary>
/// Blocks the employee's real keyboard and mouse while the admin has remote control switched on.
///
/// How it works: low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) see every input event before it
/// reaches any application. Events that came from real hardware are swallowed; only events injected
/// by PulseDesk itself are passed through. Every RemoteInput SendInput call carries a private tag
/// in dwExtraInfo, so the hook can tell admin input apart from other injected input. Cursor moves
/// use tagged SendInput too (SetCursorPos generated untagged events that this hook swallowed).
///
/// Safety: the employee can always press Ctrl+Alt+Del (Windows security screen is never hooked).
/// No on-screen banner is shown - every control START/STOP is recorded in the admin Alerts.
/// </summary>
internal static class InputLock
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    /// <summary>dwExtraInfo tag carried by every input event this agent injects for the admin.</summary>
    internal static readonly IntPtr InjectionTag = new(0x5053444B);   // "PSDK"

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdHook
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHook
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    // delegated instances are static so the GC never collects the hook callbacks
    private static readonly HookProc KbProc = KeyboardProc;
    private static readonly HookProc MsProc = MouseProc;

    private static IntPtr _kb, _ms;
    private static volatile bool _blocking;
    private static volatile bool _hooked;

    /// <summary>Start blocking local input. Must be called from the hidden hook thread.</summary>
    public static void Enable()
    {
        _blocking = true;
        Install();
    }

    /// <summary>Stop blocking and remove the hooks. Must be called from the hidden hook thread.</summary>
    public static void Disable()
    {
        _blocking = false;
        Uninstall();
    }

    private static void Install()
    {
        if (_hooked) return;
        try
        {
            var mod = GetModuleHandleW(null);
            _kb = SetWindowsHookExW(WH_KEYBOARD_LL, KbProc, mod, 0);
            _ms = SetWindowsHookExW(WH_MOUSE_LL, MsProc, mod, 0);
            _hooked = _kb != IntPtr.Zero && _ms != IntPtr.Zero;
            if (!_hooked)
            {
                var err = Marshal.GetLastWin32Error();
                if (_kb != IntPtr.Zero) { UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
                if (_ms != IntPtr.Zero) { UnhookWindowsHookEx(_ms); _ms = IntPtr.Zero; }
                Settings.Log($"input lock: hook failed err={err} (employee input NOT blocked)");
            }
            else Settings.Log("input lock: ON (employee keyboard/mouse blocked)");
        }
        catch (Exception ex) { Settings.Log("input lock: " + ex.Message); }
    }

    private static void Uninstall()
    {
        try
        {
            if (_kb != IntPtr.Zero) { UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
            if (_ms != IntPtr.Zero) { UnhookWindowsHookEx(_ms); _ms = IntPtr.Zero; }
        }
        catch { }
        if (_hooked) Settings.Log("input lock: OFF (employee input restored)");
        _hooked = false;
    }

    private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _blocking)
        {
            var info = Marshal.PtrToStructure<KbdHook>(lParam);
            // Only our own tagged admin input is allowed through. Everything else is blocked,
            // including other injected input - Ctrl+Alt+Del still works (secure desktop is not hooked).
            if (info.extra != InjectionTag) return (IntPtr)1;
        }
        return CallNextHookEx(_kb, nCode, wParam, lParam);
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _blocking)
        {
            var info = Marshal.PtrToStructure<MouseHook>(lParam);
            if (info.extra != InjectionTag) return (IntPtr)1;
        }
        return CallNextHookEx(_ms, nCode, wParam, lParam);
    }
}
