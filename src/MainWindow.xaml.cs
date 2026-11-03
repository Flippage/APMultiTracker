using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        // The game picker closes on a click elsewhere, Esc, or when the window moves, resizes or loses focus
        PreviewMouseDown += Window_PreviewMouseDownForPicker;
        Deactivated += (_, _) => GamePicker.IsOpen = false;
        LocationChanged += (_, _) => GamePicker.IsOpen = false;
        SizeChanged += (_, _) => GamePicker.IsOpen = false;

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
        if (e.Key == Key.Escape && GamePicker.IsOpen) { GamePicker.IsOpen = false; e.Handled = true; return; }
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
                else if (n <= App.Tracker.Slots.Count) App.Tracker.SetFocus(App.Tracker.DisplayOrder[n - 1]);
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
            FocusCount.Text = f.Goal ? "GOALED" : f.Locked ? "LOCKED" : f.IsSlotLock ? f.SlotLockSummary.Replace(" UNLOCKED", "") : f.InLogicText;
            FocusCount.Foreground = f.Locked ? (Brush)FindResource("Text2Brush") : f.InLogicBrush;
            FocusCountLabel.Text = f.IsSlotLock ? " unlocked" : f.IsBK || f.Goal || f.Locked ? "" : " in logic";
            FocusBarScale.ScaleX = f.Progress;
            FocusBar.Background = f.Goal ? Brushes2.InLogic : Brushes2.Accent;
            FocusChecked.Text = f.HasData ? $"{f.Checked} / {f.Total} checked · {f.PercentText}" : f.StatusDetail;
            GoTag.Visibility = f.HasGoMode && !f.Goal && !f.Locked && !f.IsSlotLock ? Visibility.Visible : Visibility.Collapsed;
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
            FocusCount.Text = any ? store.Slots.Sum(s => s.ToDo).ToString() : "–";
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

    /// <summary>The focus card opens the game picker (works from any page, e.g. the Feed).</summary>
    private void FocusCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (App.Tracker.Slots.Count == 0) { GoToTracker(); return; }
        // The card toggles the picker
        if (GamePicker.IsOpen) { GamePicker.IsOpen = false; return; }
        BuildGamePicker();
        GamePickerScroll.MaxHeight = Math.Max(160, ActualHeight - 140);   // never taller than the window
        GamePicker.CustomPopupPlacementCallback = PlaceGamePicker;
        GamePicker.IsOpen = true;
    }

    /// <summary>Closes the picker on a click anywhere else in the window (it stays open itself, so the card can toggle it).</summary>
    private void Window_PreviewMouseDownForPicker(object sender, MouseButtonEventArgs e)
    {
        if (!GamePicker.IsOpen || e.OriginalSource is not DependencyObject d) return;
        if (IsWithin(d, FocusCard) || (GamePicker.Child != null && IsWithin(d, GamePicker.Child))) return;
        GamePicker.IsOpen = false;
    }

    private static bool IsWithin(DependencyObject d, DependencyObject root)
    {
        for (var cur = d; cur != null; cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur))
            if (cur == root) return true;
        return false;
    }

    /// <summary>Right of the focus card, tops lined up; moved up only as far as needed to stay inside the window.</summary>
    private CustomPopupPlacement[] PlaceGamePicker(Size popup, Size target, Point offset)
    {
        var dpi = VisualTreeHelper.GetDpi(this);   // the sizes here are in device pixels
        double sy = dpi.DpiScaleY;
        double cardTop = FocusCard.TranslatePoint(new Point(0, 0), this).Y * sy;
        double windowBottom = ((FrameworkElement)Content).ActualHeight * sy;
        double visible = popup.Height - 16 * sy;   // the panel's bottom margin is only room for its shadow
        double y = Math.Min(0, windowBottom - 12 * sy - cardTop - visible);   // up if it would run off the bottom
        y = Math.Max(y, -cardTop + 12 * sy);                                   // but never above the window's top
        return new[] { new CustomPopupPlacement(new Point(target.Width + 10 * dpi.DpiScaleX, y), PopupPrimaryAxis.Horizontal) };
    }

    private void FocusPrev_Click(object sender, RoutedEventArgs e) { App.Tracker.CycleFocus(-1); e.Handled = true; }
    private void FocusNext_Click(object sender, RoutedEventArgs e) { App.Tracker.CycleFocus(1); e.Handled = true; }

    /// <summary>All games plus each game's box art, name and number in logic, in card order; the focused one outlined.</summary>
    private void BuildGamePicker()
    {
        GamePickerTiles.Children.Clear();
        GamePickerTiles.Children.Add(PickerTile(null));
        foreach (var s in App.Tracker.DisplayOrder) GamePickerTiles.Children.Add(PickerTile(s));
    }

    private FrameworkElement PickerTile(SlotView? s)
    {
        bool current = App.Tracker.Focused == s;
        var art = new Border { Width = 84, Height = 112, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)) };
        if (s != null && !string.IsNullOrEmpty(s.ArtUrl)) Art.SetUrl(art, s.ArtUrl);
        else
            art.Child = new Viewbox
            {
                Width = 30, Height = 30, Opacity = 0.35,
                Child = new System.Windows.Shapes.Path { Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource("IconGrid"), Stroke = Brushes2.Text },
            };
        string count = s == null ? App.Tracker.Slots.Sum(x => x.ToDo).ToString() : s.Locked ? "LOCKED" : s.Goal ? "GOALED" : s.InLogicText;
        var label = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        label.Children.Add(new TextBlock
        {
            Text = s?.Title ?? "All games", FontSize = 12, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes2.Text,
        });
        label.Children.Add(new TextBlock
        {
            Text = count, FontSize = 12, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
            Foreground = s == null ? Brushes2.InLogic : s.Locked || s.Goal ? Brushes2.Sub : s.InLogicBrush,
        });
        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(label);
        var tile = new Border
        {
            Width = 104, Padding = new Thickness(8), Margin = new Thickness(3), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
            Background = current ? (Brush)FindResource("AccentDimBrush") : Brushes.Transparent,
            BorderBrush = current ? (Brush)FindResource("AccentLineBrush") : Brushes.Transparent, BorderThickness = new Thickness(1.5),
            Child = stack, ToolTip = s == null ? "All games" : $"{s.Title} · {s.SlotName}",
        };
        tile.MouseEnter += (_, _) => { if (!current) tile.Background = (Brush)FindResource("HoverBrush"); };
        tile.MouseLeave += (_, _) => { if (!current) tile.Background = Brushes.Transparent; };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            GamePicker.IsOpen = false;
            App.Tracker.SetFocus(s);
            e.Handled = true;
        };
        return tile;
    }

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

    // ------------------------------------------------------------------ GO MODE banner

    private SlotView? _goBannerSlot;
    private readonly DispatcherTimer _goBannerTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private bool _goBannerHooked;

    public void ShowGoModeBanner(SlotView slot)
    {
        _goBannerSlot = slot;
        GoBannerText.Text = $"{slot.SlotName} ({slot.Title}) has entered GO MODE!";
        Art.SetUrl(GoBannerArt, slot.ArtUrl);
        GoBanner.Visibility = Visibility.Visible;
        GoBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        if (!_goBannerHooked) { _goBannerHooked = true; _goBannerTimer.Tick += (_, _) => HideGoBanner(); }
        _goBannerTimer.Stop();
        _goBannerTimer.Start();
    }

    private void HideGoBanner()
    {
        _goBannerTimer.Stop();
        GoBanner.BeginAnimation(OpacityProperty, null);
        GoBanner.Visibility = Visibility.Collapsed;
    }

    private void GoBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Button>(d) != null) return;   // the × button
        var slot = _goBannerSlot;
        HideGoBanner();
        if (slot == null) return;
        var live = App.Tracker.Live.FirstOrDefault(l => l.Slots.Contains(slot));
        if (live != null && App.Tracker.Active != live.World) App.Tracker.Activate(live.World);
        App.Tracker.SetFocus(slot);
        GoToTracker();
    }

    private void GoBannerClose_Click(object sender, RoutedEventArgs e) => HideGoBanner();

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
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
        // From the Feed: the focus card's arrows step through the games, and its picker lists them all
        var before = App.Tracker.Focused;
        FocusNext_Click(FocusNext, new RoutedEventArgs(Button.ClickEvent));
        if (App.Tracker.Focused == before) throw new InvalidOperationException("The focus card's next arrow didn't change the focused game.");
        FocusPrev_Click(FocusPrev, new RoutedEventArgs(Button.ClickEvent));
        if (App.Tracker.Focused != before) throw new InvalidOperationException("The focus card's previous arrow didn't go back.");
        // A click on the focus card opens the picker, a second one closes it
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
        FocusCard_Click(FocusCard, click);
        UpdateLayout();
        if (!GamePicker.IsOpen) throw new InvalidOperationException("Clicking the focus card didn't open the game picker.");
        if (GamePickerTiles.Children.Count != App.Tracker.Slots.Count + 1)
            throw new InvalidOperationException($"The game picker shows {GamePickerTiles.Children.Count} tiles for {App.Tracker.Slots.Count} games.");
        FocusCard_Click(FocusCard, click);
        if (GamePicker.IsOpen) throw new InvalidOperationException("Clicking the focus card again didn't close the game picker.");
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
