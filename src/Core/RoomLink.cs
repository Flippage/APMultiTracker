using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace APMultiTracker;

/// <summary>What archipelago.gg (or another Archipelago web host) says about a room.</summary>
public sealed record RoomStatus(int LastPort, IReadOnlyList<RoomPlayer> Players);

/// <summary>
/// Room links like https://archipelago.gg/room/AbC123…: lets the app wake a sleeping room and find the port it's on now
/// (rooms can come back on a different port after going idle).
/// </summary>
public static class RoomLink
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Pattern = new(@"^(?:(?<scheme>https?)://)?(?<host>[A-Za-z0-9.\-]+(?::\d+)?)/room/(?<id>[A-Za-z0-9_\-]{8,})",
                                                RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Not a browser user agent, so the room page answers without its (large) log.
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"APMultiTracker/{App.VersionTag.TrimStart('v')}");
        return c;
    }

    /// <summary>Normalises a pasted room link to https://host/room/id, or null if it isn't one.</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Pattern.Match(text.Trim());
        if (!m.Success) return null;
        string scheme = m.Groups["scheme"].Success ? m.Groups["scheme"].Value.ToLowerInvariant() : "https";
        return $"{scheme}://{m.Groups["host"].Value}/room/{m.Groups["id"].Value}";
    }

    private static (string Base, string Host, string Id)? Split(string link)
    {
        var m = Pattern.Match(link);
        if (!m.Success) return null;
        string scheme = m.Groups["scheme"].Success ? m.Groups["scheme"].Value : "https";
        string host = m.Groups["host"].Value;
        return ($"{scheme}://{host}", host.Split(':')[0], m.Groups["id"].Value);
    }

    /// <summary>The address to connect to: the room's host with the given port.</summary>
    public static string? ServerFor(string link, int port) => Split(link) is { } p && port > 0 ? $"{p.Host}:{port}" : null;

    public static async Task<RoomStatus?> GetStatusAsync(string link)
    {
        if (Split(link) is not { } p) return null;
        try
        {
            using var resp = await Http.GetAsync($"{p.Base}/api/room_status/{p.Id}");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            int port = r.TryGetProperty("last_port", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : 0;
            var players = new List<RoomPlayer>();
            if (r.TryGetProperty("players", out var pl) && pl.ValueKind == JsonValueKind.Array)
            {
                int slot = 1;
                foreach (var entry in pl.EnumerateArray())
                {
                    // [name, game] in slot order
                    if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2)
                        players.Add(new RoomPlayer(slot, entry[0].GetString() ?? "", entry[1].GetString() ?? ""));
                    slot++;
                }
            }
            return new RoomStatus(port, players);
        }
        catch (Exception ex)
        {
            Log.Warn("room", $"Couldn't read room status: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opening the room page is what starts a sleeping room on archipelago.gg.</summary>
    public static async Task WakeAsync(string link)
    {
        if (Split(link) is not { } p) return;
        try { using var _ = await Http.GetAsync($"{p.Base}/room/{p.Id}"); }
        catch (Exception ex) { Log.Warn("room", $"Couldn't open the room page: {ex.Message}"); }
    }

    /// <summary>Wakes the room if asked, then waits (up to ~30 s) for it to report a port.</summary>
    public static async Task<int> FindPortAsync(string link, bool wake)
    {
        if (wake) await WakeAsync(link);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var status = await GetStatusAsync(link);
            if (status == null) return 0;
            if (status.LastPort > 0) return status.LastPort;
            await Task.Delay(3000);   // still starting up
        }
        return 0;
    }
}

/// <summary>
/// Follows archipelago.gg rooms that restart on a new port: when a multiworld with a room link can't connect, wake
/// the room, read its current port and, if it moved, update the multiworld and reconnect.
/// </summary>
public sealed class RoomWatcher
{
    private readonly TrackerStore _store;
    private readonly Dictionary<string, DateTime> _lastCheck = new();

    public RoomWatcher(TrackerStore store)
    {
        _store = store;
        store.Changed += slot => { if (slot != null) _ = OnSlotChangedAsync(slot); };
    }

    private async Task OnSlotChangedAsync(SlotView slot)
    {
        if (slot.Status is not ("reconnecting" or "error")) return;
        var live = _store.Live.FirstOrDefault(l => l.Slots.Contains(slot));
        if (live == null || string.IsNullOrEmpty(live.World.RoomLink)) return;
        await CheckAsync(live.World, wake: true);
    }

    /// <summary>Checks one multiworld's room (at most every 45 s). True if its server address changed.</summary>
    public async Task<bool> CheckAsync(Multiworld mw, bool wake)
    {
        if (string.IsNullOrEmpty(mw.RoomLink)) return false;
        if (wake)
        {
            // Waking happens when a connection fails, which can be every few seconds: once per 45 s is plenty.
            var now = DateTime.UtcNow;
            if (_lastCheck.TryGetValue(mw.Id, out var last) && now - last < TimeSpan.FromSeconds(45)) return false;
            _lastCheck[mw.Id] = now;
        }

        int port = await RoomLink.FindPortAsync(mw.RoomLink, wake);
        string? server = port > 0 ? RoomLink.ServerFor(mw.RoomLink, port) : null;
        if (server == null || string.Equals(server, mw.Server.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        Log.Info("room", $"{mw.Name}: room moved from {mw.Server} to {server}");
        string old = mw.Server;
        mw.Server = server;
        App.SaveConfig();
        _store.Notice(mw, $"The room moved from {old} to {server}. Reconnecting…");
        _store.Reload(mw);
        return true;
    }

    /// <summary>On startup: bring every connected multiworld's port up to date before it starts failing.</summary>
    public async Task CheckAllAsync()
    {
        foreach (var live in _store.Live.ToList())
            await CheckAsync(live.World, wake: false);
    }
}
