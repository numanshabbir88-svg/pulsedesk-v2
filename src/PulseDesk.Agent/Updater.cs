using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;

namespace PulseDesk.Agent;

/// <summary>
/// Silent, in-place agent updates — the same way a phone app refreshes itself. No invite, no
/// uninstall/re-enroll, no zip for the employee to extract. The running agent checks a tiny
/// static manifest on the server, verifies the SHA-256 of the downloaded bundle AND of the exe
/// inside it, installs it into the per-user app folder, repoints auto-start, then hands over to
/// the new process. Enrollment (agent.json) is never touched.
///
/// The per-user folder is deliberate: a normal employee process cannot overwrite Program Files,
/// and asking for UAC would defeat the whole point. If the process happens to be elevated, the
/// classic Program Files copy is refreshed too.
/// </summary>
public sealed class Updater : BackgroundService
{
    public const string ManifestName = "agent-update.json";
    public static string ManagedDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PulseDesk", "app");
    public static string ManagedExePath => Path.Combine(ManagedDir, "PulseDesk.Agent.exe");
    public static string BuildMarker => Path.Combine(Settings.Dir, "installed-build.txt");

    private readonly Settings _s;
    private readonly IHostApplicationLifetime _life;
    private readonly HttpClient _http = HttpFactory.Create(TimeSpan.FromMinutes(10));

    public Updater(Settings s, IHostApplicationLifetime life)
    {
        _s = s;
        _life = life;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Server)) return;

        // Let the first live frame and the socket settle before any background download.
        try { await Task.Delay(TimeSpan.FromSeconds(45), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) { Settings.Log("auto-update: " + ex.Message); }

            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var baseUrl = _s.Server.TrimEnd('/');
        // Cache-busting on the manifest only: the 61 MB bundle itself is served from the direct
        // origin URL in the manifest (Cloudflare workers can truncate large downloads).
        var manifestUrl = baseUrl + "/" + ManifestName + "?v=" + DateTime.UtcNow.Ticks;
        using var resp = await _http.GetAsync(manifestUrl, ct);
        resp.EnsureSuccessStatusCode();

        string exeHash = "", zipHash = "", url = "";
        using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)))
        {
            var root = doc.RootElement;
            static string Str(JsonElement r, string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            exeHash = Str(root, "exeSha256");
            zipHash = Str(root, "zipSha256");
            url = Str(root, "url");
        }
        if (!LooksLikeHash(exeHash) || !LooksLikeHash(zipHash) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Settings.Log("auto-update: invalid update manifest");
            return;
        }

        var current = ReadInstalledHash();
        if (string.Equals(current, exeHash, StringComparison.OrdinalIgnoreCase)) return;  // already current

        Directory.CreateDirectory(ManagedDir);
        if (File.Exists(ManagedExePath) &&
            string.Equals(Hash(ManagedExePath), exeHash, StringComparison.OrdinalIgnoreCase))
        {
            Activate(ManagedExePath, exeHash);
            return;
        }

        var tmpZip = Path.Combine(Path.GetTempPath(), "pds-u-" + Guid.NewGuid().ToString("N") + ".zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), "pds-u-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var dl = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                dl.EnsureSuccessStatusCode();
                using var fs = File.Create(tmpZip);
                await dl.Content.CopyToAsync(fs, ct);
            }
            if (!string.Equals(Hash(tmpZip), zipHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("update bundle hash mismatch - not installed");

            ZipFile.ExtractToDirectory(tmpZip, tmpDir, true);
            var extracted = Path.Combine(tmpDir, "PulseDesk.Agent.exe");
            if (!File.Exists(extracted)) throw new InvalidOperationException("exe missing in update bundle");
            if (!string.Equals(Hash(extracted), exeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("agent hash mismatch - not installed");

            var staged = ManagedExePath + ".new";
            File.Copy(extracted, staged, true);
            if (!string.Equals(Hash(staged), exeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("staged agent hash mismatch - not installed");
            File.Move(staged, ManagedExePath, true);

            Settings.Log($"auto-update installed {exeHash[..12]} (was {current[..Math.Min(12, current.Length)]})");
            Activate(ManagedExePath, exeHash);
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
        }
    }

    private void Activate(string exe, string sha)
    {
        // Next logon starts the silent-updated build. RepairStartup() now honours this path too,
        // so opening the employee status window cannot point auto-start back at the old exe.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            key?.SetValue(Installer.RunValueName, $"\"{exe}\" --run");
        }
        catch (Exception ex) { Settings.Log("auto-update Run key: " + ex.Message); }

        // Best-effort: refresh the classic installed copy when this process has admin rights.
        // A normal user cannot write Program Files; that is fine, auto-start uses the new build.
        try
        {
            var classic = Path.Combine(Installer.InstallDir, "PulseDesk.Agent.exe");
            if (File.Exists(Path.GetDirectoryName(classic)!)) File.Copy(exe, classic, true);
        }
        catch { }

        try
        {
            File.WriteAllText(BuildMarker,
                "installed=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "sha256=" + sha + Environment.NewLine +
                "source=auto-updater" + Environment.NewLine);
        }
        catch (Exception ex) { Settings.Log("auto-update marker: " + ex.Message); }

        // Hand-over: the new exe waits for THIS pid to exit, then starts the collector. A brief
        // reconnect is unavoidable when replacing a running process; enrollment is untouched.
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--wait-run " + Environment.ProcessId,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });
        }
        catch (Exception ex)
        {
            Settings.Log("auto-update hand-over failed: " + ex.Message);
            return;
        }

        _life.StopApplication();
    }

    private static string ReadInstalledHash()
    {
        try
        {
            if (!File.Exists(BuildMarker)) return "";
            foreach (var line in File.ReadAllLines(BuildMarker))
                if (line.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
                    return line[7..].Trim();
        }
        catch { }
        return "";
    }

    private static bool LooksLikeHash(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private static string Hash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
