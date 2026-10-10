using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace APMultiTracker;

public enum EngineState { NoArchipelago, NoTracker, NotInstalled, Stopped, Starting, Running, Error }

/// <summary>
/// Finds Archipelago, installs the engine apworld, starts the engine through ArchipelagoLauncher and keeps the
/// connection to it alive. The engine exits by itself when this app does.
/// </summary>
public sealed class EngineHost
{
    public const string ComponentName = "APMultiTracker Engine";
    public const string EngineFile = "apmultitracker.apworld";
    public const string TrackerReleasesUrl = "https://github.com/FarisTheAncient/Archipelago/releases?q=Tracker";

    private readonly Dispatcher _ui;
    private Process? _process;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _startCts;
    private bool _wantRunning;
    private DateTime _lastAutoRestart = DateTime.MinValue;

    public EngineClient Client { get; }
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string Detail { get; private set; } = "";

    public string? ApDir { get; private set; }
    public string ApVersion { get; private set; } = "";
    public string? UtVersion { get; private set; }
    public string? InstalledEngineVersion { get; private set; }
    public static string BundledEngineVersion { get; } = ReadBundledVersion();
    public bool EngineOutdated => InstalledEngineVersion != null && Compare(InstalledEngineVersion, BundledEngineVersion) < 0;

    public event Action? Changed;
    /// <summary>Raised (UI thread) each time a connection to the engine is established.</summary>
    public event Action? Connected;

    public EngineHost(Dispatcher ui)
    {
        _ui = ui;
        Client = new EngineClient(ui);
        Client.Disconnected += OnDisconnected;
    }

    private void Set(EngineState state, string detail = "")
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ detection

    /// <summary>Re-scan the Archipelago folder: is it there, is UT installed, is our engine installed (and current)?</summary>
    public void Detect()
    {
        ApDir = FindArchipelago(App.Config.ArchipelagoDir);
        ApVersion = ApDir == null ? "" : ReadApVersion(ApDir);
        UtVersion = ApDir == null ? null : FindTracker(ApDir);
        InstalledEngineVersion = ApDir == null ? null : FindInstalledEngine(ApDir);
        if (Client.IsConnected) { Changed?.Invoke(); return; }
        if (ApDir == null) Set(EngineState.NoArchipelago, "Archipelago wasn't found. Choose its folder in Settings.");
        else if (UtVersion == null) Set(EngineState.NoTracker, "Universal Tracker isn't installed in Archipelago.");
        else if (InstalledEngineVersion == null) Set(EngineState.NotInstalled, "The tracker engine isn't installed in Archipelago yet.");
        else if (State is EngineState.NoArchipelago or EngineState.NoTracker or EngineState.NotInstalled) Set(EngineState.Stopped);
        else Changed?.Invoke();
    }

    public static string? FindArchipelago(string configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Archipelago"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Archipelago"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Archipelago"));
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var root = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is string dn && dn.StartsWith("Archipelago", StringComparison.OrdinalIgnoreCase)
                        && k.GetValue("InstallLocation") is string loc && loc.Length > 0)
                        candidates.Add(loc);
                }
            }
            catch { }
        }
        return candidates.FirstOrDefault(IsArchipelagoDir);
    }

    public static bool IsArchipelagoDir(string dir) =>
        Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "ArchipelagoLauncher.exe")) || File.Exists(Path.Combine(dir, "Launcher.py")));

    private static string ReadApVersion(string dir)
    {
        try
        {
            string manifest = Path.Combine(dir, "manifest.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                return string.Join(".", doc.RootElement.GetProperty("version").EnumerateArray().Select(v => v.GetInt32()));
            }
            string utils = Path.Combine(dir, "Utils.py");
            if (File.Exists(utils))
            {
                var m = Regex.Match(File.ReadAllText(utils), "__version__ = \"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return "";
    }

    /// <summary>UT's version if any installed apworld contains TrackerCore.py (or a source checkout has it).</summary>
    private static string? FindTracker(string dir)
    {
        string? found = null;
        string custom = Path.Combine(dir, "custom_worlds");
        if (Directory.Exists(custom))
        {
            foreach (var file in Directory.EnumerateFiles(custom, "*.apworld"))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    var core = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/TrackerCore.py", StringComparison.OrdinalIgnoreCase));
                    if (core == null) continue;
                    string folder = core.FullName[..core.FullName.LastIndexOf('/')];
                    var init = zip.GetEntry(folder + "/__init__.py");
                    string v = "";
                    if (init != null)
                    {
                        using var r = new StreamReader(init.Open());
                        var m = Regex.Match(r.ReadToEnd(), "UT_VERSION\\s*=\\s*\"([^\"]+)\"");
                        if (m.Success) v = m.Groups[1].Value;
                    }
                    if (found == null || Compare(v.TrimStart('v'), found.TrimStart('v')) > 0) found = v.Length > 0 ? v : "installed";
                }
                catch { }
            }
        }
        foreach (var sub in new[] { Path.Combine("lib", "worlds", "tracker"), Path.Combine("worlds", "tracker") })
            if (found == null && Directory.Exists(Path.Combine(dir, sub))) found = "installed";
        return found;
    }

    private static string? FindInstalledEngine(string dir)
    {
        string file = Path.Combine(dir, "custom_worlds", EngineFile);
        if (File.Exists(file))
        {
            try
            {
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("apmultitracker/archipelago.json");
                if (entry != null)
                {
                    using var doc = JsonDocument.Parse(entry.Open());
                    return doc.RootElement.GetProperty("world_version").GetString();
                }
            }
            catch { }
            return "0.0.0";
        }
        string folder = Path.Combine(dir, "worlds", "apmultitracker", "archipelago.json");   // source checkout (development)
        if (File.Exists(folder))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(folder));
                return doc.RootElement.GetProperty("world_version").GetString();
            }
            catch { }
        }
        return null;
    }

    private static string ReadBundledVersion()
    {
        try
        {
            using var s = typeof(EngineHost).Assembly.GetManifestResourceStream(EngineFile);
            if (s == null) return "0.0.0";
            using var zip = new ZipArchive(s, ZipArchiveMode.Read);
            using var doc = JsonDocument.Parse(zip.GetEntry("apmultitracker/archipelago.json")!.Open());
            return doc.RootElement.GetProperty("world_version").GetString() ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }

    public static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out int n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    // ------------------------------------------------------------------ install

    /// <summary>Copies the bundled engine into Archipelago's custom_worlds, restarting the engine if it's running.</summary>
    public async Task InstallEngineAsync()
    {
        if (ApDir == null) throw new InvalidOperationException("Archipelago wasn't found.");
        bool wasRunning = Client.IsConnected || _process is { HasExited: false };
        if (wasRunning) await StopAsync();
        string dir = Path.Combine(ApDir, "custom_worlds");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, EngineFile);
        using (var s = typeof(EngineHost).Assembly.GetManifestResourceStream(EngineFile)
                       ?? throw new InvalidOperationException("This build doesn't contain the engine."))
        {
            string tmp = target + ".tmp";
            using (var f = File.Create(tmp)) await s.CopyToAsync(f);
            File.Move(tmp, target, overwrite: true);
        }
        Log.Info("engine", $"Installed engine {BundledEngineVersion} to {target}");
        Detect();
        if (wasRunning || _wantRunning) await StartAsync();
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>Connect to an engine already on the port, or launch one through Archipelago and wait for it.</summary>
    public async Task StartAsync()
    {
        _wantRunning = true;
        if (Client.IsConnected) return;
        _startCts?.Cancel();
        var cts = _startCts = new CancellationTokenSource();
        int port = App.Config.EnginePort;
        Set(EngineState.Starting, "Connecting to the engine\u2026");

        if (await Client.ConnectAsync(port, _token, cts.Token)) { OnConnected(); return; }

        Detect();
        if (State is EngineState.NoArchipelago or EngineState.NoTracker or EngineState.NotInstalled) return;
        if (EngineOutdated)
        {
            try { await InstallEngineAsync(); return; }
            catch (Exception ex) { Log.Error("engine", "Auto-update of the engine failed", ex); }
        }

        try { _process = Launch(ApDir!, port); }
        catch (Exception ex)
        {
            Log.Error("engine", "Couldn't start the engine", ex);
            Set(EngineState.Error, "Couldn't start Archipelago: " + ex.Message);
            return;
        }
        Set(EngineState.Starting, "Starting the engine (Archipelago loads all your apworlds first)\u2026");

        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            await Task.Delay(700);
            if (cts.IsCancellationRequested) return;
            if (await Client.ConnectAsync(port, _token, cts.Token)) { OnConnected(); return; }
            if (_process is { HasExited: true })
            {
                Set(EngineState.Error, "The engine closed while starting. Check the engine log (Settings \u2192 Open engine logs).");
                return;
            }
        }
        if (!cts.IsCancellationRequested) Set(EngineState.Error, "The engine didn't start in time. Check the engine log in Settings.");
    }

    private Process Launch(string dir, int port)
    {
        var args = new List<string> { ComponentName, "--", "--port", port.ToString(), "--token", _token,
                                       "--parent", Environment.ProcessId.ToString() };
        ProcessStartInfo psi;
        string exe = Path.Combine(dir, "ArchipelagoLauncher.exe");
        if (File.Exists(exe)) psi = new ProcessStartInfo(exe);
        else
        {
            // Source checkout (development): run Launcher.py with its virtualenv's Python if it has one.
            string venv = Path.Combine(dir, ".venv", "Scripts", "python.exe");
            psi = new ProcessStartInfo(File.Exists(venv) ? venv : "python");
            args.Insert(0, Path.Combine(dir, "Launcher.py"));
            psi.Environment["SKIP_REQUIREMENTS_UPDATE"] = "1";
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.WorkingDirectory = dir;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        Log.Info("engine", $"Launching engine: {psi.FileName} {string.Join(' ', args.Select(a => a == _token ? "***" : a))}");
        return Process.Start(psi) ?? throw new InvalidOperationException("Process didn't start.");
    }

    private void OnConnected()
    {
        Log.Info("engine", $"Connected to engine {Client.EngineVersion} (UT {Client.UtVersion}, AP {Client.ApVersion})");
        Set(EngineState.Running);
        Connected?.Invoke();
    }

    private void OnDisconnected()
    {
        if (!_wantRunning) { Set(EngineState.Stopped); return; }
        Log.Warn("engine", "Lost the engine connection");
        // Restart it, but not in a tight loop if it keeps dying.
        if (DateTime.UtcNow - _lastAutoRestart > TimeSpan.FromMinutes(1))
        {
            _lastAutoRestart = DateTime.UtcNow;
            Set(EngineState.Starting, "The engine stopped. Restarting\u2026");
            _ = _ui.InvokeAsync(StartAsync);
        }
        else Set(EngineState.Error, "The engine keeps stopping. Check the engine log in Settings.");
    }

    public async Task StopAsync()
    {
        _wantRunning = false;
        _startCts?.Cancel();
        if (Client.IsConnected)
        {
            await Client.ShutdownAsync();
            await Client.CloseAsync();
        }
        var p = _process;
        _process = null;
        if (p != null)
        {
            try { if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true); } catch { }
        }
        Set(EngineState.Stopped);
        Detect();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>Best effort on app exit (the engine also exits by itself once we're gone).</summary>
    public void KillNow()
    {
        _wantRunning = false;
        try { Client.ShutdownAsync().Wait(500); } catch { }
        try { if (_process is { HasExited: false } p && !p.WaitForExit(1500)) p.Kill(entireProcessTree: true); } catch { }
    }

    public string? EngineLogFolder => ApDir == null ? null : Path.Combine(ApDir, "logs");
}
