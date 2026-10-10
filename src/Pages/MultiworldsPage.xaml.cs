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
    public List<SlotChip> Slots { get; init; } = new();
    public string CountText => $"{World.Slots.Count} slot{(World.Slots.Count == 1 ? "" : "s")}";
    public Visibility ActiveVis => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivateVis => IsActive ? Visibility.Collapsed : Visibility.Visible;
    public Brush Outline => IsActive ? (Brush)Application.Current.FindResource("AccentLineBrush") : Brushes.Transparent;
}

public partial class MultiworldsPage : UserControl
{
    public MultiworldsPage()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Refresh(); };
        BoxArt.Changed += _ => { if (IsVisible) Refresh(); };
        Refresh();
    }

    public void Refresh()
    {
        string? active = App.Tracker?.Active?.Id;
        var rows = App.Config.Multiworlds.Select(w => new MultiworldRow
        {
            World = w,
            IsActive = w.Id == active,
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
        App.Config.FocusedSlotId = null;
        App.Tracker.Activate(w);
        Refresh();
        Main?.GoToTracker();
    }

    private void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        App.Tracker.Activate(null);
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
        if (App.Tracker.Active?.Id == w.Id) App.Tracker.Activate(null);
        App.Config.Multiworlds.Remove(w);
        App.SaveConfig();
        Refresh();
    }
}
