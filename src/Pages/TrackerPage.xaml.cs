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
        LayoutUpdated += (_, _) => PlaceTallSide();   // keep a lifted Items / Hints column lined up with the check list
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
        HintsToggle.IsChecked = App.Config.ShowHints;
        FoundHintsToggle.IsChecked = App.Config.ShowFoundHints;
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
        UpdateSideLayout();
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
        FPretend.Text = s.ManualItems.Count.ToString();
        FPretendLabel.Text = s.ManualItems.Count == 1 ? " pretend item" : " pretend items";
        FPretendChip.Visibility = s.HasManual ? Visibility.Visible : Visibility.Collapsed;
        FPretendChip.ToolTip = s.HasManual
            ? "The logic is counting these as received:\n" + string.Join("\n", s.ManualItems) + "\n\nClick to see them in the Items panel."
            : null;
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
        UnhideAllBtn.Visibility = App.Config.Filter == CheckFilter.Hidden && s.Hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        EventsHeader.Text = $"EVENTS · {s.Events.Count}";
        ItemsList.ItemsSource = SortedItems(s);
        EventsList.ItemsSource = s.Events;
        PretendChips.ItemsSource = s.ManualItems;
        RefreshHints(s);
        ApplyItemsTab(s);
        RefreshList();
    }

    private void RefreshList()
    {
        var s = _shown;
        if (s == null) return;
        string q = Search.Text.Trim();
        var filter = App.Config.Filter;
        bool hideExcluded = App.Config.HideExcluded && filter != CheckFilter.Hidden;
        IEnumerable<CheckItem> items = s.Locations.Where(l => !(hideExcluded && l.Excluded)).Where(l => filter switch
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
        UpdateSideLayout();

        var accent = (Brush)FindResource("AccentBrush");
        var normal = (Brush)FindResource("Text2Brush");
        FeedBtn.Background = on ? (Brush)FindResource("AccentDimBrush") : Brushes.Transparent;
        FeedBtn.BorderBrush = on ? (Brush)FindResource("AccentLineBrush") : (Brush)FindResource("LineBrush");
        FeedBtnIcon.Stroke = on ? accent : normal;
        FeedBtnText.Foreground = on ? accent : normal;
        FeedBtn.ToolTip = on ? "Hide the feed" : "Show the feed next to the tracker";
    }

    // ------------------------------------------------------------------ Items / Hints column next to a bottom feed

    private bool _sideTall;

    /// <summary>
    /// With the feed along the bottom, the Items / Hints column runs from the top of the check list to the bottom of the
    /// window (the feed stops beside it): hint lists need the height more than the feed needs the width.
    /// </summary>
    private void UpdateSideLayout()
    {
        bool sideOn = ItemsPanel.Visibility == Visibility.Visible || HintsPanel.Visibility == Visibility.Visible;
        bool tall = App.Config.ShowFeedInTracker && App.Config.FeedDock == "Bottom" && sideOn
                    && FocusView.Visibility == Visibility.Visible;
        if (tall != _sideTall)
        {
            _sideTall = tall;
            if (SidePanels.Parent is Panel from) from.Children.Remove(SidePanels);
            if (tall)
            {
                Grid.SetColumn(SidePanels, 0); Grid.SetColumnSpan(SidePanels, 3);
                Grid.SetRow(SidePanels, 0); Grid.SetRowSpan(SidePanels, 3);
                SidePanels.HorizontalAlignment = HorizontalAlignment.Right;
                SidePanels.VerticalAlignment = VerticalAlignment.Stretch;
                DockRoot.Children.Add(SidePanels);
            }
            else
            {
                Grid.SetColumn(SidePanels, 1); Grid.SetColumnSpan(SidePanels, 1);
                Grid.SetRow(SidePanels, 0); Grid.SetRowSpan(SidePanels, 3);
                SidePanels.HorizontalAlignment = HorizontalAlignment.Stretch;
                SidePanels.Width = double.NaN;
                SidePanels.Margin = new Thickness(0);
                FocusBody.Children.Add(SidePanels);
            }
        }
        double width = ItemsCol.Width.Value;   // the column stays reserved in the focused view
        // Only the bottom dock's margins are ours to change (ApplyFeedDock sets the right dock's).
        if (App.Config.FeedDock == "Bottom")
        {
            FeedDock.Margin = new Thickness(32, 0, tall ? 32 + width : 32, 20);
            FeedHSplit.Margin = new Thickness(0, 0, tall ? 32 + width : 0, 0);
        }
        if (tall) PlaceTallSide();
    }

    /// <summary>Lines the lifted column up with the check list's top (and the window's right / bottom margins).</summary>
    private void PlaceTallSide()
    {
        if (!_sideTall || !FocusBody.IsVisible) return;
        double top = FocusBody.TranslatePoint(new Point(0, 0), DockRoot).Y;
        double width = ItemsCol.Width.Value;
        var m = new Thickness(0, top, 32, 20);
        if (SidePanels.Margin != m) SidePanels.Margin = m;
        if (Math.Abs(SidePanels.Width - width) > 0.5 || double.IsNaN(SidePanels.Width)) SidePanels.Width = width;
    }

    /// <summary>Self-test: the docked feed is on screen with a usable size.</summary>
    public void AssertFeedDocked()
    {
        if (FeedDock.ActualWidth < 250 || FeedDock.ActualHeight < 120)
            throw new InvalidOperationException($"Docked feed ({App.Config.FeedDock}) is {FeedDock.ActualWidth:0}x{FeedDock.ActualHeight:0}.");
        if (MainContent.ActualWidth < 300 || MainContent.ActualHeight < 250)
            throw new InvalidOperationException($"Tracker squeezed to {MainContent.ActualWidth:0}x{MainContent.ActualHeight:0} by the docked feed.");
        // Its spacing must be the dock's own (a later layout change once overwrote it with the bottom dock's).
        var want = App.Config.FeedDock == "Bottom" ? new Thickness(32, 0, FeedDock.Margin.Right, 20) : new Thickness(0, 26, 24, 20);
        if (FeedDock.Margin != want)
            throw new InvalidOperationException($"Docked feed ({App.Config.FeedDock}) has the wrong spacing: {FeedDock.Margin}.");
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
        // Four toggles in one row squeeze the header when narrow: two rows of two instead.
        ViewToggles.MaxWidth = narrow ? 250 : double.PositiveInfinity;
        foreach (var t in new FrameworkElement[] { GroupToggle, ItemsToggle, HintsToggle, ExplainToggle })
            t.Margin = narrow ? new Thickness(14, 2, 0, 2) : new Thickness(t == GroupToggle ? 0 : 18, 0, 0, 0);
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
        // Right-hand column: Items and/or Hints, stacked when both are on.
        bool items = ItemsToggle.IsChecked == true, hints = HintsToggle.IsChecked == true;
        ItemsCol.Width = new GridLength(hints ? 330 : items ? 280 : 0);
        ItemsPanel.Visibility = items ? Visibility.Visible : Visibility.Collapsed;
        HintsPanel.Visibility = hints ? Visibility.Visible : Visibility.Collapsed;
        ItemsRow.Height = items ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        HintsRow.Height = hints ? new GridLength(items ? 1.4 : 1, GridUnitType.Star) : new GridLength(0);
        HintsPanel.Margin = new Thickness(10, items && hints ? 10 : 0, 0, 0);
        if (hints && _shown != null) _ = LoadHintItemsAsync(_shown, refresh: false);
        UpdateSideLayout();
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
        if (_shown != null)
            UnhideAllBtn.Visibility = f == CheckFilter.Hidden && _shown.Hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Config.GroupByRegion = GroupToggle.IsChecked == true;
        App.Config.ShowItems = ItemsToggle.IsChecked == true;
        App.Config.ShowExplain = ExplainToggle.IsChecked == true;
        App.Config.ShowHints = HintsToggle.IsChecked == true;
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

    /// <summary>Like UT's /ignore_all: hide every check that's in logic right now (new entrances aren't checks).</summary>
    private async void CtxHideAll_Click(object sender, RoutedEventArgs e)
    {
        var s = _shown;
        if (s == null) return;
        var ids = s.Locations.Where(l => l.IsInLogic && l.Id > 0).Select(l => l.Id).ToList();
        if (ids.Count == 0) return;
        if (Window.GetWindow(this) is not MainWindow w) return;
        string what = ids.Count == 1 ? "1 check" : $"{ids.Count} checks";
        if (!await w.ConfirmAsync($"Hide {what}?",
                $"Hides every check that's in logic for {s.SlotName} ({s.Title}). Find them under the Hidden filter, where you can unhide them again.",
                "Hide all")) return;
        if (_shown == s) await App.Tracker.SetHiddenAsync(s, ids, true);
    }

    /// <summary>Like UT's /reset_ignored: show every hidden check again.</summary>
    private async void CtxUnhideAll_Click(object sender, RoutedEventArgs e)
    {
        var s = _shown;
        if (s == null) return;
        var ids = s.Locations.Where(l => l.IsHidden).Select(l => l.Id).ToList();
        if (ids.Count == 0) return;
        if (Window.GetWindow(this) is not MainWindow w) return;
        string what = ids.Count == 1 ? "1 hidden check" : $"{ids.Count} hidden checks";
        if (!await w.ConfirmAsync($"Unhide {what}?", $"Shows every check you've hidden for {s.SlotName} ({s.Title}) again.", "Unhide all")) return;
        if (_shown == s) await App.Tracker.SetHiddenAsync(s, ids, false);
    }

    // ------------------------------------------------------------------ items panel tabs

    private void ApplyItemsTab(SlotView s)
    {
        bool events = App.Config.ShowEvents;
        ItemsList.Visibility = events ? Visibility.Collapsed : Visibility.Visible;
        PretendArea.Visibility = events ? Visibility.Collapsed : Visibility.Visible;
        ItemsSortBtn.Visibility = events ? Visibility.Collapsed : Visibility.Visible;
        ItemsSortBtn.Text = App.Config.ItemsLatestFirst ? "Latest first ▾" : "A–Z ▾";
        EventsList.Visibility = events ? Visibility.Visible : Visibility.Collapsed;
        EventsEmpty.Visibility = events && s.Events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsHeader.Foreground = (Brush)FindResource(events ? "SubBrush" : "AccentBrush");
        EventsHeader.Foreground = (Brush)FindResource(events ? "AccentBrush" : "SubBrush");
    }

    // ------------------------------------------------------------------ hints: points, buy a hint, hint lists

    private HintItem? _hintPick;
    private SlotView? _hintSlot;

    private void RefreshHints(SlotView s)
    {
        if (_hintSlot != s)
        {
            // Another game: start the picker over.
            _hintSlot = s;
            _hintPick = null;
            HintBox.Text = "";
            HintResultBox.Visibility = Visibility.Collapsed;
            if (HintsToggle.IsChecked == true) _ = LoadHintItemsAsync(s, refresh: false);
        }
        HintPointsText.Text = s.HintCost > 0 ? $"{s.HintPoints} points · {s.HintCost} per hint" : $"{s.HintPoints} points";
        HintAffordText.Text = !s.HasData ? ""
            : s.HintCost == 0 ? "Hints are free in this room."
            : s.HintPoints >= s.HintCost ? $"You can buy {s.HintPoints / s.HintCost} {(s.HintPoints / s.HintCost == 1 ? "hint" : "hints")}. Re-showing a hint you already have is free."
            : $"You need {s.HintCost - s.HintPoints} more points for a hint. You get points for checking locations.";
        bool showFound = App.Config.ShowFoundHints;
        var mine = s.Hints.Where(h => h.Mine && (showFound || !h.Found)).ToList();
        var theirs = s.Hints.Where(h => !h.Mine && h.InMyWorld && (showFound || !h.Found)).ToList();
        HintsMine.ItemsSource = mine;
        HintsTheirs.ItemsSource = theirs;
        HintsMineHeader.Text = $"WHERE YOUR ITEMS ARE · {mine.Count}";
        HintsTheirsHeader.Text = $"OTHERS' ITEMS IN YOUR WORLD · {theirs.Count}";
        UpdateHintButton();
        CtxHint.Header = s.HintCost > 0 ? $"Hint this location · {s.HintCost} points" : "Hint this location";
        CtxHint.IsEnabled = s.IsTracking && (s.HintCost == 0 || s.HintPoints >= s.HintCost);
    }

    private async System.Threading.Tasks.Task LoadHintItemsAsync(SlotView s, bool refresh)
    {
        if (!s.IsTracking) return;
        await App.Tracker.HintItemsAsync(s, refresh);
        if (_shown == s && _hintPick != null)
            _hintPick = s.HintItems?.FirstOrDefault(i => i.Name == _hintPick.Name) ?? _hintPick;   // fresh counts / hinted state
        UpdateHintButton();
    }

    private void UpdateHintButton()
    {
        var s = _shown;
        var pick = _hintPick;
        if (s == null || pick == null)
        {
            HintBuyBtn.IsEnabled = false;
            HintBuyBtn.Content = "Pick an item to hint";
            return;
        }
        bool free = pick.Hinted || s.HintCost == 0;
        bool afford = free || s.HintPoints >= s.HintCost;
        HintBuyBtn.IsEnabled = afford && s.IsTracking;
        HintBuyBtn.Content = pick.Hinted ? "Show this hint again · free"
                           : s.HintCost == 0 ? "Get hint · free"
                           : afford ? $"Buy hint · {s.HintCost} points"
                           : $"Need {s.HintCost} points (you have {s.HintPoints})";
    }

    private async void HintBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var s = _shown;
        string q = HintBox.Text.Trim();
        if (_hintPick != null && q != _hintPick.Name) { _hintPick = null; UpdateHintButton(); }
        if (_hintPick != null && !_hintShowAll) return;   // the box just got the picked name
        if (s == null || (!HintBox.IsKeyboardFocused && !_hintShowAll)) return;
        var items = await App.Tracker.HintItemsAsync(s);
        if (_shown != s || HintBox.Text.Trim() != q) return;
        // Every item in the game (typing filters): names that start with the text first, then the rest by name.
        if (_hintPick != null && q == _hintPick.Name) q = "";   // reopening after a pick shows the whole list
        var matches = items.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(i => q.Length > 0 && i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                           .ThenBy(i => i.Name, NaturalComparer.Instance).ToList();
        HintSuggest.ItemsSource = matches;
        if (matches.Count > 0) HintSuggest.SelectedIndex = 0;
        HintPopup.IsOpen = matches.Count > 0 && (_hintPick == null || _hintShowAll);
        _hintShowAll = false;
    }

    private void HintBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_hintPick == null) HintBox_TextChanged(sender, null!);   // show the list straight away
    }

    private void HintBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && HintPopup.IsOpen)
        {
            HintSuggest.SelectedIndex = Math.Min(HintSuggest.SelectedIndex + 1, HintSuggest.Items.Count - 1);
            HintSuggest.ScrollIntoView(HintSuggest.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && HintPopup.IsOpen)
        {
            HintSuggest.SelectedIndex = Math.Max(HintSuggest.SelectedIndex - 1, 0);
            HintSuggest.ScrollIntoView(HintSuggest.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (HintPopup.IsOpen && HintSuggest.SelectedItem is HintItem pick) PickHintItem(pick);
            else if (_hintPick != null && HintBuyBtn.IsEnabled) HintBuy_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { HintPopup.IsOpen = false; e.Handled = true; }
    }

    private void HintBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject d && (HintPopup.IsAncestorOf(d) || HintBoxRow.IsAncestorOf(d))) return;
        HintPopup.IsOpen = false;
    }

    private bool _hintShowAll;

    /// <summary>The ▾ next to the box: open (or close) the full list to scroll through.</summary>
    private void HintArrow_Click(object sender, RoutedEventArgs e)
    {
        if (HintPopup.IsOpen) { HintPopup.IsOpen = false; return; }
        _hintShowAll = true;
        HintBox.Focus();
        HintBox_TextChanged(HintBox, null!);
    }

    private void HintSuggest_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HintItem pick) PickHintItem(pick);
    }

    private void PickHintItem(HintItem pick)
    {
        _hintPick = pick;
        HintPopup.IsOpen = false;
        HintBox.Text = pick.Name;
        HintBox.CaretIndex = pick.Name.Length;
        UpdateHintButton();
    }

    private async void HintBuy_Click(object sender, RoutedEventArgs e)
    {
        var s = _shown;
        if (s == null || _hintPick == null) return;
        await BuyHintAsync(s, "item", _hintPick.Name);
    }

    private async System.Threading.Tasks.Task BuyHintAsync(SlotView s, string kind, string name)
    {
        HintBuyBtn.IsEnabled = false;
        HintResult.ItemsSource = new[] { new[] { new FeedSegment(kind == "location" ? $"Asking what's at {name}…" : $"Asking where {name} is…", "") } };
        HintResultBox.Visibility = Visibility.Visible;
        var (lines, error) = await App.Tracker.BuyHintAsync(s, kind, name);
        if (_shown != s) return;
        HintResult.ItemsSource = error != null ? new[] { new[] { new FeedSegment(error, "red") } }
                               : lines.Count > 0 ? lines
                               : new[] { new[] { new FeedSegment("No answer from the server yet. Check the feed.", "") } };
        await LoadHintItemsAsync(s, refresh: true);
    }

    private void FoundHints_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Config.ShowFoundHints = FoundHintsToggle.IsChecked == true;
        App.SaveConfig();
        if (_shown != null) RefreshHints(_shown);
    }

    /// <summary>Right-click → Hint this location (UT's !hint_location): shows the result in the Hints panel.</summary>
    private async void CtxHint_Click(object sender, RoutedEventArgs e)
    {
        var s = _shown;
        if (s == null || CheckListBox.SelectedItem is not CheckItem c || c.IsEntrance) return;
        HintsToggle.IsChecked = true;   // show the answer
        await BuyHintAsync(s, "location", c.LookupName);
    }

    // ------------------------------------------------------------------ pretend items (UT's /manually_collect)

    private void PretendChip_Click(object sender, MouseButtonEventArgs e)
    {
        ItemsToggle.IsChecked = true;   // show the Items panel, on its Items tab
        SetItemsTab(false);
    }

    private async void PretendBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PretendError.Visibility = Visibility.Collapsed;
        var s = _shown;
        string q = PretendBox.Text.Trim();
        bool all = _pretendShowAll;
        _pretendShowAll = false;
        if (s == null || _pretendClearing) { PretendPopup.IsOpen = false; return; }
        if (!PretendBox.IsKeyboardFocused && !all) return;   // like Where is my item?: the list goes with the box
        var names = await App.Tracker.ItemNamesAsync(s);
        if (_shown != s || PretendBox.Text.Trim() != q) return;   // moved on while waiting
        var have = new HashSet<string>(s.ManualItems);
        var matches = names.Where(n => !have.Contains(n) && n.Contains(q, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(n => n.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                           .ThenBy(n => n, NaturalComparer.Instance).ToList();
        PretendSuggest.ItemsSource = matches;
        if (matches.Count > 0) PretendSuggest.SelectedIndex = 0;
        PretendPopup.IsOpen = matches.Count > 0 && (PretendBox.IsKeyboardFocused || all);
    }

    private void PretendBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && PretendPopup.IsOpen)
        {
            PretendSuggest.SelectedIndex = Math.Min(PretendSuggest.SelectedIndex + 1, PretendSuggest.Items.Count - 1);
            PretendSuggest.ScrollIntoView(PretendSuggest.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && PretendPopup.IsOpen)
        {
            PretendSuggest.SelectedIndex = Math.Max(PretendSuggest.SelectedIndex - 1, 0);
            PretendSuggest.ScrollIntoView(PretendSuggest.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // The highlighted suggestion, or exactly what was typed
            string name = PretendPopup.IsOpen && PretendSuggest.SelectedItem is string pick ? pick : PretendBox.Text.Trim();
            if (name.Length > 0) _ = AddPretendAsync(name);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { PretendPopup.IsOpen = false; e.Handled = true; }
    }

    private void PretendBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking a suggestion moves focus into the popup: keep it open for that click.
        if (e.NewFocus is DependencyObject d && (PretendPopup.IsAncestorOf(d) || PretendBoxRow.IsAncestorOf(d))) return;
        PretendPopup.IsOpen = false;
    }

    private bool _pretendShowAll, _pretendClearing;

    /// <summary>Clicking into the box opens the full list straight away (typing then filters it).</summary>
    private void PretendBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PretendBox_TextChanged(PretendBox, null!);

    /// <summary>The ▾ next to the box: open (or close) the full item list to scroll through.</summary>
    private void PretendArrow_Click(object sender, RoutedEventArgs e)
    {
        if (PretendPopup.IsOpen) { PretendPopup.IsOpen = false; return; }
        _pretendShowAll = true;
        PretendBox.Focus();
        PretendBox_TextChanged(PretendBox, null!);
    }

    private void PretendSuggest_Click(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is string name) _ = AddPretendAsync(name);
    }

    private async System.Threading.Tasks.Task AddPretendAsync(string name)
    {
        var s = _shown;
        if (s == null) return;
        PretendPopup.IsOpen = false;
        string? error = await App.Tracker.SetManualAsync(s, s.ManualItems.Append(name));
        if (error != null)
        {
            PretendError.Text = error;
            PretendError.Visibility = Visibility.Visible;
            return;
        }
        PretendError.Visibility = Visibility.Collapsed;
        _pretendClearing = true;   // clearing the box after adding shouldn't pop the list back open
        PretendBox.Text = "";
        _pretendClearing = false;
    }

    private async void PretendRemove_Click(object sender, RoutedEventArgs e)
    {
        var s = _shown;
        if (s == null || (sender as FrameworkElement)?.DataContext is not string name) return;
        string? error = await App.Tracker.SetManualAsync(s, s.ManualItems.Where(n => n != name));
        PretendError.Text = error ?? "";
        PretendError.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A-Z (progression items first), or newest first.</summary>
    private static IReadOnlyList<ItemCount> SortedItems(SlotView s) => App.Config.ItemsLatestFirst
        ? s.Items.OrderByDescending(i => i.Last).ThenBy(i => i.Name, NaturalComparer.Instance).ToList()
        : s.Items;

    private void ItemsSort_Click(object sender, MouseButtonEventArgs e)
    {
        App.Config.ItemsLatestFirst = !App.Config.ItemsLatestFirst;
        App.SaveConfig();
        if (_shown == null) return;
        ItemsList.ItemsSource = SortedItems(_shown);
        ApplyItemsTab(_shown);
    }

    private void ItemsTab_Click(object sender, MouseButtonEventArgs e) => SetItemsTab(false);
    private void EventsTab_Click(object sender, MouseButtonEventArgs e) => SetItemsTab(true);

    private void SetItemsTab(bool events)
    {
        if (App.Config.ShowEvents == events) return;
        App.Config.ShowEvents = events;
        App.SaveConfig();
        if (_shown != null) ApplyItemsTab(_shown);
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
            if (lines.Count == 0) { ShowExplain("There's no explanation for that.", null); return; }
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
        // A clicked button keeps keyboard focus: that must not resize it or recolour its border (it once did).
        double explainW = ExplainBtn.ActualWidth, pathW = PathBtn.ActualWidth;
        var pathBorder = (PathBtn.Template.FindName("Bd", PathBtn) as Border)?.BorderBrush;
        ExplainBtn.Focus(); UpdateLayout();
        if (Math.Abs(ExplainBtn.ActualWidth - explainW) > 0.5) throw new InvalidOperationException("A focused button changed size.");
        PathBtn.Focus(); UpdateLayout();
        if (Math.Abs(PathBtn.ActualWidth - pathW) > 0.5) throw new InvalidOperationException("A focused button changed size.");
        if ((PathBtn.Template.FindName("Bd", PathBtn) as Border)?.BorderBrush != pathBorder)
            throw new InvalidOperationException("A focused button's border changed colour.");
        ExplainToggle.IsChecked = false;
        Search.Text = "zzzz-no-match";
        RefreshList();
        if (_checks.Count != 0) throw new InvalidOperationException("Search didn't filter.");
        Search.Text = "";
        RefreshList();
        SelfTestDropdown();
        App.Tracker.SetFocus(null);
        UpdateLayout();
    }

    /// <summary>Self-test: the hint dropdown opens under its box, closes on a click elsewhere or when the window moves.</summary>
    private void SelfTestDropdown()
    {
        HintsToggle.IsChecked = true;
        UpdateLayout();
        HintSuggest.ItemsSource = new[] { new HintItem("Mantis Claw", 1, 1, false, false), new HintItem("Monarch Wings", 1, 0, false, true) };
        HintPopup.IsOpen = true;
        UpdateLayout();
        if (!HintPopup.IsOpen || HintPopup.ActualHeight < 20) throw new InvalidOperationException("Hint dropdown didn't open.");
        var box = HintBoxRow.TranslatePoint(new Point(0, HintBoxRow.ActualHeight), HintsPanel);
        var drop = HintPopup.TranslatePoint(new Point(0, 0), HintsPanel);
        if (Math.Abs(drop.Y - box.Y) > 12 || Math.Abs(drop.X - box.X) > 2) throw new InvalidOperationException("Hint dropdown isn't under its box.");

        void Click(UIElement on) => on.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = PreviewMouseDownEvent, Source = on });
        Click(HintBox);
        if (!HintPopup.IsOpen) throw new InvalidOperationException("Hint dropdown closed on a click in its own box.");
        Click(FTitle);
        if (HintPopup.IsOpen) throw new InvalidOperationException("Hint dropdown stayed open after a click elsewhere.");

        HintPopup.IsOpen = true;
        var w = Window.GetWindow(this)!;
        w.Left += 1;
        w.Left -= 1;
        if (HintPopup.IsOpen) throw new InvalidOperationException("Hint dropdown stayed open when the window moved.");
        HintsToggle.IsChecked = false;
        UpdateLayout();

        // "Pretend you have an item…" works the same way: clicking into the box opens the list.
        ItemsToggle.IsChecked = true;
        UpdateLayout();
        _shown!.ItemNames = new[] { "Mantis Claw", "Monarch Wings", "Mothwing Cloak" };
        PretendBox.Focus();
        UpdateLayout();
        if (!PretendPopup.IsOpen) throw new InvalidOperationException("Pretend dropdown didn't open when the box was clicked.");
        Click(FTitle);
        if (PretendPopup.IsOpen) throw new InvalidOperationException("Pretend dropdown stayed open after a click elsewhere.");
        ItemsToggle.IsChecked = false;
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
