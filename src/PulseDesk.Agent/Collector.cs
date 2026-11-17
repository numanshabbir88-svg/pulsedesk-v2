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
    private volatile int _liveQuality = 72;
    private volatile int _liveIntervalMs = 90;

    // Rolling per-frame costs, logged every 30 frames ("live perf" in agent.log).
    private int _perfFrames;
    private long _perfCaptureMs;
    private long _perfSendMs;
    private long _perfBytes;

    // While remote control is active the stream switches to a low-latency preset; these keep
    // the admin's chosen quality so it can be restored on STOP / watchdog / disconnect.
    private bool _controlQualitySaved;
    private int _savedWidth, _savedQuality, _savedInterval;

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
                    StopControl("remote control auto-released (no admin heartbeat)");
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

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static ulong _lastHash;
    private static bool _hashValid;

    private static byte[]? CaptureJpeg(int maxWidth, long quality, bool skipUnchanged = false)
    {
        // Capture the PRIMARY screen only. VirtualScreen merges every monitor into one very
        // wide bitmap, which is why the admin preview looked cropped / squeezed on laptops
        // with a second display.
        var b = PrimaryScreenBounds();
        if (b.Width <= 0 || b.Height <= 0) return null;

        int w = b.Width, h = b.Height;
        if (b.Width > maxWidth)
        {
            w = maxWidth;
            h = Math.Max(1, (int)Math.Round(b.Height * (maxWidth / (double)b.Width)));
        }

        using var bmp = CaptureScreen(b, w, h);

        if (skipUnchanged)
        {
            var hash = SampleHash(bmp);
            if (_hashValid && hash == _lastHash) return null;   // nothing moved: no encode, no send
            _lastHash = hash;
            _hashValid = true;
        }

        return EncodeJpeg(bmp, quality);
    }

    /// <summary>JPEG-encodes a captured frame with the cached codec (no per-call lookup).</summary>
    private static byte[] EncodeJpeg(Bitmap bmp, long quality)
    {
        using var ms = new MemoryStream(96 * 1024);
        using var pars = new EncoderParameters(1);
        pars.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
            Math.Clamp(quality, 20, 95));
        bmp.Save(ms, JpegCodec, pars);
        return ms.ToArray();
    }

    /// <summary>
    /// Local diagnostic (`PulseDesk.Agent.exe --bench [file]`): times capture, change-hash and
    /// JPEG encode for every preset and writes the numbers to a text file. It answers one
    /// question - is the live frame rate limited by this laptop's CPU or by the network?
    /// </summary>
    public static void Benchmark(string? outPath, int frames = 30)
    {
        var path = string.IsNullOrWhiteSpace(outPath)
            ? Path.Combine(Settings.Dir, "bench.txt")
            : outPath;
        var lines = new List<string>();
        var b = PrimaryScreenBounds();
        lines.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  screen {b.Width}x{b.Height}  {frames} frames/preset");

        var presets = new (string name, int width, long q)[]
        {
            ("hd 1920/76", 1920, 76),
            ("balanced 1600/66", 1600, 66),
            ("fast 1280/50", 1280, 50),
            ("tiny 960/45", 960, 45),
        };

        foreach (var (name, width, q) in presets)
        {
            int w = b.Width, h = b.Height;
            if (b.Width > width)
            {
                w = width;
                h = Math.Max(1, (int)Math.Round(b.Height * (width / (double)b.Width)));
            }

            double cap = 0, hash = 0, enc = 0;
            long bytes = 0;
            for (int i = 0; i < frames; i++)
            {
                var sw = Stopwatch.StartNew();
                using var bmp = CaptureScreen(b, w, h);
                cap += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                SampleHash(bmp);
                hash += sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var jpg = EncodeJpeg(bmp, q);
                enc += sw.Elapsed.TotalMilliseconds;
                bytes += jpg.Length;

                Thread.Sleep(25);   // let the desktop change a little between frames
            }

            var total = (cap + hash + enc) / frames;
            lines.Add($"{name,-18} {w}x{h,-9} capture {cap / frames,6:F1} ms | hash {hash / frames,5:F1} ms | " +
                      $"jpeg {enc / frames,6:F1} ms | total {total,6:F1} ms | max {1000 / total,5:F1} fps | " +
                      $"{bytes / frames / 1024.0,6:F1} KB/frame");
        }

        try { File.WriteAllLines(path, lines); } catch { }
        Settings.Log("bench: " + path);
    }

    /// <summary>
    /// Cheap fingerprint of a frame (48x27 sampled pixels). Identical fingerprints mean the
    /// screen did not change, so the frame is skipped entirely - this keeps live streaming
    /// light on idle desktops and leaves the CPU free to encode real motion.
    /// </summary>
    private static ulong SampleHash(Bitmap bmp)
    {
        ulong h = 1469598103934665603UL;   // FNV-1a
        const int cols = 48, rows = 27;
        int dx = Math.Max(1, bmp.Width / cols), dy = Math.Max(1, bmp.Height / rows);
        for (int y = 0; y < bmp.Height; y += dy)
        {
            for (int x = 0; x < bmp.Width; x += dx)
            {
                var c = bmp.GetPixel(x, y);
                h = (h ^ c.R) * 1099511628211UL;
                h = (h ^ c.G) * 1099511628211UL;
                h = (h ^ c.B) * 1099511628211UL;
            }
        }
        return h;
    }

    /// <summary>
    /// Copies the screen straight into a bitmap of the requested (already scaled) size with
    /// BitBlt/StretchBlt, so there is no full-resolution intermediate bitmap and no per-frame
    /// high-quality GDI+ resampling. That is what makes the live stream smooth.
    /// </summary>
    private static Bitmap CaptureScreen(Rectangle src, int w, int h)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return CaptureFallback(src, w, h);
        IntPtr memDc = IntPtr.Zero, hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            memDc = Native.CreateCompatibleDC(screenDc);
            hbmp = Native.CreateCompatibleBitmap(screenDc, w, h);
            if (memDc != IntPtr.Zero && hbmp != IntPtr.Zero)
            {
                old = Native.SelectObject(memDc, hbmp);
                bool ok;
                if (w == src.Width && h == src.Height)
                {
                    // 1:1 copy - no resampling, the cheapest and sharpest path
                    ok = Native.BitBlt(memDc, 0, 0, w, h, screenDc, src.Left, src.Top, Native.SRCCOPY);
                }
                else
                {
                    Native.SetStretchBltMode(memDc, Native.HALFTONE);
                    ok = Native.StretchBlt(memDc, 0, 0, w, h, screenDc, src.Left, src.Top,
                        src.Width, src.Height, Native.SRCCOPY);
                }
                if (ok) return Image.FromHbitmap(hbmp);
            }
        }
        catch { }
        finally
        {
            if (memDc != IntPtr.Zero && old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
        return CaptureFallback(src, w, h);
    }

    /// <summary>GDI+ path used only when the GDI copy fails (rare, e.g. protected content).</summary>
    private static Bitmap CaptureFallback(Rectangle src, int w, int h)
    {
        var target = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var full = CaptureFull(src);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImage(full, 0, 0, w, h);
        return target;
    }

    private static Bitmap CaptureFull(Rectangle src)
    {
        var full = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(full);
        g.CopyFromScreen(src.Left, src.Top, 0, 0, src.Size, CopyPixelOperation.SourceCopy);
        return full;
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
                    var sw = Stopwatch.StartNew();
                    var ws = _ws;
                    var bytes = CaptureJpeg(_liveWidth, _liveQuality, true);
                    long captureMs = sw.ElapsedMilliseconds;
                    long sendMs = 0;
                    if (bytes != null && ws != null)
                    {
                        await _wsSend.WaitAsync(ct);
                        try
                        {
                            var sendSw = Stopwatch.StartNew();
                            await ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
                            sendMs = sendSw.ElapsedMilliseconds;
                        }
                        finally { _wsSend.Release(); }
                    }

                    _perfFrames++;
                    _perfCaptureMs += captureMs;
                    _perfSendMs += sendMs;
                    _perfBytes += bytes?.Length ?? 0;
                    if (_perfFrames >= 30)
                    {
                        Settings.Log($"live perf: capture {_perfCaptureMs / _perfFrames} ms, " +
                                     $"send {_perfSendMs / _perfFrames} ms, " +
                                     $"{_perfBytes / _perfFrames / 1024} KB/frame, " +
                                     $"target {_liveIntervalMs} ms");
                        _perfFrames = 0; _perfCaptureMs = 0; _perfSendMs = 0; _perfBytes = 0;
                    }

                    // Keep the requested frame rate: wait only for the time capture+send did not use.
                    var wait = _liveIntervalMs - (int)sw.ElapsedMilliseconds;
                    await Task.Delay(Math.Max(10, wait), ct);
                }
                else await Task.Delay(500, ct);
            }
            catch { await Task.Delay(1000, ct); }
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
                // keeps NAT / mobile-router mappings alive when the stream is idle
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
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
            finally { StopControl("remote control auto-released (agent disconnected)"); _ws = null; _streamOn = false; }
            await Task.Delay(3000, ct);
        }
    }

    /// <summary>
    /// Single place that releases remote control: releases the input hooks, restores the quality
    /// and restores the quality the admin had chosen before control started. Used by the STOP
    /// command, the 45 s watchdog and the socket-disconnect safety path.
    /// </summary>
    private void StopControl(string reason)
    {
        if (!_controlOn) return;
        _controlOn = false;
        ControlBanner.Hide();   // releases the input hooks (no window is shown)
        if (_controlQualitySaved)
        {
            _liveWidth = _savedWidth;
            _liveQuality = _savedQuality;
            _liveIntervalMs = _savedInterval;
            _controlQualitySaved = false;
            _hashValid = false;
        }
        Settings.Log(reason);
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
                    if (_streamOn) _hashValid = false;   // a new viewer always gets a first frame
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
                    _ = CaptureAndUpload(1920, 85);
                    break;

                // ---- live quality (admin can pick Full HD 1080p / Ultra / Balanced / Fast)
                case "quality":
                    if (j.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv))
                        _liveWidth = Math.Clamp(wv, 480, 3840);
                    if (j.TryGetProperty("q", out var q) && q.TryGetInt32(out var qv))
                        _liveQuality = Math.Clamp(qv, 20, 95);
                    if (j.TryGetProperty("ms", out var m) && m.TryGetInt32(out var mv))
                        _liveIntervalMs = Math.Clamp(mv, 40, 2000);
                    _hashValid = false;   // quality change must produce a fresh frame
                    break;

                // ---- remote control (mouse + keyboard) ----
                case "control-on":
                    if (!_controlOn)
                    {
                        // Downscaling costs MORE than a 1:1 BitBlt on integrated GPUs, so the
                        // control preset keeps the native size and only lowers JPEG quality.
                        _savedWidth = _liveWidth;
                        _savedQuality = _liveQuality;
                        _savedInterval = _liveIntervalMs;
                        _controlQualitySaved = true;
                        _liveWidth = 4096;      // >= screen width => no StretchBlt
                        _liveQuality = 60;
                        _liveIntervalMs = 50;   // ~15-20 fps while the mouse is being driven
                        _hashValid = false;
                    }
                    _controlOn = true;
                    _inputSeen = false;
                    _lastControlSeen = Environment.TickCount64;
                    _lastNudge = Environment.TickCount64;
                    ControlBanner.Show();   // installs input hooks silently - no visible window
                    RemoteInput.Nudge();   // wake a sleeping display if the session is not locked
                    Settings.Log(Native.IsScreenLocked()
                        ? "remote control ON by admin - device is LOCKED, input will work after unlock"
                        : "remote control ON by admin (employee input will be blocked)");
                    _ = SendStatus();
                    break;
                case "control-off":
                    StopControl("remote control OFF by admin (employee input restored)");
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
