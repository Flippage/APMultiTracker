using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Security;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace APMultiTracker;

/// <summary>
/// GO MODE notifications: a Windows toast (or an in-app banner when the app is in front) and a jingle.
/// Toasts from an unpackaged app can't carry their own sound, so the toast is silent and the app plays the jingle itself
/// (unless Windows is holding notifications back, e.g. Focus Assist / do not disturb).
/// </summary>
public static class Notifier
{
    private static MediaPlayer? _player;

    /// <summary>Our notification identity: fixed, so Windows lists one "APMultiTracker" whichever exe is running.</summary>
    public const string Aumid = "Flippage.APMultiTracker";
    private static bool _registered;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Call at startup, before any window is shown (the taskbar and notifications then agree on who we are).</summary>
    public static void Init()
    {
        if (App.DemoMode) return;
        // Register the name first: Windows reads it when the identity is first used, and otherwise shows the raw ID.
        try { EnsureRegistered(); } catch (Exception ex) { Log.Warn("notify", $"Couldn't register for notifications: {ex.Message}"); }
        try { SetCurrentProcessExplicitAppUserModelID(Aumid); } catch { }
    }

    /// <summary>Name and icon for Windows' notification settings (HKCU, like any desktop app that sends notifications).</summary>
    private static void EnsureRegistered()
    {
        if (_registered) return;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
        {
            key.SetValue("DisplayName", "APMultiTracker");
            if (AppIconPng() is string icon) key.SetValue("IconUri", icon);
            key.SetValue("IconBackgroundColor", "FF000000");
        }
        _registered = true;
    }

    /// <summary>The app icon as a PNG file (Windows needs a file for the notification settings icon).</summary>
    private static string? AppIconPng()
    {
        try
        {
            string file = Path.Combine(ConfigStore.Dir, "appicon.png");
            if (File.Exists(file)) return file;
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res == null) return null;
            using var stream = res.Stream;
            var frame = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                            .Frames.OrderByDescending(f => f.PixelWidth).First();
            var png = new PngBitmapEncoder();
            png.Frames.Add(frame);
            Directory.CreateDirectory(ConfigStore.Dir);
            using var outFile = File.Create(file);
            png.Save(outFile);
            return file;
        }
        catch { return null; }
    }

    private static void ShowSlot(string slotId)
    {
        var live = App.Tracker.Live.FirstOrDefault(l => l.Slots.Any(s => s.Ref.Id == slotId));
        var slot = live?.Slots.First(s => s.Ref.Id == slotId);
        if (live == null || slot == null) { ((App)Application.Current).ShowMain(); return; }
        if (App.Tracker.Active != live.World) App.Tracker.Activate(live.World);
        App.Tracker.SetFocus(slot);
        (Application.Current.MainWindow as MainWindow)?.GoToTracker();
        ((App)Application.Current).ShowMain();
    }

    /// <summary>A game just went into (real) go mode.</summary>
    public static void GoMode(SlotView slot, LiveWorld live)
    {
        if (App.DemoMode || !App.Config.GoModeNotify) return;
        var w = Application.Current?.MainWindow as MainWindow;
        bool inFront = w != null && w.IsVisible && w.IsActive && w.WindowState != WindowState.Minimized;
        if (inFront) w!.ShowGoModeBanner(slot);
        else ShowToast(slot, live);
        bool quiet = WindowsIsQuiet();
        if (App.Config.GoModeSound && !quiet) PlayJingle();
        Log.Info("notify", $"GO MODE: {slot.SlotName} ({slot.Title}) - {(inFront ? "banner" : "Windows notification")}, " +
                           $"sound {(App.Config.GoModeSound ? quiet ? "skipped (Windows is in do not disturb)" : "played" : "off")}");
    }

    /// <summary>Settings → Test: a real notification (or banner) and the jingle, for the first game being tracked.</summary>
    public static void Test()
    {
        var live = App.Tracker.ActiveLive ?? App.Tracker.Live.FirstOrDefault();
        var slot = live?.Slots.FirstOrDefault();
        if (slot != null && live != null)
        {
            // Always a Windows notification here: the app is in front while you press Test.
            ShowToast(slot, live);
        }
        if (App.Config.GoModeSound) PlayJingle();
    }

    private static async void ShowToast(SlotView slot, LiveWorld live)
    {
        try
        {
            EnsureRegistered();
            string? image = !string.IsNullOrEmpty(slot.ArtUrl) ? await ImageCache.GetFileAsync(slot.ArtUrl) : null;
            image ??= AppIconPng();
            string Esc(string text) => SecurityElement.Escape(text) ?? "";
            string imageXml = image == null || !File.Exists(image) ? ""
                : $"<image placement=\"appLogoOverride\" hint-crop=\"none\" src=\"{Esc(new Uri(image).AbsoluteUri)}\"/>";
            string xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                imageXml +
                "<text hint-maxLines=\"1\">GO MODE</text>" +
                $"<text>{Esc($"{slot.SlotName} ({slot.Title}) has entered GO MODE!")}</text>" +
                $"<text placement=\"attribution\">{Esc(live.World.Name)}</text>" +
                "</binding></visual>" +
                "<audio silent=\"true\"/>" +   // the app plays the jingle itself
                "</toast>";
            var doc = new global::Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            var toast = new global::Windows.UI.Notifications.ToastNotification(doc)
            {
                Tag = slot.Ref.Id,
                Group = "gomode",
                ExpirationTime = DateTimeOffset.Now.AddHours(1),
            };
            string slotId = slot.Ref.Id;
            // Clicking it (while the app is running) opens that game.
            toast.Activated += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() => ShowSlot(slotId));
            var notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier(Aumid);
            notifier.Show(toast);
            Log.Info("notify", "Windows notification shown");
            // Windows keeps it up for its own "dismiss after" time (5 s by default); never longer than 6 s.
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(6));
            try { notifier.Hide(toast); } catch { }
        }
        catch (Exception ex) { Log.Warn("notify", $"Couldn't show the GO MODE notification: {ex.Message}"); }
    }

    /// <summary>The chosen sound (or the built-in fanfare). Also used by Settings → Test.</summary>
    public static void PlayJingle()
    {
        try
        {
            string path = App.Config.GoModeSoundPath is { Length: > 0 } custom && File.Exists(custom) ? custom : BuiltInJingle();
            _player ??= new MediaPlayer();
            _player.Open(new Uri(path));
            _player.Volume = Math.Clamp(App.Config.GoModeVolume, 0, 100) / 100.0;
            _player.Play();
        }
        catch (Exception ex) { Log.Warn("notify", $"Couldn't play the GO MODE sound: {ex.Message}"); }
    }

    /// <summary>The fanfare ships inside the app; MediaPlayer needs a file, so it's written out once.</summary>
    private static string BuiltInJingle()
    {
        string path = Path.Combine(ConfigStore.Dir, "sounds", "gomode.wav");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/gomode.wav"))
                      ?? throw new FileNotFoundException("gomode.wav");
            using var src = res.Stream;
            using var dst = File.Create(path);
            src.CopyTo(dst);
        }
        return path;
    }

    // Windows' own "should I bother the user?" answer: no sound while it's holding notifications back.
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    private static bool WindowsIsQuiet()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return false;
            // 4 = presentation mode, 6 = quiet time / Focus Assist / do not disturb.
            // 2 and 3 (full-screen apps and games) still get the jingle: that's exactly when you'd want to hear it.
            return state is 4 or 6;
        }
        catch { return false; }
    }
}
