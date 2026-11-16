using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PulseDesk.Agent;

/// <summary>
/// Employee-facing home window (opened by the desktop / Start Menu "PulseDesk" icon).
///
/// First run after installation it asks for consent with a single red "Allow" button.
/// Nothing is recorded until that button is pressed - the agent stays paused, so
/// this window is the actual consent record. Later runs show the current state.
/// Deliberately calm and branded: no scary system dialogs and no monitoring pop-ups.
/// </summary>
internal sealed class EmployeeHome : Form
{
    private enum HomeState { Consent, Active, Paused }
    private enum StatusKind { On, Waiting, Error }

    // ---- brand palette ------------------------------------------------------
    private static readonly Color Bg = Color.FromArgb(10, 47, 45);
    private static readonly Color Main = Color.FromArgb(241, 248, 246);
    private static readonly Color Dim = Color.FromArgb(166, 196, 190);
    private static readonly Color Accent = Color.FromArgb(47, 184, 160);
    private static readonly Color Red = Color.FromArgb(211, 58, 58);
    private static readonly Color RedHover = Color.FromArgb(233, 74, 74);
    private static readonly Color Error = Color.FromArgb(255, 158, 158);

    private static readonly Font FTitle = new("Segoe UI Semibold", 14F);
    private static readonly Font FSub = new("Segoe UI", 9F);
    private static readonly Font FHero = new("Segoe UI Semibold", 23F);
    private static readonly Font FHeroSub = new("Segoe UI", 10F);
    private static readonly Font FBody = new("Segoe UI", 9F);
    private static readonly Font FSmall = new("Segoe UI", 8.75F);
    private static readonly Font FLogo = new("Segoe UI Semibold", 19F);
    private static readonly Font FClose = new("Segoe UI", 11F);

    private const int W = 580;
    private const int H = 436;

    private readonly Label _hero;
    private readonly Label _heroSub;
    private readonly StatusPill _pill;
    private readonly Label _note;
    private readonly Label _error;
    private readonly PillButton _allow;
    private readonly LinkLabel _later;

    private HomeState _state = HomeState.Consent;
    private bool _busy;

    public static int ShowHome()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch { }

        using var f = new EmployeeHome();
        f.ShowDialog();
        return 0;
    }

    private EmployeeHome()
    {
        Text = "PulseDesk";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(W, H);
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = FBody;
        DoubleBuffered = true;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // window close chip (the form is borderless so it needs one)
        var close = new Label
        {
            Text = "\u2715",
            Font = FClose,
            ForeColor = Dim,
            BackColor = Bg,
            Bounds = new Rectangle(W - 58, 18, 34, 34),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        close.MouseEnter += (_, _) => close.ForeColor = Main;
        close.MouseLeave += (_, _) => close.ForeColor = Dim;
        close.Click += (_, _) => Close();
        Controls.Add(close);

        // logo badge (rounded panel + letter)
        var logo = new Panel { Bounds = new Rectangle(32, 22, 44, 44), BackColor = Accent };
        RoundRegion(logo, 12);
        logo.Controls.Add(new Label
        {
            Text = "P",
            Font = FLogo,
            ForeColor = Color.White,
            BackColor = Accent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        });
        Controls.Add(logo);

        Controls.Add(MakeLabel("PulseDesk", FTitle, Main, 86, 20, 300, 26));
        Controls.Add(MakeLabel("Company support app", FSub, Dim, 86, 47, 300, 20));

        _hero = MakeLabel("Thanks for connecting with us", FHero, Main, 32, 92, 516, 40);
        _heroSub = MakeLabel("", FHeroSub, Dim, 32, 134, 516, 44);
        Controls.Add(_hero);
        Controls.Add(_heroSub);

        _pill = new StatusPill { Location = new Point(32, 190) };
        Controls.Add(_pill);

        _note = MakeLabel("", FSmall, Dim, 32, 238, 516, 54);
        _error = MakeLabel("", FSmall, Error, 32, 296, 516, 30);
        Controls.Add(_note);
        Controls.Add(_error);

        _allow = new PillButton
        {
            Bounds = new Rectangle(32, 330, 516, 50),
            Text = "Allow",
            Fill = Red,
            FillHover = RedHover
        };
        _allow.Click += OnAllow;
        Controls.Add(_allow);

        _later = new LinkLabel
        {
            Text = "Not now",
            Bounds = new Rectangle(32, 388, 516, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            LinkColor = Dim,
            ActiveLinkColor = Main,
            VisitedLinkColor = Dim,
            BackColor = Bg,
            Font = FSmall,
            TabStop = true
        };
        _later.LinkClicked += (_, _) => Close();
        Controls.Add(_later);

        ApplyState(ReadState());

        // Opened from the icon on an already-consented machine: make sure the background
        // collector is actually running (e.g. after it was stopped manually).
        if (_state == HomeState.Active) Installer.EnsureAgent();
    }

    // ---- layout helpers ----------------------------------------------------
    private static Label MakeLabel(string text, Font font, Color color, int x, int y, int w, int h,
        Color? back = null) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        BackColor = back ?? Bg,
        Bounds = new Rectangle(x, y, w, h),
        AutoSize = false,
        UseMnemonic = false
    };

    /// <summary>Clips a control to a rounded rectangle (logo badge); re-applied on resize.</summary>
    private static void RoundRegion(Control c, int radius)
    {
        void Apply()
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using var path = Rounded(new Rectangle(0, 0, c.Width, c.Height), radius);
            var old = c.Region;
            c.Region = new Region(path);
            old?.Dispose();
        }
        c.Resize += (_, _) => Apply();
        c.HandleCreated += (_, _) => Apply();
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // borderless window: drag it from the empty background
    private const int WM_NCHITTEST = 0x84, HTCAPTION = 2;
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == (IntPtr)1) m.Result = HTCAPTION;
    }

    // ---- state -------------------------------------------------------------
    private static HomeState ReadState()
    {
        var s = Settings.Load();
        if (string.IsNullOrWhiteSpace(s.DeviceToken) || string.IsNullOrWhiteSpace(s.ConsentAt))
            return HomeState.Consent;
        return s.Paused ? HomeState.Paused : HomeState.Active;
    }

    private void ApplyState(HomeState state)
    {
        _state = state;
        _error.Text = "";

        switch (state)
        {
            case HomeState.Consent:
                _hero.Text = "Thanks for connecting with us";
                _heroSub.Text = "PulseDesk is installed on this computer. Press Allow below to finish.";
                _pill.Set(StatusKind.Waiting, "One step left");
                _note.Text = "You're in control: you can remove PulseDesk any time from Settings \u2192 Apps, " +
                             "or ask your admin.";
                _allow.Visible = true;
                _allow.Enabled = true;
                _allow.Text = "Allow";
                _later.Text = "Not now";
                break;

            case HomeState.Active:
                _hero.Text = "Thanks for connecting with us";
                _heroSub.Text = "You're all set. PulseDesk is running quietly in the background.";
                _pill.Set(StatusKind.On, "Connected");
                _note.Text = "You can remove PulseDesk any time from Settings \u2192 Apps, or ask your admin.";
                _allow.Visible = false;
                _later.Text = "Close";
                break;

            case HomeState.Paused:
                _hero.Text = "Thanks for connecting with us";
                _heroSub.Text = "PulseDesk is running in the background. It is paused right now.";
                _pill.Set(StatusKind.Waiting, "Paused by your admin");
                _note.Text = "You can remove PulseDesk any time from Settings \u2192 Apps, or ask your admin.";
                _allow.Visible = false;
                _later.Text = "Close";
                break;
        }
    }

    private async void OnAllow(object? sender, EventArgs e)
    {
        if (_state != HomeState.Consent || _busy) return;

        _busy = true;
        _allow.Enabled = false;
        _allow.Text = "Connecting...";
        _error.Text = "";
        _pill.Set(StatusKind.Waiting, "Connecting...");

        var error = await Task.Run(Installer.GiveConsent);

        _busy = false;
        if (error != null)
        {
            _allow.Enabled = true;
            _allow.Text = "Allow";
            _pill.Set(StatusKind.Error, "Not connected");
            _error.Text = error;
            return;
        }

        ApplyState(HomeState.Active);
    }

    // ---- small custom controls --------------------------------------------
    private sealed class StatusPill : Control
    {
        private string _text = "";
        private Color _bg = Color.FromArgb(70, 58, 22);
        private Color _fg = Color.FromArgb(255, 212, 121);
        private Color _dot = Color.FromArgb(255, 193, 69);

        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI Semibold", 9F);
            Size = new Size(260, 28);
            BackColor = Bg;
        }

        public void Set(StatusKind kind, string text)
        {
            switch (kind)
            {
                case StatusKind.On:
                    _bg = Color.FromArgb(22, 70, 56);
                    _fg = Color.FromArgb(126, 226, 179);
                    _dot = Color.FromArgb(63, 208, 138);
                    break;
                case StatusKind.Error:
                    _bg = Color.FromArgb(72, 31, 31);
                    _fg = Error;
                    _dot = Color.FromArgb(255, 107, 107);
                    break;
                default:
                    _bg = Color.FromArgb(70, 58, 22);
                    _fg = Color.FromArgb(255, 212, 121);
                    _dot = Color.FromArgb(255, 193, 69);
                    break;
            }

            _text = text;
            Width = Math.Min(516, TextRenderer.MeasureText(text, Font).Width + 46);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Rounded(r, 14))
            using (var b = new SolidBrush(_bg))
                g.FillPath(b, p);
            using (var dot = new SolidBrush(_dot))
                g.FillEllipse(dot, 13, Height / 2 - 4, 8, 8);
            TextRenderer.DrawText(g, _text, Font, new Rectangle(28, 0, Width - 34, Height), _fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class PillButton : Button
    {
        public Color Fill { get; set; } = Red;
        public Color FillHover { get; set; } = RedHover;

        private bool _hover, _press;

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI Semibold", 11F);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var p = Rounded(r, 12);

            var baseColor = Enabled ? (_hover || _press ? FillHover : Fill) : Color.FromArgb(130, Fill);
            using (var b = new SolidBrush(baseColor))
                g.FillPath(b, p);

            TextRenderer.DrawText(g, Text, Font, r, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Focused && Enabled)
            {
                using var pen = new Pen(Color.FromArgb(190, 255, 255, 255), 2F);
                var inner = r;
                inner.Inflate(-3, -3);
                using var fp = Rounded(inner, 10);
                g.DrawPath(pen, fp);
            }
        }
    }
}
