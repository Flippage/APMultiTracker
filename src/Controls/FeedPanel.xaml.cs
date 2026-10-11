using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>A feed message as shown: highlighted when it involves one of the active multiworld's slots.</summary>
public sealed record FeedRow(FeedMessage Message, bool Mine);

/// <summary>
/// The room's messages as they happen (like Archipelago's Text Client) for the active multiworld, with filters and a
/// chat box. Used full-size on the Feed page and docked beside or under the Tracker.
/// </summary>
public partial class FeedPanel : UserControl
{
    private readonly ObservableCollection<FeedRow> _shown = new();
    private HashSet<int> _mine = new();
    private string _mineKey = "";
    private LiveWorld? _world;
    private ScrollViewer? _scroll;
    private bool _atBottom = true;
    private bool _syncing;
    private string? _asSlotId;

    /// <summary>Raised when the docked panel's dock buttons are used ("Right" / "Bottom") or it's closed (null).</summary>
    public event Action<string?>? DockRequested;
    /// <summary>The visible message count changed (the Feed page shows it in its subtitle).</summary>
    public event Action? CountChanged;

    public FeedPanel()
    {
        InitializeComponent();
        FontSize = 13.5;
        List.ItemsSource = _shown;
        App.Tracker.WorldsChanged += () => { if (IsVisible) Rebuild(); };
        App.Tracker.Changed += _ =>
        {
            if (!IsVisible) return;
            UpdateAs();
            if (MineKey() != _mineKey) Rebuild();   // slot numbers became known: re-highlight
        };
        App.Tracker.FocusChanged += () => { if (IsVisible) FollowFocusedGame(); };
        App.Tracker.FeedAdded += OnFeedAdded;
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            FollowFocusedGame();
            Rebuild();
            ScrollToEnd();
        };
        Loaded += (_, _) => _scroll ??= FindScroll(List);
        LoadFilters();
    }

    /// <summary>Docked layout: own title bar with dock/close buttons, slightly smaller text, compact chat box.</summary>
    public bool Docked
    {
        get => DockHeader.Visibility == Visibility.Visible;
        set
        {
            DockHeader.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            FontSize = value ? 13 : 13.5;
            List.Background = value ? Brushes.Transparent : (Brush)FindResource("CardBrush");
            List.Padding = value ? new Thickness(0) : new Thickness(6);
            List.ItemContainerStyle = (Style)FindResource(value ? "DenseRow" : "PlainRow");
            ChatBar.Padding = value ? new Thickness(0) : new Thickness(10);
            ChatBar.Background = value ? Brushes.Transparent : (Brush)FindResource("CardBrush");
            AsLabel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Highlights the dock button for the current position.</summary>
    public void SetDockPosition(string position)
    {
        var on = (Brush)FindResource("AccentBrush");
        var off = (Brush)FindResource("Text2Brush");
        DockRightBtn.Foreground = position == "Right" ? on : off;
        DockBottomBtn.Foreground = position == "Bottom" ? on : off;

        // Along the bottom height is precious: the filters share the title row, so more messages fit.
        bool bottom = position == "Bottom";
        Grid.SetRow(FilterBar, bottom ? 0 : 1);
        FilterBar.Margin = bottom ? new Thickness(110, -2, 108, 8) : new Thickness(0, 0, 0, 10);
        ChatBar.Margin = bottom ? new Thickness(0, 6, 0, 0) : new Thickness(0, 10, 0, 0);
    }

    public int Count => _world?.Feed.Count ?? 0;

    private void LoadFilters()
    {
        _syncing = true;
        MineToggle.IsChecked = App.Config.FeedOnlyMine;
        (App.Config.FeedFilter switch
        {
            "Items" => ItemsPill,
            "Hints" => HintsPill,
            "Chat" => ChatPill,
            "Activity" => ActivityPill,
            _ => AllPill,
        }).IsChecked = true;
        _syncing = false;
    }

    // ------------------------------------------------------------------ list

    /// <summary>Slot numbers of the active multiworld's games (known once each slot has connected).</summary>
    private static string MineKey() => string.Join(",", (App.Tracker.ActiveLive?.Slots ?? new()).Select(s => s.SlotNumber).Where(n => n > 0).OrderBy(n => n));

    private bool IsMine(FeedMessage m) => _mine.Count > 0 && m.Players.Any(_mine.Contains);

    private bool Passes(FeedMessage m)
    {
        bool kind = App.Config.FeedFilter switch
        {
            "Items" => m.IsItem,
            "Hints" => m.IsHint,
            "Chat" => m.IsChat,
            "Activity" => !m.IsItem && !m.IsHint && !m.IsChat,
            _ => true,
        };
        if (!kind) return false;
        if (!App.Config.FeedOnlyMine || _world == null) return true;
        return _mine.Count == 0 || IsMine(m);
    }

    public void Rebuild()
    {
        LoadFilters();   // the other feed (page or docked) may have changed them
        _world = App.Tracker.ActiveLive;
        _mineKey = MineKey();
        _mine = _mineKey.Length == 0 ? new() : _mineKey.Split(',').Select(int.Parse).ToHashSet();
        _shown.Clear();
        if (_world != null) foreach (var m in _world.Feed) if (Passes(m)) _shown.Add(new FeedRow(m, IsMine(m)));
        UpdateEmpty();
        UpdateAs();
        UpdateCount();
        JumpBtn.Visibility = Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        DockCount.Text = _world == null ? "" : $"· {_world.Feed.Count}";
        CountChanged?.Invoke();
    }

    private void OnFeedAdded(LiveWorld world, FeedMessage m)
    {
        if (!IsVisible || world != _world) return;
        UpdateCount();
        if (!Passes(m)) return;
        while (_shown.Count >= LiveWorld.FeedLimit) _shown.RemoveAt(0);
        _shown.Add(new FeedRow(m, IsMine(m)));
        UpdateEmpty();
        if (_atBottom) ScrollToEnd();
        else JumpBtn.Visibility = Visibility.Visible;
    }

    private void UpdateEmpty()
    {
        bool empty = _shown.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        (EmptyTitle.Text, EmptySub.Text) = _world == null
            ? ("No multiworld active", "Activate one on Multiworlds to see its messages here.")
            : _world.Feed.Count == 0
                ? ("Waiting for messages", "Items sent, hints and chat in this room will show up here as they happen.")
                : ("Nothing matches", "Try another filter.");
    }

    private void ScrollToEnd()
    {
        if (_shown.Count == 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            (_scroll ??= FindScroll(List))?.ScrollToEnd();
            _atBottom = true;
            JumpBtn.Visibility = Visibility.Collapsed;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void List_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv) return;
        _scroll = sv;
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0) return;   // content grew; keep the old answer
        _atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 24;
        if (_atBottom) JumpBtn.Visibility = Visibility.Collapsed;
    }

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroll(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void Jump_Click(object sender, RoutedEventArgs e) => ScrollToEnd();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (sender is RadioButton { Tag: string tag }) App.Config.FeedFilter = tag;
        App.Config.FeedOnlyMine = MineToggle.IsChecked == true;
        App.SaveConfig();
        Rebuild();
        ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is FeedRow r) try { Clipboard.SetText(r.Message.Text); } catch { }
    }

    private void DockRight_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke("Right");
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke("Bottom");
    private void Close_Click(object sender, RoutedEventArgs e) => DockRequested?.Invoke(null);

    // ------------------------------------------------------------------ sending

    /// <summary>Chat as the game you're looking at: the focused game becomes the sender.</summary>
    private void FollowFocusedGame()
    {
        if (App.Tracker.Focused is SlotView f) { _asSlotId = f.Ref.Id; UpdateAs(); }
    }

    private SlotView? AsSlot =>
        App.Tracker.Slots.FirstOrDefault(s => s.Ref.Id == _asSlotId && s.IsTracking)
        ?? App.Tracker.Slots.FirstOrDefault(s => s.IsTracking)
        ?? App.Tracker.Slots.FirstOrDefault();

    private void UpdateAs()
    {
        var s = AsSlot;
        AsText.Text = s?.SlotName ?? "–";
        AsBtn.IsEnabled = App.Tracker.Slots.Count > 1;
        SendBtn.IsEnabled = s != null;
    }

    private void As_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AsBtn, Placement = PlacementMode.Top };
        foreach (var s in App.Tracker.Slots)
        {
            var item = new MenuItem { Header = $"{s.SlotName}  ·  {s.Title}", IsCheckable = true, IsChecked = s == AsSlot, IsEnabled = s.IsTracking };
            item.Click += (_, _) => { _asSlotId = s.Ref.Id; UpdateAs(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Send_Click(sender, e);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        string text = Input.Text.Trim();
        var slot = AsSlot;
        if (text.Length == 0 || slot == null) return;
        SendBtn.IsEnabled = false;
        string? error = App.Engine.Client.IsConnected ? await App.Engine.Client.SayAsync(slot.Ref.Id, text) : "The tracker engine isn't running.";
        SendBtn.IsEnabled = true;
        SendError.Text = error ?? "";
        SendError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error == null)
        {
            Input.Text = "";
            _atBottom = true;
        }
        Input.Focus();
    }

    public string AsSlotName => AsSlot?.SlotName ?? "";

    public void SelfTest()
    {
        foreach (var pill in new[] { ItemsPill, HintsPill, ChatPill, ActivityPill, AllPill }) { pill.IsChecked = true; UpdateLayout(); }
        MineToggle.IsChecked = true; UpdateLayout();
        MineToggle.IsChecked = false; UpdateLayout();
        if (_world != null && _world.Feed.Count > 0 && _shown.Count == 0) throw new InvalidOperationException("Feed shows no messages.");
    }
}
