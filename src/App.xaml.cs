using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace APMultiTracker;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = null!;
    public static TwitchService Twitch { get; private set; } = null!;
    public static EngineHost Engine { get; private set; } = null!;
    public static TrackerStore Tracker { get; private set; } = null!;
    public static Updater Updater { get; private set; } = null!;
    public static bool IsExiting { get; private set; }
    public static bool DemoMode { get; private set; }

    public static string VersionTag
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v == null ? "v0.0.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private static bool _selfTest;
    private static string? _screenshotDir;
    private static Exception? _selfTestError;

    public static new App Current => (App)Application.Current;
    public static string ErrorLogPath => System.IO.Path.Combine(ConfigStore.Dir, "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        _selfTest = e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        int si = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (si >= 0) _screenshotDir = System.IO.Path.GetFullPath(si + 1 < e.Args.Length ? e.Args[si + 1] : "screenshots");
        DemoMode = _selfTest || _screenshotDir != null;

        if (!DemoMode)
        {
            // One instance per settings folder (a test copy with APMULTITRACKER_HOME never touches the real one).
            string scope = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(ConfigStore.Dir.ToLowerInvariant())))[..12];
            _mutex = new Mutex(true, "APMultiTracker.SingleInstance.v1." + scope, out bool isFirst);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "APMultiTracker.Show.v1." + scope);
            if (!isFirst)
            {
                _showSignal.Set();
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
        // Menus open to the right of the cursor, even on PCs set up for "right-handed" (tablet) menus.
        if (SystemParameters.MenuDropAlignment)
            typeof(SystemParameters).GetField("_menuDropAlignment",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.SetValue(null, false);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            LogError("UI", args.Exception);
            Log.Error("ui", "Unhandled UI error", args.Exception);
            if (_selfTest) _selfTestError ??= args.Exception;
        };

        try
        {
            StartApp();
        }
        catch (Exception ex)
        {
            LogError("Startup", ex);
            if (_selfTest) { Console.Error.WriteLine(ex); Shutdown(1); return; }
            MessageBox.Show("APMultiTracker couldn't start.\n\n" + ex.GetBaseException().Message +
                            "\n\nDetails were saved to:\n" + ErrorLogPath, "APMultiTracker",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartApp()
    {
        if (DemoMode)
        {
            ConfigStore.ReadOnly = true;
            Config = Demo.BuildConfig();
        }
        else Config = ConfigStore.Load();

        // "Update automatically on next launch": swap in the downloaded version before any UI appears.
        if (!DemoMode && Updater.TryInstallOnLaunch(Config))
        {
            RestartInto(Environment.ProcessPath!, "");
            return;
        }

        Twitch = new TwitchService();
        Engine = new EngineHost(Dispatcher);
        Tracker = new TrackerStore(Engine);
        Updater = new Updater(Config);
        Engine.Changed += () =>
        {
            if (Engine.State != EngineState.Running) Tracker.MarkAllOffline();
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (_showSignal != null)
            ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.InvokeAsync(ShowMain), null, Timeout.Infinite, false);

        Tracker.Restore();

        if (_selfTest) { _ = RunSelfTestAsync(window); return; }
        if (_screenshotDir != null)
        {
            _ = Dispatcher.InvokeAsync(async () => { await Demo.ScreenshotsAsync(window, _screenshotDir); ExitWith(0); });
            return;
        }
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try { await Twitch.InitAsync(); } catch { }
        Updater.Start();
        Engine.Detect();
        await Engine.StartAsync();
    }

    // ------------------------------------------------------------------ diagnostics

    public static void LogError(string where, Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Dir);
            System.IO.File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {VersionTag} {where}: {ex}\n\n");
        }
        catch { }
    }

    /// <summary>CI smoke test: real window, fake tracking data, visit every page, exit 0 (or 1 on any error).</summary>
    private async Task RunSelfTestAsync(MainWindow window)
    {
        try
        {
            Demo.Feed(Tracker);
            await Task.Delay(300);
            await window.SelfTestAsync();
            if (!window.IsVisible) throw new InvalidOperationException("Main window is not visible.");
            if (EngineHost.BundledEngineVersion == "0.0.0") throw new InvalidOperationException("The engine apworld isn't embedded.");
            if (EngineHost.Compare("0.10.0", "0.9.9") <= 0) throw new InvalidOperationException("Version compare is wrong.");
        }
        catch (Exception ex) { _selfTestError ??= ex; }

        if (_selfTestError != null) { LogError("selftest", _selfTestError); Console.Error.WriteLine(_selfTestError); ExitWith(1); }
        else { Console.WriteLine("SELFTEST OK " + VersionTag); ExitWith(0); }
    }

    private void ExitWith(int code)
    {
        IsExiting = true;
        Shutdown(code);
    }

    public static void SaveConfig()
    {
        try { ConfigStore.Save(Config); } catch (Exception ex) { Log.Error("config", "Save failed", ex); }
    }

    public static void SetStartup(bool on)
    {
        Config.StartWithWindows = on;
        SaveConfig();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (on) key.SetValue("APMultiTracker", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("APMultiTracker", throwOnMissingValue: false);
        }
        catch { }
    }

    public void ShowMain()
    {
        if (MainWindow is not Window w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        if (!Config.AlwaysOnTop) { w.Topmost = true; w.Topmost = false; }
    }

    /// <summary>Exit and start <paramref name="exePath"/> (used after installing an update).</summary>
    public void RestartInto(string exePath, string args)
    {
        IsExiting = true;
        try { if (Config != null) ConfigStore.Save(Config); } catch { }
        Engine?.KillNow();
        // Free the single-instance lock first, or the new process would just hand off to us and quit.
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, args) { UseShellExecute = false }); }
        catch { }
        Shutdown();
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        SaveConfig();
        if (!DemoMode) Engine?.KillNow();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
