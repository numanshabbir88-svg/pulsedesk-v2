using System.Text.Json;

namespace PulseDesk.Agent;

public sealed class Settings
{
    public string Server { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ConsentAt { get; set; } = "";
    public int ScreenshotIntervalSec { get; set; } = 600;
    public bool Paused { get; set; }

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PulseDesk");

    public static string FilePath => Path.Combine(Dir, "agent.json");
    public static string LogPath => Path.Combine(Dir, "agent.log");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}

/// <summary>Reads the enrollment block that the server appended to this exe.</summary>
public static class EmbeddedConfig
{
    public sealed class Conf { public string code { get; set; } = ""; public string server { get; set; } = ""; }

    public static Conf? Read()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int take = (int)Math.Min(8192, fs.Length);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            fs.ReadExactly(buf, 0, take);

            var text = System.Text.Encoding.UTF8.GetString(buf);
            var idx = text.LastIndexOf("PLSDCONF:", StringComparison.Ordinal);
            if (idx < 0) return null;
            var json = text[(idx + "PLSDCONF:".Length)..].Trim('\0', '\r', '\n', ' ');
            return JsonSerializer.Deserialize<Conf>(json);
        }
        catch { return null; }
    }
}
