using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5080");
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<Live>();
var app = builder.Build();

var db = app.Services.GetRequiredService<Db>();
db.Init();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---------------------------------------------------------------- config
var cfg = app.Configuration;
string AdminPassword = cfg["AdminPassword"] ?? "changeme";
string PublicBase = (cfg["PublicBaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
string AgentBundle = cfg["AgentBundlePath"] ?? "agent/PulseDesk.Agent.exe";
string ShotDir = Path.GetFullPath(cfg["ScreenshotDir"] ?? "data/shots");
Directory.CreateDirectory(ShotDir);

// ---------------------------------------------------------------- retention (keeps admin disk free)
int retainActivity = int.TryParse(cfg["RetentionDaysActivity"], out var ra) ? ra : 30;
int retainShots = int.TryParse(cfg["RetentionDaysShots"], out var rsh) ? rsh : 7;

void RunRetention()
{
    try
    {
        using var c = db.Open();
        var a = c.CreateCommand();
        a.CommandText = "DELETE FROM activity WHERE ts < $cut";
        a.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-retainActivity).ToString("O"));
        var removedActivity = a.ExecuteNonQuery();

        var qs = c.CreateCommand();
        qs.CommandText = "SELECT id,path FROM shots WHERE ts < $cut";
        qs.Parameters.AddWithValue("$cut", DateTime.UtcNow.AddDays(-retainShots).ToString("O"));
        var ids = new List<long>();
        using (var r = qs.ExecuteReader())
            while (r.Read())
            {
                ids.Add(r.GetInt64(0));
                try
                {
                    var p = Path.Combine(ShotDir, r.GetString(1));
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
        foreach (var id in ids)
        {
            var d = c.CreateCommand();
            d.CommandText = "DELETE FROM shots WHERE id=$i";
            d.Parameters.AddWithValue("$i", id);
            d.ExecuteNonQuery();
        }

        if (removedActivity > 0 || ids.Count > 0)
            Console.WriteLine($"[retention] activity rows removed={removedActivity}, screenshots removed={ids.Count}");
    }
    catch (Exception ex) { Console.WriteLine("[retention] " + ex.Message); }
}

_ = Task.Run(async () =>
{
    RunRetention();
    while (true)
    {
        await Task.Delay(TimeSpan.FromHours(6));
        RunRetention();
    }
});

// ---------------------------------------------------------------- auth helpers
var adminTokens = new ConcurrentDictionary<string, DateTime>();
var loginFails = new ConcurrentDictionary<string, (int Count, DateTime BlockedUntil)>();

bool IsAdmin(HttpRequest r)
{
    var t = r.Headers["X-Admin-Token"].ToString();
    return !string.IsNullOrEmpty(t) && adminTokens.TryGetValue(t, out var exp) && exp > DateTime.UtcNow;
}

string ClientKey(HttpRequest r)
{
    var cf = r.Headers["CF-Connecting-IP"].ToString();
    if (!string.IsNullOrEmpty(cf)) return cf;
    return r.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
}

// ---------------------------------------------------------------- admin auth
app.MapPost("/api/admin/login", async (HttpRequest req) =>
{
    var key = ClientKey(req);
    if (loginFails.TryGetValue(key, out var st) && st.BlockedUntil > DateTime.UtcNow)
    {
        var mins = Math.Max(1, Math.Ceiling((st.BlockedUntil - DateTime.UtcNow).TotalMinutes));
        return Results.Json(new { ok = false, error = $"Bahut zyada attempts - {mins:0} minute baad try karein" }, statusCode: 429);
    }

    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var pw = body.TryGetProperty("password", out var p) ? p.GetString() : "";
    if (pw != AdminPassword)
    {
        var count = (loginFails.TryGetValue(key, out var cur) ? cur.Count : 0) + 1;
        if (count >= 5)
        {
            loginFails[key] = (0, DateTime.UtcNow.AddMinutes(5));
            return Results.Json(new { ok = false, error = "5 ghalat attempts - 5 minute ke liye block" }, statusCode: 429);
        }
        loginFails[key] = (count, DateTime.MinValue);
        return Results.Json(new { ok = false, error = "Wrong password" }, statusCode: 401);
    }
    loginFails.TryRemove(key, out _);

    var tok = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    adminTokens[tok] = DateTime.UtcNow.AddHours(12);
    return Results.Json(new { ok = true, token = tok });
});

// ---------------------------------------------------------------- enrollment
app.MapPost("/api/enroll", async (HttpRequest req) =>
{
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    string code = b.GetProperty("code").GetString() ?? "";
    string machine = b.GetProperty("machine").GetString() ?? "unknown";
    string user = b.GetProperty("user").GetString() ?? "unknown";
    string os = b.GetProperty("os").GetString() ?? "";
    string ver = b.GetProperty("agentVersion").GetString() ?? "";
    string consent = b.TryGetProperty("consentAt", out var c) ? (c.GetString() ?? "") : "";

    using var conn = db.Open();
    var q = conn.CreateCommand();
    q.CommandText = "SELECT email,used_at,device_id FROM enrollments WHERE code=$c";
    q.Parameters.AddWithValue("$c", code);
    using var rd = q.ExecuteReader();
    if (!rd.Read()) return Results.Json(new { ok = false, error = "Invalid invite code" }, statusCode: 400);
    var email = rd.GetString(0);
    var used = rd.IsDBNull(1) ? null : rd.GetString(1);
    var existingDevice = rd.IsDBNull(2) ? null : rd.GetString(2);
    rd.Close();

    var deviceId = Guid.NewGuid().ToString("N");
    var devToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var now = DateTime.UtcNow.ToString("O");
    var consentValue = string.IsNullOrEmpty(consent) ? now : consent;

    // Re-install / re-enroll with the same invite: reuse the device row instead of duplicating it.
    if (!string.IsNullOrEmpty(existingDevice))
    {
        var chk = conn.CreateCommand();
        chk.CommandText = "SELECT COUNT(*) FROM devices WHERE id=$d";
        chk.Parameters.AddWithValue("$d", existingDevice);
        var exists = Convert.ToInt64(chk.ExecuteScalar() ?? 0L) > 0;
        if (exists)
        {
            var updDev = conn.CreateCommand();
            updDev.CommandText = @"UPDATE devices SET name=$n,user_name=$u,os=$o,agent_version=$v,
                                   token=$t,consent_at=$c,last_seen=$now WHERE id=$d";
            updDev.Parameters.AddWithValue("$n", machine);
            updDev.Parameters.AddWithValue("$u", user);
            updDev.Parameters.AddWithValue("$o", os);
            updDev.Parameters.AddWithValue("$v", ver);
            updDev.Parameters.AddWithValue("$t", devToken);
            updDev.Parameters.AddWithValue("$c", consentValue);
            updDev.Parameters.AddWithValue("$now", now);
            updDev.Parameters.AddWithValue("$d", existingDevice);
            updDev.ExecuteNonQuery();

            db.Alert(existingDevice, "enrolled", $"Device {machine} ({user}) re-enrolled (reinstall)", "info");
            return Results.Json(new { ok = true, deviceId = existingDevice, deviceToken = devToken, name = machine });
        }
    }

    // Same physical machine enrolled again with a *different* invite link: reuse the
    // newest row with the same machine name instead of piling up duplicates.
    var sameQ = conn.CreateCommand();
    sameQ.CommandText = "SELECT id FROM devices WHERE lower(name)=lower($n) ORDER BY last_seen DESC LIMIT 1";
    sameQ.Parameters.AddWithValue("$n", machine);
    if (sameQ.ExecuteScalar() is string sameMachine && sameMachine.Length > 0)
    {
        var updSame = conn.CreateCommand();
        updSame.CommandText = @"UPDATE devices SET name=$n,user_name=$u,os=$o,agent_version=$v,
                                token=$t,consent_at=$c,last_seen=$now,email=$e WHERE id=$d";
        updSame.Parameters.AddWithValue("$n", machine);
        updSame.Parameters.AddWithValue("$u", user);
        updSame.Parameters.AddWithValue("$o", os);
        updSame.Parameters.AddWithValue("$v", ver);
        updSame.Parameters.AddWithValue("$t", devToken);
        updSame.Parameters.AddWithValue("$c", consentValue);
        updSame.Parameters.AddWithValue("$now", now);
        updSame.Parameters.AddWithValue("$e", email);
        updSame.Parameters.AddWithValue("$d", sameMachine);
        updSame.ExecuteNonQuery();

        var updEnr = conn.CreateCommand();
        updEnr.CommandText = "UPDATE enrollments SET used_at=$now, device_id=$d WHERE code=$c";
        updEnr.Parameters.AddWithValue("$now", now);
        updEnr.Parameters.AddWithValue("$d", sameMachine);
        updEnr.Parameters.AddWithValue("$c", code);
        updEnr.ExecuteNonQuery();

        db.Alert(sameMachine, "enrolled", $"Device {machine} ({user}) re-enrolled (same machine, new invite)", "info");
        return Results.Json(new { ok = true, deviceId = sameMachine, deviceToken = devToken, name = machine });
    }

    var ins = conn.CreateCommand();
    ins.CommandText = @"INSERT INTO devices(id,name,user_name,os,agent_version,token,consent_at,created_at,last_seen,email)
                        VALUES($i,$n,$u,$o,$v,$t,$c,$now,$now,$e)";
    ins.Parameters.AddWithValue("$i", deviceId);
    ins.Parameters.AddWithValue("$n", machine);
    ins.Parameters.AddWithValue("$u", user);
    ins.Parameters.AddWithValue("$o", os);
    ins.Parameters.AddWithValue("$v", ver);
    ins.Parameters.AddWithValue("$t", devToken);
    ins.Parameters.AddWithValue("$c", string.IsNullOrEmpty(consent) ? now : consent);
    ins.Parameters.AddWithValue("$now", now);
    ins.Parameters.AddWithValue("$e", email);
    ins.ExecuteNonQuery();

    var upd = conn.CreateCommand();
    upd.CommandText = "UPDATE enrollments SET used_at=$now, device_id=$d WHERE code=$c";
    upd.Parameters.AddWithValue("$now", now);
    upd.Parameters.AddWithValue("$d", deviceId);
    upd.Parameters.AddWithValue("$c", code);
    upd.ExecuteNonQuery();

    db.Alert(deviceId, "enrolled", $"Device {machine} ({user}) enrolled", "info");
    return Results.Json(new { ok = true, deviceId, deviceToken = devToken, name = machine });
});

// ---------------------------------------------------------------- agent ingest
app.MapPost("/api/activity", async (HttpRequest req) =>
{
    using var conn = db.Open();
    var t = req.Headers["X-Device-Token"].ToString();
    var did = db.DeviceId(conn, t);
    if (did == null) return Results.Json(new { ok = false }, statusCode: 401);

    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    if (!b.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array)
        return Results.Json(new { ok = true });

    using var tx = conn.BeginTransaction();
    foreach (var s in arr.EnumerateArray())
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO activity(device_id,ts,process,title,idle_sec,active)
                            VALUES($d,$t,$p,$w,$i,$a)";
        cmd.Parameters.AddWithValue("$d", did);
        cmd.Parameters.AddWithValue("$t", s.GetProperty("ts").GetString());
        cmd.Parameters.AddWithValue("$p", s.GetProperty("process").GetString() ?? "");
        var title = s.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
        cmd.Parameters.AddWithValue("$w", title.Length > 400 ? title[..400] : title);
        cmd.Parameters.AddWithValue("$i", s.TryGetProperty("idle", out var idle) ? idle.GetInt32() : 0);
        cmd.Parameters.AddWithValue("$a", s.TryGetProperty("active", out var ac) && ac.GetBoolean() ? 1 : 0);
        cmd.ExecuteNonQuery();
    }
    tx.Commit();
    db.Touch(conn, did);
    return Results.Json(new { ok = true });
});

app.MapPost("/api/hb", async (HttpRequest req) =>
{
    using var conn = db.Open();
    var t = req.Headers["X-Device-Token"].ToString();
    var did = db.DeviceId(conn, t);
    if (did == null) return Results.Json(new { ok = false }, statusCode: 401);

    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    int paused = b.TryGetProperty("paused", out var p) && p.GetBoolean() ? 1 : 0;
    var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE devices SET last_seen=$n, paused=$p WHERE id=$d";
    cmd.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O"));
    cmd.Parameters.AddWithValue("$p", paused);
    cmd.Parameters.AddWithValue("$d", did);
    cmd.ExecuteNonQuery();

    // pair screenshot interval from server
    var q = conn.CreateCommand();
    q.CommandText = "SELECT screenshot_interval_sec, paused FROM devices WHERE id=$d";
    q.Parameters.AddWithValue("$d", did);
    using var rd = q.ExecuteReader();
    int interval = 300; int srvPaused = 0;
    if (rd.Read()) { interval = rd.GetInt32(0); srvPaused = rd.GetInt32(1); }
    return Results.Json(new { ok = true, screenshotIntervalSec = interval, serverPaused = srvPaused == 1 });
});

app.MapPost("/api/shot", async (HttpRequest req) =>
{
    using var conn = db.Open();
    var t = req.Headers["X-Device-Token"].ToString();
    var did = db.DeviceId(conn, t);
    if (did == null) return Results.Json(new { ok = false }, statusCode: 401);

    if (!req.HasFormContentType) return Results.Json(new { ok = false, error = "multipart required" }, statusCode: 400);
    var form = await req.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0) return Results.Json(new { ok = false }, statusCode: 400);
    if (file.Length > 8 * 1024 * 1024) return Results.Json(new { ok = false, error = "too large" }, statusCode: 413);

    var ts = DateTime.UtcNow;
    var dir = Path.Combine(ShotDir, did);
    Directory.CreateDirectory(dir);
    var name = ts.ToString("yyyyMMdd_HHmmss_fff") + ".jpg";
    var path = Path.Combine(dir, name);
    await using (var fs = File.Create(path)) await file.CopyToAsync(fs);

    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO shots(device_id,ts,path) VALUES($d,$t,$p)";
    cmd.Parameters.AddWithValue("$d", did);
    cmd.Parameters.AddWithValue("$t", ts.ToString("O"));
    cmd.Parameters.AddWithValue("$p", Path.Combine(did, name));
    cmd.ExecuteNonQuery();
    db.Touch(conn, did);
    return Results.Json(new { ok = true });
});

// HTTP-only agents (Android app) cannot keep a WebSocket open on every network:
// they poll for commands and deliver live frames with plain POSTs instead.
app.MapGet("/api/agent/poll", (HttpRequest req) =>
{
    using var conn = db.Open();
    var t = req.Headers["X-Device-Token"].ToString();
    var did = db.DeviceId(conn, t);
    if (did == null) return Results.Json(new { ok = false }, statusCode: 401);

    int paused = 0, interval = 300;
    var q = conn.CreateCommand();
    q.CommandText = "SELECT paused, screenshot_interval_sec FROM devices WHERE id=$d";
    q.Parameters.AddWithValue("$d", did);
    using (var rd = q.ExecuteReader())
        if (rd.Read()) { paused = rd.GetInt32(0); interval = rd.GetInt32(1); }

    var c = conn.CreateCommand();
    c.CommandText = "SELECT COUNT(*) FROM commands WHERE device_id=$d AND command='shot-now'";
    c.Parameters.AddWithValue("$d", did);
    var shotNow = Convert.ToInt32(c.ExecuteScalar()) > 0;
    if (shotNow)
    {
        var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM commands WHERE device_id=$d AND command='shot-now'";
        del.Parameters.AddWithValue("$d", did);
        del.ExecuteNonQuery();
    }

    db.Touch(conn, did);
    var live = app.Services.GetRequiredService<Live>();
    return Results.Json(new
    {
        ok = true,
        paused = paused == 1,
        streaming = live.HasViewers(did),
        shotNow,
        intervalSec = interval
    });
});

app.MapPost("/api/agent/frame", async (HttpRequest req) =>
{
    using var conn = db.Open();
    var t = req.Headers["X-Device-Token"].ToString();
    var did = db.DeviceId(conn, t);
    if (did == null) return Results.Json(new { ok = false }, statusCode: 401);
    if (req.ContentLength is > 4 * 1024 * 1024)
        return Results.Json(new { ok = false, error = "too large" }, statusCode: 413);

    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    if (ms.Length == 0) return Results.Json(new { ok = false }, statusCode: 400);

    db.Touch(conn, did);
    var live = app.Services.GetRequiredService<Live>();
    if (!live.HasViewers(did)) return Results.Json(new { ok = true, streamed = false });
    await live.FanoutFrame(did, ms.GetBuffer(), (int)ms.Length);
    return Results.Json(new { ok = true, streamed = true });
});

app.MapGet("/mobile/app.apk", () =>
{
    var apk = Path.Combine(app.Environment.ContentRootPath, "agent", "PulseDeskMobile.apk");
    return File.Exists(apk)
        ? Results.File(apk, "application/vnd.android.package-archive", "PulseDeskMobile.apk")
        : Results.NotFound();
});

// ---------------------------------------------------------------- admin api
app.MapGet("/api/admin/devices", (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT d.id,d.name,d.user_name,d.os,d.agent_version,d.consent_at,d.created_at,d.last_seen,
                               d.paused,d.screenshot_interval_sec,d.email,
                               (SELECT COUNT(*) FROM activity a WHERE a.device_id=d.id) AS samples
                        FROM devices d ORDER BY d.last_seen DESC";
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read())
    {
        // timestamps are stored as UTC ISO-8601 ("...Z"); parse as DateTimeOffset so the
        // comparison is not shifted by the local time zone.
        var last = DateTimeOffset.TryParse(rd.GetString(7), out var dt) ? dt.UtcDateTime : DateTime.MinValue;
        list.Add(new
        {
            id = rd.GetString(0), name = rd.GetString(1), user = rd.GetString(2), os = rd.GetString(3),
            agentVersion = rd.GetString(4), consentAt = rd.GetString(5), createdAt = rd.GetString(6),
            lastSeen = rd.GetString(7),
            online = (DateTime.UtcNow - last).TotalSeconds < 90,
            paused = rd.GetInt32(8) == 1,
            screenshotIntervalSec = rd.GetInt32(9),
            email = rd.IsDBNull(10) ? "" : rd.GetString(10),
            samples = rd.GetInt32(11)
        });
    }
    return Results.Json(list);
});

app.MapDelete("/api/admin/device/{id}", (HttpRequest req, string id) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    foreach (var sql in new[]
             {
                 "DELETE FROM activity WHERE device_id=$d",
                 "DELETE FROM shots WHERE device_id=$d",
                 "DELETE FROM alerts WHERE device_id=$d",
                 "DELETE FROM commands WHERE device_id=$d"
             })
    {
        var c = conn.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$d", id);
        c.ExecuteNonQuery();
    }
    var unlink = conn.CreateCommand();
    unlink.CommandText = "UPDATE enrollments SET device_id=NULL WHERE device_id=$d";
    unlink.Parameters.AddWithValue("$d", id);
    unlink.ExecuteNonQuery();

    var del = conn.CreateCommand();
    del.CommandText = "DELETE FROM devices WHERE id=$d";
    del.Parameters.AddWithValue("$d", id);
    del.ExecuteNonQuery();

    try
    {
        var dir = Path.Combine(ShotDir, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
    catch { }

    return Results.Json(new { ok = true });
});

app.MapGet("/api/admin/activity", (HttpRequest req, string deviceId, int minutes = 120) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT ts,process,title,idle_sec,active FROM activity
                        WHERE device_id=$d AND ts > $from ORDER BY ts DESC LIMIT 2000";
    cmd.Parameters.AddWithValue("$d", deviceId);
    cmd.Parameters.AddWithValue("$from", DateTime.UtcNow.AddMinutes(-minutes).ToString("O"));
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read())
        list.Add(new { ts = rd.GetString(0), process = rd.GetString(1), title = rd.GetString(2), idle = rd.GetInt32(3), active = rd.GetInt32(4) == 1 });
    return Results.Json(list);
});

app.MapGet("/api/admin/summary", (HttpRequest req, string deviceId, int minutes = 120) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT process, COUNT(*) n, SUM(active) act FROM activity
                        WHERE device_id=$d AND ts > $from GROUP BY process ORDER BY n DESC LIMIT 25";
    cmd.Parameters.AddWithValue("$d", deviceId);
    cmd.Parameters.AddWithValue("$from", DateTime.UtcNow.AddMinutes(-minutes).ToString("O"));
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read()) list.Add(new { process = rd.GetString(0), samples = rd.GetInt32(1), active = rd.GetInt32(2) });
    return Results.Json(list);
});

app.MapGet("/api/admin/shots", (HttpRequest req, string deviceId, int limit = 60) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id,ts,path FROM shots WHERE device_id=$d ORDER BY id DESC LIMIT $l";
    cmd.Parameters.AddWithValue("$d", deviceId);
    cmd.Parameters.AddWithValue("$l", limit);
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read()) list.Add(new { id = rd.GetInt64(0), ts = rd.GetString(1), url = "/shots/" + rd.GetString(2).Replace('\\', '/') });
    return Results.Json(list);
});

app.MapGet("/shots/{device}/{file}", (HttpRequest req, string device, string file) =>
{
    if (!IsAdmin(req)) return Results.StatusCode(401);
    if (device.Contains("..") || file.Contains("..") || file.Contains('/') || file.Contains('\\'))
        return Results.NotFound();
    var p = Path.Combine(ShotDir, device, file);
    if (!File.Exists(p)) return Results.NotFound();
    return Results.File(p, "image/jpeg");
});

app.MapGet("/api/admin/alerts", (HttpRequest req, int limit = 100) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT a.id,a.ts,a.kind,a.message,a.severity,d.name FROM alerts a
                        LEFT JOIN devices d ON d.id=a.device_id ORDER BY a.id DESC LIMIT $l";
    cmd.Parameters.AddWithValue("$l", limit);
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read())
        list.Add(new { id = rd.GetInt64(0), ts = rd.GetString(1), kind = rd.GetString(2), message = rd.GetString(3), severity = rd.GetString(4), device = rd.IsDBNull(5) ? "" : rd.GetString(5) });
    return Results.Json(list);
});

app.MapPost("/api/admin/command", async (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var deviceId = b.GetProperty("deviceId").GetString() ?? "";
    var command = b.GetProperty("command").GetString() ?? "";
    using var conn = db.Open();

    if (command is "pause" or "resume")
    {
        var u = conn.CreateCommand();
        u.CommandText = "UPDATE devices SET paused=$p WHERE id=$d";
        u.Parameters.AddWithValue("$p", command == "pause" ? 1 : 0);
        u.Parameters.AddWithValue("$d", deviceId);
        u.ExecuteNonQuery();
        db.Alert(deviceId, "control", "Admin ne monitoring " + (command == "pause" ? "PAUSE" : "RESUME") + " ki", "warn");
    }
    if (command == "shot-now")
        db.Alert(deviceId, "control", "Admin ne instant screenshot liya", "info");

    var q = conn.CreateCommand();
    q.CommandText = "INSERT INTO commands(device_id,command,created_at) VALUES($d,$c,$t)";
    q.Parameters.AddWithValue("$d", deviceId);
    q.Parameters.AddWithValue("$c", command);
    q.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
    q.ExecuteNonQuery();

    await app.Services.GetRequiredService<Live>().SendCommand(deviceId, command);
    return Results.Json(new { ok = true });
});

// ---------------------------------------------------------------- invites
app.MapPost("/api/admin/invite", async (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
    var email = b.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
    var note = b.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
    var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO enrollments(code,email,created_at) VALUES($c,$e,$t)";
    cmd.Parameters.AddWithValue("$c", code);
    cmd.Parameters.AddWithValue("$e", email);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
    cmd.ExecuteNonQuery();

    var link = $"{PublicBase}/join/{code}";
    var sent = await Email.Send(cfg, email, link, note);
    return Results.Json(new { ok = true, code, link, emailSent = sent });
});

app.MapGet("/api/admin/invites", (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Json(new { ok = false }, statusCode: 401);
    using var conn = db.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT code,email,created_at,used_at FROM enrollments ORDER BY created_at DESC LIMIT 100";
    var list = new List<object>();
    using var rd = cmd.ExecuteReader();
    while (rd.Read())
        list.Add(new { code = rd.GetString(0), email = rd.GetString(1), created = rd.GetString(2), used = rd.IsDBNull(3) ? "" : rd.GetString(3) });
    return Results.Json(list);
});

// ---------------------------------------------------------------- join page + downloader
app.MapGet("/join/{code}", (HttpContext ctx, string code) =>
    Results.Content(JoinPage.Html(code, PublicBase, JoinPage.IsMobile(ctx.Request.Headers.UserAgent.ToString())), "text/html"));

app.MapGet("/download/{code}", (string code) =>
{
    using var conn = db.Open();
    var q = conn.CreateCommand();
    q.CommandText = "SELECT email,used_at FROM enrollments WHERE code=$c";
    q.Parameters.AddWithValue("$c", code);
    using var rd = q.ExecuteReader();
    if (!rd.Read()) return Results.NotFound("Invalid invite");
    rd.Close();

    if (!File.Exists(AgentBundle))
        return Results.NotFound("Agent bundle missing on server: " + AgentBundle);

    var bytes = File.ReadAllBytes(AgentBundle);
    var conf = JsonSerializer.Serialize(new { code, server = PublicBase });
    var marker = Encoding.UTF8.GetBytes("\nPLSDCONF:" + conf);
    var outBytes = new byte[bytes.Length + marker.Length];
    Buffer.BlockCopy(bytes, 0, outBytes, 0, bytes.Length);
    Buffer.BlockCopy(marker, 0, outBytes, bytes.Length, marker.Length);
    return Results.File(outBytes, "application/octet-stream", "PulseDeskSetup.exe");
});

// ---------------------------------------------------------------- realtime
app.UseWebSockets();

app.Map("/ws/agent", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var token = ctx.Request.Query["token"].ToString();
    using var conn = db.Open();
    var did = db.DeviceId(conn, token);
    if (did == null) { ctx.Response.StatusCode = 401; return; }

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var live = app.Services.GetRequiredService<Live>();
    live.AddAgent(did, ws);

    var buf = new byte[512 * 1024];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close) break;
            if (res.MessageType == WebSocketMessageType.Binary)
                await live.FanoutFrame(did, buf, res.Count);
        }
    }
    catch { }
    finally { live.RemoveAgent(did, ws); }
});

app.Map("/ws/admin", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var tok = ctx.Request.Query["token"].ToString();
    if (!adminTokens.TryGetValue(tok, out var exp) || exp < DateTime.UtcNow) { ctx.Response.StatusCode = 401; return; }
    var deviceId = ctx.Request.Query["deviceId"].ToString();

    var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var live = app.Services.GetRequiredService<Live>();
    live.AddViewer(deviceId, ws);
    await live.NotifyAgent(deviceId, "{\"cmd\":\"stream\",\"on\":true}");
    try
    {
        var buf = new byte[8 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var res = await ws.ReceiveAsync(buf, CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch { }
    finally
    {
        live.RemoveViewer(deviceId, ws);
        await live.NotifyAgent(deviceId, "{\"cmd\":\"stream\",\"on\":false}");
    }
});

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();
