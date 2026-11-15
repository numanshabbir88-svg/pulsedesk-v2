using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PulseDesk.Agent;

/// <summary>
/// Remote mouse + keyboard injection for the admin console (Windows agent only).
/// Input is accepted ONLY while the admin has switched remote control on for this device
/// ("control-on"), and a small always-on-top banner tells the user that is happening.
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

    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    private const uint RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    private const uint MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040;
    private const uint WHEEL = 0x0800, HWHEEL = 0x01000;

    private const uint KEYUP = 0x0002, UNICODE = 0x0004, EXTENDEDKEY = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // ---------------------------------------------------------------- mouse
    public static void Move(double nx, double ny)
    {
        var b = ScreenBounds();
        int x = b.Left + (int)Math.Round(Math.Clamp(nx, 0, 1) * (b.Width - 1));
        int y = b.Top + (int)Math.Round(Math.Clamp(ny, 0, 1) * (b.Height - 1));
        SetCursorPos(x, y);
        InputLock.SetTarget(x, y);
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

    private static void Click(uint flag, uint data)
    {
        var inp = new INPUT[1];
        inp[0].type = INPUT_MOUSE;
        inp[0].u.mi.dwFlags = flag;
        inp[0].u.mi.mouseData = data;
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
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
        SendInput(1, inp, Marshal.SizeOf<INPUT>());
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
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT UnicodeKey(char ch, bool up)
    {
        var inp = new INPUT();
        inp.type = INPUT_KEYBOARD;
        inp.u.ki.wVk = 0;
        inp.u.ki.wScan = ch;
        inp.u.ki.dwFlags = UNICODE | (up ? KEYUP : 0);
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
/// Small always-on-top badge so the employee can see that the admin is controlling the PC.
/// Visibility is controlled by the agent ("control-on" / "control-off" commands).
/// </summary>
internal static class ControlBanner
{
    private static Form? _form;
    private static System.Windows.Forms.Timer? _pulse;
    private static readonly object _gate = new();
    private static bool _started;

    public static void Show() => Toggle(true);
    public static void Hide() => Toggle(false);

    private static void Toggle(bool visible)
    {
        lock (_gate)
        {
            if (!_started)
            {
                _started = true;
                StartThread();
            }
        }

        var f = _form;
        if (f == null)
        {
            // form is still being created on its own thread - retry shortly
            Task.Delay(250).ContinueWith(_ => Toggle(visible));
            return;
        }

        try
        {
            if (f.IsHandleCreated)
                f.BeginInvoke(new Action(() => Apply(f, visible)));
            else
                f.HandleCreated += (_, _) => f.BeginInvoke(new Action(() => Apply(f, visible)));
        }
        catch { }
    }

    private static void Apply(Form f, bool visible)
    {
        try
        {
            f.Visible = visible;
            if (visible) f.TopMost = true;

            // employee keyboard + mouse are blocked only while control is on
            if (visible) InputLock.Enable(); else InputLock.Disable();
            if (_pulse != null) { if (visible) _pulse.Start(); else _pulse.Stop(); }
        }
        catch { }
    }

    private static void StartThread()
    {
        var t = new Thread(() =>
        {
            try
            {
                var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
                var width = 400;
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    ClientSize = new Size(width, 38),
                    Location = new Point(screen.Left + (screen.Width - width) / 2, screen.Top + 14),
                    BackColor = Color.FromArgb(13, 59, 57),
                    ShowInTaskbar = false,
                    TopMost = true,
                    Visible = false
                };
                f.Controls.Add(new Label
                {
                    Text = "PulseDesk - admin control active (keyboard + mouse locked)",
                    ForeColor = Color.FromArgb(223, 236, 232),
                    Font = new Font("Segoe UI", 9.5f),
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter
                });
                _form = f;

                // keeps the cursor pinned where the admin left it (employee mouse cannot drift it)
                _pulse = new System.Windows.Forms.Timer { Interval = 120 };
                _pulse.Tick += (_, _) => InputLock.ReassertCursor();

                _ = f.Handle;
                Application.Run(new ApplicationContext(f));
            }
            catch (Exception ex) { Settings.Log("control banner: " + ex.Message); }
        })
        { IsBackground = true, Name = "PulseDeskControlBanner" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }
}
