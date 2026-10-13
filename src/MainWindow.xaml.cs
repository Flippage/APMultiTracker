using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace APMultiTracker;

public partial class MainWindow : Window
{
    private readonly TrackerPage _tracker = new();
    private readonly MultiworldsPage _multiworlds = new();
    private readonly FeedPage _feed = new();
    private int _unread;
    private readonly SettingsPage _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        // Wide enough for four full game columns, but never bigger than the screen.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1500, work.Width - 40);
        Height = Math.Min(940, work.Height - 40);
        RestoreWindowPlacement();
        LocationChanged += (_, _) => RememberWindowPlacement();
        SizeChanged += (_, _) => RememberWindowPlacement();
        StateChanged += (_, _) => RememberWindowPlacement();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;
        Topmost = App.Config.AlwaysOnTop;

        App.Engine.Changed += () => Dispatcher.InvokeAsync(UpdateEngineChip);
        App.Updater.Changed += () => Dispatcher.InvokeAsync(UpdateUpdateBadge);
        App.Updater.Notice += downloaded => Dispatcher.InvokeAsync(() => QueueUpdateToast(downloaded));
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) ShowQueuedToast(); };
        App.Tracker.Changed += slot => { if (slot == null || App.Tracker.Focused == null || slot == App.Tracker.Focused) UpdateFocusCard(); };
        BoxArt.Changed += _ => { if (App.Tracker.Focused == null) UpdateFocusCard(); };
        App.Tracker.FocusChanged += UpdateFocusCard;
        App.Tracker.FeedAdded += (world, m) =>
        {
            if (world != App.Tracker.ActiveLive || Host.Content == _feed || m.Kind is "Join" or "Part") return;
            if (Host.Content == _tracker && App.Config.ShowFeedInTracker) return;   // already visible, docked
            _unread++;
            UpdateFeedBadge();
        };
        App.Tracker.WorldsChanged += () => { _unread = 0; UpdateFeedBadge(); UpdateFocusCard(); };
        SideDock.SizeChanged += (_, _) => UpdateArtVisibility();
        FocusCard.SizeChanged += (_, _) => UpdateArtVisibility();
        PreviewKeyDown += OnPreviewKeyDown;

        UpdateEngineChip();
        UpdateFocusCard();
        UpdateUpdateBadge();
        // Open on the page you were last using.
        (App.Config.LastPage switch
        {
            "Feed" => NavFeed,
            "Multiworlds" => NavMultiworlds,
            "Settings" => NavSettings,
            _ => NavTracker,
        }).IsChecked = true;
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = sender == NavMultiworlds ? _multiworlds : sender == NavSettings ? _settings
                     : sender == NavFeed ? _feed : _tracker;
        if (Host.Content == _feed) { _unread = 0; UpdateFeedBadge(); }
        if (!App.DemoMode)
        {
            App.Config.LastPage = Host.Content == _feed ? "Feed" : Host.Content == _multiworlds ? "Multiworlds"
                                : Host.Content == _settings ? "Settings" : "Tracker";
            App.SaveConfig();
        }
    }

    public TrackerPage TrackerView => _tracker;

    public void GoToTracker() { NavTracker.IsChecked = true; Host.Content = _tracker; }
    public void GoToSettings() => NavSettings.IsChecked = true;

    private void UpdateFeedBadge()
    {
        FeedBadge.Visibility = _unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedBadgeText.Text = _unread > 99 ? "99+" : _unread.ToString();
    }

    public void OpenMultiworldEditor(Multiworld? world)
    {
        NavMultiworlds.IsChecked = true;
        Host.Content = new EditMultiworldPage(world);
    }

    public void OpenPatchNotes()
    {
        NavSettings.IsChecked = true;
        Host.Content = new PatchNotesPage();
    }

    public void ClosePatchNotes() => Host.Content = _settings;

    public void CloseMultiworldEditor()
    {
        _multiworlds.Refresh();
        Host.Content = _multiworlds;
    }

    /// <summary>Ctrl+1…9 focus a game, Ctrl+0 / Esc-free overview, Ctrl+Tab cycles.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ArtLayer.Visibility == Visibility.Visible || ConfirmLayer.Visibility == Visibility.Visible) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Tab)
            {
                App.Tracker.CycleFocus(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                GoToTracker();
                e.Handled = true;
            }
            else if (e.Key is >= Key.D0 and <= Key.D9 || e.Key is >= Key.NumPad0 and <= Key.NumPad9)
            {
                int n = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                if (n == 0) App.Tracker.SetFocus(null);
                else if (n <= App.Tracker.Slots.Count) App.Tracker.SetFocus(App.Tracker.Slots[n - 1]);
                else return;
                GoToTracker();
                e.Handled = true;
            }
        }
    }

    // ------------------------------------------------------------------ engine chip

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
    private static readonly Brush GoodDot = Frozen(0x4A, 0xDE, 0x80);
    private static readonly Brush WarnDot = Frozen(0xFB, 0xBF, 0x24);
    private static readonly Brush BadDot = Frozen(0xF8, 0x71, 0x71);
    private static readonly Brush OffDot = Frozen(0x6B, 0x6B, 0x6B);

    private void UpdateEngineChip()
    {
        var e = App.Engine;
        (string title, string sub, Brush dot) = e.State switch
        {
            EngineState.Running => ("Engine running", $"UT {e.Client.UtVersion} · AP {e.Client.ApVersion}", GoodDot),
            EngineState.Starting => ("Engine starting…", "Loading Archipelago", WarnDot),
            EngineState.NoArchipelago => ("Archipelago not found", "Open Settings", BadDot),
            EngineState.NoTracker => ("Universal Tracker missing", "Open Settings", BadDot),
            EngineState.NotInstalled => ("Engine not installed", "Open Settings", WarnDot),
            EngineState.Error => ("Engine error", "Open Settings", BadDot),
            _ => ("Engine stopped", "Click to open Settings", OffDot),
        };
        if (App.DemoMode && e.State != EngineState.Running) (title, sub, dot) = ("Engine running", "UT v0.3.4 · AP 0.6.7", GoodDot);
        EngineTitle.Text = title;
        EngineSub.Text = sub;
        EngineDot.Fill = EngineGlow.Fill = dot;
        bool needsSetup = !App.DemoMode && e.State is EngineState.NoArchipelago or EngineState.NoTracker or EngineState.NotInstalled or EngineState.Error;
        SetupBadge.Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
        UpdateUpdateBadge();
        EngineChip.ToolTip = string.IsNullOrEmpty(e.Detail) ? "Engine settings" : e.Detail;
    }

    private void EngineChip_Click(object sender, MouseButtonEventArgs e) => GoToSettings();

    // ------------------------------------------------------------------ focus card

    private void UpdateFocusCard()
    {
        var store = App.Tracker;
        var f = store.Focused;
        if (f != null)
        {
            FocusLabel.Text = $"FOCUSED · {store.Slots.IndexOf(f) + 1}/{store.Slots.Count}";
            FocusGame.Text = f.Title;
            FocusSlot.Text = f.SlotName;
            Art.SetUrl(FocusArt, f.ArtUrl);
            FocusCollage.Visibility = Visibility.Collapsed;
            FocusArtIdle.Visibility = string.IsNullOrEmpty(f.ArtUrl) ? Visibility.Visible : Visibility.Collapsed;
            FocusCount.Text = f.Goal ? "GOALED" : f.InLogicText;
            FocusCount.Foreground = f.InLogicBrush;
            FocusCountLabel.Text = f.IsBK || f.Goal ? "" : " in logic";
            FocusBarScale.ScaleX = f.Progress;
            FocusBar.Background = f.Goal ? Brushes2.InLogic : Brushes2.Accent;
            FocusChecked.Text = f.HasData ? $"{f.Checked} / {f.Total} checked · {f.PercentText}" : f.StatusDetail;
            GoTag.Visibility = f.HasGoMode && !f.Goal ? Visibility.Visible : Visibility.Collapsed;
            GoText.Text = f.IsGoGlitched ? "GO (GLITCHED)" : "GO MODE";
            GoTag.Background = (Brush)FindResource(f.IsGoGlitched ? "WarnBgBrush" : "InLogicDimBrush");
            GoText.Foreground = (Brush)FindResource(f.IsGoGlitched ? "GlitchedBrush" : "InLogicBrush");
            f.PropertyChanged -= Focused_PropertyChanged;
            f.PropertyChanged += Focused_PropertyChanged;
        }
        else
        {
            var mw = store.Active;
            FocusLabel.Text = "ALL GAMES";
            FocusGame.Text = mw == null ? "No multiworld" : mw.Name;
            FocusSlot.Text = mw == null ? "Set one up on Multiworlds" : $"{store.Slots.Count} games";
            Art.SetUrl(FocusArt, null);
            FocusArt.Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            FocusArtIdle.Visibility = BuildCollage(store.Slots) ? Visibility.Collapsed : Visibility.Visible;
            bool any = store.Slots.Any(s => s.HasData);
            FocusCount.Text = any ? store.Slots.Sum(s => s.InLogic).ToString() : "–";
            FocusCount.Foreground = Brushes2.InLogic;
            FocusCountLabel.Text = " in logic";
            int chk = store.Slots.Sum(s => s.Checked), tot = store.Slots.Sum(s => s.Total);
            FocusBarScale.ScaleX = tot > 0 ? (double)chk / tot : 0;
            FocusBar.Background = Brushes2.Accent;
            FocusChecked.Text = tot > 0 ? $"{chk} / {tot} checked · {(double)chk / tot * 100:0}%" : "";
            GoTag.Visibility = Visibility.Collapsed;
        }
        UpdateArtVisibility();
    }

    /// <summary>All games: a grid of every game's box art (2×2 up to 4 games, 3×3 up to 9…). False if there's no art yet.</summary>
    private bool BuildCollage(System.Collections.Generic.IList<SlotView> slots)
    {
        var arts = slots.Where(s => !string.IsNullOrEmpty(s.ArtUrl)).Select(s => s.ArtUrl!).ToList();
        FocusCollage.Children.Clear();
        if (arts.Count == 0) { FocusCollage.Visibility = Visibility.Collapsed; return false; }
        int cols = arts.Count == 1 ? 1 : (int)Math.Ceiling(Math.Sqrt(arts.Count));
        int rows = (int)Math.Ceiling(arts.Count / (double)cols);
        FocusCollage.Columns = cols;
        FocusCollage.Rows = rows;
        foreach (var url in arts)
        {
            var cell = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(3), Margin = new Thickness(1) };
            Art.SetUrl(cell, url);
            FocusCollage.Children.Add(cell);
        }
        FocusCollage.Visibility = Visibility.Visible;
        return true;
    }

    private void Focused_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == App.Tracker.Focused && e.PropertyName is "" or nameof(SlotView.ArtUrl) or nameof(SlotView.Status) or nameof(SlotView.Message))
            UpdateFocusCard();
    }

    private void FocusCard_Click(object sender, MouseButtonEventArgs e) => GoToTracker();

    /// <summary>Shrink (then hide) the focus card's art so the sidebar never overlaps the menu on short windows.</summary>
    private void UpdateArtVisibility()
    {
        if (_artCheckQueued) return;
        _artCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _artCheckQueued = false;
            if (SideDock.ActualHeight <= 0) return;
            const double fullH = 112, minH = 56, gap = 10;
            double menu = 0;
            foreach (UIElement child in NavMenu.Children) menu += child.DesiredSize.Height;
            double used = SideHeader.DesiredSize.Height + EngineChip.DesiredSize.Height + FocusCard.DesiredSize.Height + menu;
            double artNow = FocusArtBox.Visibility == Visibility.Visible ? FocusArt.Height + gap : 0;
            double room = SideDock.ActualHeight - (used - artNow) - 8;
            double h = Math.Floor(Math.Min(fullH, room - gap));
            if (h >= minH)
            {
                if (Math.Abs(FocusArt.Height - h) >= 1) { FocusArt.Height = h; FocusArt.Width = Math.Round(h * 0.75); }
                FocusArtBox.Visibility = Visibility.Visible;
            }
            else FocusArtBox.Visibility = Visibility.Collapsed;
        }));
    }
    private bool _artCheckQueued;

    // ------------------------------------------------------------------ box art picker

    private string? _artGame;

    public void OpenArtPicker(string game)
    {
        if (string.IsNullOrEmpty(game)) return;
        _artGame = game;
        ArtTitle.Text = "Box art for " + game;
        ArtSub.Text = App.Twitch.IsSignedIn
            ? "Search Twitch and pick the right game. Your choice is remembered for this game."
            : "Connect Twitch in Settings to search for box art. Until then, art is matched by name automatically.";
        var known = BoxArt.Known(game);
        ArtPicker.SetSelected(known == null ? null : new CategoryRef { Name = game, BoxArtUrl = known });
        ArtPicker.SelectionChanged -= ArtPicked;
        ArtPicker.SelectionChanged += ArtPicked;
        ArtLayer.Visibility = Visibility.Visible;
    }

    private void ArtPicked(CategoryRef? c)
    {
        if (_artGame != null && c != null) BoxArt.SetOverride(_artGame, c.BoxArtUrl);
    }

    private void ArtReset_Click(object sender, RoutedEventArgs e)
    {
        if (_artGame != null) BoxArt.SetOverride(_artGame, null);
        ArtLayer.Visibility = Visibility.Collapsed;
    }

    private void ArtClose_Click(object sender, RoutedEventArgs e) => ArtLayer.Visibility = Visibility.Collapsed;
    private void ArtBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => ArtLayer.Visibility = Visibility.Collapsed;

    // ------------------------------------------------------------------ updates

    private void UpdateUpdateBadge()
    {
        bool setup = SetupBadge.Visibility == Visibility.Visible;
        UpdateBadge.Visibility = App.Updater.HasUpdate && !setup ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool? _queuedToast;          // null = none; true = downloaded; false = available
    private AnimationClock? _toastClock;

    private void QueueUpdateToast(bool downloaded)
    {
        _queuedToast = downloaded;
        if (IsVisible && WindowState != WindowState.Minimized) ShowQueuedToast();
    }

    private void ShowQueuedToast()
    {
        if (_queuedToast is not bool downloaded) return;
        _queuedToast = null;
        string tag = App.Updater.LatestTag ?? "A new version";
        ToastText.Text = downloaded
            ? $"APMultiTracker {tag} is downloaded and ready to install."
            : $"APMultiTracker {tag} is available to download and install.";
        UpdateToast.Visibility = Visibility.Visible;
        UpdateToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        var drain = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(10));
        _toastClock = drain.CreateClock();
        _toastClock.Completed += (_, _) => HideToast();
        ToastBarScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _toastClock);
    }

    private void HideToast()
    {
        if (UpdateToast.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            UpdateToast.Visibility = Visibility.Collapsed;
            UpdateToast.BeginAnimation(OpacityProperty, null);
        };
        UpdateToast.BeginAnimation(OpacityProperty, fade);
        _toastClock?.Controller?.Stop();
        _toastClock = null;
    }

    private void Toast_MouseEnter(object sender, MouseEventArgs e) => _toastClock?.Controller?.Pause();
    private void Toast_MouseLeave(object sender, MouseEventArgs e) => _toastClock?.Controller?.Resume();
    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();
    private void ToastNotes_Click(object sender, RoutedEventArgs e) => App.Updater.OpenReleaseNotes();

    private void ToastUpdate_Click(object sender, RoutedEventArgs e)
    {
        HideToast();
        GoToSettings();                     // show progress there
        _ = App.Updater.UpdateNowAsync();
    }

    // ------------------------------------------------------------------ in-app confirmation

    private TaskCompletionSource<bool>? _confirm;
    private IInputElement? _confirmReturnFocus;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        _confirm?.TrySetResult(false);
        _confirm = new TaskCompletionSource<bool>();
        ConfirmTitle.Text = title;
        ConfirmMessage.Text = message;
        ConfirmYes.Content = confirmText;
        _confirmReturnFocus = Keyboard.FocusedElement;
        ConfirmLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(140);
        ConfirmLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        ConfirmScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        ConfirmScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, dur) { EasingFunction = ease });
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ConfirmCard.Focus()));
        return _confirm.Task;
    }

    private void CloseConfirm(bool result)
    {
        if (_confirm == null) return;
        var tcs = _confirm;
        _confirm = null;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (_confirm == null) ConfirmLayer.Visibility = Visibility.Collapsed; };
        ConfirmLayer.BeginAnimation(OpacityProperty, fade);
        (_confirmReturnFocus as UIElement)?.Focus();
        tcs.TrySetResult(result);
    }

    private void ConfirmYes_Click(object sender, RoutedEventArgs e) => CloseConfirm(true);
    private void ConfirmNo_Click(object sender, RoutedEventArgs e) => CloseConfirm(false);
    private void ConfirmBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseConfirm(false);

    // ------------------------------------------------------------------ self-test

    public async Task SelfTestAsync()
    {
        // Opening the Feed with a game focused: that game's slot is the one you chat as.
        GoToTracker();
        App.Tracker.SetFocus(App.Tracker.Slots[1]);
        UpdateLayout();
        NavFeed.IsChecked = true;
        UpdateLayout();
        if (_feed.AsSlotName != App.Tracker.Slots[1].SlotName)
            throw new InvalidOperationException($"Feed chats as '{_feed.AsSlotName}', expected the focused '{App.Tracker.Slots[1].SlotName}'.");
        _feed.SelfTest();
        App.Tracker.SetFocus(null);

        // The feed docked in the Tracker, on the right and along the bottom.
        GoToTracker();
        foreach (var dock in new[] { "Right", "Bottom" })
        {
            App.Config.ShowFeedInTracker = true;
            App.Config.FeedDock = dock;
            _tracker.ApplyFeedDock();
            UpdateLayout();
            await Task.Delay(100);
            _tracker.AssertFeedDocked();
        }
        App.Config.ShowFeedInTracker = false;
        App.Config.FeedDock = "Right";
        _tracker.ApplyFeedDock();
        UpdateLayout();
        foreach (var nav in new[] { NavMultiworlds, NavSettings, NavTracker })
        {
            nav.IsChecked = true;
            UpdateLayout();
        }
        OpenMultiworldEditor(App.Config.Multiworlds.FirstOrDefault());
        UpdateLayout();
        CloseMultiworldEditor();
        OpenMultiworldEditor(null);
        UpdateLayout();
        CloseMultiworldEditor();
        GoToTracker();
        App.Tracker.SetFocus(null);
        UpdateLayout();
        for (int i = 0; i < App.Tracker.Slots.Count + 1; i++)
        {
            App.Tracker.CycleFocus(1);
            UpdateLayout();
            await Task.Delay(50);
        }
        _tracker.SelfTest();
        QueueUpdateToast(true);
        UpdateLayout();
        HideToast();
        OpenArtPicker("Hollow Knight");
        UpdateLayout();
        ArtLayer.Visibility = Visibility.Collapsed;
        var confirm = ConfirmAsync("Remove?", "Self-test", "Remove");
        await Task.Delay(200);
        CloseConfirm(true);
        if (!await confirm) throw new InvalidOperationException("Confirm dialog did not return the confirm result.");
    }

    // ------------------------------------------------------------------ window

    // ------------------------------------------------------------------ window placement

    /// <summary>Put the window back where it was, if that spot is still on a connected screen.</summary>
    private void RestoreWindowPlacement()
    {
        var c = App.Config;
        if (App.DemoMode || c.WindowWidth < MinWidth || c.WindowHeight < MinHeight) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(c.WindowLeft, c.WindowTop, c.WindowWidth, c.WindowHeight);
        var visible = Rect.Intersect(screen, saved);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 120) return;   // that monitor is gone
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = c.WindowLeft; Top = c.WindowTop; Width = c.WindowWidth; Height = c.WindowHeight;
        if (c.WindowMaximized) SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
    }

    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _placementHooked;

    /// <summary>Keeps the window's normal position/size and maximised state in the config (saved a second later).</summary>
    private void RememberWindowPlacement()
    {
        if (App.DemoMode || !IsLoaded || WindowState == WindowState.Minimized) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (b.IsEmpty || b.Width < 100) return;
        var c = App.Config;
        c.WindowLeft = b.Left; c.WindowTop = b.Top; c.WindowWidth = b.Width; c.WindowHeight = b.Height;
        c.WindowMaximized = WindowState == WindowState.Maximized;
        if (!_placementHooked)
        {
            _placementHooked = true;
            _placementSave.Tick += (_, _) => { _placementSave.Stop(); App.SaveConfig(); };
        }
        _placementSave.Stop();
        _placementSave.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        RememberWindowPlacement();
        if (App.IsExiting) return;
        e.Cancel = true;
        Dispatcher.InvokeAsync(App.Current.ExitApp);
    }
}
