using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace APMultiTracker;

/// <summary>Game name → known box art URL (for small previews in lists).</summary>
public sealed class GameArtConverter : IValueConverter
{
    public static readonly GameArtConverter Instance = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string g && g.Length > 0 ? BoxArt.Known(g) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class EditMultiworldPage : UserControl
{
    private readonly Multiworld? _original;
    private readonly ObservableCollection<SlotRef> _slots = new();

    public EditMultiworldPage(Multiworld? world)
    {
        InitializeComponent();
        _original = world;
        Heading.Text = world == null ? "New multiworld" : "Edit multiworld";
        NameBox.Text = world?.Name ?? "";
        ServerBox.Text = world?.Server ?? "";
        PasswordBox.Password = world?.Password ?? "";
        YamlBox.Text = world?.YamlFolder ?? "";
        foreach (var s in world?.Slots ?? new())
            _slots.Add(new SlotRef { Id = s.Id, Name = s.Name, Game = s.Game });
        if (_slots.Count == 0) _slots.Add(new SlotRef());
        SlotList.ItemsSource = _slots;
        Loaded += (_, _) => { if (world == null) NameBox.Focus(); };
    }

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    /// <summary>Accepts host:port, "/connect host:port", ws(s)://… or archipelago://name:pass@host:port.</summary>
    public static (string Server, string? Slot, string? Password) ParseAddress(string text)
    {
        text = text.Trim();
        if (text.StartsWith("/connect", StringComparison.OrdinalIgnoreCase)) text = text[8..].Trim();
        var m = Regex.Match(text, @"^archipelago://(?:(?<user>[^:@/]*)(?::(?<pass>[^@/]*))?@)?(?<host>[^/?#]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            string? user = m.Groups["user"].Success && m.Groups["user"].Value.Length > 0 ? Uri.UnescapeDataString(m.Groups["user"].Value) : null;
            string? pass = m.Groups["pass"].Success && m.Groups["pass"].Value.Length > 0 ? Uri.UnescapeDataString(m.Groups["pass"].Value) : null;
            return (m.Groups["host"].Value, user, pass);
        }
        text = Regex.Replace(text, "^wss?://", "", RegexOptions.IgnoreCase).TrimEnd('/');
        return (text, null, null);
    }

    private void ServerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var (server, slot, pass) = ParseAddress(ServerBox.Text);
        ServerBox.Text = server;
        if (pass != null && PasswordBox.Password.Length == 0) PasswordBox.Password = pass;
        if (slot != null && !_slots.Any(s => s.Name.Equals(slot, StringComparison.OrdinalIgnoreCase)))
        {
            var empty = _slots.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Name));
            if (empty != null) _slots.Remove(empty);
            _slots.Add(new SlotRef { Name = slot });
        }
    }

    private void AddSlot_Click(object sender, RoutedEventArgs e) => _slots.Add(new SlotRef());

    private void RemoveSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SlotRef s) _slots.Remove(s);
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        ServerBox_LostFocus(sender, e);
        var first = _slots.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Name));
        if (string.IsNullOrWhiteSpace(ServerBox.Text) || first == null)
        {
            ShowFind("Enter the server and at least one slot name first.");
            return;
        }
        if (!App.Engine.Client.IsConnected)
        {
            ShowFind("The tracker engine isn't running yet, so the room can't be read. Check Settings.");
            return;
        }
        FindBtn.IsEnabled = false;
        ShowFind("Connecting to the room…");
        try
        {
            var reply = await App.Engine.Client.RequestAsync(new JsonObject
            {
                ["cmd"] = "probe", ["server"] = ServerBox.Text.Trim(), ["slot"] = first.Name.Trim(),
                ["password"] = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null,
            }, TimeSpan.FromSeconds(30));
            if (reply == null) { ShowFind("No answer from the room."); return; }
            if (reply["error"]?.GetValue<string>() is string err) { ShowFind(err); return; }
            var players = (reply["players"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(p => new RoomPlayer(p["slot"]!.GetValue<int>(), p["name"]!.GetValue<string>(), p["game"]!.GetValue<string>()))
                .ToList();
            // Fill in games for the slots already listed.
            foreach (var s in _slots)
                if (players.FirstOrDefault(p => p.Name.Equals(s.Name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } p)
                    s.Game = p.Game;
            SlotList.ItemsSource = null;
            SlotList.ItemsSource = _slots;
            var rest = players.Where(p => !_slots.Any(s => s.Name.Trim().Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            RoomPlayers.ItemsSource = rest;
            foreach (var g in players.Select(p => p.Game).Distinct()) _ = BoxArt.ResolveAsync(g);
            ShowFind(rest.Count == 0 ? $"All {players.Count} slots in this room are listed."
                                     : $"{players.Count} slots in this room. Click to add the ones you want to track.");
        }
        finally { FindBtn.IsEnabled = true; }
    }

    private void AddPlayer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RoomPlayer p) return;
        var empty = _slots.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Name));
        if (empty != null) _slots.Remove(empty);
        _slots.Add(new SlotRef { Name = p.Name, Game = p.Game });
        if (RoomPlayers.ItemsSource is System.Collections.Generic.List<RoomPlayer> list)
        {
            list.Remove(p);
            RoomPlayers.ItemsSource = null;
            RoomPlayers.ItemsSource = list;
        }
    }

    private void ShowFind(string text)
    {
        FindStatus.Text = text;
        FindStatus.Visibility = Visibility.Visible;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder with this room's player YAMLs" };
        if (YamlBox.Text.Length > 0 && System.IO.Directory.Exists(YamlBox.Text)) dlg.InitialDirectory = YamlBox.Text;
        else if (App.Engine.ApDir is string ap) dlg.InitialDirectory = System.IO.Path.Combine(ap, "Players");
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) YamlBox.Text = dlg.FolderName;
    }

    private Multiworld? SaveWorld()
    {
        ServerBox_LostFocus(this, new RoutedEventArgs());
        string name = NameBox.Text.Trim();
        string server = ServerBox.Text.Trim();
        var slots = _slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList();
        string? error = server.Length == 0 ? "Enter the server address."
                      : slots.Count == 0 ? "Add at least one slot name."
                      : slots.GroupBy(s => s.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) ? "Each slot can only be listed once."
                      : null;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        if (error != null) return null;

        var w = _original ?? new Multiworld();
        w.Name = name.Length > 0 ? name : server;
        w.Server = server;
        w.Password = PasswordBox.Password;
        w.YamlFolder = YamlBox.Text.Trim();
        foreach (var s in slots) s.Name = s.Name.Trim();
        w.Slots = slots;
        if (_original == null) App.Config.Multiworlds.Add(w);
        App.SaveConfig();
        if (App.Tracker.Active?.Id == w.Id) App.Tracker.Reload();
        return w;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (SaveWorld() != null) Main?.CloseMultiworldEditor();
    }

    private void SaveActivate_Click(object sender, RoutedEventArgs e)
    {
        var w = SaveWorld();
        if (w == null) return;
        if (App.Tracker.Active?.Id != w.Id)
        {
            App.Config.FocusedSlotId = null;
            App.Tracker.Activate(w);
        }
        Main?.CloseMultiworldEditor();
        Main?.GoToTracker();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Main?.CloseMultiworldEditor();
}
