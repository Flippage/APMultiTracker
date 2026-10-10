using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>The room's messages as they happen (like Archipelago's Text Client), for the active multiworld.</summary>
public partial class FeedPage : UserControl
{
    private readonly ObservableCollection<FeedMessage> _shown = new();
    private LiveWorld? _world;
    private ScrollViewer? _scroll;
    private bool _atBottom = true;
    private bool _syncing;
    private string? _asSlotId;

    public FeedPage()
    {
        InitializeComponent();
        List.ItemsSource = _shown;
        App.Tracker.WorldsChanged += () => { if (IsVisible) Rebuild(); };
        App.Tracker.Changed += _ => { if (IsVisible) UpdateAs(); };
        App.Tracker.FeedAdded += OnFeedAdded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) { Rebuild(); ScrollToEnd(); } };
        Loaded += (_, _) => _scroll ??= FindScroll(List);

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
        var mine = _world.Slots.Where(s => s.SlotNumber > 0).Select(s => s.SlotNumber).ToHashSet();
        return mine.Count == 0 || m.Players.Any(mine.Contains);
    }

    private void Rebuild()
    {
        _world = App.Tracker.ActiveLive;
        _shown.Clear();
        if (_world != null) foreach (var m in _world.Feed) if (Passes(m)) _shown.Add(m);
        SubText.Text = _world == null ? "No multiworld active"
                     : $"{_world.World.Name} · {_world.World.Server} · {_world.Feed.Count} message{(_world.Feed.Count == 1 ? "" : "s")}";
        UpdateEmpty();
        UpdateAs();
        JumpBtn.Visibility = Visibility.Collapsed;
    }

    private void OnFeedAdded(LiveWorld world, FeedMessage m)
    {
        if (!IsVisible || world != _world) return;
        SubText.Text = $"{world.World.Name} · {world.World.Server} · {world.Feed.Count} messages";
        if (!Passes(m)) return;
        while (_shown.Count >= LiveWorld.FeedLimit) _shown.RemoveAt(0);
        _shown.Add(m);
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

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_world == null) return;
        _world.Feed.Clear();
        Rebuild();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is FeedMessage m) try { Clipboard.SetText(m.Text); } catch { }
    }

    // ------------------------------------------------------------------ sending

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

    public void SelfTest()
    {
        foreach (var pill in new[] { ItemsPill, HintsPill, ChatPill, ActivityPill, AllPill }) { pill.IsChecked = true; UpdateLayout(); }
        MineToggle.IsChecked = true; UpdateLayout();
        MineToggle.IsChecked = false; UpdateLayout();
        if (_world != null && _world.Feed.Count > 0 && _shown.Count == 0) throw new InvalidOperationException("Feed shows no messages.");
    }
}
