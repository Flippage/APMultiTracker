using System;
using System.Collections.Generic;

namespace APMultiTracker;

public sealed class AppConfig
{
    /// <summary>Archipelago install folder (contains ArchipelagoLauncher.exe). Empty = auto-detect.</summary>
    public string ArchipelagoDir { get; set; } = "";
    public int EnginePort { get; set; } = 38291;

    public List<Multiworld> Multiworlds { get; set; } = new();
    public string? ActiveMultiworldId { get; set; }

    /// <summary>Multiworlds kept connected in the background (reconnected on startup).</summary>
    public List<string> ConnectedMultiworldIds { get; set; } = new();

    /// <summary>Per multiworld: the focused slot (SlotRef.Id). Missing = the all-games overview.</summary>
    public Dictionary<string, string> FocusByWorld { get; set; } = new();

    // ---- feed view
    public bool FeedOnlyMine { get; set; }
    public string FeedFilter { get; set; } = "All";

    /// <summary>Archipelago game name → box art URL picked by the user.</summary>
    public Dictionary<string, string> ArtOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- check list view
    public CheckFilter Filter { get; set; } = CheckFilter.InLogic;
    public bool GroupByRegion { get; set; } = true;
    public bool ShowRegionNames { get; set; } = true;

    // ---- app
    public bool StartWithWindows { get; set; }
    public bool AlwaysOnTop { get; set; }
}

public enum CheckFilter { InLogic, Reachable, All, Hinted, Hidden }

/// <summary>A saved room: where to connect and which slots to track.</summary>
public sealed class Multiworld
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>host:port, e.g. archipelago.gg:38281</summary>
    public string Server { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Folder with the players' YAMLs (only needed by games that can't rebuild without one).</summary>
    public string YamlFolder { get; set; } = "";
    public List<SlotRef> Slots { get; set; } = new();
}

public sealed class SlotRef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Learned from the server once connected, so art and names show before connecting.</summary>
    public string Game { get; set; } = "";
}

public sealed class TokenSet
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ProfileImageUrl { get; set; } = "";
}

public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

public sealed class CategoryRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BoxArtUrl { get; set; } = "";
}

public sealed record RoomPlayer(int Slot, string Name, string Game);
