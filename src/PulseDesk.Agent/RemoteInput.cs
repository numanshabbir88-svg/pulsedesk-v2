using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PulseDesk.Agent;

/// <summary>
/// Remote mouse + keyboard injection for the admin console (Windows agent only).
/// Input is accepted ONLY while the admin has switched remote control on for this device
/// ("control-on"). Control is silent on the employee's screen; the input hooks run on a hidden
/// STA thread and every START/STOP is written to the admin Alerts.
/// </summary>
internal static class RemoteInput
{
    // ---------------------------------------------------------------- win32
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    private const uint RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    private const uint MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040;
    private const uint WHEEL = 0x0800, HWHEEL = 0x01000;

    private const uint KEYUP = 0x0002, UNICODE = 0x0004, EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int X, int Y);

    private static long _lastFailLog;

    /// <summary>
    /// SendInput can silently fail (UIPI when the target window is more privileged, or a
    /// disconnected input desktop). Log it instead of failing invisibly, and fall back for moves.
    /// </summary>
    private static uint Send(INPUT[] inputs)
    {
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            long now = Environment.TickCount64;
            if (now - _lastFailLog > 5000)
            {
                _lastFailLog = now;
                Settings.Log($"remote input: SendInput failed err={Marshal.GetLastWin32Error()}");
            }
        }
        return sent;
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ---------------------------------------------------------------- mouse
    public static void Move(double nx, double ny)
    {
        var b = ScreenBounds();
        int x = b.Left + (int)Math.Round(Math.Clamp(nx, 0, 1) * (b.Width - 1));
        int y = b.Top + (int)Math.Round(Math.Clamp(ny, 0, 1) * (b.Height - 1));
        // SetCursorPos used to generate an UNTAGGED mouse event that InputLock's own hook
        // swallowed, so the pointer never really moved. Use tagged injected input instead:
        // the hook recognizes our tag, passes the move, and Windows updates the real cursor.
        MoveAbsolute(x, y, b);
    }

    private static void MoveAbsolute(int x, int y, Rectangle b)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].u.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE;
        inp[0].u.mi.dx = (int)Math.Round((x - b.Left) * 65535.0 / Math.Max(1, b.Width - 1));
        inp[0].u.mi.dy = (int)Math.Round((y - b.Top) * 65535.0 / Math.Max(1, b.Height - 1));
        inp[0].u.mi.dwExtraInfo = InputLock.InjectionTag;
        if (Send(inp) == 0)
        {
            // Untagged SetCursorPos is not blocked by the low-level mouse hook (no injected
            // event is generated), so the admin still gets a visible cursor on a failed SendInput.
            try { SetCursorPos(x, y); } catch { }
        }
    }

    public static void Button(string button, bool down)
    {
        uint flag = (button, down) switch
        {
            ("right", true) => RIGHTDOWN,
            ("right", false) => RIGHTUP,
            ("middle", true) => MIDDLEDOWN,
            ("middle", false) => MIDDLEUP,
            (_, true) => LEFTDOWN,
            (_, false) => LEFTUP,
        };
        Click(flag, 0);
    }

    public static void Wheel(int delta, bool horizontal = false)
    {
        Click(horizontal ? HWHEEL : WHEEL, unchecked((uint)delta));
    }

    /// <summary>Harmless key tap (F15) used to reset the idle timer / wake the display.</summary>
    public static void Nudge()
    {
        try
        {
            Key(0x7E, true);   // VK_F15 - no application uses it
            Key(0x7E, false);
        }
        catch { }
    }

    private static void Click(uint flag, uint data)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].u.mi.dwFlags = flag;
        inp[0].u.mi.mouseData = data;
        inp[0].u.mi.dwExtraInfo = InputLock.InjectionTag;
        Send(inp);
    }

    // ---------------------------------------------------------------- keyboard
    public static void Key(int vk, bool down)
    {
        if (vk <= 0 || vk > 0xFF) return;
        var inp = new INPUT[1];
        inp[0].type = INPUT_KEYBOARD;
        inp[0].u.ki.wVk = (ushort)vk;
        inp[0].u.ki.wScan = (ushort)MapVirtualKey((uint)vk, 0);
        uint flags = down ? 0 : KEYUP;
        if (IsExtended(vk)) flags |= EXTENDEDKEY;
        inp[0].u.ki.dwFlags = flags;
        inp[0].u.ki.dwExtraInfo = InputLock.InjectionTag;
        Send(inp);
    }

    /// <summary>Types literal text (layout independent) - used for normal typing.</summary>
    public static void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var list = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            list.Add(UnicodeKey(ch, false));
            list.Add(UnicodeKey(ch, true));
        }
        Send(list.ToArray());
    }

    private static INPUT UnicodeKey(char ch, bool up)
    {
        var inp = new INPUT();
        inp.type = INPUT_KEYBOARD;
        inp.u.ki.wVk = 0;
        inp.u.ki.wScan = ch;
        inp.u.ki.dwFlags = UNICODE | (up ? KEYUP : 0);
        inp.u.ki.dwExtraInfo = InputLock.InjectionTag;
        return inp;
    }

    private static bool IsExtended(int vk) => vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
        or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3;

    // ---------------------------------------------------------------- helpers
    private static Rectangle ScreenBounds()
    {
        try
        {
            var b = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
            if (b.Width > 0 && b.Height > 0) return b;
        }
        catch { }
        var v = SystemInformation.VirtualScreen;
        return v.Width > 0 ? v : new Rectangle(0, 0, 1920, 1080);
    }
}

/// <summary>
/// Headless host for the low-level input-lock hooks. Nothing is shown on the employee's
/// screen: consent is recorded at install time and every START/STOP is written to Alerts.
/// A hidden STA thread with a message pump is still required - Windows delivers low-level
/// keyboard/mouse hook callbacks only on a thread that pumps messages.
/// </summary>
internal static class ControlBanner
{
    private static Form? _host;          // hidden message-pump host, never made visible
    private static readonly object _gate = new();
    private static bool _started;

    public static void Show() => Toggle(true);
    public static void Hide() => Toggle(false);

    private static void Toggle(bool active)
    {
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                StartThread();
            }
        }

        var f = _host;
        if (f == null)
        {
            Task.Delay(250).ContinueWith(_ => Toggle(active));
            return;
        }

        try
        {
            if (f.IsHandleCreated)
                f.BeginInvoke(new Action(() => Apply(f, active)));
            else
                f.HandleCreated += (_, _) => f.BeginInvoke(new Action(() => Apply(f, active)));
        }
        catch { }
    }

    private static void Apply(Form f, bool active)
    {
        try
        {
            if (active) InputLock.Enable(); else InputLock.Disable();
        }
        catch { }
    }

    private static void StartThread()
    {
        var t = new Thread(() =>
        {
            try
            {
                // 1x1 hidden form: exists only to keep an STA message loop alive for the hooks.
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    ClientSize = new Size(1, 1),
                    ShowInTaskbar = false,
                    Visible = false
                };
                _host = f;

                _ = f.Handle;
                Application.Run(new ApplicationContext(f));
            }
            catch (Exception ex) { Settings.Log("control hooks: " + ex.Message); }
        })
        { IsBackground = true, Name = "PulseDeskControlHooks" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
