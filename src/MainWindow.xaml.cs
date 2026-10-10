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
    private readonly SettingsPage _settings = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleTitleBar(new WindowInteropHelper(this).Handle);
        Closing += OnClosing;
        Topmost = App.Config.AlwaysOnTop;

        App.Engine.Changed += () => Dispatcher.InvokeAsync(UpdateEngineChip);
        App.Tracker.Changed += slot => { if (slot == null || App.Tracker.Focused == null || slot == App.Tracker.Focused) UpdateFocusCard(); };
        App.Tracker.FocusChanged += UpdateFocusCard;
        SideDock.SizeChanged += (_, _) => UpdateArtVisibility();
        FocusCard.SizeChanged += (_, _) => UpdateArtVisibility();
        PreviewKeyDown += OnPreviewKeyDown;

        UpdateEngineChip();
        UpdateFocusCard();
        NavTracker.IsChecked = true;
    }

    // ------------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host == null) return;
        Host.Content = sender == NavMultiworlds ? _multiworlds : sender == NavSettings ? _settings : _tracker;
    }

    public TrackerPage TrackerView => _tracker;

    public void GoToTracker() { NavTracker.IsChecked = true; Host.Content = _tracker; }
    public void GoToSettings() => NavSettings.IsChecked = true;

    public void OpenMultiworldEditor(Multiworld? world)
    {
        NavMultiworlds.IsChecked = true;
        Host.Content = new EditMultiworldPage(world);
    }

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
            FocusArtIdle.Visibility = string.IsNullOrEmpty(f.ArtUrl) ? Visibility.Visible : Visibility.Collapsed;
            FocusCount.Text = f.HasData ? f.InLogic.ToString() : "–";
            FocusCountLabel.Text = " in logic";
            FocusBarScale.ScaleX = f.Progress;
            FocusChecked.Text = f.HasData ? $"{f.Checked} / {f.Total} checked · {f.PercentText}" : f.StatusDetail;
            GoTag.Visibility = f.GoMode == "no" ? Visibility.Collapsed : Visibility.Visible;
            GoText.Text = f.GoModeText;
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
            FocusArtIdle.Visibility = Visibility.Visible;
            bool any = store.Slots.Any(s => s.HasData);
            FocusCount.Text = any ? store.Slots.Sum(s => s.InLogic).ToString() : "–";
            FocusCountLabel.Text = " in logic";
            int chk = store.Slots.Sum(s => s.Checked), tot = store.Slots.Sum(s => s.Total);
            FocusBarScale.ScaleX = tot > 0 ? (double)chk / tot : 0;
            FocusChecked.Text = tot > 0 ? $"{chk} / {tot} checked · {(double)chk / tot * 100:0}%" : "";
            GoTag.Visibility = Visibility.Collapsed;
        }
        UpdateArtVisibility();
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
        OpenArtPicker("Hollow Knight");
        UpdateLayout();
        ArtLayer.Visibility = Visibility.Collapsed;
        var confirm = ConfirmAsync("Remove?", "Self-test", "Remove");
        await Task.Delay(200);
        CloseConfirm(true);
        if (!await confirm) throw new InvalidOperationException("Confirm dialog did not return the confirm result.");
    }

    // ------------------------------------------------------------------ window

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting) return;
        e.Cancel = true;
        Dispatcher.InvokeAsync(App.Current.ExitApp);
    }
}
