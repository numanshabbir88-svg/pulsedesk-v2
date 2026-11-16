using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseDesk.Agent;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var self = Environment.ProcessPath!;

        // ---- explicit install / uninstall (both need admin)
        if (mode is "--install" or "--uninstall")
        {
            if (!IsAdmin()) return RelaunchElevated(args);
            return mode == "--install" ? Installer.Run() : Installer.Uninstall();
        }

        // ---- explicit background run (auto-start)
        if (mode == "--run") return RunCollector(args);

        // ---- no arguments: two cases
        //   a) freshly downloaded setup with an embedded invite  -> install
        //   b) already installed exe                              -> collect
        var settings = Settings.Load();
        var installed = !string.IsNullOrWhiteSpace(settings.DeviceToken) && File.Exists(Settings.FilePath);

        if (!installed)
        {
            var conf = EmbeddedConfig.Read();
            if (conf == null || string.IsNullOrWhiteSpace(conf.code))
            {
                MessageBox.Show(
                    "Ye setup file kisi invite link se download karein.\n\n" +
                    "Direct file copy kar ke install nahi ho sakta - har device ka apna invite code hota hai.",
                    "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
            if (!IsAdmin()) return RelaunchElevated(args);
            return Installer.Run();
        }

        // Already installed: repair the logon auto-start entry (covers re-running the setup
        // on a machine where the entry was lost) and keep the agent running.
        Installer.RepairStartup();
        return RunCollector(args);
    }

    private static int RunCollector(string[] args)
    {
        // PerMonitorV2: without it Windows bitmap-stretches the process on 125%/150% scaled
        // laptops, so the screen capture (and the mouse mapping) becomes blurry. Must run
        // before any window/handle is created.
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

        // One agent per user session - avoids double samples when both startup + manual run happen.
        using var mutex = new Mutex(true, @"Local\PulseDeskAgent", out var fresh);
        if (!fresh) return 0;

        var settings = Settings.Load();

        var host = Host.CreateDefaultBuilder(args)
            .UseWindowsService(o => { o.ServiceName = Installer.ServiceName; })
            .ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.AddProvider(new FileLoggerProvider());
            })
            .ConfigureServices(s =>
            {
                s.AddSingleton(settings);
                s.AddHostedService<Collector>();
            })
            .Build();

        host.Run();
        return 0;
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Re-launch the same exe elevated (single UAC prompt for the employee).</summary>
    private static int RelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args),
                WorkingDirectory = AppContext.BaseDirectory
            };
            Process.Start(psi);
            return 0;
        }
        catch
        {
            MessageBox.Show(
                "Install ke liye administrator permission chahiye.\n" +
                "UAC prompt mein 'Yes' dabaein, ya IT/admin se install karwayein.",
                "PulseDesk v.2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 5;
        }
    }
}

internal sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName);
    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _cat;
        public FileLogger(string cat) => _cat = cat;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            Settings.Log($"[{logLevel}] {_cat}: {msg}" + (exception != null ? " | " + exception.Message : ""));
        }
    }
}
