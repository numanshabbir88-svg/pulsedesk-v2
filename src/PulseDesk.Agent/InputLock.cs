using System.Runtime.InteropServices;

namespace PulseDesk.Agent;

/// <summary>
/// Blocks the employee's real keyboard and mouse while the admin has remote control switched on.
///
/// How it works: low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) see every input event before it
/// reaches any application. Events that came from real hardware are swallowed; events injected by
/// PulseDesk (SendInput, flagged LLKHF_INJECTED / LLMHF_INJECTED) are passed through, so the admin
/// keeps full control. A timer re-asserts the admin's cursor position because Windows may still
/// move the pointer for a few pixels before the hook runs.
///
/// Safety: the employee can always press Ctrl+Alt+Del (Windows security screen is never hooked),
/// and the always-on-top banner shows that control is active.
/// </summary>
internal static class InputLock
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x02;

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

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    // delegated instances are static so the GC never collects the hook callbacks
    private static readonly HookProc KbProc = KeyboardProc;
    private static readonly HookProc MsProc = MouseProc;

    private static IntPtr _kb, _ms;
    private static volatile bool _blocking;
    private static volatile bool _hooked;
    private static volatile int _targetX = int.MinValue;
    private static volatile int _targetY;

    /// <summary>Start blocking local input. Must be called from the banner UI thread.</summary>
    public static void Enable()
    {
        _blocking = true;
        Install();
    }

    /// <summary>Stop blocking and remove the hooks. Must be called from the banner UI thread.</summary>
    public static void Disable()
    {
        _blocking = false;
        Uninstall();
    }

    /// <summary>Remember where the admin put the cursor, so it can be re-asserted.</summary>
    public static void SetTarget(int x, int y)
    {
        _targetX = x;
        _targetY = y;
    }

    /// <summary>Called ~8x per second by the banner timer while control is active.</summary>
    public static void ReassertCursor()
    {
        if (!_blocking) return;
        int x = _targetX;
        if (x == int.MinValue) return;
        try { SetCursorPos(x, _targetY); } catch { }
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
            if ((info.flags & LLKHF_INJECTED) == 0)
                return (IntPtr)1;      // swallow the employee's key
        }
        return CallNextHookEx(_kb, nCode, wParam, lParam);
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _blocking)
        {
            var info = Marshal.PtrToStructure<MouseHook>(lParam);
            if ((info.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) == 0)
                return (IntPtr)1;      // swallow the employee's move / click / wheel
        }
        return CallNextHookEx(_ms, nCode, wParam, lParam);
    }
}
