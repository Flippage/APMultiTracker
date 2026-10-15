using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace APMultiTracker;

public partial class TrackerPage : UserControl, INotifyPropertyChanged
{
    private double _tileWidth = 200;
    /// <summary>Game card width: as wide as 200, narrower (down to 130) so they stay on one row when space is tight.</summary>
    public double TileWidth
    {
        get => _tileWidth;
        private set
        {
            if (Math.Abs(_tileWidth - value) < 0.5) return;
            _tileWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileWidth)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileArtVisibility)));
        }
    }
    /// <summary>Narrow cards drop their small box art so the game name keeps its room.</summary>
    public Visibility TileArtVisibility => _tileWidth < 150 ? Visibility.Collapsed : Visibility.Visible;
    private readonly ObservableCollection<CheckItem> _checks = new();
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private SlotView? _shown;
    private bool _syncing;

    public bool ShowRegionLine => !App.Config.GroupByRegion;

    public TrackerPage()
    {
        InitializeComponent();
        DataContext = this;
        _view = new ListCollectionView(_checks);
        CheckListBox.ItemsSource = _view;
        Tiles.ItemsSource = App.Tracker.Slots;
        Columns.ItemsSource = App.Tracker.Slots;

        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); RefreshList(); };
        App.Tracker.Changed += OnTrackerChanged;
        App.Tracker.FocusChanged += RefreshAll;
        App.Engine.Changed += () => Dispatcher.InvokeAsync(UpdateBanner);
        BoxArt.Changed += _ => { if (_shown != null) Art.SetUrl(FArt, _shown.ArtUrl); };
        SizeChanged += (_, _) => { UpdateColumnsWidth(); UpdateCompact(); FitExplainHeight(); ClampFeedDock(); };
        MainContent.SizeChanged += (_, _) => { UpdateCompact(); UpdateTileWidth(); };
        Strip.SizeChanged += (_, _) => UpdateTileWidth();
        App.Tracker.Slots.CollectionChanged += (_, _) => UpdateTileWidth();
        OverviewScroll.SizeChanged += (_, _) => UpdateColumnsWidth();   // also fires when All games is first shown
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) RefreshAll(); };

        DockedFeed.DockRequested += pos =>
        {
            if (pos == null) App.Config.ShowFeedInTracker = false;
            else App.Config.FeedDock = pos;
            App.SaveConfig();
            ApplyFeedDock();
        };
        ApplyFeedDock();

        _syncing = true;
        GroupToggle.IsChecked = App.Config.GroupByRegion;
        OverviewGroupToggle.IsChecked = App.Config.OverviewGroupByRegion;
        ItemsToggle.IsChecked = App.Config.ShowItems;
        ExplainToggle.IsChecked = App.Config.ShowExplain;
        (App.Config.Filter switch
        {
            CheckFilter.Reachable => FReachPill,
            CheckFilter.All => FAllPill,
            CheckFilter.Hinted => FHintPill,
            CheckFilter.Hidden => FHiddenPill,
            CheckFilter.Entrances => FEntrancePill,
            _ => FInLogicPill,
        }).IsChecked = true;
        _syncing = false;
        ApplyGrouping();
        RefreshAll();
    }

    // ------------------------------------------------------------------ refresh

    private void OnTrackerChanged(SlotView? slot)
    {
        if (slot == null) { RefreshAll(); return; }
        if (!App.Tracker.Slots.Contains(slot)) return;   // a multiworld tracking in the background
        UpdateAllCount();
        if (slot == _shown) RefreshFocused();
    }

    private void RefreshAll()
    {
        var store = App.Tracker;
        var mw = store.Active;
        TitleText.Text = mw?.Name ?? "Tracker";
        SubText.Text = mw == null ? "No multiworld active" : $"{mw.Server} · {store.Slots.Count} game{(store.Slots.Count == 1 ? "" : "s")}";
        bool hasGames = store.Slots.Count > 0;
        EmptyState.Visibility = hasGames ? Visibility.Collapsed : Visibility.Visible;
        Strip.Visibility = hasGames ? Visibility.Visible : Visibility.Collapsed;
        RefreshAllBtn.IsEnabled = hasGames;
        EmptyTitle.Text = mw == null ? "Nothing to track yet" : "This multiworld has no slots";
        EmptySub.Text = mw == null
            ? "Add your multiworld's server and slot names, then activate it to track every game here."
            : "Edit it on Multiworlds and add the slot names you want to track.";

        var focused = store.Focused;
        _syncing = true;
        AllTile.IsChecked = focused == null;
        SyncTileChecks();
        _syncing = false;

        OverviewView.Visibility = hasGames && focused == null ? Visibility.Visible : Visibility.Collapsed;
        FocusView.Visibility = hasGames && focused != null ? Visibility.Visible : Visibility.Collapsed;
        if (focused != _shown)
        {
            _shown = focused;
            _checks.Clear();   // different game: start at the top
            Search.Text = "";
        }
        UpdateAllCount();
        UpdateColumnsWidth();
        if (focused != null) RefreshFocused();
        UpdateBanner();
    }

    private void UpdateAllCount()
    {
        var slots = App.Tracker.Slots;
        AllCount.Text = slots.Any(s => s.HasData) ? slots.Sum(s => s.ToDo).ToString() : "–";
    }

    /// <summary>Columns share the width; with many games they keep a readable minimum and scroll sideways.</summary>
    private void UpdateColumnsWidth()
    {
        int n = App.Tracker.Slots.Count;
        double avail = OverviewScroll.ActualWidth;
        // Not laid out yet (e.g. the view was hidden while a game was focused): size naturally for now;
        // OverviewScroll.SizeChanged calls back once it has a real size.
        if (n == 0 || avail <= 0 || OverviewScroll.ActualHeight <= 0)
        {
            Columns.Width = double.NaN;
            Columns.Height = double.NaN;
            return;
        }
        Columns.Width = Math.Max(avail, n * 270);
        Columns.Height = Math.Max(0, OverviewScroll.ActualHeight - (Columns.Width > avail + 1 ? 12 : 0));
    }

    private void SyncTileChecks()
    {
        var focused = App.Tracker.Focused;
        foreach (var item in Tiles.Items)
        {
            if (Tiles.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp
                && VisualTreeHelper.GetChildrenCount(cp) > 0 && VisualTreeHelper.GetChild(cp, 0) is RadioButton rb)
                rb.IsChecked = item == focused;
        }
    }

    private void RefreshFocused()
    {
        var s = _shown;
        if (s == null) return;
        FTitle.Text = s.Title;
        FSub.Text = $"Slot · {s.SlotName}" + (s.Items.Count > 0 ? $" · {s.Items.Sum(i => i.Count)} items received" : "");
        Art.SetUrl(FArt, s.ArtUrl);
        FInLogic.Text = s.InLogicText;
        FInLogicLabel.Text = s.IsBK ? "" : " in logic";
        FInLogicText.Foreground = s.InLogicBrush;
        FInLogicChip.Background = (Brush)FindResource(s.IsBK ? "DangerBgBrush" : "InLogicDimBrush");
        FInLogicChip.ToolTip = s.IsBK ? s.InLogicTip : null;
        FGlitched.Text = s.Glitched.ToString();
        FGlitchedChip.Visibility = s.Glitched > 0 ? Visibility.Visible : Visibility.Collapsed;
        FHinted.Text = s.Hinted.ToString();
        FHintedChip.Visibility = s.Hinted > 0 ? Visibility.Visible : Visibility.Collapsed;
        FEntrances.Text = s.Entrances.ToString();
        FEntrancesLabel.Text = s.Entrances == 1 ? " new entrance" : " new entrances";
        FEntranceChip.Visibility = s.Entrances > 0 ? Visibility.Visible : Visibility.Collapsed;
        FChecked.Text = s.HasData ? $"{s.Checked}/{s.Total}" : "–";
        FPercentChip.Visibility = s.HasData && s.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        FPercent.Text = (s.Progress * 100).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
        FBar.ScaleX = s.Progress;
        FBarFill.Background = s.Goal ? Brushes2.InLogic : Brushes2.Accent;
        FInLogicChip.Visibility = s.Released ? Visibility.Collapsed : Visibility.Visible;   // nothing left to do
        FGoChip.Visibility = s.HasGoMode ? Visibility.Visible : Visibility.Collapsed;
        FGo.Text = s.GoModeText;
        FGoChip.ToolTip = s.GoModeTip;
        FGoChip.Background = (Brush)FindResource(s.IsGoGlitched ? "WarnBgBrush" : "InLogicDimBrush");
        FGoChip.BorderBrush = FGo.Foreground = (Brush)FindResource(s.IsGoGlitched ? "GlitchedBrush" : "InLogicBrush");
        FHiddenPill.Content = s.Hidden > 0 ? $"Hidden ({s.Hidden})" : "Hidden";
        FHintPill.Content = s.Hinted > 0 ? $"Hinted ({s.Hinted})" : "Hinted";
        FEntrancePill.Content = s.Entrances > 0 ? $"New entrances ({s.Entrances})" : "New entrances";
        // Only for games that have some (or while it's the chosen filter, so it doesn't vanish under you)
        FEntrancePill.Visibility = s.Entrances > 0 || App.Config.Filter == CheckFilter.Entrances ? Visibility.Visible : Visibility.Collapsed;

        // Notice: errors and busy states first, then UT warnings.
        string? notice = null;
        bool danger = false, retry = false;
        if (s.IsError) { notice = s.Message.Length > 0 ? s.Message : "Something went wrong."; danger = retry = true; }
        else if (!s.IsTracking) notice = s.StatusDetail;
        else if (s.Errors.Count > 0) { notice = string.Join("  ·  ", s.Errors); danger = true; }
        else if (s.HasWarning) notice = s.Warning;
        FNotice.Visibility = notice == null ? Visibility.Collapsed : Visibility.Visible;
        FNoticeText.Text = notice ?? "";
        FNotice.Background = (Brush)FindResource(danger ? "DangerBgBrush" : "WarnBgBrush");
        FNotice.BorderBrush = (Brush)FindResource(danger ? "DangerLineBrush" : "WarnLineBrush");
        FNoticeText.Foreground = (Brush)FindResource(danger ? "DangerBrush" : "WarnTextBrush");
        FNoticeBtn.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;

        ItemsHeader.Text = $"ITEMS RECEIVED · {s.Items.Sum(i => i.Count)}";
        ItemsList.ItemsSource = s.Items;
        RefreshList();
    }

    private void RefreshList()
    {
        var s = _shown;
        if (s == null) return;
        string q = Search.Text.Trim();
        var filter = App.Config.Filter;
        IEnumerable<CheckItem> items = s.Locations.Where(l => filter switch
        {
            CheckFilter.InLogic => l.IsInLogic || l.IsEntrance,
            CheckFilter.Reachable => l.IsInLogic || l.IsEntrance || l.IsGlitched,
            CheckFilter.Hinted => l.HasHint && !l.IsHidden,
            CheckFilter.Hidden => l.IsHidden,
            CheckFilter.Entrances => l.IsEntrance,
            _ => !l.IsHidden,
        });
        if (q.Length > 0)
            items = items.Where(l => l.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || l.Region.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || (l.Hint?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        var sorted = App.Config.GroupByRegion
            ? items.OrderBy(l => l.Region, NaturalComparer.Instance).ThenBy(l => l.Rank).ThenBy(l => l.Name, NaturalComparer.Instance)
            : items.OrderBy(l => l.HasHint ? 0 : 1).ThenBy(l => l.Rank).ThenBy(l => l.Name, NaturalComparer.Instance);
        ListSync.Sync(_checks, sorted.ToList());
        // Group containers can be reused for other regions as the list changes: re-apply collapsed state.
        Dispatcher.BeginInvoke(new Action(() => { foreach (var g in FindGroupItems(CheckListBox)) ApplyGroupState(g); }),
                               DispatcherPriority.Loaded);

        bool empty = _checks.Count == 0;
        ListEmpty.Visibility = empty && s.HasData ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            (ListEmptyTitle.Text, ListEmptySub.Text) =
                q.Length > 0 ? ("No matches", "Nothing matches your search.")
                : s.Released ? ("Goaled!", "This room releases on goal, so the remaining checks were sent to the other players.")
                : s.IsDone ? ("Every check is done!", "This game has nothing left.")
                : filter switch
                {
                    CheckFilter.InLogic => ("Nothing in logic right now", "Waiting on items from the other games. Try “All remaining” to see what's left."),
                    CheckFilter.Hinted => ("No hints", "Checks someone hints for you will show up here."),
                    CheckFilter.Hidden => ("No hidden checks", "Hover a check and click the eye to hide it."),
                    CheckFilter.Entrances => ("No new entrances", "Entrances you can reach but haven't been through yet show up here."),
                    _ => ("Nothing here", ""),
                };
        }
    }

    // ------------------------------------------------------------------ collapsible region groups

    private HashSet<string> CollapsedFor(SlotView s)
    {
        if (!App.Config.CollapsedRegions.TryGetValue(s.Ref.Id, out var list))
            App.Config.CollapsedRegions[s.Ref.Id] = list = new List<string>();
        return new HashSet<string>(list);
    }

    private void SaveCollapsed(SlotView s, IEnumerable<string> regions)
    {
        App.Config.CollapsedRegions[s.Ref.Id] = regions.Distinct().ToList();
        App.SaveConfig();
    }

    /// <summary>The slot a region group belongs to: the focused game, or an overview column's game.</summary>
    private SlotView? SlotOfGroup(DependencyObject d)
    {
        var list = FindAncestor<ListBox>(d);
        if (list == null || list == CheckListBox) return _shown;
        return list.DataContext as SlotView;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void ApplyGroupState(GroupItem g)
    {
        if (g.DataContext is not CollectionViewGroup group || SlotOfGroup(g) is not SlotView slot) return;
        g.ApplyTemplate();
        bool collapsed = CollapsedFor(slot).Contains(group.Name?.ToString() ?? "");
        if (g.Template.FindName("Items", g) is UIElement items)
            items.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (g.Template.FindName("Chevron", g) is FrameworkElement chevron)
            chevron.RenderTransform = new RotateTransform(collapsed ? -90 : 0);   // the template's transform is shared/frozen
    }

    private void Group_Loaded(object sender, RoutedEventArgs e) { if (sender is GroupItem g) ApplyGroupState(g); }

    private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not CollectionViewGroup group) return;
        var list = FindAncestor<ListBox>(fe);
        if (list == null || SlotOfGroup(fe) is not SlotView slot) return;
        string name = group.Name?.ToString() ?? "";
        var collapsed = CollapsedFor(slot);
        bool collapse = !collapsed.Contains(name);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+click: every region in this list follows this one.
            var all = (list.Items.Groups ?? new System.Collections.ObjectModel.ReadOnlyObservableCollection<object>(new()))
                .OfType<CollectionViewGroup>().Select(gr => gr.Name?.ToString() ?? "");
            SaveCollapsed(slot, collapse ? collapsed.Union(all) : collapsed.Except(all));
        }
        else
        {
            if (collapse) collapsed.Add(name); else collapsed.Remove(name);
            SaveCollapsed(slot, collapsed);
        }
        foreach (var g in FindGroupItems(list)) ApplyGroupState(g);
        e.Handled = true;
    }

    /// <summary>Demo/self-test: flip the overview's "Group by region" toggle.</summary>
    public void SetOverviewGrouping(bool on) => OverviewGroupToggle.IsChecked = on;

    private void OverviewGroup_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Config.OverviewGroupByRegion = OverviewGroupToggle.IsChecked == true;
        App.SaveConfig();
        foreach (var s in App.Tracker.Slots) s.ApplyOverviewGrouping();
    }

    /// <summary>Shrink the game cards (to 130 px, art hidden below 150) before letting them wrap onto a second row.</summary>
    private void UpdateTileWidth()
    {
        int n = App.Tracker.Slots.Count;
        double avail = Strip.ActualWidth - AllTile.ActualWidth - AllTile.Margin.Right - 10;
        if (n == 0 || avail <= 0) return;
        const double chrome = 8 * 2 + 2 * 2 + 10;   // card padding, border and the gap after it
        TileWidth = Math.Clamp(Math.Floor(avail / n - chrome), 130, 200);
    }

    // ------------------------------------------------------------------ feed docked beside / under the tracker

    /// <summary>The docked feed can't squeeze the tracker below a usable size (or push itself off-screen).</summary>
    private void ClampFeedDock()
    {
        double w = DockRoot.ActualWidth, h = DockRoot.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double mainMin = Math.Min(640, Math.Max(360, w - 16 - 300));
        MainCol.MinWidth = mainMin;
        FeedCol.MaxWidth = Math.Max(300, Math.Min(640, w - 16 - mainMin));
        FeedRow.MaxHeight = Math.Max(150, h - 12 - 380);
    }

    private void FeedBtn_Click(object sender, RoutedEventArgs e)
    {
        App.Config.ShowFeedInTracker = !App.Config.ShowFeedInTracker;
        App.SaveConfig();
        ApplyFeedDock();
    }

    /// <summary>Show/hide the docked feed on the right or along the bottom, at the size the user last dragged it to.</summary>
    public void ApplyFeedDock()
    {
        bool on = App.Config.ShowFeedInTracker;
        bool right = App.Config.FeedDock != "Bottom";
        FeedDock.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FeedVSplit.Visibility = on && right ? Visibility.Visible : Visibility.Collapsed;
        FeedHSplit.Visibility = on && !right ? Visibility.Visible : Visibility.Collapsed;

        FeedSplitCol.Width = new GridLength(on && right ? 16 : 0);
        FeedCol.Width = new GridLength(on && right ? App.Config.FeedDockWidth : 0);
        FeedCol.MinWidth = on && right ? 300 : 0;
        FeedSplitRow.Height = new GridLength(on && !right ? 12 : 0);
        FeedRow.Height = new GridLength(on && !right ? App.Config.FeedDockHeight : 0);
        FeedRow.MinHeight = on && !right ? 150 : 0;

        if (right)
        {
            Grid.SetRow(FeedDock, 0); Grid.SetColumn(FeedDock, 2);
            Grid.SetRowSpan(FeedDock, 3); Grid.SetColumnSpan(FeedDock, 1);
            FeedDock.Margin = new Thickness(0, 26, 24, 20);
            MainContent.Margin = new Thickness(32, 26, on ? 0 : 32, 20);
        }
        else
        {
            Grid.SetRow(FeedDock, 2); Grid.SetColumn(FeedDock, 0);
            Grid.SetRowSpan(FeedDock, 1); Grid.SetColumnSpan(FeedDock, 3);
            FeedDock.Margin = new Thickness(32, 0, 32, 20);
            MainContent.Margin = new Thickness(32, 26, 32, on ? 0 : 20);
        }
        DockedFeed.SetDockPosition(right ? "Right" : "Bottom");
        ClampFeedDock();

        var accent = (Brush)FindResource("AccentBrush");
        var normal = (Brush)FindResource("Text2Brush");
        FeedBtn.Background = on ? (Brush)FindResource("AccentDimBrush") : Brushes.Transparent;
        FeedBtn.BorderBrush = on ? (Brush)FindResource("AccentLineBrush") : (Brush)FindResource("LineBrush");
        FeedBtnIcon.Stroke = on ? accent : normal;
        FeedBtnText.Foreground = on ? accent : normal;
        FeedBtn.ToolTip = on ? "Hide the feed" : "Show the feed next to the tracker";
    }

    /// <summary>Self-test: the docked feed is on screen with a usable size.</summary>
    public void AssertFeedDocked()
    {
        if (FeedDock.ActualWidth < 250 || FeedDock.ActualHeight < 120)
            throw new InvalidOperationException($"Docked feed ({App.Config.FeedDock}) is {FeedDock.ActualWidth:0}x{FeedDock.ActualHeight:0}.");
        if (MainContent.ActualWidth < 300 || MainContent.ActualHeight < 250)
            throw new InvalidOperationException($"Tracker squeezed to {MainContent.ActualWidth:0}x{MainContent.ActualHeight:0} by the docked feed.");
    }

    private void FeedSplit_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (App.Config.FeedDock == "Bottom") App.Config.FeedDockHeight = Math.Round(FeedRow.ActualHeight);
        else App.Config.FeedDockWidth = Math.Round(FeedCol.ActualWidth);
        App.SaveConfig();
    }

    // ------------------------------------------------------------------ layout on small windows

    /// <summary>Short windows: drop the big box art from the game header (the sidebar already shows it).</summary>
    private void UpdateCompact()
    {
        bool compact = ActualHeight > 0 && ActualHeight < 900;
        FArtBox.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FHeader.Padding = compact ? new Thickness(16, 10, 12, 4) : new Thickness(16);
        FTitle.FontSize = compact ? 18 : 22;
        ViewToggles.Margin = compact ? new Thickness(0, 40, 4, 4) : new Thickness(0, 44, 4, 6);
        // Narrow tracker (small window or a wide docked feed): shorter labels and a smaller search box.
        double w = MainContent.ActualWidth;
        bool narrow = w > 0 && w < 820;
        Search.Width = double.NaN;
        if (Search.Parent is FrameworkElement box) box.Width = narrow ? 170 : 240;
        GroupToggle.Content = narrow ? "Regions" : "Group by region";
        FeedBtnText.Visibility = w > 0 && w < 700 ? Visibility.Collapsed : Visibility.Visible;
        FArtBox.Visibility = compact || (w > 0 && w < 640) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Keep at least a few checks visible: the explain panel never takes more than the room left over.</summary>
    private void FitExplainHeight()
    {
        if (ExplainToggle.IsChecked != true || ExplainRow.Parent is not Grid grid || grid.ActualHeight <= 0) return;
        double max = Math.Max(120, Math.Min(grid.ActualHeight - 10 - 130, grid.ActualHeight * 0.55));
        double want = Math.Min(App.Config.ExplainHeight, max);
        if (Math.Abs(ExplainRow.ActualHeight - want) > 1) ExplainRow.Height = new GridLength(want);
    }

    private void ExplainSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        App.Config.ExplainHeight = Math.Round(ExplainRow.ActualHeight);
        App.SaveConfig();
    }

    private static IEnumerable<GroupItem> FindGroupItems(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is GroupItem g) yield return g;
            foreach (var nested in FindGroupItems(child)) yield return nested;
        }
    }

    private void ApplyGrouping()
    {
        using (_view.DeferRefresh())
        {
            _view.GroupDescriptions.Clear();
            if (App.Config.GroupByRegion) _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CheckItem.Region)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRegionLine)));
        bool explain = ExplainToggle.IsChecked == true;
        ExplainPanel.Visibility = explain ? Visibility.Visible : Visibility.Collapsed;
        ExplainSplitter.Visibility = ExplainPanel.Visibility;
        ExplainRow.MinHeight = explain ? 120 : 0;
        ExplainRow.Height = new GridLength(explain ? App.Config.ExplainHeight : 0);
        FitExplainHeight();
        ItemsCol.Width = ItemsToggle.IsChecked == true ? new GridLength(280) : new GridLength(0);
        ItemsPanel.Visibility = ItemsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBanner()
    {
        var e = App.Engine;
        string? title = null, text = null, button = "Open Settings";
        if (App.DemoMode || App.Tracker.Active == null) { }
        else if (e.State == EngineState.NotInstalled)
        { title = "One more step"; text = "Install the tracker engine into Archipelago (copies one .apworld into custom_worlds)."; button = "Install engine"; }
        else if (e.State is EngineState.NoArchipelago or EngineState.NoTracker)
        { title = "Finish setting up"; text = e.Detail; }
        else if (e.State == EngineState.Error) { title = "The tracker engine isn't running"; text = e.Detail; }
        else if (e.State == EngineState.Stopped) { title = "The tracker engine is stopped"; text = "Start it to track your games."; button = "Start engine"; }
        else if (e.State == EngineState.Starting) { title = "Starting the tracker engine…"; text = e.Detail; button = null; }
        Banner.Visibility = title == null ? Visibility.Collapsed : Visibility.Visible;
        BannerTitle.Text = title ?? "";
        BannerText.Text = text ?? "";
        BannerBtn.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
        BannerBtn.Content = button ?? "";
    }

    // ------------------------------------------------------------------ events

    private void Tile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb) { _syncing = true; rb.IsChecked = rb.DataContext == App.Tracker.Focused; _syncing = false; }
    }

    private void Tile_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { DataContext: SlotView s }) return;
        App.Tracker.SetFocus(s);
    }

    private void AllTile_Checked(object sender, RoutedEventArgs e)
    {
        if (!_syncing) App.Tracker.SetFocus(null);
    }

    private void Column_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SlotView s) App.Tracker.SetFocus(s);
    }

    private void Column_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SlotView s) App.Tracker.SetFocus(s);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<CheckFilter>(tag, out var f)) return;
        App.Config.Filter = f;
        App.SaveConfig();
        RefreshList();
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Config.GroupByRegion = GroupToggle.IsChecked == true;
        App.Config.ShowItems = ItemsToggle.IsChecked == true;
        App.Config.ShowExplain = ExplainToggle.IsChecked == true;
        App.SaveConfig();
        ApplyGrouping();
        RefreshList();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void ToggleHidden_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null || (sender as FrameworkElement)?.DataContext is not CheckItem c) return;
        await App.Tracker.SetHiddenAsync(_shown, new[] { c.Id }, !c.IsHidden);
    }

    private async void CtxHide_Click(object sender, RoutedEventArgs e)
    {
        if (_shown == null || CheckListBox.SelectedItem is not CheckItem c || c.IsEntrance) return;
        await App.Tracker.SetHiddenAsync(_shown, new[] { c.Id }, !c.IsHidden);
    }

    private void CtxCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CheckListBox.SelectedItem is CheckItem c) try { Clipboard.SetText(c.Name); } catch { }
    }

    private void CheckList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CheckListBox.SelectedItem is CheckItem c) _ = ExplainAsync(BaseName(c), "explain");
    }

    // ------------------------------------------------------------------ explain / logical path

    /// <summary>UT looks locations up by their real name (the shown name may carry an alias).</summary>
    private static string BaseName(CheckItem c) => c.LookupName;

    private void CtxExplain_Click(object sender, RoutedEventArgs e)
    {
        if (CheckListBox.SelectedItem is CheckItem c) _ = ExplainAsync(BaseName(c), "explain");
    }

    private void CtxPath_Click(object sender, RoutedEventArgs e)
    {
        if (CheckListBox.SelectedItem is CheckItem c) _ = ExplainAsync(BaseName(c), "path");
    }

    private void Explain_Click(object sender, RoutedEventArgs e) => _ = ExplainAsync(ExplainQuery.Text, "explain");
    private void Path_Click(object sender, RoutedEventArgs e) => _ = ExplainAsync(ExplainQuery.Text, "path");

    private void ExplainQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = ExplainAsync(ExplainQuery.Text, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "path" : "explain");
        e.Handled = true;
    }

    private void ExplainClose_Click(object sender, RoutedEventArgs e) => ExplainToggle.IsChecked = false;

    private int _explainSeq;

    public async System.Threading.Tasks.Task ExplainAsync(string query, string mode)
    {
        var slot = _shown;
        query = query.Trim();
        if (slot == null || query.Length == 0) return;
        ExplainToggle.IsChecked = true;
        ExplainQuery.Text = query;
        int seq = ++_explainSeq;
        SetExplainMode(mode);
        // The last answer stays put until the new one arrives; only a slow answer shows a status line.
        _ = ShowStatusIfSlowAsync(seq, mode == "path" ? $"Finding a path to {query}\u2026" : $"Explaining {query}\u2026");
        try
        {
            if (!App.Engine.Client.IsConnected) { ShowExplain("The tracker engine isn't running.", "red"); return; }
            var reply = await App.Engine.Client.RequestAsync(new JsonObject
            {
                ["cmd"] = "explain", ["id"] = slot.Ref.Id, ["query"] = query, ["mode"] = mode,
            }, TimeSpan.FromSeconds(60));
            if (seq != _explainSeq) return;
            if (reply?["type"]?.GetValue<string>() == "error")
            {
                string msg = reply["message"]?.GetValue<string>() ?? "Unknown error";
                Log.Warn("explain", $"{mode} '{query}' failed: {msg}");
                ShowExplain("Couldn't do that: " + msg, "red");
                return;
            }
            if (reply?["lines"] is not JsonArray lines) { ShowExplain("No answer from the engine.", "red"); return; }
            if (lines.Count == 0) { ShowExplain("Universal Tracker had nothing to say about that.", null); return; }
            try { RenderExplain(mode, query, lines); }
            catch (Exception ex)
            {
                Log.Error("explain", $"Couldn't show {mode} for '{query}'", ex);
                ShowExplain("Couldn't show the answer: " + ex.Message, "red");
            }
        }
        finally
        {
            if (seq == _explainSeq) ExplainStatus.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Shows engine output: lines of {text, color} segments.</summary>
    public void RenderExplain(string mode, string query, JsonArray lines)
    {
        ExplainToggle.IsChecked = true;
        SetExplainMode(mode);
        ExplainQuery.Text = query;
        {
            ExplainOutput.Children.Clear();
            ExplainScroll.ScrollToTop();
            ExplainOutput.Children.Add(new TextBlock
            {
                FontSize = 11.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6),
                Foreground = (Brush)FindResource("AccentBrush"), TextWrapping = TextWrapping.Wrap,
                Text = (mode == "path" ? "LOGICAL PATH TO " : "EXPLAIN \u00b7 ") + query.ToUpperInvariant()
                       + $"   \u00b7   {DateTime.Now:HH:mm:ss}",
            });

            foreach (var line in lines.OfType<JsonArray>())
            {
                var tb = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 3),
                                         Foreground = (Brush)FindResource("Text2Brush") };
                foreach (var seg in line.OfType<JsonObject>())
                {
                    var run = new System.Windows.Documents.Run(seg["text"]?.ToString() ?? "");
                    if (ExplainColor(seg["color"]?.ToString()) is Brush b) run.Foreground = b;
                    tb.Inlines.Add(run);
                }
                ExplainOutput.Children.Add(tb);
            }
        }
    }

    private async System.Threading.Tasks.Task ShowStatusIfSlowAsync(int seq, string text)
    {
        await System.Threading.Tasks.Task.Delay(300);
        if (seq != _explainSeq) return;
        ExplainStatus.Text = text;
        ExplainStatus.Visibility = Visibility.Visible;
    }

    /// <summary>The button for the answer being shown is the highlighted one.</summary>
    private void SetExplainMode(string mode)
    {
        ExplainBtn.Style = (Style)FindResource(mode == "path" ? "OutlineButton" : "AccentButton");
        PathBtn.Style = (Style)FindResource(mode == "path" ? "AccentButton" : "OutlineButton");
    }

    private void ShowExplain(string text, string? color)
    {
        ExplainOutput.Children.Clear();
        ExplainOutput.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                                                   Foreground = ExplainColor(color) ?? (Brush)FindResource("SubBrush") });
    }

    /// <summary>Archipelago's text colours (plus item/location/player/entrance kinds) on the dark theme.</summary>
    private static Brush? ExplainColor(string? name) => (name ?? "").ToLowerInvariant() switch
    {
        "green" or "location" => Brushes2.InLogic,
        "red" or "salmon" => Brushes2.Danger,
        "yellow" or "player" => Brushes2.Glitched,
        "blue" or "slateblue" or "cyan" or "entrance" => Brushes2.Accent,
        "magenta" or "plum" or "item" => Brushes2.Hint,
        "white" => Brushes2.Text,
        _ => null,
    };

    private async void RefreshAll_Click(object sender, RoutedEventArgs e) => await App.Tracker.RefreshAsync(null);
    private async void RefreshOne_Click(object sender, RoutedEventArgs e) => await App.Tracker.RefreshAsync(_shown);

    private void Art_Click(object sender, MouseButtonEventArgs e)
    {
        if (_shown != null && _shown.Game.Length > 0) (Window.GetWindow(this) as MainWindow)?.OpenArtPicker(_shown.Game);
    }

    /// <summary>Connected multiworlds (instant) plus a way to the full list.</summary>
    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SwitchBtn, Placement = PlacementMode.Bottom };
        foreach (var live in App.Tracker.Live.OrderBy(l => App.Config.Multiworlds.IndexOf(l.World)))
        {
            string count = live.HasData ? $"  ·  {live.InLogic} in logic" : "";
            var item = new MenuItem { Header = live.World.Name + count, IsCheckable = true, IsChecked = live.World == App.Tracker.Active };
            item.Click += (_, _) => App.Tracker.Activate(live.World);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "All multiworlds…" };
        manage.Click += (_, _) => Multiworlds_Click(this, new RoutedEventArgs());
        menu.Items.Add(manage);
        menu.IsOpen = true;
    }

    private void Multiworlds_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.NavMultiworlds.SetCurrentValue(ToggleButton.IsCheckedProperty, true);

    private async void BannerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (App.Engine.State == EngineState.NotInstalled)
        {
            BannerBtn.IsEnabled = false;
            try { await App.Engine.InstallEngineAsync(); await App.Engine.StartAsync(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), "Couldn't install the engine: " + ex.Message, "APMultiTracker"); }
            finally { BannerBtn.IsEnabled = true; }
        }
        else if (App.Engine.State == EngineState.Stopped) await App.Engine.StartAsync();
        else (Window.GetWindow(this) as MainWindow)?.GoToSettings();
    }

    public void SelfTest()
    {
        if (App.Tracker.Slots.Count == 0) throw new InvalidOperationException("Self-test expected demo slots.");
        App.Tracker.SetFocus(App.Tracker.Slots[0]);
        UpdateLayout();
        foreach (var pill in new[] { FReachPill, FAllPill, FHintPill, FHiddenPill, FEntrancePill, FInLogicPill })
        {
            pill.IsChecked = true;
            UpdateLayout();
        }
        if (_checks.Count == 0) throw new InvalidOperationException("Focused game shows no in-logic checks.");
        GroupToggle.IsChecked = !GroupToggle.IsChecked;
        ItemsToggle.IsChecked = true;
        UpdateLayout();
        GroupToggle.IsChecked = !GroupToggle.IsChecked;
        ItemsToggle.IsChecked = false;
        ExplainToggle.IsChecked = true;
        ShowExplain("Self-test", "green");
        UpdateLayout();
        ExplainToggle.IsChecked = false;
        Search.Text = "zzzz-no-match";
        RefreshList();
        if (_checks.Count != 0) throw new InvalidOperationException("Search didn't filter.");
        Search.Text = "";
        RefreshList();
        App.Tracker.SetFocus(null);
        UpdateLayout();
    }

    /// <summary>Self-test: the All games columns have a real size (they once ended up 0px tall).</summary>
    public void AssertOverviewLaidOut()
    {
        if (Columns.ActualHeight < 100 || Columns.ActualWidth < 100)
            throw new InvalidOperationException($"All games columns are {Columns.ActualWidth:0}x{Columns.ActualHeight:0} after switching from a game.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
