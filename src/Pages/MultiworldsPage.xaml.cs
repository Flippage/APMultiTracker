using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace APMultiTracker;

public sealed class SlotChip
{
    public string Name { get; init; } = "";
    public string Game { get; init; } = "";
    public string? ArtUrl { get; init; }
}

public sealed class MultiworldRow
{
    public Multiworld World { get; init; } = null!;
    public bool IsActive { get; init; }
    public bool IsConnected { get; init; }
    public int InLogic { get; init; }
    public bool HasData { get; init; }
    public string LiveText => IsConnected && HasData ? $"{InLogic} in logic" : "";
    public Visibility ConnectedVis => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackgroundVis => IsConnected && !IsActive ? Visibility.Visible : Visibility.Collapsed;
    public string ActivateText => IsConnected ? "Switch to" : "Activate";
    public string ActivateTip => IsConnected ? "Show this multiworld (it's already tracking)" : "Connect and track these games";
    public List<SlotChip> Slots { get; init; } = new();
    public string CountText => $"{World.Slots.Count} slot{(World.Slots.Count == 1 ? "" : "s")}";
    public Visibility ActiveVis => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivateVis => IsActive ? Visibility.Collapsed : Visibility.Visible;
    public Brush Outline => IsActive ? (Brush)Application.Current.FindResource("AccentLineBrush") : Brushes.Transparent;
}

public partial class MultiworldsPage : UserControl
{
    private readonly System.Windows.Threading.DispatcherTimer _liveRefresh = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MultiworldsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        App.Tracker.WorldsChanged += () => { if (IsVisible) Refresh(); };
        _liveRefresh.Tick += (_, _) => { _liveRefresh.Stop(); if (IsVisible) Refresh(); };
        App.Tracker.Changed += _ => { if (IsVisible && !_liveRefresh.IsEnabled) _liveRefresh.Start(); };
        BoxArt.Changed += _ => { if (IsVisible) Refresh(); };
        Refresh();
    }

    public void Refresh()
    {
        string? active = App.Tracker?.Active?.Id;
        var live = App.Tracker?.Live.ToDictionary(l => l.World.Id) ?? new();
        var rows = App.Config.Multiworlds.Select(w => new MultiworldRow
        {
            World = w,
            IsActive = w.Id == active,
            IsConnected = live.ContainsKey(w.Id),
            InLogic = live.TryGetValue(w.Id, out var lw) ? lw.InLogic : 0,
            HasData = lw?.HasData ?? false,
            Slots = w.Slots.Select(s =>
            {
                if (s.Game.Length > 0 && BoxArt.Known(s.Game) == null) _ = BoxArt.ResolveAsync(s.Game);
                return new SlotChip
                {
                    Name = s.Name,
                    Game = s.Game.Length > 0 ? s.Game : "Not connected yet",
                    ArtUrl = BoxArt.Known(s.Game),
                };
            }).ToList(),
        }).ToList();
        WorldList.ItemsSource = rows;
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Multiworld? WorldOf(object sender) => (sender as Button)?.CommandParameter as Multiworld;
    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void New_Click(object sender, RoutedEventArgs e) => Main?.OpenMultiworldEditor(null);
    private void Edit_Click(object sender, RoutedEventArgs e) { if (WorldOf(sender) is { } w) Main?.OpenMultiworldEditor(w); }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (WorldOf(sender) is not { } w) return;
        App.Tracker.Activate(w);
        Refresh();
        Main?.GoToTracker();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (WorldOf(sender) is not { } w) return;
        App.Tracker.Disconnect(w);
        Refresh();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (WorldOf(sender) is not { } w) return;
        var copy = JsonSerializer.Deserialize<Multiworld>(JsonSerializer.Serialize(w))!;
        copy.Id = Guid.NewGuid().ToString("N");
        foreach (var s in copy.Slots) s.Id = Guid.NewGuid().ToString("N");
        copy.Name = w.Name + " (copy)";
        App.Config.Multiworlds.Insert(App.Config.Multiworlds.IndexOf(w) + 1, copy);
        App.SaveConfig();
        Refresh();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (WorldOf(sender) is not { } w || Main is not { } main) return;
        if (!await main.ConfirmAsync($"Delete “{w.Name}”?", "Its server and slot list will be removed. Your hidden checks are kept in Archipelago.", "Delete")) return;
        App.Tracker.Disconnect(w);
        App.Config.Multiworlds.Remove(w);
        App.SaveConfig();
        Refresh();
    }
}
