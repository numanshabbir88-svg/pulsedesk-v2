using Microsoft.Data.Sqlite;

public sealed class Db
{
    private readonly string _cs;
    public Db(IConfiguration cfg)
    {
        var path = Path.GetFullPath(cfg["DbPath"] ?? "data/pulsedesk.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var p = c.CreateCommand();
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    public void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS devices (
  id TEXT PRIMARY KEY, name TEXT, user_name TEXT, os TEXT, agent_version TEXT,
  token TEXT UNIQUE, consent_at TEXT, created_at TEXT, last_seen TEXT,
  paused INTEGER DEFAULT 0, screenshot_interval_sec INTEGER DEFAULT 600, email TEXT
);
CREATE TABLE IF NOT EXISTS activity (
  id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, ts TEXT,
  process TEXT, title TEXT, idle_sec INTEGER, active INTEGER
);
CREATE INDEX IF NOT EXISTS ix_activity ON activity(device_id, ts);
CREATE TABLE IF NOT EXISTS shots (
  id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, ts TEXT, path TEXT
);
CREATE TABLE IF NOT EXISTS alerts (
  id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, ts TEXT,
  kind TEXT, message TEXT, severity TEXT
);
CREATE TABLE IF NOT EXISTS enrollments (
  code TEXT PRIMARY KEY, email TEXT, created_at TEXT, used_at TEXT, device_id TEXT
);
CREATE TABLE IF NOT EXISTS commands (
  id INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT, command TEXT,
  created_at TEXT, delivered_at TEXT
);";
        cmd.ExecuteNonQuery();
    }

    public string? DeviceId(SqliteConnection c, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var q = c.CreateCommand();
        q.CommandText = "SELECT id FROM devices WHERE token=$t";
        q.Parameters.AddWithValue("$t", token);
        var r = q.ExecuteScalar();
        return r as string;
    }

    public void Touch(SqliteConnection c, string deviceId)
    {
        var q = c.CreateCommand();
        q.CommandText = "UPDATE devices SET last_seen=$n WHERE id=$d";
        q.Parameters.AddWithValue("$n", DateTime.UtcNow.ToString("O"));
        q.Parameters.AddWithValue("$d", deviceId);
        q.ExecuteNonQuery();
    }

    public void Alert(string deviceId, string kind, string message, string severity)
    {
        using var c = Open();
        var q = c.CreateCommand();
        q.CommandText = "INSERT INTO alerts(device_id,ts,kind,message,severity) VALUES($d,$t,$k,$m,$s)";
        q.Parameters.AddWithValue("$d", deviceId);
        q.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        q.Parameters.AddWithValue("$k", kind);
        q.Parameters.AddWithValue("$m", message);
        q.Parameters.AddWithValue("$s", severity);
        q.ExecuteNonQuery();
    }
}
