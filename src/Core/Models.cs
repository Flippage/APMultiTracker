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
    /// <summary>Show the feed docked in the Tracker, on the "Right" or at the "Bottom".</summary>
    public bool ShowFeedInTracker { get; set; }
    public string FeedDock { get; set; } = "Right";
    public double FeedDockWidth { get; set; } = 400;
    public double FeedDockHeight { get; set; } = 250;

    /// <summary>Archipelago game name → box art URL picked by the user.</summary>
    public Dictionary<string, string> ArtOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- check list view
    public CheckFilter Filter { get; set; } = CheckFilter.InLogic;
    public bool GroupByRegion { get; set; } = true;
    public bool ShowRegionNames { get; set; } = true;
    public bool OverviewGroupByRegion { get; set; }
    public double ExplainHeight { get; set; } = 200;
    /// <summary>Per slot (SlotRef.Id): region groups collapsed in the check list.</summary>
    public Dictionary<string, List<string>> CollapsedRegions { get; set; } = new();

    // ---- updates
    public bool AutoDownloadUpdates { get; set; } = true;
    public bool InstallUpdatesOnLaunch { get; set; }
    public PendingUpdate? PendingUpdate { get; set; }

    // ---- remembered between runs
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string LastPage { get; set; } = "Tracker";
    public bool ShowItems { get; set; }
    public bool ShowExplain { get; set; }

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
    /// <summary>The room's page (https://archipelago.gg/room/…), if known: lets the app follow the room to a new port.</summary>
    public string RoomLink { get; set; } = "";
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

/// <summary>An update that's been downloaded and verified, waiting to be installed.</summary>
public sealed class PendingUpdate
{
    public string Version { get; set; } = "";
    public string ExePath { get; set; } = "";
}

public sealed record DeviceCode(string Code, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

public sealed class CategoryRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BoxArtUrl { get; set; } = "";
}

public sealed record RoomPlayer(int Slot, string Name, string Game);
