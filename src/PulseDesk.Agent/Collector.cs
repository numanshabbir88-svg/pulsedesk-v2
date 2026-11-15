using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;

namespace PulseDesk.Agent;

public sealed class Collector : BackgroundService
{
    private sealed class Sample
    {
        public string ts { get; set; } = "";
        public string process { get; set; } = "";
        public string title { get; set; } = "";
        public int idle { get; set; }
        public bool active { get; set; }
    }

    private readonly Settings _s;
    private readonly HttpClient _http = HttpFactory.Create(TimeSpan.FromSeconds(25));
    private readonly List<Sample> _buffer = new();
    private DateTime _lastShot = DateTime.MinValue;
    private ClientWebSocket? _ws;
    private volatile bool _streamOn;
    private volatile bool _srvPaused;
    private readonly object _sync = new();

    // remote control (Windows only): mouse + keyboard are accepted only while the admin
    // has switched it on for this device.
    private volatile bool _controlOn;
    private volatile bool _inputSeen;
    private long _lastControlSeen;
    private long _lastNudge;
    private readonly SemaphoreSlim _wsSend = new(1, 1);
    private volatile int _liveWidth = 1600;
    private volatile int _liveQuality = 65;
    private volatile int _liveIntervalMs = 350;

    public Collector(Settings s)
    {
        _s = s;
        if (!string.IsNullOrEmpty(_s.Server))
        {
            _http.BaseAddress = new Uri(_s.Server.TrimEnd('/') + "/");
            _http.DefaultRequestHeaders.Add("X-Device-Token", _s.DeviceToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_s.DeviceToken) || string.IsNullOrEmpty(_s.Server))
        {
            Settings.Log("No enrollment - agent idle. Run setup from the invite link.");
            return;
        }

        Settings.Log($"Agent started. Server={_s.Server} Device={_s.DeviceId}");
        _ = Task.Run(() => SocketLoop(ct), ct);
        _ = Task.Run(() => LiveLoop(ct), ct);

        var lastFlush = DateTime.UtcNow;
        var lastHb = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Safety watchdog: if the admin console stops sending heartbeats / input for
                // 45 seconds, release remote control so the employee never stays locked out.
                if (_controlOn && Environment.TickCount64 - _lastControlSeen > 45_000)
                {
                    _controlOn = false;
                    ControlBanner.Hide();
                    Settings.Log("remote control auto-released (no admin heartbeat)");
                }

                // While the admin is in control the laptop must not fall asleep or auto-lock
                // mid-session; a harmless F15 every 45s resets the idle timer.
                Native.KeepAwake(_controlOn);
                if (_controlOn && Environment.TickCount64 - _lastNudge > 45_000)
                {
                    _lastNudge = Environment.TickCount64;
                    RemoteInput.Nudge();
                }

                if (!_s.Paused && !_srvPaused)
                {
                    var (proc, title) = Native.Foreground();
                    int idle = Native.IdleSeconds();
                    lock (_sync)
                    {
                        _buffer.Add(new Sample
                        {
                            ts = DateTime.UtcNow.ToString("O"),
                            process = proc,
                            title = title,
                            idle = idle,
                            active = idle < 60
                        });
                    }

                    if ((DateTime.UtcNow - _lastShot).TotalSeconds >= Math.Max(5, _s.ScreenshotIntervalSec))
                        await CaptureAndUpload(1600, 78);

                    if ((DateTime.UtcNow - lastHb).TotalSeconds >= 30)
                    {
                        lastHb = DateTime.UtcNow;
                        await Heartbeat();
                    }
                }

                if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 30)
                {
                    lastFlush = DateTime.UtcNow;
                    await Flush();
                }
            }
            catch (Exception ex)
            {
                Settings.Log("loop error: " + ex.Message);
            }

            await Task.Delay(5000, ct);
        }
    }

    private async Task Flush()
    {
        List<Sample> batch;
        lock (_sync)
        {
            if (_buffer.Count == 0) return;
            batch = _buffer.ToList();
            _buffer.Clear();
        }
        try
        {
            var r = await _http.PostAsJsonAsync("api/activity", new { samples = batch });
            if (!r.IsSuccessStatusCode) Settings.Log("activity post " + (int)r.StatusCode);
        }
        catch (Exception ex) { Settings.Log("activity fail: " + ex.Message); }
    }

    private async Task Heartbeat()
    {
        try
        {
            var r = await _http.PostAsJsonAsync("api/hb", new { paused = _s.Paused });
            if (!r.IsSuccessStatusCode) return;
            var j = await r.Content.ReadFromJsonAsync<JsonElement>();
            if (j.TryGetProperty("screenshotIntervalSec", out var iv)) _s.ScreenshotIntervalSec = iv.GetInt32();
            if (j.TryGetProperty("serverPaused", out var sp)) _srvPaused = sp.GetBoolean();
        }
        catch { }
    }

    private async Task CaptureAndUpload(int maxWidth, long quality)
    {
        _lastShot = DateTime.UtcNow;
        try
        {
            var bytes = CaptureJpeg(maxWidth, quality);
            if (bytes == null) return;
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(content, "file", "shot.jpg");
            var r = await _http.PostAsync("api/shot", form);
            if (!r.IsSuccessStatusCode) Settings.Log("shot " + (int)r.StatusCode);
        }
        catch (Exception ex) { Settings.Log("shot fail: " + ex.Message); }
    }

    private static byte[]? CaptureJpeg(int maxWidth, long quality)
    {
        // Capture the PRIMARY screen only. VirtualScreen merges every monitor into one very
        // wide bitmap, which is why the admin preview looked cropped / squeezed on laptops
        // with a second display.
        var b = PrimaryScreenBounds();
        if (b.Width <= 0 || b.Height <= 0) return null;
        using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
        }

        Image target = bmp;
        Bitmap? scaled = null;
        if (b.Width > maxWidth)
        {
            int w = maxWidth, h = (int)Math.Round(b.Height * (maxWidth / (double)b.Width));
            scaled = new Bitmap(w, h);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g2.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g2.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g2.DrawImage(bmp, 0, 0, w, h);
            target = scaled;
        }

        using var ms = new MemoryStream();
        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var pars = new EncoderParameters(1);
        pars.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        target.Save(ms, enc, pars);
        scaled?.Dispose();
        return ms.ToArray();
    }

    private static Rectangle PrimaryScreenBounds()
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

    private async Task LiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_streamOn && _ws is { State: System.Net.WebSockets.WebSocketState.Open })
                {
                    var ws = _ws;
                    var bytes = CaptureJpeg(_liveWidth, _liveQuality);
                    if (bytes != null && ws != null)
                    {
                        await _wsSend.WaitAsync(ct);
                        try
                        {
                            await ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
                        }
                        finally { _wsSend.Release(); }
                    }
                    await Task.Delay(_liveIntervalMs, ct);
                }
                else await Task.Delay(700, ct);
            }
            catch { await Task.Delay(1500, ct); }
        }
    }

    private async Task SocketLoop(CancellationToken ct)
    {
        var url = _s.Server.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/')
                  + "/ws/agent?token=" + Uri.EscapeDataString(_s.DeviceToken);
        var buf = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ws = new ClientWebSocket();
                ws.Options.Proxy = null;   // direct connection, never use a stale system proxy
                await ws.ConnectAsync(new Uri(url), ct);
                _ws = ws;
                Settings.Log("ws connected");

                while (ws.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buf, 0, res.Count);
                    HandleCommand(text);
                }
            }
            catch (Exception ex) { Settings.Log("ws: " + ex.Message); }
            finally { _ws = null; _streamOn = false; }
            await Task.Delay(5000, ct);
        }
    }

    private void HandleCommand(string json)
    {
        try
        {
            var j = JsonDocument.Parse(json).RootElement;
            var cmd = j.TryGetProperty("cmd", out var c) ? c.GetString() : "";
            switch (cmd)
            {
                case "stream":
                    _streamOn = j.TryGetProperty("on", out var on) && on.GetBoolean();
                    break;
                case "pause":
                    _s.Paused = true; _s.Save();
                    Settings.Log("paused by admin");
                    break;
                case "resume":
                    _s.Paused = false; _s.Save();
                    Settings.Log("resumed by admin");
                    break;
                case "shot-now":
                    _ = CaptureAndUpload(1600, 78);
                    break;

                // ---- live quality (admin can pick Sharp / Balanced / Fast)
                case "quality":
                    if (j.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv))
                        _liveWidth = Math.Clamp(wv, 480, 2560);
                    if (j.TryGetProperty("q", out var q) && q.TryGetInt32(out var qv))
                        _liveQuality = Math.Clamp(qv, 20, 90);
                    if (j.TryGetProperty("ms", out var m) && m.TryGetInt32(out var mv))
                        _liveIntervalMs = Math.Clamp(mv, 120, 2000);
                    break;

                // ---- remote control (mouse + keyboard) ----
                case "control-on":
                    _controlOn = true;
                    _inputSeen = false;
                    _lastControlSeen = Environment.TickCount64;
                    _lastNudge = Environment.TickCount64;
                    ControlBanner.Show();
                    RemoteInput.Nudge();   // wake a sleeping display if the session is not locked
                    Settings.Log(Native.IsScreenLocked()
                        ? "remote control ON by admin - device is LOCKED, input will work after unlock"
                        : "remote control ON by admin (employee input will be blocked)");
                    _ = SendStatus();
                    break;
                case "control-off":
                    _controlOn = false;
                    ControlBanner.Hide();
                    Settings.Log("remote control OFF by admin (employee input restored)");
                    break;
                case "ping":
                    _lastControlSeen = Environment.TickCount64;
                    break;
                case "status":
                    _ = SendStatus();
                    break;
                case "input":
                    if (_controlOn)
                    {
                        _lastControlSeen = Environment.TickCount64;
                        if (!_inputSeen)
                        {
                            _inputSeen = true;
                            Settings.Log("remote input received from admin");
                        }
                        HandleInput(j);
                    }
                    break;
            }
        }
        catch { }
    }

    /// <summary>Reports lock state to the admin console (it cannot be seen from the video alone).</summary>
    private async Task SendStatus()
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                cmd = "status",
                locked = Native.IsScreenLocked(),
                control = _controlOn,
                ts = DateTime.UtcNow.ToString("O")
            });
            await SendText(json);
        }
        catch { }
    }

    private async Task SendText(string json)
    {
        var ws = _ws;
        if (ws is not { State: System.Net.WebSockets.WebSocketState.Open }) return;
        await _wsSend.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(json),
                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
        finally { _wsSend.Release(); }
    }

    /// <summary>Mouse / keyboard message coming from the admin console.</summary>
    private static void HandleInput(JsonElement j)
    {
        var kind = j.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
        switch (kind)
        {
            case "move":
                RemoteInput.Move(Num(j, "x"), Num(j, "y"));
                break;
            case "click":
            {
                var button = j.TryGetProperty("button", out var bt) ? bt.GetString() ?? "left" : "left";
                var down = j.TryGetProperty("down", out var d) && d.ValueKind == JsonValueKind.True;
                RemoteInput.Move(Num(j, "x"), Num(j, "y"));
                RemoteInput.Button(button, down);
                break;
            }
            case "wheel":
                var delta = j.TryGetProperty("delta", out var de) && de.TryGetInt32(out var dv) ? dv : 0;
                RemoteInput.Wheel(delta);
                break;
            case "key":
            {
                var vk = j.TryGetProperty("vk", out var v) && v.TryGetInt32(out var vv) ? vv : 0;
                var down = j.TryGetProperty("down", out var d2) && d2.ValueKind == JsonValueKind.True;
                RemoteInput.Key(vk, down);
                break;
            }
            case "text":
                RemoteInput.Text(j.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
                break;
        }
    }

    private static double Num(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
