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
                        await CaptureAndUpload(1280, 70);

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
        var b = SystemInformation.VirtualScreen;
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
            int w = maxWidth, h = (int)(b.Height * (maxWidth / (double)b.Width));
            scaled = new Bitmap(w, h);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
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

    private async Task LiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_streamOn && _ws is { State: System.Net.WebSockets.WebSocketState.Open })
                {
                    var bytes = CaptureJpeg(1280, 45);
                    if (bytes != null)
                        await _ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Binary, true, ct);
                    await Task.Delay(900, ct);
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
                    _ = CaptureAndUpload(1600, 75);
                    break;
            }
        }
        catch { }
    }
}
