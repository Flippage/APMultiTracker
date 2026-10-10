using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace APMultiTracker;

/// <summary>
/// Finds box art for an Archipelago game name. Order: the user's pick → known name differences → Twitch's public
/// box-art CDN by name (no login needed) → Twitch category search (only when signed in). Hits are cached on disk.
/// All calls happen on the UI thread.
/// </summary>
public static class BoxArt
{
    private const string Cdn = "https://static-cdn.jtvnw.net/ttv-boxart/";
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly string CachePath = Path.Combine(ConfigStore.Dir, "art-names.json");
    private static readonly Dictionary<string, Task<string?>> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _cache;

    /// <summary>Raised (UI thread) when a game's art changes, so views showing it can refresh.</summary>
    public static event Action<string>? Changed;

    /// <summary>Archipelago game names that differ from their Twitch category.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A Link to the Past"] = "The Legend of Zelda: A Link to the Past",
        ["Ocarina of Time"] = "The Legend of Zelda: Ocarina of Time",
        ["Ship of Harkinian"] = "The Legend of Zelda: Ocarina of Time",
        ["The Wind Waker"] = "The Legend of Zelda: The Wind Waker",
        ["Links Awakening DX"] = "The Legend of Zelda: Link's Awakening DX",
        ["Twilight Princess"] = "The Legend of Zelda: Twilight Princess",
        ["Majora's Mask Recompiled"] = "The Legend of Zelda: Majora's Mask",
        ["Minish Cap"] = "The Legend of Zelda: The Minish Cap",
        ["Pokemon Red and Blue"] = "Pokémon Red/Blue",
        ["Pokemon Emerald"] = "Pokémon Emerald",
        ["Pokemon Crystal"] = "Pokémon Crystal",
        ["Pokemon FireRed and LeafGreen"] = "Pokémon FireRed/LeafGreen",
        ["Pokemon Platinum"] = "Pokémon Platinum",
        ["Kingdom Hearts 2"] = "Kingdom Hearts II",
        ["Starcraft 2"] = "StarCraft II",
        ["DOOM 1993"] = "DOOM",
        ["DOOM II"] = "DOOM II: Hell on Earth",
        ["Heretic"] = "Heretic",
        ["Donkey Kong Country 3"] = "Donkey Kong Country 3: Dixie Kong's Double Trouble!",
        ["Donkey Kong Country 2"] = "Donkey Kong Country 2: Diddy's Kong Quest",
        ["Yoshi's Island"] = "Super Mario World 2: Yoshi's Island",
        ["SMZ3"] = "Super Metroid",
        ["Super Mario Land 2"] = "Super Mario Land 2: 6 Golden Coins",
        ["Spyro 2"] = "Spyro 2: Ripto's Rage!",
        ["Spyro 3"] = "Spyro: Year of the Dragon",
        ["Spyro: A Hero's Tail"] = "Spyro: A Hero's Tail",
        ["Castlevania - Circle of the Moon"] = "Castlevania: Circle of the Moon",
        ["Castlevania 64"] = "Castlevania",
        ["Sonic Adventure 2 Battle"] = "Sonic Adventure 2",
        ["Mario & Luigi Superstar Saga"] = "Mario & Luigi: Superstar Saga",
        ["Yu-Gi-Oh! 2006"] = "Yu-Gi-Oh! Ultimate Masters: World Championship Tournament 2006",
        ["Celeste 64"] = "Celeste 64: Fragments of the Mountain",
        ["Landstalker - The Treasures of King Nole"] = "Landstalker",
        ["Lufia II Ancient Cave"] = "Lufia II: Rise of the Sinistrals",
        ["Old School Runescape"] = "Old School RuneScape",
        ["Final Fantasy Mystic Quest"] = "Final Fantasy Mystic Quest",
        ["Huniepop"] = "HuniePop",
        ["Resident Evil 3 Remake"] = "Resident Evil 3",
        ["Resident Evil 2 Remake"] = "Resident Evil 2",
        ["Banjo-Tooie"] = "Banjo-Tooie",
        ["Super Mario 64"] = "Super Mario 64",
        ["SM64 Romhack"] = "Super Mario 64",
        ["Diddy Kong Racing"] = "Diddy Kong Racing",
        ["Mega Man Battle Network 3"] = "Mega Man Battle Network 3",
        ["Kirby's Dream Land 3"] = "Kirby's Dream Land 3",
        ["Metroid Prime"] = "Metroid Prime",
        ["TUNIC"] = "TUNIC",
        ["Jak and Daxter: The Precursor Legacy"] = "Jak and Daxter: The Precursor Legacy",
        ["Slay the Spire"] = "Slay the Spire",
        ["Plants vs. Zombies: Replanted"] = "Plants vs. Zombies",
        ["Gears Tactics"] = "Gears Tactics",
        ["Ori and the Blind Forest"] = "Ori and the Blind Forest",
    };

    /// <summary>Box art URL we already know (user pick or cached lookup), without any network call.</summary>
    public static string? Known(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return null;
        if (App.Config.ArtOverrides.TryGetValue(game, out var url) && url.Length > 0) return url;
        return Cache.TryGetValue(game, out var hit) && hit.Length > 0 ? hit : null;
    }

    public static Task<string?> ResolveAsync(string game)
    {
        if (string.IsNullOrWhiteSpace(game)) return Task.FromResult<string?>(null);
        if (Known(game) is string known) return Task.FromResult<string?>(known);
        if (InFlight.TryGetValue(game, out var running)) return running;
        var task = LookupAsync(game);
        InFlight[game] = task;
        return task;
    }

    public static void SetOverride(string game, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) App.Config.ArtOverrides.Remove(game);
        else App.Config.ArtOverrides[game] = url;
        App.SaveConfig();
        Changed?.Invoke(game);
        if (url == null) _ = ResolveAsync(game);
    }

    private static async Task<string?> LookupAsync(string game)
    {
        try
        {
            foreach (var name in Candidates(game))
            {
                string url = Cdn + Uri.EscapeDataString(name) + "-{width}x{height}.jpg";
                if (await ExistsAsync(url)) return Remember(game, url);
            }
            if (App.Twitch.IsSignedIn)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var hits = await App.Twitch.SearchCategoriesAsync(Aliases.GetValueOrDefault(game, game), cts.Token);
                var best = hits.FirstOrDefault(h => string.Equals(h.Name, game, StringComparison.OrdinalIgnoreCase))
                           ?? hits.FirstOrDefault();
                if (best != null && best.BoxArtUrl.Length > 0)
                    return Remember(game, best.BoxArtUrl.Replace("52x72", "{width}x{height}"));
            }
        }
        catch (Exception ex) { Log.Warn("art", $"Box art lookup failed for {game}: {ex.Message}"); }
        finally { InFlight.Remove(game); }
        return null;
    }

    private static IEnumerable<string> Candidates(string game)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Raw()
        {
            if (Aliases.TryGetValue(game, out var alias)) yield return alias;
            yield return game;
            string clean = game.Replace("™", "").Replace("®", "").Trim();
            yield return clean;
            if (clean.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) yield return clean[4..];
            else yield return "The " + clean;
            foreach (var sep in new[] { " - ", ": " })
            {
                int i = clean.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0) { yield return clean[..i]; yield return clean.Remove(i, sep.Length).Insert(i, sep == " - " ? ": " : " - "); }
            }
        }
        foreach (var c in Raw()) if (c.Length > 0 && seen.Add(c)) yield return c;
    }

    /// <summary>The CDN answers 200 for a real category and redirects to a placeholder for unknown names.</summary>
    private static async Task<bool> ExistsAsync(string templateUrl)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, templateUrl.Replace("{width}x{height}", "52x72"));
        using var resp = await Http.SendAsync(req);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    private static string Remember(string game, string url)
    {
        Cache[game] = url;
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(Cache));
        }
        catch { }
        Changed?.Invoke(game);
        return url;
    }

    private static Dictionary<string, string> Cache
    {
        get
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(CachePath))
                    _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
            }
            catch { }
            _cache = new Dictionary<string, string>(_cache ?? new(), StringComparer.OrdinalIgnoreCase);
            return _cache;
        }
    }
}
