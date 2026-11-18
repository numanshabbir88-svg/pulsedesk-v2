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
    // A newly connected viewer (or a quality change) needs a frame immediately instead of
    // waiting for the next capture tick - that removes the 4-5 s "connect" delay.
    private volatile bool _forceFrame;

    // Adaptive live rate control. SendAsync blocks when the uplink/tunnel is slow; we watch
    // the real send time every 12 frames and shrink the stream until frames flow again, then
    // slowly grow it back toward the quality the admin selected.
    private int _targetWidth = 1600, _targetQuality = 72, _targetInterval = 90;
    // The last preset the admin explicitly selected. Used so a repeated quality command
    // (sent every 10 s as a keep-alive) never resets the rate the agent learned for this link.
    private int _appliedWidth, _appliedQuality, _appliedInterval;
    private int _adaptFrames, _settleFrames;
    private long _adaptSendMs, _adaptBytes;
    private long _lastSendMs;
    private int _slowFrames;    // blocked frames in the current 6-frame window

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
    /// Cheap fingerprint of a frame (96x54 sampled pixels). Identical fingerprints mean the
    /// screen did not change, so the frame is skipped entirely - this keeps live streaming
    /// light on idle desktops and leaves the CPU free to encode real motion. The denser grid
    /// catches small changes (mouse pointer, typing) that a 48x27 grid used to miss.
    /// </summary>
    private static ulong SampleHash(Bitmap bmp)
    {
        ulong h = 1469598103934665603UL;   // FNV-1a
        const int cols = 96, rows = 54;
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

    /// <summary>
    /// Keeps the stream within the available uplink. A slow link shows up as a long SendAsync
    /// time; we then lower resolution/quality and slow the frame rate until it recovers.
    /// Never touches remote control - that keeps its own fixed low-latency preset.
    /// </summary>
    private void AdaptLive()
    {
        if (_controlOn) return;
        // Adapt on a 6-frame window using a ROBUST signal: how many frames actually blocked.
        // A Cloudflare quick tunnel has random 300 ms - 2 s stalls even when the link is fine;
        // averaging send times (or reacting to a single frame) let one hiccup destroy the
        // picture. Requiring 3 of 6 blocked frames ignores those random spikes.
        if (_adaptFrames < 6) return;
        int slow = _slowFrames;
        long avgSend = _adaptSendMs / _adaptFrames;
        _adaptFrames = 0; _adaptSendMs = 0; _adaptBytes = 0; _slowFrames = 0;

        int oldW = _liveWidth, oldQ = _liveQuality, oldMs = _liveIntervalMs;
        if (slow >= 3 || avgSend > 250)
        {
            // Congestion: give the tunnel more breathing room between frames. JPEG quality is
            // NEVER lowered here - blurry/garbled text was the whole complaint. Width only
            // drops as a last resort, and never below 640 px (still readable on a laptop).
            _settleFrames = 0;
            if (_liveIntervalMs < 300) _liveIntervalMs = Math.Min(300, _liveIntervalMs + 40);
            else if (_liveWidth > 640) _liveWidth = Math.Max(640, (int)(_liveWidth * 0.9));
        }
        else if (slow <= 1 && avgSend < 60)
        {
            // Healthy window: heal quickly so a brief stall does not leave quality low.
            if (++_settleFrames >= 2)
            {
                _settleFrames = 0;
                if (_liveWidth < _targetWidth) _liveWidth = Math.Min(_targetWidth, (int)(_liveWidth * 1.12) + 1);
                else if (_liveQuality < _targetQuality) _liveQuality = Math.Min(_targetQuality, _liveQuality + 4);
                else if (_liveIntervalMs > _targetInterval) _liveIntervalMs = Math.Max(_targetInterval, _liveIntervalMs - 25);
            }
        }
        else _settleFrames = 0;

        if (_liveWidth != oldW || _liveQuality != oldQ || _liveIntervalMs != oldMs)
            Settings.Log($"live adapt: send={avgSend}ms slow={slow}/6 -> {_liveWidth}x{_liveQuality} @{_liveIntervalMs}ms");
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
                    bool force = _forceFrame;
                    _forceFrame = false;
                    // Skip truly unchanged screens: on a slow uplink, sending idle frames
                    // saturates the tunnel and makes real motion laggy. The hash samples
                    // 96x54 points, so small movements (cursor, typing) still get through.
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
                    if (bytes != null)
                    {
                        _lastSendMs = sendMs;
                        _adaptFrames++; _adaptSendMs += sendMs; _adaptBytes += bytes.Length;
                        if (sendMs > 100) _slowFrames++;
                        AdaptLive();
                    }
                    if (_perfFrames >= 30)
                    {
                        Settings.Log($"live perf: capture {_perfCaptureMs / _perfFrames} ms, " +
                                     $"send {_perfSendMs / _perfFrames} ms, " +
                                     $"{_perfBytes / _perfFrames / 1024} KB/frame, " +
                                     $"target {_liveIntervalMs} ms");
                        _perfFrames = 0; _perfCaptureMs = 0; _perfSendMs = 0; _perfBytes = 0;
                    }

                    // Keep the requested frame rate: wait only for the time capture+send did not use.
                    var wait = force ? 0 : _liveIntervalMs - (int)sw.ElapsedMilliseconds;
                    await Task.Delay(Math.Max(10, wait), ct);
                }
                else await Task.Delay(120, ct);   // was 500 ms - first frame after connect arrives sooner
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
                    if (_streamOn) { _hashValid = false; _forceFrame = true; }  // first frame now
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
                        _targetWidth = Math.Clamp(wv, 320, 3840);
                    if (j.TryGetProperty("q", out var q) && q.TryGetInt32(out var qv))
                        _targetQuality = Math.Clamp(qv, 20, 95);
                    if (j.TryGetProperty("ms", out var m) && m.TryGetInt32(out var mv))
                        _targetInterval = Math.Clamp(mv, 40, 2000);
                    // Apply a NEW preset at once - waiting for a "healthy" send left the agent
                    // stuck on 960/1600 px for the whole session when the tunnel was congested.
                    // A repeated identical command (the browser's 10 s keep-alive) is ignored so
                    // the agent keeps the rate it learned for this link.
                    bool presetChanged = _targetWidth != _appliedWidth ||
                                         _targetQuality != _appliedQuality ||
                                         _targetInterval != _appliedInterval;
                    if (presetChanged)
                    {
                        _appliedWidth = _targetWidth;
                        _appliedQuality = _targetQuality;
                        _appliedInterval = _targetInterval;
                        // Never change the low-latency preset while an employee's input is
                        // being controlled; StopControl restores the admin's saved quality.
                        if (!_controlOn)
                        {
                            _liveWidth = _targetWidth;
                            _liveQuality = _targetQuality;
                            _liveIntervalMs = _targetInterval;
                        }
                    }
                    // A healthy link should not stay at the slow rate it learned during an old
                    // stall. The browser repeats this command every 10 s; recover the full rate
                    // as soon as the last frame went out instantly. A slow link keeps its rate.
                    else if (!_controlOn && _lastSendMs < 25 &&
                             (_liveIntervalMs > _targetInterval || _liveWidth < _targetWidth))
                    {
                        _liveWidth = _targetWidth;
                        _liveIntervalMs = _targetInterval;
                    }
                    Settings.Log($"live quality cmd: {_targetWidth}x{_targetQuality} @{_targetInterval}ms changed={presetChanged} lastSend={_lastSendMs}ms");
                    _adaptFrames = 0; _adaptSendMs = 0; _adaptBytes = 0; _settleFrames = 0;
                    _slowFrames = 0;
                    _hashValid = false;   // quality change must produce a fresh frame
                    _forceFrame = true;
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
