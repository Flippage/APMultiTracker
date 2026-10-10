using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace APMultiTracker;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Error, Unavailable }

/// <summary>
/// Self-updater backed by GitHub Releases (public repo, no auth).
///  • Checks the latest release 15 s after start and every 6 h.
///  • Picks the file matching this build (standalone → .zip, small → -small-needs-dotnet10.exe).
///  • Downloads to %LOCALAPPDATA%\APMultiTracker\updates\vX.Y.Z\, verifies GitHub's SHA-256 digest.
///  • Install: rename the running EXE to .old, copy the new one in its place, relaunch, exit.
///    The .old file is removed on the next start. Settings (AppData) are never touched.
/// </summary>
public sealed class Updater
{
    public const string Repo = "Flippage/APMultiTracker";

    /// <summary>GitHub's API for this repo. APMULTITRACKER_UPDATE_API points it elsewhere (testing only).</summary>
    private static string ApiBase =>
        Environment.GetEnvironmentVariable("APMULTITRACKER_UPDATE_API") is { Length: > 0 } api
            ? api.TrimEnd('/') : $"https://api.github.com/repos/{Repo}";

    // NOTE: static initializers run top to bottom. CurrentVersion must come before anything that uses it
    // (the HttpClient's User-Agent) — v1.1.0 crashed on start because of exactly this ordering.
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));
    public static string CurrentTag => $"v{CurrentVersion.ToString(3)}";

    private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);
    private static HttpClient Http => HttpLazy.Value;
    private static readonly string UpdatesDir =
        Environment.GetEnvironmentVariable("APMULTITRACKER_HOME") is { Length: > 0 } home
            ? Path.Combine(Path.GetFullPath(home), "updates")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APMultiTracker", "updates");

    private readonly AppConfig _cfg;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private CancellationTokenSource? _bgDownload;
    private string? _assetUrl, _assetName, _assetDigest;
    private long _assetSize;
    private string? _notified;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? LatestTag { get; private set; }
    public string? NotesUrl { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastChecked { get; private set; }

    /// <summary>True when something is waiting for the user (Settings badge).</summary>
    public bool HasUpdate => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Ready;

    public event Action? Changed;
    /// <summary>Raised once per version when it's worth telling the user (in-app toast). Arg = downloaded?</summary>
    public event Action<bool>? Notice;

    public Updater(AppConfig cfg)
    {
        _cfg = cfg;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(6);
            await CheckAsync();
        };
    }

    public void Start()
    {
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Start();
        _ = CleanupAsync();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"APMultiTracker/{CurrentVersion.ToString(3)}");
        return c;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static Version? ParseTag(string? tag) =>
        Version.TryParse((tag ?? "").Trim().TrimStart('v', 'V'), out var v) ? Normalize(v) : null;

    /// <summary>Standalone build (bundles .NET) vs the small one — baked in at publish time.</summary>
    private static bool IsStandalone =>
        !string.Equals(Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SelfContained")?.Value, "false", StringComparison.OrdinalIgnoreCase);

    private void Set(UpdateState s)
    {
        State = s;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ check

    public async Task CheckAsync(bool manual = false)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing) return;
        Error = null;
        if (manual || State is UpdateState.Idle or UpdateState.UpToDate or UpdateState.Error) Set(UpdateState.Checking);
        try
        {
            using var resp = await Http.GetAsync($"{ApiBase}/releases/latest");
            LastChecked = DateTime.Now;
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404)
                {
                    // A private repo (or one with no releases yet) looks like this to an anonymous check.
                    Error = null;
                    Set(UpdateState.Unavailable);
                }
                else if (manual) { Error = $"GitHub returned {(int)resp.StatusCode}."; Set(UpdateState.Error); }
                else Set(UpdateState.UpToDate);
                return;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            var latest = ParseTag(tag);
            if (latest == null || latest <= CurrentVersion) { LatestTag = tag; Set(UpdateState.UpToDate); return; }
            Log.Info("update", $"Update available: {tag} (running {CurrentTag})");

            LatestTag = $"v{latest.ToString(3)}";
            NotesUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            PickAsset(r);

            // Already downloaded on an earlier run?
            var p = _cfg.PendingUpdate;
            if (p != null && ParseTag(p.Version) == latest && File.Exists(p.ExePath))
            {
                Set(UpdateState.Ready);
                RaiseNotice(true);
                return;
            }

            Set(UpdateState.Available);
            if (_cfg.AutoDownloadUpdates) MaybeStartBackgroundDownload();
            else RaiseNotice(false);
        }
        catch (Exception ex)
        {
            if (manual) { Error = "Couldn't reach GitHub: " + ex.Message; Set(UpdateState.Error); }
            else if (State == UpdateState.Checking) Set(UpdateState.Idle);
        }
    }

    private void PickAsset(JsonElement release)
    {
        _assetUrl = _assetName = _assetDigest = null;
        _assetSize = 0;
        bool standalone = IsStandalone;
        foreach (var a in release.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            bool isSmall = name.Contains("small", StringComparison.OrdinalIgnoreCase);
            bool match = standalone
                ? !isSmall && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                : isSmall && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            // Fallback for standalone if a release ever lacks the zip: the full exe.
            bool fallback = standalone && _assetUrl == null && !isSmall && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (!match && !fallback) continue;
            _assetName = name;
            _assetUrl = a.GetProperty("browser_download_url").GetString();
            _assetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            _assetDigest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (match) break;
        }
    }

    private void RaiseNotice(bool downloaded)
    {
        string key = (LatestTag ?? "") + (downloaded ? ":ready" : ":available");
        if (_notified == key) return;
        _notified = key;
        Notice?.Invoke(downloaded);
    }

    // ------------------------------------------------------------------ download

    private void MaybeStartBackgroundDownload()
    {
        if (State != UpdateState.Available || !_cfg.AutoDownloadUpdates || _bgDownload != null) return;
        _bgDownload = new CancellationTokenSource();
        var cts = _bgDownload;
        _ = Task.Run(async () =>
        {
            bool ok = await DownloadAsync(cts.Token);
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                _bgDownload = null;
                if (ok) RaiseNotice(true);
                else if (cts.IsCancellationRequested) Set(UpdateState.Available);
            });
        });
    }

    /// <summary>Downloads + verifies the release file; returns true when the update is Ready.</summary>
    private async Task<bool> DownloadAsync(CancellationToken ct)
    {
        if (_assetUrl == null || _assetName == null || LatestTag == null)
        {
            await UI(() => { Error = "This release has no file for your version of APMultiTracker."; Set(UpdateState.Error); });
            return false;
        }
        string dir = Path.Combine(UpdatesDir, LatestTag);
        string part = Path.Combine(dir, _assetName + ".part");
        string exe = Path.Combine(dir, "APMultiTracker.exe");
        await UI(() => { Progress = 0; Error = null; Set(UpdateState.Downloading); });
        try
        {
            Directory.CreateDirectory(dir);
            using (var resp = await Http.GetAsync(_assetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? _assetSize;
                using var sha = SHA256.Create();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using (var dst = File.Create(part))
                {
                    var buf = new byte[81920];
                    long done = 0;
                    var lastReport = DateTime.MinValue;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        done += n;
                        if (total > 0 && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(150))
                        {
                            lastReport = DateTime.UtcNow;
                            double p = (double)done / total;
                            await UI(() => { Progress = p; Changed?.Invoke(); });
                        }
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

                // Integrity: GitHub publishes "sha256:<hex>" for every release file.
                string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                string? expected = _assetDigest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
                    ? _assetDigest[7..].ToLowerInvariant() : null;
                if (expected != null ? actual != expected : (_assetSize > 0 && new FileInfo(part).Length != _assetSize))
                    throw new InvalidDataException("The download didn't match GitHub's checksum, so it was discarded.");
            }

            if (_assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(part);
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("APMultiTracker.exe", StringComparison.OrdinalIgnoreCase))
                            ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The update package didn't contain APMultiTracker.exe.");
                entry.ExtractToFile(exe, overwrite: true);
                zip.Dispose();
                File.Delete(part);
            }
            else
            {
                File.Move(part, exe, overwrite: true);
            }

            Log.Info("update", $"Downloaded and verified {LatestTag}");
            await UI(() =>
            {
                _cfg.PendingUpdate = new PendingUpdate { Version = LatestTag, ExePath = exe };
                App.SaveConfig();
                Progress = 1;
                Set(UpdateState.Ready);
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            await UI(() => Set(UpdateState.Available));
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("update", "Download failed", ex);
            TryDelete(part);
            await UI(() => { Error = ex.Message; Set(UpdateState.Error); });
            return false;
        }
    }

    private static Task UI(Action a) => App.Current.Dispatcher.InvokeAsync(a).Task;

    // ------------------------------------------------------------------ install

    /// <summary>"Update now": download if needed, then install and relaunch.</summary>
    public async Task UpdateNowAsync()
    {
        if (State == UpdateState.Available)
        {
            _bgDownload?.Cancel();
            _bgDownload = null;
            if (!await DownloadAsync(CancellationToken.None)) return;
        }
        if (State != UpdateState.Ready) return;
        Set(UpdateState.Installing);
        await Task.Delay(400);   // let the UI show "Installing…"
        if (!TryInstall(_cfg, out string? err))
        {
            Error = err;
            Set(UpdateState.Error);
            return;
        }
        App.Current.RestartInto(Environment.ProcessPath!, "");
    }

    /// <summary>
    /// Swap the running EXE for the downloaded one. Windows allows renaming a running EXE, so:
    /// APMultiTracker.exe → APMultiTracker.exe.old, then copy the new file into place.
    /// </summary>
    public static bool TryInstall(AppConfig cfg, out string? error)
    {
        error = null;
        var p = cfg.PendingUpdate;
        string? target = Environment.ProcessPath;
        if (p == null || !File.Exists(p.ExePath)) { error = "The downloaded update is missing. Check for updates again."; return false; }
        if (target == null) { error = "Couldn't find APMultiTracker's own location."; return false; }

        string old = target + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(target, old);
        }
        catch (Exception ex)
        {
            error = "APMultiTracker can't replace itself in this folder (" + ex.Message + "). " +
                    "Move APMultiTracker.exe to a folder you own, such as Documents, and try again.";
            return false;
        }
        try
        {
            File.Copy(p.ExePath, target, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Move(old, target); } catch { }   // put the original back
            error = "Couldn't write the new version: " + ex.Message;
            return false;
        }
        Log.Info("update", $"Installed {p.Version}; restarting");
        cfg.PendingUpdate = null;
        try { ConfigStore.Save(cfg); } catch { }
        return true;
    }

    /// <summary>Startup hook for "Update automatically on next launch". True = installed, caller should relaunch.</summary>
    public static bool TryInstallOnLaunch(AppConfig cfg)
    {
        var p = cfg.PendingUpdate;
        if (!cfg.AutoDownloadUpdates || !cfg.InstallUpdatesOnLaunch || p == null) return false;
        var v = ParseTag(p.Version);
        if (v == null || v <= CurrentVersion || !File.Exists(p.ExePath)) return false;
        return TryInstall(cfg, out _);
    }

    /// <summary>After an update: remove APMultiTracker.exe.old and stale downloads.</summary>
    private async Task CleanupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(10));   // the old process may still be exiting
        string? target = Environment.ProcessPath;
        if (target != null) TryDelete(target + ".old");

        var p = _cfg.PendingUpdate;
        if (p != null && (ParseTag(p.Version) is not { } pv || pv <= CurrentVersion || !File.Exists(p.ExePath)))
        {
            _cfg.PendingUpdate = null;
            App.SaveConfig();
        }
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;
            foreach (var d in Directory.GetDirectories(UpdatesDir))
            {
                var v = ParseTag(Path.GetFileName(d));
                bool keep = v != null && v > CurrentVersion && _cfg.PendingUpdate?.ExePath.StartsWith(d, StringComparison.OrdinalIgnoreCase) == true;
                if (!keep) { try { Directory.Delete(d, recursive: true); } catch { } }
            }
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void OpenReleaseNotes()
    {
        string url = NotesUrl ?? $"https://github.com/{Repo}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
