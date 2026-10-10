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
public sealed record CheckItem(long Id, string Name, string Region, string Status, bool Excluded, string? Hint)
{
    public bool IsHidden => Status == "ignored";
    public bool IsInLogic => Status == "in_logic";
    public bool IsGlitched => Status == "glitched";
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    public int Rank => Status switch { "in_logic" => 0, "glitched" => 1, "out_of_logic" => 2, _ => 3 };

    public Brush DotBrush => Status switch
    {
        "in_logic" => Brushes2.InLogic,
        "glitched" => Brushes2.Glitched,
        _ => Brushes2.OutLogic,
    };
    public Brush NameBrush => Status switch
    {
        "in_logic" => Brushes2.Text,
        "glitched" => Brushes2.GlitchedText,
        _ => Brushes2.Dim,
    };
    public string StatusTip => Status switch
    {
        "in_logic" => "In logic",
        "glitched" => "Reachable with glitches / tricks",
        "ignored" => "Hidden",
        _ => "Out of logic",
    };
}

public sealed record ItemCount(string Name, int Count, bool Progression);

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
}

/// <summary>Live state of one tracked slot, bound by the UI.</summary>
public sealed class SlotView : INotifyPropertyChanged
{
    public SlotRef Ref { get; }
    public int Index { get; set; }

    public SlotView(SlotRef slot) { Ref = slot; _game = slot.Game; }

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

    public int Checked { get; private set; }
    public int Total { get; private set; }
    public int InLogic { get; private set; }
    public int Glitched { get; private set; }
    public int Hinted { get; private set; }
    public int Hidden { get; private set; }
    public string GoMode { get; private set; } = "no";
    public IReadOnlyList<CheckItem> Locations { get; private set; } = Array.Empty<CheckItem>();
    public IReadOnlyList<ItemCount> Items { get; private set; } = Array.Empty<ItemCount>();
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public bool HasData { get; private set; }

    /// <summary>In-logic (then glitched) checks for the all-games overview, kept in place between updates.</summary>
    public ObservableCollection<CheckItem> Overview { get; } = new();

    public bool IsTracking => Status == "tracking";
    public bool IsBusy => Status is "connecting" or "generating" or "reconnecting";
    public bool IsError => Status == "error";
    public double Progress => Total > 0 ? (double)Checked / Total : 0;
    public string CheckedText => Total > 0 ? $"{Checked}/{Total}" : "–";
    public string PercentText => Total > 0 ? $"{Progress * 100:0}%" : "";
    public string InLogicText => HasData ? InLogic.ToString() : "–";
    public bool IsDone => HasData && Total > 0 && Checked >= Total;
    public bool IsGoMode => GoMode == "yes";
    public string GoModeText => GoMode switch { "yes" => "GO MODE", "glitched" => "GO (GLITCHED)", _ => "" };

    public string StatusText => Status switch
    {
        "tracking" => IsDone ? "All checks done" : $"{InLogic} in logic",
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
        Checked = (int)Json.Long(m["checked"]);
        Total = (int)Json.Long(m["total"]);
        InLogic = (int)Json.Long(m["in_logic"]);
        Glitched = (int)Json.Long(m["glitched"]);
        Hinted = (int)Json.Long(m["hinted"]);
        Hidden = (int)Json.Long(m["ignored"]);
        GoMode = m["go_mode"]?.GetValue<string>() ?? "no";
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
                    o["hint"]?.GetValue<string>()));
            }
        Locations = locs;

        var items = new List<ItemCount>();
        if (m["items"] is JsonArray its)
            foreach (var n in its)
                if (n is JsonObject o)
                    items.Add(new ItemCount(o["name"]?.GetValue<string>() ?? "?", (int)Json.Long(o["count"], 1), o["prog"]?.GetValue<bool>() ?? false));
        Items = items.OrderByDescending(i => i.Progression).ThenBy(i => i.Name, NaturalComparer.Instance).ToList();

        Errors = (m["errors"] as JsonArray)?.Select(e => e?.GetValue<string>() ?? "").Where(e => e.Length > 0).ToList()
                 ?? new List<string>();
        HasData = true;

        var overview = locs.Where(l => l.Status is "in_logic" or "glitched")
            .OrderBy(l => l.HasHint ? 0 : 1).ThenBy(l => l.Rank).ThenBy(l => l.Name, NaturalComparer.Instance).ToList();
        ListSync.Sync(Overview, overview);
        NotifyAll();
    }

    public void Reset()
    {
        Status = "offline";
        Message = "";
        HasData = false;
        Locations = Array.Empty<CheckItem>();
        Items = Array.Empty<ItemCount>();
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

/// <summary>Owns the active multiworld's slots and keeps the engine tracking exactly those.</summary>
public sealed class TrackerStore
{
    private readonly EngineHost _engine;
    public ObservableCollection<SlotView> Slots { get; } = new();
    public Multiworld? Active { get; private set; }

    /// <summary>Slot data changed (any slot). The argument is the slot, or null when the whole list changed.</summary>
    public event Action<SlotView?>? Changed;

    public TrackerStore(EngineHost engine)
    {
        _engine = engine;
        engine.Client.Message += OnMessage;
        engine.Connected += () => _ = TrackAllAsync();
        BoxArt.Changed += game => { foreach (var s in Slots) if (string.Equals(s.Game, game, StringComparison.OrdinalIgnoreCase)) s.LoadArt(); };
    }

    public SlotView? Find(string? id) => id == null ? null : Slots.FirstOrDefault(s => s.Ref.Id == id);

    /// <summary>The game shown on its own, or null for the all-games overview.</summary>
    public SlotView? Focused => Find(App.Config.FocusedSlotId);
    public event Action? FocusChanged;

    public void SetFocus(SlotView? slot)
    {
        if (App.Config.FocusedSlotId == slot?.Ref.Id) return;
        App.Config.FocusedSlotId = slot?.Ref.Id;
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

    public void Activate(Multiworld? mw)
    {
        var old = Slots.ToList();
        Active = mw;
        App.Config.ActiveMultiworldId = mw?.Id;
        App.SaveConfig();
        Slots.Clear();
        if (mw != null)
        {
            int i = 0;
            foreach (var r in mw.Slots.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
            {
                var v = new SlotView(r) { Index = i++ };
                v.LoadArt();
                Slots.Add(v);
            }
        }
        if (App.Config.FocusedSlotId != null && Find(App.Config.FocusedSlotId) == null) App.Config.FocusedSlotId = null;
        Changed?.Invoke(null);
        FocusChanged?.Invoke();
        _ = SyncEngineAsync(old);
    }

    /// <summary>Re-apply after the multiworld was edited (server, slots, folder…).</summary>
    public void Reload()
    {
        if (Active == null) return;
        var mw = App.Config.Multiworlds.FirstOrDefault(m => m.Id == Active.Id);
        Activate(mw);
    }

    private async System.Threading.Tasks.Task SyncEngineAsync(List<SlotView> old)
    {
        if (!_engine.Client.IsConnected) return;
        foreach (var o in old)
            if (Find(o.Ref.Id) == null) await _engine.Client.UntrackAsync(o.Ref.Id);
        await TrackAllAsync();
    }

    private async System.Threading.Tasks.Task TrackAllAsync()
    {
        if (Active == null) return;
        foreach (var s in Slots.ToList())
        {
            if (s.Status == "offline") s.Status = "connecting";
            await _engine.Client.TrackAsync(s.Ref.Id, Active, s.Ref);
        }
    }

    public System.Threading.Tasks.Task RefreshAsync(SlotView? slot) => _engine.Client.RefreshAsync(slot?.Ref.Id);

    public System.Threading.Tasks.Task SetHiddenAsync(SlotView slot, IEnumerable<long> ids, bool hidden) =>
        _engine.Client.SetIgnoredAsync(slot.Ref.Id, ids.ToArray(), hidden);

    private void OnMessage(JsonObject m)
    {
        string type = m["type"]?.GetValue<string>() ?? "";
        if (type == "error") { Log.Warn("engine", m["message"]?.GetValue<string>() ?? ""); return; }
        var slot = Find(m["id"]?.GetValue<string>());
        if (slot == null)
        {
            // Left over from a multiworld we're no longer showing.
            if (type is "slot" or "status" && m["id"]?.GetValue<string>() is string stale && _engine.Client.IsConnected)
                _ = _engine.Client.UntrackAsync(stale);
            return;
        }
        if (type == "slot") slot.ApplySnapshot(m);
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

    public void RaiseChanged(SlotView? slot) => Changed?.Invoke(slot);

    /// <summary>When the engine goes away, every slot is offline until it's back.</summary>
    public void MarkAllOffline()
    {
        foreach (var s in Slots) if (s.Status != "offline") { s.Status = "offline"; s.Message = ""; }
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
