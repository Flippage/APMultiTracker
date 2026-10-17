using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>One location that hasn't been checked yet.</summary>
public sealed record CheckItem(long Id, string Name, string Region, string Status, bool Excluded, string? Hint, string? Key = null)
{
    /// <summary>The location's real name (Name may include an alias), for explain / logical path.</summary>
    public string LookupName => string.IsNullOrEmpty(Key) ? Name : Key;
    public bool IsHidden => Status == "ignored";
    public bool IsInLogic => Status == "in_logic";
    public bool IsGlitched => Status == "glitched";
    /// <summary>A reachable entrance whose destination isn't known yet (entrance randomizers), like UT's unconnected lines.</summary>
    public bool IsEntrance => Status == "entrance";
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    /// <summary>Sort order: by status, with excluded checks after the others of the same status (as UT groups them).</summary>
    public int Rank => Status switch { "in_logic" or "entrance" => 0, "glitched" => 2, "out_of_logic" => 4, _ => 6 } + (Excluded ? 1 : 0);
    /// <summary>Shown with an EXCLUDED tag (not when hidden: the Hidden list is just that).</summary>
    public bool ShowExcluded => Excluded && !IsHidden;

    public Brush DotBrush => Status switch
    {
        "in_logic" => Brushes2.InLogic,
        "entrance" => Brushes2.Entrance,
        "glitched" => Brushes2.Glitched,
        _ => Brushes2.OutLogic,
    };
    public Brush NameBrush => Excluded && Status is "in_logic" or "glitched" ? Brushes2.Sub : Status switch
    {
        "in_logic" => Brushes2.Text,
        "entrance" => Brushes2.Entrance,   // like UT's unconnected lines
        "glitched" => Brushes2.GlitchedText,
        _ => Brushes2.Dim,
    };
    public string StatusTip => Status switch
    {
        "in_logic" when Excluded => "In logic (excluded)",
        "glitched" when Excluded => "Reachable with glitches / tricks (excluded)",
        "in_logic" => "In logic",
        "entrance" => "An entrance you can reach but haven't been through yet. Where it leads shows up once you've found it",
        "glitched" => "Reachable with glitches / tricks",
        "ignored" => "Hidden",
        _ => "Out of logic",
    };
}

public sealed record ItemCount(string Name, int Count, bool Progression);

/// <summary>An event the logic has reached (UT's /event_inventory), and the event location it came from.</summary>
public sealed record EventItem(string Name, string Location)
{
    /// <summary>Most games name the event after its location; only show the location when it adds something.</summary>
    public bool ShowLocation => Location.Length > 0 && Location != Name;
}

internal static class Json
{
    /// <summary>A number from a JsonNode whether it was parsed (JsonElement) or built in code (int/long).</summary>
    public static long Long(JsonNode? n, long fallback = 0)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (long)d;
        return fallback;
    }
}

internal static class Brushes2
{
    private static SolidColorBrush F(uint rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }
    public static readonly Brush InLogic = F(0x4ADE80);
    public static readonly Brush Glitched = F(0xFBBF24);
    public static readonly Brush GlitchedText = F(0xFDE7B0);
    public static readonly Brush OutLogic = F(0x5A5A5A);
    public static readonly Brush Text = F(0xF2F2F2);
    public static readonly Brush Dim = F(0x8A8A8A);
    public static readonly Brush Accent = F(0x22C7E6);
    public static readonly Brush Danger = F(0xF87171);
    public static readonly Brush Sub = F(0x9A9A9A);
    public static readonly Brush Hint = F(0xC084FC);
    public static readonly Brush Entrance = F(0x89F336);   // UT's colour for unconnected entrances
}

/// <summary>Live state of one tracked slot, bound by the UI.</summary>
public sealed class SlotView : INotifyPropertyChanged
{
    public SlotRef Ref { get; }
    public int Index { get; set; }

    public SlotView(SlotRef slot)
    {
        Ref = slot;
        _game = slot.Game;
        OverviewView = new System.Windows.Data.ListCollectionView(Overview);
        if (App.Config.OverviewGroupByRegion)
            OverviewView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CheckItem.Region)));
    }

    private string _game;
    public string Game { get => _game; set { if (Set(ref _game, value)) { Notify(nameof(Title)); LoadArt(); } } }
    public string SlotName => Ref.Name;
    public string Title => string.IsNullOrEmpty(Game) ? Ref.Name : Game;

    private string _status = "offline";
    public string Status { get => _status; set { if (Set(ref _status, value)) NotifyStatus(); } }
    private string _message = "";
    public string Message { get => _message; set { if (Set(ref _message, value)) NotifyStatus(); } }
    private string _warning = "";
    public string Warning { get => _warning; set { if (Set(ref _warning, value)) Notify(nameof(HasWarning)); } }
    public bool HasWarning => Warning.Length > 0;

    /// <summary>The slot number on the server (0 until connected).</summary>
    public int SlotNumber { get; private set; }
    public int Checked { get; private set; }
    public int Total { get; private set; }
    public int InLogic { get; private set; }
    public int Glitched { get; private set; }
    public int Hinted { get; private set; }
    public int Hidden { get; private set; }
    /// <summary>Reachable entrances that don't lead anywhere known yet.</summary>
    public int Entrances { get; private set; }
    /// <summary>Things to do now: checks in logic plus new entrances to go through.</summary>
    public int ToDo => InLogic + Entrances;
    public string GoMode { get; private set; } = "no";
    /// <summary>The slot has reported its goal as complete (client status GOAL).</summary>
    public bool Goal { get; private set; }
    public bool ShowCount => !Goal;
    /// <summary>The room auto-releases on goal, so a goaled slot's remaining checks have been sent out.</summary>
    public bool AutoRelease { get; private set; }
    public bool Released => Goal && AutoRelease;
    public IReadOnlyList<CheckItem> Locations { get; private set; } = Array.Empty<CheckItem>();
    public IReadOnlyList<ItemCount> Items { get; private set; } = Array.Empty<ItemCount>();
    public IReadOnlyList<EventItem> Events { get; private set; } = Array.Empty<EventItem>();
    /// <summary>Items we're pretending to have (UT's /manually_collect): the logic counts them as received.</summary>
    public IReadOnlyList<string> ManualItems { get; private set; } = Array.Empty<string>();
    public bool HasManual => ManualItems.Count > 0;
    /// <summary>The game's item names, fetched once for the "Pretend you have…" suggestions.</summary>
    public IReadOnlyList<string>? ItemNames { get; set; }
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public bool HasData { get; private set; }

    /// <summary>In-logic (then glitched) checks for the all-games overview, kept in place between updates.</summary>
    public ObservableCollection<CheckItem> Overview { get; } = new();
    /// <summary>The overview list as shown: optionally grouped by region.</summary>
    public System.Windows.Data.ListCollectionView OverviewView { get; }

    /// <summary>Re-sort and (un)group the overview after the "Group by region" setting changes.</summary>
    public void ApplyOverviewGrouping()
    {
        using (OverviewView.DeferRefresh())
        {
            OverviewView.GroupDescriptions.Clear();
            if (App.Config.OverviewGroupByRegion)
                OverviewView.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CheckItem.Region)));
        }
        ListSync.Sync(Overview, SortOverview(Locations));
    }

    private static List<CheckItem> SortOverview(IEnumerable<CheckItem> locs)
    {
        var shown = locs.Where(l => l.Status is "in_logic" or "glitched" or "entrance" && !(l.Excluded && App.Config.HideExcluded));
        return (App.Config.OverviewGroupByRegion
                ? shown.OrderBy(l => l.Region, NaturalComparer.Instance).ThenBy(l => l.Rank).ThenBy(l => l.Name, NaturalComparer.Instance)
                : shown.OrderBy(l => l.HasHint ? 0 : 1).ThenBy(l => l.Rank).ThenBy(l => l.Name, NaturalComparer.Instance))
            .ToList();
    }

    public bool IsTracking => Status == "tracking";
    public bool IsBusy => Status is "connecting" or "generating" or "reconnecting";
    public bool IsError => Status == "error";
    public double Progress => Total > 0 ? (double)Checked / Total : 0;
    public string CheckedText => Total > 0 ? $"{Checked}/{Total}" : "–";
    public string PercentText => Total > 0 ? $"{Progress * 100:0}%" : "";
    /// <summary>"BK": nothing in logic and the goal isn't reachable either (so not goaled and not in go mode).</summary>
    public bool IsBK => HasData && ToDo == 0 && !Goal && GoMode != "yes";
    public string InLogicText => IsBK ? "BK" : HasData ? ToDo.ToString() : "–";
    public Brush InLogicBrush => IsBK ? Brushes2.Danger : Brushes2.InLogic;
    public string InLogicTip => IsBK ? "BK: nothing in logic right now" : Goal ? "Goal complete"
                              : Entrances > 0 ? $"{InLogic} {(InLogic == 1 ? "check" : "checks")} in logic + {Entrances} new {(Entrances == 1 ? "entrance" : "entrances")}"
                              : "Checks in logic";
    public string ToDoTip => HasManual ? InLogicTip + $" (pretending to have {ManualItems.Count} {(ManualItems.Count == 1 ? "item" : "items")})" : InLogicTip;
    public bool IsDone => HasData && Total > 0 && Checked >= Total;
    public bool IsGoMode => GoMode == "yes";
    /// <summary>Show the go mode pill: the goal can be reached now (in logic, or with glitches), like UT's "Go mode"
    /// header. Once the goal is done the same pill says GOALED.</summary>
    public bool HasGoMode => HasData && (Goal || GoMode is "yes" or "glitched");
    public bool IsGoGlitched => !Goal && GoMode == "glitched";
    public string GoModeText => Goal ? "GOALED" : GoMode switch { "yes" => "GO MODE", "glitched" => "GO MODE (GLITCHED)", _ => "" };
    public string GoModeShort => Goal ? "GOALED" : "GO MODE";
    public string GoModeTip => Released ? "Goal complete. This room releases on goal, so the remaining checks were sent out"
                             : Goal ? "Goal complete. This room doesn't release on goal: the remaining checks stay until the host releases them"
                             : GoMode == "glitched" ? "Your goal can be reached, but only with glitches"
                             : "Everything needed to finish this game is in logic";

    public string StatusText => Status switch
    {
        "tracking" => Released ? "Goaled" : IsDone ? "All checks done" : $"{InLogic} in logic",
        "connecting" => "Connecting…",
        "generating" => "Building logic…",
        "reconnecting" => "Reconnecting…",
        "error" => "Error",
        "offline" => "Not connected",
        _ => Status,
    };

    public string StatusDetail => !string.IsNullOrEmpty(Message) ? Message : StatusText;

    public Brush StatusBrush => Status switch
    {
        "tracking" => Brushes2.InLogic,
        "error" => Brushes2.Danger,
        "offline" => Brushes2.OutLogic,
        _ => Brushes2.Glitched,
    };

    private string? _artUrl;
    public string? ArtUrl { get => _artUrl; private set => Set(ref _artUrl, value); }

    public async void LoadArt()
    {
        if (string.IsNullOrEmpty(Game)) { ArtUrl = null; return; }
        ArtUrl = BoxArt.Known(Game);
        if (ArtUrl == null) ArtUrl = await BoxArt.ResolveAsync(Game);
    }

    public void ApplyStatus(JsonObject m)
    {
        string game = m["game"]?.GetValue<string>() ?? "";
        if (game.Length > 0) Game = game;
        Status = m["status"]?.GetValue<string>() ?? Status;
        Message = m["message"]?.GetValue<string>() ?? "";
    }

    public void ApplySnapshot(JsonObject m)
    {
        ApplyStatus(m);
        SlotNumber = (int)Json.Long(m["slot"]);
        Checked = (int)Json.Long(m["checked"]);
        Total = (int)Json.Long(m["total"]);
        InLogic = (int)Json.Long(m["in_logic"]);
        Glitched = (int)Json.Long(m["glitched"]);
        Hinted = (int)Json.Long(m["hinted"]);
        Hidden = (int)Json.Long(m["ignored"]);
        GoMode = m["go_mode"]?.GetValue<string>() ?? "no";
        Goal = m["goal"]?.GetValue<bool>() ?? false;
        ManualItems = (m["manual_items"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(n => n.Length > 0).ToList()
                      ?? new List<string>();
        AutoRelease = m["auto_release"]?.GetValue<bool>() ?? false;
        Warning = m["warning"]?.GetValue<string>() ?? "";

        var locs = new List<CheckItem>();
        if (m["locations"] is JsonArray arr)
            foreach (var n in arr)
            {
                if (n is not JsonObject o) continue;
                locs.Add(new CheckItem(
                    Json.Long(o["id"]),
                    o["name"]?.GetValue<string>() ?? "?",
                    o["region"]?.GetValue<string>() ?? "",
                    o["status"]?.GetValue<string>() ?? "out_of_logic",
                    o["excluded"]?.GetValue<bool>() ?? false,
                    o["hint"]?.GetValue<string>(),
                    o["key"]?.GetValue<string>()));
            }
        if (Released)
        {
            // Goaled in a room that auto-releases: whatever is left was sent to the other games.
            locs.Clear();
            Checked = Total;
            InLogic = Glitched = Hinted = Hidden = 0;
            Entrances = 0;
        }
        if (m["entrances"] is JsonArray ents)
            foreach (var n in ents)
                if (n is JsonObject o && o["name"]?.GetValue<string>() is string name)
                {
                    string region = o["region"]?.GetValue<string>() ?? "";
                    // Negative ids keep them apart from locations; explain / path look at the room the entrance is in.
                    locs.Add(new CheckItem(-1 - (name.GetHashCode() & 0x3FFFFFFF), name, region, "entrance", false, null, region));
                }
        Entrances = locs.Count(l => l.IsEntrance);
        // Excluded checks are ones you've chosen not to care about: they don't count toward in logic / glitched.
        InLogic = locs.Count(l => l.IsInLogic && !l.Excluded);
        Glitched = locs.Count(l => l.IsGlitched && !l.Excluded);
        Locations = locs;

        var items = new List<ItemCount>();
        if (m["items"] is JsonArray its)
            foreach (var n in its)
                if (n is JsonObject o)
                    items.Add(new ItemCount(o["name"]?.GetValue<string>() ?? "?", (int)Json.Long(o["count"], 1), o["prog"]?.GetValue<bool>() ?? false));
        Items = items.OrderByDescending(i => i.Progression).ThenBy(i => i.Name, NaturalComparer.Instance).ToList();
        Events = (m["events"] as JsonArray)?.OfType<JsonObject>()
                     .Select(o => new EventItem(o["name"]?.GetValue<string>() ?? "?", o["location"]?.GetValue<string>() ?? ""))
                     .OrderBy(e => e.Name, NaturalComparer.Instance).ToList()
                 ?? new List<EventItem>();

        Errors = (m["errors"] as JsonArray)?.Select(e => e?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList()
                 ?? new List<string>();
        HasData = true;

        ListSync.Sync(Overview, SortOverview(locs));
        NotifyAll();
    }

    public void Reset()
    {
        Status = "offline";
        Message = "";
        HasData = false;
        Locations = Array.Empty<CheckItem>();
        Items = Array.Empty<ItemCount>();
        Events = Array.Empty<EventItem>();
        Overview.Clear();
        NotifyAll();
    }

    private void NotifyStatus()
    {
        Notify(nameof(StatusText)); Notify(nameof(StatusDetail)); Notify(nameof(StatusBrush));
        Notify(nameof(IsTracking)); Notify(nameof(IsBusy)); Notify(nameof(IsError));
    }

    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name!);
        return true;
    }
}

/// <summary>One coloured piece of a feed message.</summary>
public sealed record FeedSegment(string Text, string Color);

/// <summary>A server message (item sent, hint, chat, join…), like a line in the Text Client.</summary>
public sealed record FeedMessage(string Kind, DateTime Time, IReadOnlyList<FeedSegment> Segments, IReadOnlyList<int> Players)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
    public string TimeText => Time.ToString("HH:mm");
    public bool IsItem => Kind is "ItemSend" or "ItemCheat";
    public bool IsHint => Kind == "Hint";
    public bool IsChat => Kind is "Chat" or "ServerChat";
}

/// <summary>A multiworld the engine is connected to: its slots and its message feed.</summary>
public sealed class LiveWorld
{
    public const int FeedLimit = 1500;
    public Multiworld World { get; }
    public List<SlotView> Slots { get; } = new();
    public ObservableCollection<FeedMessage> Feed { get; } = new();
    public LiveWorld(Multiworld world) => World = world;

    public int InLogic => Slots.Sum(s => s.ToDo);
    public bool HasData => Slots.Any(s => s.HasData);

    public void AddFeed(FeedMessage m)
    {
        Feed.Add(m);
        while (Feed.Count > FeedLimit) Feed.RemoveAt(0);
    }
}

/// <summary>
/// Keeps every connected multiworld tracking in the background and exposes the active one (shown in the UI).
/// Switching multiworlds is instant because nothing is torn down.
/// </summary>
public sealed class TrackerStore
{
    private readonly EngineHost _engine;
    private readonly Dictionary<string, LiveWorld> _live = new();

    /// <summary>The active multiworld's slots (what the Tracker page and sidebar show).</summary>
    public ObservableCollection<SlotView> Slots { get; } = new();
    public Multiworld? Active { get; private set; }
    public LiveWorld? ActiveLive => Active == null ? null : _live.GetValueOrDefault(Active.Id);
    public IReadOnlyCollection<LiveWorld> Live => _live.Values;
    public bool IsConnected(Multiworld mw) => _live.ContainsKey(mw.Id);

    /// <summary>Slot data changed. The argument is the slot, or null when the whole list changed.</summary>
    public event Action<SlotView?>? Changed;
    public event Action? FocusChanged;
    /// <summary>A multiworld was connected, disconnected or activated.</summary>
    public event Action? WorldsChanged;
    /// <summary>A feed message arrived for the given multiworld.</summary>
    public event Action<LiveWorld, FeedMessage>? FeedAdded;

    public TrackerStore(EngineHost engine)
    {
        _engine = engine;
        engine.Client.Message += OnMessage;
        engine.Connected += () => _ = TrackAllAsync();
        BoxArt.Changed += game =>
        {
            foreach (var w in _live.Values)
                foreach (var s in w.Slots)
                    if (string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)) s.LoadArt();
        };
    }

    /// <summary>On startup: reconnect the multiworlds that were connected last time and show the active one.</summary>
    public void Restore()
    {
        foreach (var id in App.Config.ConnectedMultiworldIds.ToList())
            if (App.Config.Multiworlds.FirstOrDefault(m => m.Id == id) is { } mw) Connect(mw, save: false);
        var active = App.Config.Multiworlds.FirstOrDefault(m => m.Id == App.Config.ActiveMultiworldId);
        Activate(active);
    }

    public SlotView? Find(string? id)
    {
        if (id == null) return null;
        foreach (var w in _live.Values)
            foreach (var s in w.Slots)
                if (s.Ref.Id == id) return s;
        return null;
    }

    // ------------------------------------------------------------------ focus (per multiworld)

    /// <summary>The game shown on its own, or null for the all-games overview.</summary>
    public SlotView? Focused =>
        Active != null && App.Config.FocusByWorld.TryGetValue(Active.Id, out var id) ? Slots.FirstOrDefault(s => s.Ref.Id == id) : null;

    public void SetFocus(SlotView? slot)
    {
        if (Active == null || Focused == slot) return;
        if (slot == null) App.Config.FocusByWorld.Remove(Active.Id);
        else App.Config.FocusByWorld[Active.Id] = slot.Ref.Id;
        App.SaveConfig();
        FocusChanged?.Invoke();
    }

    /// <summary>Step through overview → game 1 → game 2 … (wrapping).</summary>
    public void CycleFocus(int delta)
    {
        int n = Slots.Count + 1;
        int cur = Focused == null ? 0 : Slots.IndexOf(Focused) + 1;
        int next = ((cur + delta) % n + n) % n;
        SetFocus(next == 0 ? null : Slots[next - 1]);
    }

    // ------------------------------------------------------------------ multiworlds

    /// <summary>Show this multiworld (connecting it first if needed). Others stay connected.</summary>
    public void Activate(Multiworld? mw)
    {
        if (mw != null && !_live.ContainsKey(mw.Id)) Connect(mw);
        Active = mw;
        App.Config.ActiveMultiworldId = mw?.Id;
        App.SaveConfig();
        Slots.Clear();
        if (mw != null) foreach (var s in _live[mw.Id].Slots) Slots.Add(s);
        Changed?.Invoke(null);
        FocusChanged?.Invoke();
        WorldsChanged?.Invoke();
    }

    /// <summary>Start tracking a multiworld in the background.</summary>
    public void Connect(Multiworld mw, bool save = true)
    {
        if (_live.ContainsKey(mw.Id)) return;
        var live = new LiveWorld(mw);
        BuildSlots(live);
        _live[mw.Id] = live;
        if (save) SaveConnected();
        WorldsChanged?.Invoke();
        _ = TrackWorldAsync(live);
    }

    /// <summary>Stop tracking a multiworld. If it was showing, nothing is shown until another is activated.</summary>
    public void Disconnect(Multiworld mw)
    {
        if (!_live.Remove(mw.Id, out var live)) return;
        SaveConnected();
        foreach (var s in live.Slots) _ = _engine.Client.UntrackAsync(s.Ref.Id);
        _ = _engine.Client.ClearFeedAsync(mw.Id);
        if (Active?.Id == mw.Id) Activate(null);
        else WorldsChanged?.Invoke();
    }

    /// <summary>Re-apply after a multiworld was edited (server, slots, folder…), keeping slots that didn't change.</summary>
    public void Reload(Multiworld mw)
    {
        if (!_live.TryGetValue(mw.Id, out var live)) return;
        var old = live.Slots.ToList();
        BuildSlots(live, old);
        foreach (var o in old)
            if (!live.Slots.Contains(o)) _ = _engine.Client.UntrackAsync(o.Ref.Id);
        if (Active?.Id == mw.Id) Activate(mw);
        _ = TrackWorldAsync(live);
    }

    private static void BuildSlots(LiveWorld live, List<SlotView>? reuse = null)
    {
        live.Slots.Clear();
        int i = 0;
        foreach (var r in live.World.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
        {
            var v = reuse?.FirstOrDefault(o => o.Ref.Id == r.Id && o.Ref.Name == r.Name);
            if (v == null) { v = new SlotView(r); v.LoadArt(); }
            v.Index = i++;
            live.Slots.Add(v);
        }
    }

    private void SaveConnected()
    {
        App.Config.ConnectedMultiworldIds = _live.Keys.ToList();
        App.SaveConfig();
    }

    private async System.Threading.Tasks.Task TrackAllAsync()
    {
        foreach (var live in _live.Values.ToList()) await TrackWorldAsync(live);
    }

    private async System.Threading.Tasks.Task TrackWorldAsync(LiveWorld live)
    {
        if (!_engine.Client.IsConnected) return;
        foreach (var s in live.Slots.ToList())
        {
            if (s.Status == "offline") s.Status = "connecting";
            await _engine.Client.TrackAsync(s.Ref.Id, live.World, s.Ref);
        }
    }

    // ------------------------------------------------------------------ engine commands

    public System.Threading.Tasks.Task RefreshAsync(SlotView? slot)
    {
        if (slot != null) return _engine.Client.RefreshAsync(slot.Ref.Id);
        return System.Threading.Tasks.Task.WhenAll(Slots.Select(s => _engine.Client.RefreshAsync(s.Ref.Id)));
    }

    public System.Threading.Tasks.Task<string?> SetManualAsync(SlotView slot, IEnumerable<string> items) =>
        _engine.Client.SetManualAsync(slot.Ref.Id, items);

    public async System.Threading.Tasks.Task<IReadOnlyList<string>> ItemNamesAsync(SlotView slot) =>
        slot.ItemNames is { Count: > 0 } cached ? cached : slot.ItemNames = await _engine.Client.ItemNamesAsync(slot.Ref.Id);

    public System.Threading.Tasks.Task SetHiddenAsync(SlotView slot, IEnumerable<long> ids, bool hidden) =>
        _engine.Client.SetIgnoredAsync(slot.Ref.Id, ids.ToArray(), hidden);

    // ------------------------------------------------------------------ engine messages

    private void OnMessage(JsonObject m)
    {
        string type = m["type"]?.GetValue<string>() ?? "";
        if (type == "error") { Log.Warn("engine", m["message"]?.GetValue<string>() ?? ""); return; }
        if (type == "feed") { AddFeed(m["room"]?.GetValue<string>(), m); return; }
        if (type == "feed_backlog")
        {
            string? room = m["room"]?.GetValue<string>();
            if (room != null && _live.TryGetValue(room, out var lw) && lw.Feed.Count == 0 && m["messages"] is JsonArray arr)
                foreach (var item in arr.OfType<JsonObject>()) AddFeed(room, item);
            return;
        }

        var slot = Find(m["id"]?.GetValue<string>());
        if (slot == null)
        {
            // Left over from a multiworld that's no longer connected.
            if (type is "slot" or "status" && m["id"]?.GetValue<string>() is string stale && _engine.Client.IsConnected)
                _ = _engine.Client.UntrackAsync(stale);
            return;
        }
        if (type == "slot")
        {
            // Say so (feed, notification) when a game goes into real go mode: not when it already was at startup,
            // and not when it's only thanks to pretend items.
            bool hadData = slot.HasData, wasGo = slot.GoMode == "yes";
            slot.ApplySnapshot(m);
            if (hadData && !wasGo && slot.GoMode == "yes" && !slot.HasManual && !slot.Goal
                && _live.Values.FirstOrDefault(l => l.Slots.Contains(slot)) is { } live)
            {
                Notifier.GoMode(slot, live);
                AddFeed(live.World.Id, new JsonObject
                {
                    ["kind"] = "Text",
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    ["segments"] = new JsonArray(
                        new JsonObject { ["text"] = $"{slot.SlotName} ({slot.Title}) has entered " },
                        new JsonObject { ["text"] = "GO MODE", ["color"] = "green" },
                        new JsonObject { ["text"] = "!" }),
                    ["players"] = new JsonArray(),
                });
            }
        }
        else if (type == "status") slot.ApplyStatus(m);
        else if (type == "removed") slot.Reset();
        else return;

        if (slot.Game.Length > 0 && slot.Ref.Game != slot.Game)
        {
            slot.Ref.Game = slot.Game;   // remembered for next time (art + names before connecting)
            App.SaveConfig();
        }
        Changed?.Invoke(slot);
    }

    internal void AddFeed(string? room, JsonObject m)
    {
        if (room == null || !_live.TryGetValue(room, out var live)) return;
        var segs = (m["segments"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(x => new FeedSegment(x["text"]?.GetValue<string>() ?? "", x["color"]?.GetValue<string>() ?? ""))
            .ToList();
        var players = (m["players"] as JsonArray ?? new JsonArray()).Select(x => (int)Json.Long(x)).ToList();
        double t = m["time"] is JsonValue tv && tv.TryGetValue(out double d) ? d : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var msg = new FeedMessage(m["kind"]?.GetValue<string>() ?? "Text",
            DateTimeOffset.FromUnixTimeMilliseconds((long)(t * 1000)).LocalDateTime, segs, players);
        live.AddFeed(msg);
        FeedAdded?.Invoke(live, msg);
    }

    public void RaiseChanged(SlotView? slot) => Changed?.Invoke(slot);

    /// <summary>A message from the app itself in a multiworld's feed (e.g. "the room moved").</summary>
    public void Notice(Multiworld mw, string text) => AddFeed(mw.Id, new JsonObject
    {
        ["kind"] = "Text",
        ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        ["segments"] = new JsonArray(new JsonObject { ["text"] = text, ["color"] = "blue" }),
        ["players"] = new JsonArray(),
    });

    /// <summary>Re-sorts every game's lists after a display setting changed (e.g. Hide excluded checks).</summary>
    public void RefreshViews()
    {
        foreach (var w in _live.Values)
            foreach (var s in w.Slots)
                s.ApplyOverviewGrouping();
        Changed?.Invoke(null);
    }

    /// <summary>When the engine goes away, every slot is offline until it's back.</summary>
    public void MarkAllOffline()
    {
        foreach (var w in _live.Values)
            foreach (var s in w.Slots)
                if (s.Status != "offline") { s.Status = "offline"; s.Message = ""; }
        Changed?.Invoke(null);
    }
}

/// <summary>Patches an ObservableCollection into the target order with the fewest changes, so lists keep their scroll.</summary>
public static class ListSync
{
    public static void Sync<T>(ObservableCollection<T> target, IList<T> source)
    {
        var wanted = new HashSet<T>(source);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], source[i])) continue;
            int at = -1;
            for (int j = i + 1; j < target.Count; j++)
                if (EqualityComparer<T>.Default.Equals(target[j], source[i])) { at = j; break; }
            if (at >= 0) target.Move(at, i);
            else target.Insert(i, source[i]);
        }
        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>"Check 2" before "Check 10", like Explorer.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
