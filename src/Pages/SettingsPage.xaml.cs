using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace APMultiTracker;

public partial class SettingsPage : UserControl
{
    private DeviceCode? _device;
    private CancellationTokenSource? _loginCts;
    private DateTime _deviceExpiry;
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        ConfigPathRun.Text = ConfigStore.Dir;
        VersionText.Text = $"APMultiTracker {App.VersionTag} \u00b7 bundled engine v{EngineHost.BundledEngineVersion}";
        _countdown.Tick += (_, _) => UpdateCountdown();
        App.Twitch.AuthChanged += () => Dispatcher.InvokeAsync(UpdateAccount);
        App.Engine.Changed += () => Dispatcher.InvokeAsync(UpdateEngine);
        App.Updater.Changed += () => Dispatcher.InvokeAsync(UpdateUpdates);
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            if (!App.DemoMode && !App.Engine.Client.IsConnected) App.Engine.Detect();
            UpdateEngine();
            UpdateAccount();
            UpdateUpdates();
        };
        UpdateEngine();
        UpdateAccount();
        UpdateUpdates();
    }

    // ------------------------------------------------------------ engine

    private void SetCheck(Border icon, System.Windows.Shapes.Path path, bool ok, bool warn = false)
    {
        path.Data = (Geometry)FindResource(ok ? "IconCheck" : warn ? "IconDownload" : "IconClose");
        path.Stroke = (Brush)FindResource(ok ? "GoodBrush" : warn ? "WarnBrush" : "DangerBrush");
        icon.Background = ok ? new SolidColorBrush(Color.FromRgb(0x10, 0x29, 0x1A)) : (Brush)FindResource(warn ? "WarnBgBrush" : "DangerBgBrush");
    }

    private void UpdateEngine()
    {
        var e = App.Engine;
        bool demo = App.DemoMode;
        bool hasAp = demo || e.ApDir != null;
        ApPath.Text = demo ? @"C:\ProgramData\Archipelago" : e.ApDir ?? "Not found. Click Change\u2026 to choose your Archipelago folder.";
        if (hasAp && !demo && e.ApVersion.Length > 0) ApPath.Text += $"   (Archipelago {e.ApVersion})";

        bool ut = demo || e.UtVersion != null;
        SetCheck(UtIcon, UtPath, ut);
        UtTitle.Text = ut ? $"Universal Tracker {(demo ? "v0.3.4" : e.UtVersion)}" : "Universal Tracker isn't installed";
        UtSub.Text = ut ? "Its logic engine is what works out which checks are in logic."
                        : "Download tracker.apworld and put it in Archipelago's custom_worlds folder (or double-click it).";
        UtBtn.Visibility = ut ? Visibility.Collapsed : Visibility.Visible;
        UtBtn.IsEnabled = hasAp;

        bool installed = demo || e.InstalledEngineVersion != null;
        bool outdated = !demo && e.EngineOutdated;
        SetCheck(EngIcon, EngPath, installed && !outdated, warn: true);
        EngTitle.Text = !installed ? "Tracker engine isn't installed"
                      : outdated ? $"Engine update available (v{e.InstalledEngineVersion} \u2192 v{EngineHost.BundledEngineVersion})"
                      : $"Tracker engine v{(demo ? EngineHost.BundledEngineVersion : e.InstalledEngineVersion)}";
        EngSub.Text = !installed ? "One click: copies apmultitracker.apworld into custom_worlds."
                    : outdated ? "Installs the engine bundled with this version of the app."
                    : "Installed in custom_worlds.";
        InstallBtn.Visibility = !installed || outdated ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.Content = installed ? "Update engine" : "Install engine";
        InstallBtn.IsEnabled = hasAp;

        var state = demo ? EngineState.Running : e.State;
        (string title, string sub, string color) = state switch
        {
            EngineState.Running => ("Engine running", $"Universal Tracker {e.Client.UtVersion} \u00b7 Archipelago {e.Client.ApVersion}", "GoodBrush"),
            EngineState.Starting => ("Starting\u2026", e.Detail, "AccentBrush"),
            EngineState.Error => ("Engine error", e.Detail, "DangerBrush"),
            EngineState.Stopped => ("Engine stopped", "Start it to track your games.", "SubBrush"),
            _ => ("Engine not ready", e.Detail, "WarnBrush"),
        };
        if (demo) sub = "Universal Tracker v0.3.4 \u00b7 Archipelago 0.6.7";
        RunTitle.Text = title;
        RunSub.Text = sub;
        RunRow.BorderBrush = (Brush)FindResource(color);
        bool ready = installed && ut && hasAp;
        StartBtn.Visibility = state is EngineState.Running or EngineState.Starting ? Visibility.Collapsed : Visibility.Visible;
        StartBtn.IsEnabled = ready;
        StopBtn.Visibility = state is EngineState.Running or EngineState.Starting ? Visibility.Visible : Visibility.Collapsed;
        StopBtn.Content = state == EngineState.Running ? "Restart" : "Stop";
        LogsBtn.IsEnabled = e.EngineLogFolder != null;

        _loading = true;
        TopToggle.IsChecked = App.Config.AlwaysOnTop;
        StartupToggle.IsChecked = App.Config.StartWithWindows;
        _loading = false;
    }

    private void BrowseAp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Archipelago folder (with ArchipelagoLauncher.exe)" };
        if (App.Engine.ApDir != null) dlg.InitialDirectory = App.Engine.ApDir;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!EngineHost.IsArchipelagoDir(dlg.FolderName))
        {
            MessageBox.Show(Window.GetWindow(this), "That folder doesn't contain ArchipelagoLauncher.exe.", "APMultiTracker");
            return;
        }
        App.Config.ArchipelagoDir = dlg.FolderName;
        App.SaveConfig();
        App.Engine.Detect();
        UpdateEngine();
    }

    private void GetUt_Click(object sender, RoutedEventArgs e) => OpenUrl(EngineHost.TrackerReleasesUrl);

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        try
        {
            await App.Engine.InstallEngineAsync();
            if (App.Engine.State == EngineState.Stopped) await App.Engine.StartAsync();
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "Windows didn't allow writing to Archipelago's custom_worlds folder. " +
                            "Copy apmultitracker.apworld there yourself, or run APMultiTracker as administrator once.", "APMultiTracker");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Couldn't install the engine: " + ex.Message, "APMultiTracker");
        }
        UpdateEngine();
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await App.Engine.StartAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.State == EngineState.Running) await App.Engine.RestartAsync();
        else await App.Engine.StopAsync();
    }

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.EngineLogFolder is string dir && System.IO.Directory.Exists(dir))
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Log.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void Top_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Config.AlwaysOnTop = TopToggle.IsChecked == true;
        App.SaveConfig();
        if (Window.GetWindow(this) is Window w) w.Topmost = App.Config.AlwaysOnTop;
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) App.SetStartup(StartupToggle.IsChecked == true);
    }

    // ------------------------------------------------------------ updates

    private void UpdateUpdates()
    {
        var u = App.Updater;
        string tag = u.LatestTag ?? "";
        string cur = Updater.CurrentTag;
        string title, sub, color, bg;
        Geometry icon = (Geometry)FindResource("IconDownload");
        bool showNow = false, showNotes = false, showCheck = false, showBar = false;

        switch (u.State)
        {
            case UpdateState.Available:
                title = $"Update available \u00b7 {tag}";
                sub = "Click Update now to download and install. APMultiTracker restarts by itself.";
                color = "WarnBrush"; bg = "WarnBgBrush"; showNow = showNotes = true;
                break;
            case UpdateState.Ready:
                title = $"{tag} downloaded \u00b7 ready to install";
                sub = App.Config.AutoDownloadUpdates && App.Config.InstallUpdatesOnLaunch
                    ? "Installs automatically next time APMultiTracker starts, or click Update now."
                    : "Click Update now to install. APMultiTracker restarts by itself.";
                color = "WarnBrush"; bg = "WarnBgBrush"; showNow = showNotes = true;
                break;
            case UpdateState.Downloading:
                title = $"Downloading {tag}\u2026 {u.Progress * 100:0}%";
                sub = "Verified against GitHub's SHA-256 checksum when finished.";
                color = "AccentBrush"; bg = "AccentDimBrush"; showBar = true;
                break;
            case UpdateState.Installing:
                title = $"Installing {tag}\u2026";
                sub = "APMultiTracker will close and reopen in a moment.";
                color = "AccentBrush"; bg = "AccentDimBrush"; icon = (Geometry)FindResource("IconRefresh");
                break;
            case UpdateState.Checking:
                title = "Checking for updates\u2026";
                sub = $"You're on {cur}.";
                color = "AccentBrush"; bg = "AccentDimBrush"; icon = (Geometry)FindResource("IconRefresh");
                break;
            case UpdateState.Error:
                title = "Update failed";
                sub = u.Error ?? "Something went wrong.";
                color = "DangerBrush"; bg = "WarnBgBrush"; showCheck = true;
                break;
            default:
                title = $"You're up to date \u00b7 {cur}";
                sub = "Your multiworlds and settings are kept across updates.";
                color = "GoodBrush"; bg = "CardBrush"; icon = (Geometry)FindResource("IconCheck"); showCheck = true;
                break;
        }

        UpdTitle.Text = title;
        UpdSub.Text = sub;
        UpdIcon.Data = icon;
        UpdIcon.Stroke = (Brush)FindResource(color);
        UpdIconBox.Background = u.State is UpdateState.UpToDate or UpdateState.Idle
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x29, 0x1A)) : (Brush)FindResource(bg);
        UpdStatusRow.BorderBrush = (Brush)FindResource(color);
        UpdNowBtn.Visibility = showNow ? Visibility.Visible : Visibility.Collapsed;
        UpdNotesBtn.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
        UpdCheckBtn.Visibility = showCheck ? Visibility.Visible : Visibility.Collapsed;
        UpdBarTrack.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        UpdBarScale.ScaleX = Math.Clamp(u.Progress, 0, 1);
        UpdFooter.Text = u.LastChecked is DateTime t
            ? $"Current version {cur} \u00b7 last checked {t:h:mm tt}"
            : $"Current version {cur}";

        _loading = true;
        AutoDlToggle.IsChecked = App.Config.AutoDownloadUpdates;
        AutoInstallToggle.IsChecked = App.Config.AutoDownloadUpdates && App.Config.InstallUpdatesOnLaunch;
        AutoInstallToggle.IsEnabled = App.Config.AutoDownloadUpdates;
        AutoInstallRow.Opacity = App.Config.AutoDownloadUpdates ? 1 : 0.45;
        AutoInstallSub.Text = App.Config.AutoDownloadUpdates
            ? "Installs a downloaded update the next time APMultiTracker starts."
            : "Needs \u201CAutomatically download new versions\u201D turned on.";
        _loading = false;
    }

    private void UpdPrefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Config.AutoDownloadUpdates = AutoDlToggle.IsChecked == true;
        if (App.Config.AutoDownloadUpdates)
            App.Config.InstallUpdatesOnLaunch = AutoInstallToggle.IsChecked == true;
        App.SaveConfig();
        UpdateUpdates();
        if (App.Config.AutoDownloadUpdates && App.Updater.State == UpdateState.Available) _ = App.Updater.CheckAsync();
    }

    private async void UpdNow_Click(object sender, RoutedEventArgs e) => await App.Updater.UpdateNowAsync();
    private async void UpdCheck_Click(object sender, RoutedEventArgs e) => await App.Updater.CheckAsync(manual: true);
    private void UpdNotes_Click(object sender, RoutedEventArgs e) => App.Updater.OpenReleaseNotes();

    // ------------------------------------------------------------ twitch

    private void UpdateAccount()
    {
        bool signedIn = App.Twitch.IsSignedIn;
        SignedOutCard.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInCard.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        if (signedIn)
        {
            var t = App.Twitch.Tokens!;
            ProfileName.Text = string.IsNullOrEmpty(t.DisplayName) ? t.Login : t.DisplayName;
            Art.SetUrl(ProfileArt, string.IsNullOrEmpty(t.ProfileImageUrl) ? null : t.ProfileImageUrl);
            StopDeviceFlow();
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ShowLoginError(null);
        ConnectBtn.IsEnabled = false;
        try
        {
            _device = await App.Twitch.StartDeviceFlowAsync();
            CodeChars.ItemsSource = Array.ConvertAll(_device.UserCode.ToCharArray(), ch => ch.ToString());
            _deviceExpiry = DateTime.UtcNow.AddSeconds(_device.ExpiresIn);
            DevicePanel.Visibility = Visibility.Visible;
            ConnectBtn.Visibility = Visibility.Collapsed;
            UpdateCountdown();
            _countdown.Start();
            OpenUrl(_device.VerificationUri);
            _loginCts = new CancellationTokenSource();
            bool ok = await App.Twitch.CompleteDeviceFlowAsync(_device, _loginCts.Token);
            StopDeviceFlow();
            if (ok) UpdateAccount();
            else ShowLoginError("The code expired. Click Connect to get a new one.");
        }
        catch (OperationCanceledException) { StopDeviceFlow(); }
        catch (Exception ex)
        {
            StopDeviceFlow();
            ShowLoginError("Couldn't connect: " + ex.Message);
        }
    }

    private void StopDeviceFlow()
    {
        _countdown.Stop();
        _loginCts?.Cancel();
        _loginCts = null;
        DevicePanel.Visibility = Visibility.Collapsed;
        ConnectBtn.Visibility = Visibility.Visible;
        ConnectBtn.IsEnabled = true;
    }

    private void UpdateCountdown()
    {
        var left = _deviceExpiry - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        WaitText.Text = $"Waiting for approval \u00b7 {(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void OpenActivate_Click(object sender, RoutedEventArgs e) { if (_device != null) OpenUrl(_device.VerificationUri); }
    private void CancelLogin_Click(object sender, RoutedEventArgs e) => StopDeviceFlow();

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await App.Twitch.SignOutAsync();
        UpdateAccount();
    }

    private void ShowLoginError(string? text)
    {
        LoginError.Text = text ?? "";
        LoginError.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
