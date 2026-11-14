using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;

namespace PulseDesk.Agent;

public static class Installer
{
    public const string ServiceName = "PulseDeskAgent";   // legacy cleanup only
    public const string RunValueName = "PulseDesk";
    public const string TaskName = "PulseDesk Agent";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PulseDesk");

    private static string TargetExe => Path.Combine(InstallDir, "PulseDesk.Agent.exe");
    internal static string InstallLogPath => Path.Combine(Settings.Dir, "install.log");

    /// <summary>Write install diagnostics somewhere readable even when the GUI message is missed.</summary>
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            File.AppendAllText(InstallLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// Idempotent self-repair used when an already-installed agent is launched again from a
    /// freshly downloaded setup: make sure the logon auto-start entry exists for the real user.
    /// </summary>
    public static void RepairStartup()
    {
        try { RegisterStartup(); }
        catch (Exception ex) { Log("repair startup failed: " + ex.Message); }
    }

    public static int Run()    {
        var conf = EmbeddedConfig.Read();
        if (conf == null || string.IsNullOrWhiteSpace(conf.code))
        {
            MessageBox.Show(
                "Ye setup file kisi invite link se download karein.\n\n" +
                "Direct file copy kar ke install nahi ho sakta - har device ka apna invite code hota hai.",
                "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        var consent = MessageBox.Show(
            "PulseDesk v.2 - Company Monitoring Agent\n\n" +
            "Is computer par company monitoring install hone wali hai.\n\n" +
            "Record hone wali cheezein:\n" +
            "  - Screen (periodic screenshots + live view jab admin dekhe)\n" +
            "  - Running apps aur window titles\n" +
            "  - Active / idle time\n\n" +
            "Record NAHI hoti:\n" +
            "  - Keystrokes (raw typing)\n" +
            "  - Files, passwords, personal messages\n\n" +
            "Data company policy ke tehat admin ke server par store hoti hai.\n" +
            "Install karne ke liye OK dabaein (ye aap ka consent hai).",
            "PulseDesk v.2 - Consent", MessageBoxButtons.OKCancel, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);

        if (consent != DialogResult.OK) return 1;

        using var progress = ProgressForm.Show("PulseDesk v.2 installing...");
        var step = "start";
        void Step(string name, Action action)
        {
            step = name;
            Log("step: " + name);
            try { progress.SetStatus(name + "..."); } catch { }
            action();
        }

        try
        {
            Step("Enrolling device", () =>
            {
                var enrolled = Enroll(conf).GetAwaiter().GetResult();
                if (enrolled == null)
                    throw new InvalidOperationException(
                        "Enrollment fail hua (server ne invite accept nahi kiya). Internet check karein ya admin se naya link lein.");

                Step("Installing files", () =>
                {
                    Directory.CreateDirectory(InstallDir);
                    var self = Environment.ProcessPath!;
                    foreach (var p in Process.GetProcessesByName("PulseDesk.Agent"))
                        if (p.Id != Environment.ProcessId) { try { p.Kill(); } catch { } }
                    if (!string.Equals(self, TargetExe, StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            try { File.Copy(self, TargetExe, true); break; }
                            catch (IOException) { Thread.Sleep(400); }
                        }
                    }
                    Log("files installed -> " + TargetExe);

                    Step("Writing configuration", () =>
                    {
                        var s = new Settings
                        {
                            Server = conf.server,
                            DeviceToken = enrolled.Value.token,
                            DeviceId = enrolled.Value.id,
                            ConsentAt = DateTime.UtcNow.ToString("O"),
                            ScreenshotIntervalSec = 600,
                            Paused = false
                        };
                        s.Save();
                        Log("settings saved -> " + Settings.FilePath);
                        GrantUsersModify(Settings.Dir);

                        Step("Setting up auto-start", () =>
                        {
                            RegisterStartup();
                            Sc("stop " + ServiceName);     // remove old service install if any
                            Sc("delete " + ServiceName);

                            Step("Starting agent", () =>
                            {
                                StartAgent();

                                Step("Finalising", () =>
                                {
                                    WriteUninstallEntry();
                                });
                            });
                        });
                    });
                });
            });

            progress.Close();
            Log("install complete");
            MessageBox.Show(
                "PulseDesk v.2 install ho gaya.\n\n" +
                "Monitoring chalu hai. Ye background mein chalta hai - koi window nahi khulti.\n" +
                "Windows Settings -> Apps mein 'PulseDesk v.2 Monitoring Agent' dikhega (wahan se uninstall).\n\n" +
                "Koi setting nahi karni - admin apni console se sab control karta hai.",
                "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            Log("INSTALL FAILED at step '" + step + "': " + ex);
            try { progress.Close(); } catch { }
            MessageBox.Show(
                "Install error (" + step + "):\n" + ex.Message +
                "\n\nLog: " + InstallLogPath,
                "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 4;
        }
    }

    private static async Task<(string id, string token)?> Enroll(EmbeddedConfig.Conf conf)
    {
        using var http = HttpFactory.Create(TimeSpan.FromSeconds(30));
        var body = new
        {
            code = conf.code,
            machine = Environment.MachineName,
            user = Environment.UserName,
            os = Environment.OSVersion.VersionString,
            agentVersion = typeof(Installer).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            consentAt = DateTime.UtcNow.ToString("O")
        };
        var r = await http.PostAsJsonAsync(conf.server.TrimEnd('/') + "/api/enroll", body);
        var text = await r.Content.ReadAsStringAsync();
        Log($"enroll POST {conf.server.TrimEnd('/')}/api/enroll -> {(int)r.StatusCode}: {text}");
        if (!r.IsSuccessStatusCode) return null;
        var j = JsonSerializer.Deserialize<JsonElement>(text);
        if (!j.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;
        return (j.GetProperty("deviceId").GetString()!, j.GetProperty("deviceToken").GetString()!);
    }

    /// <summary>HKCU Run entry - starts the agent inside the user's desktop session at logon.</summary>
    private static void RegisterStartup()
    {
        var value = $"\"{TargetExe}\" --run";

        // The installer is elevated (often under a different admin account). Write the Run
        // entry into the *interactive* user's hive as well, otherwise the employee never
        // gets an auto-start entry.
        var sid = GetConsoleUserSid();
        if (sid != null)
        {
            try
            {
                using var hk = Microsoft.Win32.Registry.Users.OpenSubKey(
                    sid.Value + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (hk != null)
                {
                    hk.SetValue(RunValueName, value);
                    Log("startup written for console user " + sid.Value);
                }
                else Log("startup: HKU\\" + sid.Value + "\\...\\Run not found");
            }
            catch (Exception ex) { Log("startup HKU failed: " + ex.Message); }
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue(RunValueName, value);
            Log("startup written for current user: " + value);
        }
        catch (Exception ex) { Log("startup HKCU failed: " + ex.Message); }
    }

    // ---- interactive (console) user lookup -------------------------------------
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, int infoClass,
        out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr p);

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    private static string? QueryWtsString(uint sessionId, int infoClass)
    {
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buf, out var _)) return null;
            try { return Marshal.PtrToStringUni(buf); }
            finally { WTSFreeMemory(buf); }
        }
        catch { return null; }
    }

    private static SecurityIdentifier? GetConsoleUserSid()
    {
        try
        {
            var session = WTSGetActiveConsoleSessionId();

            // 1) SID straight from the interactive shell's process token.
            //    This works even when the machine's domain trust is broken
            //    (NTAccount.Translate then fails with "trust relationship ... failed").
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (session != 0xFFFFFFFF && p.SessionId != (int)session) continue;
                    var sid = GetSidFromProcessToken(p.Id);
                    if (sid != null)
                    {
                        Log("interactive user SID (token): " + sid.Value);
                        return sid;
                    }
                }
                catch { }
            }

            var user = QueryWtsString(session, WtsUserName);
            if (string.IsNullOrWhiteSpace(user)) return null;

            // 2) name -> SID (needs a healthy domain / local account)
            try
            {
                var domain = QueryWtsString(session, WtsDomainName);
                var account = string.IsNullOrWhiteSpace(domain) ? user : domain + "\\" + user;
                return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            }
            catch (Exception ex) { Log("name->SID failed: " + ex.Message); }

            // 3) profile folder mapping (C:\Users\<user>) - no domain lookup needed
            var viaProfile = GetSidFromProfile(user);
            if (viaProfile != null)
            {
                Log("interactive user SID (profile): " + viaProfile.Value);
                return viaProfile;
            }
        }
        catch (Exception ex) { Log("console user lookup failed: " + ex.Message); }
        return null;
    }

    private static SecurityIdentifier? GetSidFromProfile(string userName)
    {
        try
        {
            using var pl = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (pl == null) return null;
            foreach (var sidText in pl.GetSubKeyNames())
            {
                using var k = pl.OpenSubKey(sidText);
                if (k?.GetValue("ProfileImagePath") is not string path || path.Length == 0) continue;
                var leaf = path.TrimEnd('\\').Split('\\')[^1];
                if (string.Equals(leaf, userName, StringComparison.OrdinalIgnoreCase))
                    return new SecurityIdentifier(sidText);
            }
        }
        catch { }
        return null;
    }

    // ---- process token -> SID ---------------------------------------------------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUserInfo = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, uint len,
        out uint retLen);

    private static SecurityIdentifier? GetSidFromProcessToken(int pid)
    {
        IntPtr proc = IntPtr.Zero, tok = IntPtr.Zero;
        try
        {
            proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero) return null;
            if (!OpenProcessToken(proc, TOKEN_QUERY, out tok)) return null;

            GetTokenInformation(tok, TokenUserInfo, IntPtr.Zero, 0, out var len);
            if (len == 0) return null;

            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetTokenInformation(tok, TokenUserInfo, buf, len, out _)) return null;
                var sidPtr = Marshal.ReadIntPtr(buf);   // TOKEN_USER.User.Sid
                return sidPtr == IntPtr.Zero ? null : new SecurityIdentifier(sidPtr);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return null; }
        finally
        {
            if (tok != IntPtr.Zero) CloseHandle(tok);
            if (proc != IntPtr.Zero) CloseHandle(proc);
        }
    }

    private static void StartAgent()
    {
        try
        {
            Process.Start(new ProcessStartInfo(TargetExe, "--run")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallDir
            });
        }
        catch (Exception ex) { Settings.Log("start agent failed: " + ex.Message); }
    }

    /// <summary>Agent stores its config in ProgramData - standard users must be able to write there.</summary>
    private static void GrantUsersModify(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo("icacls.exe",
                $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(20000);
        }
        catch { }
    }

    private static int Sc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            if (!string.IsNullOrWhiteSpace(so) || !string.IsNullOrWhiteSpace(se))
                Settings.Log($"sc {args.Split(' ')[0]}: {so.Trim()} {se.Trim()}".Trim());
            return p.ExitCode;
        }
        catch { return -1; }
    }

    private static void WriteUninstallEntry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PulseDesk");
            key.SetValue("DisplayName", "PulseDesk v.2 Monitoring Agent");
            key.SetValue("Publisher", "PulseDesk");
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{TargetExe}\" --uninstall");
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch { }
    }

    public static int Uninstall()
    {
        var r = MessageBox.Show("PulseDesk v.2 monitoring agent remove karein?", "PulseDesk v.2",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return 1;

        // stop + auto-start removal
        foreach (var p in Process.GetProcessesByName("PulseDesk.Agent"))
            if (p.Id != Environment.ProcessId) { try { p.Kill(); } catch { } }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue(RunValueName, false);
        }
        catch { }

        try
        {
            var sid = GetConsoleUserSid();
            if (sid != null)
            {
                using var hk = Microsoft.Win32.Registry.Users.OpenSubKey(
                    sid.Value + @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                hk?.DeleteValue(RunValueName, false);
            }
        }
        catch { }

        Sc("stop " + ServiceName);
        Sc("delete " + ServiceName);

        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PulseDesk", false);
        }
        catch { }

        try
        {
            var dir = Settings.Dir;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }

        // self-delete of the install folder is done by a tiny delayed helper
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c timeout /t 2 >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }

        MessageBox.Show("PulseDesk v.2 remove ho gaya.", "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

internal sealed class ProgressForm : Form
{
    private readonly Label _label;
    public ProgressForm(string title)
    {
        Text = title;
        Width = 380; Height = 120;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ControlBox = false;
        TopMost = true;
        BackColor = System.Drawing.Color.FromArgb(13, 59, 57);
        ForeColor = System.Drawing.Color.White;

        _label = new Label
        {
            Text = title,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Font = new System.Drawing.Font("Segoe UI", 10F),
            ForeColor = System.Drawing.Color.FromArgb(223, 236, 232)
        };
        Controls.Add(_label);
    }

    public void SetStatus(string text)
    {
        _label.Text = text;
        Application.DoEvents();
    }

    public static ProgressForm Show(string title)
    {
        var f = new ProgressForm(title);
        f.Show();
        Application.DoEvents();
        return f;
    }
}
