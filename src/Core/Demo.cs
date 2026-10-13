using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace APMultiTracker;

/// <summary>Sample data for --selftest (CI) and --screenshots (README). Never touches the user's settings.</summary>
public static class Demo
{
    private static readonly (string Id, string Slot, string Game, string[] Regions, string[] Checks, int Total, int Checked)[] Games =
    {
        ("d1", "FlippKnight", "Hollow Knight",
            new[] { "Dirtmouth", "Forgotten Crossroads", "Greenpath", "Fungal Wastes", "City of Tears" },
            new[] { "Sly_1", "Sly_2", "Sly_3", "Iselda_1", "Iselda_2", "Mask_Shard-Grub_Mother", "Vengeful_Spirit", "Grub-Crossroads_Acid",
                    "Geo_Rock-Crossroads_Above_Lever", "Mothwing_Cloak", "Whispering_Root-Greenpath", "Charm_Notch-Shrumal_Ogres",
                    "Grub-Fungal_Spore_Shroom", "Mantis_Claw", "Rancid_Egg-Crossroads_Tram", "Lifeblood_Cocoon-Greenpath" }, 203, 61),
        ("d2", "FlippLink", "Ocarina of Time",
            new[] { "Kokiri Forest", "Hyrule Field", "Kakariko Village", "Death Mountain Trail", "Lost Woods" },
            new[] { "KF Midos Top Left Chest", "KF Kokiri Sword Chest", "HF Ocarina of Time Item", "Kak Anju as Child",
                    "DMT Freestanding PoH", "LW Skull Kid", "LW Ocarina Memory Game", "Kak Windmill Freestanding PoH",
                    "HF Southeast Grotto Chest", "Kak Man on Roof" }, 412, 97),
        ("d3", "FlippFarmer", "Stardew Valley",
            new[] { "Town", "Farm", "Beach", "Mountain", "Community Center" },
            new[] { "Help Wanted: Item Delivery 1", "Help Wanted: Item Delivery 2", "Harvest Parsnip", "Copper Hoe Upgrade",
                    "Spring Crops Bundle", "Fish Shop: Bamboo Pole", "Level 2 Fishing", "Mine Level 10", "Purchase Large Pack" }, 472, 140),
        ("d4", "FlippTime", "Timespinner",
            new[] { "Lake desolation", "Tutorial", "Library", "Varndagroth tower left", "Military Fortress" },
            new[] { "Lake Desolation: Starter chest 2", "Lake Desolation: Starter chest 3", "Library: Basement",
                    "Library: Consolation", "Military Fortress: Bomber chest", "Varndagroth Tower (Left): Elevator chest" }, 180, 72),
    };

    public static AppConfig BuildConfig()
    {
        var mw = new Multiworld
        {
            Id = "demo",
            Name = "Friday Multiworld",
            Server = "archipelago.gg:38281",
            Slots = Games.Select(g => new SlotRef { Id = g.Id, Name = g.Slot, Game = g.Game }).ToList(),
        };
        return new AppConfig
        {
            Multiworlds =
            {
                mw,
                new Multiworld { Id = "demo2", Name = "Async with the crew", Server = "archipelago.gg:51234",
                    Slots = { new SlotRef { Name = "FlippMario", Game = "Super Mario 64" }, new SlotRef { Name = "FlippBanjo", Game = "Banjo-Tooie" } } },
            },
            ActiveMultiworldId = mw.Id,
            ConnectedMultiworldIds = { mw.Id, "demo2" },
            FocusByWorld = { [mw.Id] = "d1" },   // start on a game, like a real session (All games not laid out yet)
        };
    }

    public static void Feed(TrackerStore store)
    {
        var rng = new Random(7);
        foreach (var g in Games)
        {
            var slot = store.Find(g.Id);
            if (slot == null) continue;
            var locs = new JsonArray();
            int n = 0;
            foreach (var name in g.Checks)
            {
                string status = n < g.Checks.Length * 0.6 ? "in_logic" : n % 3 == 0 ? "glitched" : "out_of_logic";
                if (g.Game == "Timespinner" && n < 2) status = "in_logic";
                // Check shot: Stardew Valley in BK (nothing in logic)
                if (g.Id == "d3" && status == "in_logic" && Environment.GetEnvironmentVariable("APMT_SHOT_BK") == "1") status = "out_of_logic";
                string? hint = n == 2 ? "Mantis Claw → FlippLink" : n == 5 && g.Id == "d2" ? "Progressive Hookshot" : null;
                locs.Add(new JsonObject
                {
                    ["id"] = 1000 + n, ["name"] = name, ["region"] = g.Regions[n % g.Regions.Length],
                    ["status"] = status, ["excluded"] = false, ["hint"] = hint,
                });
                n++;
            }
            for (int i = 0; i < 40; i++)
                locs.Add(new JsonObject
                {
                    ["id"] = 5000 + i, ["name"] = $"{g.Regions[i % g.Regions.Length]} Check {i + 1}",
                    ["region"] = g.Regions[i % g.Regions.Length], ["status"] = "out_of_logic", ["excluded"] = false, ["hint"] = null,
                });
            locs.Add(new JsonObject { ["id"] = 9000, ["name"] = "Hidden example", ["region"] = g.Regions[0], ["status"] = "ignored", ["excluded"] = false, ["hint"] = null });
            var items = new JsonArray();
            foreach (var it in new[] { "Progressive Sword", "Bombs", "Hookshot", "Bottle", "Rupees (50)", "Heart Container" })
                items.Add(new JsonObject { ["name"] = it, ["count"] = rng.Next(1, 4), ["prog"] = it.Length < 9 });
            int inLogic = locs.Count(l => l!["status"]!.GetValue<string>() == "in_logic");
            slot.ApplySnapshot(new JsonObject
            {
                ["type"] = "slot", ["id"] = g.Id, ["status"] = "tracking", ["message"] = "", ["game"] = g.Game, ["slot_name"] = g.Slot,
                ["slot"] = Array.IndexOf(Games, g) + 1,
                ["checked"] = g.Checked, ["total"] = g.Total, ["in_logic"] = inLogic,
                ["glitched"] = locs.Count(l => l!["status"]!.GetValue<string>() == "glitched"),
                ["hinted"] = locs.Count(l => l!["hint"] != null), ["ignored"] = 1,
                ["go_mode"] = g.Id == "d4" ? "yes" : "no", ["goal"] = g.Id == "d2" && Environment.GetEnvironmentVariable("APMT_SHOT_GOAL") is "1" or "manual",
                ["auto_release"] = Environment.GetEnvironmentVariable("APMT_SHOT_GOAL") != "manual", ["locations"] = locs, ["items"] = items,
                ["errors"] = new JsonArray(), ["warning"] = "",
            });
        }
        store.SetFocus(null);
        store.RaiseChanged(null);
        FeedDemo(store);
    }

    private static JsonObject S(string text, string color = "") => new() { ["text"] = text, ["color"] = color };

    private static void FeedDemo(TrackerStore store)
    {
        var start = DateTimeOffset.Now.AddMinutes(-14).ToUnixTimeSeconds();
        int n = 0;
        void Add(string kind, int[] players, params JsonObject[] segs)
        {
            var arr = new JsonArray();
            foreach (var p in players) arr.Add(p);
            store.AddFeed("demo", new JsonObject
            {
                ["type"] = "feed", ["room"] = "demo", ["kind"] = kind, ["time"] = start + 70 * n++,
                ["segments"] = new JsonArray(segs), ["players"] = arr,
            });
        }
        Add("Join", new[] { 5 }, S("Kiwi (Team #1) playing A Link to the Past has joined. Client(0.6.7), ['AP']."));
        Add("ItemSend", new[] { 1, 2 }, S("FlippKnight", "player_self"), S(" sent "), S("Progressive Hookshot", "item_prog"), S(" to "), S("FlippLink", "player_self"), S(" ("), S("Sly_2", "location"), S(")"));
        Add("ItemSend", new[] { 5, 3 }, S("Kiwi", "player"), S(" sent "), S("Copper Pan", "item_useful"), S(" to "), S("FlippFarmer", "player_self"), S(" ("), S("Desert Palace - Big Chest", "location"), S(")"));
        Add("ItemSend", new[] { 4 }, S("FlippTime", "player_self"), S(" found their "), S("Security Keycard C", "item_prog"), S(" ("), S("Library: Backer room chest 5", "location"), S(")"));
        Add("Chat", new[] { 5 }, S("Kiwi: anyone have my hammer yet?"));
        Add("ItemSend", new[] { 2, 5 }, S("FlippLink", "player_self"), S(" sent "), S("Hammer", "item_prog"), S(" to "), S("Kiwi", "player"), S(" ("), S("KF Midos Top Left Chest", "location"), S(")"));
        Add("Chat", new[] { 1 }, S("FlippKnight: !hint Mantis Claw"));
        Add("Hint", new[] { 1, 5 }, S("[Hint]: "), S("FlippKnight", "player_self"), S("'s "), S("Mantis Claw", "item_prog"), S(" is at "), S("Kakariko Well - Bottom", "location"), S(" in "), S("Kiwi", "player"), S("'s World. "), S("(unspecified)"));
        Add("ItemSend", new[] { 5, 4 }, S("Kiwi", "player"), S(" sent "), S("Sand", "item"), S(" to "), S("FlippTime", "player_self"), S(" ("), S("Ice Palace - Map Chest", "location"), S(")"));
        Add("ItemSend", new[] { 3, 1 }, S("FlippFarmer", "player_self"), S(" sent "), S("Vessel_Fragment", "item_useful"), S(" to "), S("FlippKnight", "player_self"), S(" ("), S("Harvest Parsnip", "location"), S(")"));
        Add("Goal", new[] { 5 }, S("Kiwi (Team #1) has completed their goal."));
        Add("ItemSend", new[] { 1, 5 }, S("FlippKnight", "player_self"), S(" sent "), S("Spike Trap", "item_trap"), S(" to "), S("Kiwi", "player"), S(" ("), S("Grub-Crossroads_Acid", "location"), S(")"));
    }

    /// <summary>Renders each page to PNG for the README.</summary>
    public static async Task ScreenshotsAsync(MainWindow w, string dir)
    {
        Directory.CreateDirectory(dir);
        w.Width = 1400; w.Height = 940;
        Feed(App.Tracker);
        await Task.Delay(2500);   // box art
        async Task Shot(string name)
        {
            w.UpdateLayout();
            await Task.Delay(600);
            await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Native.CaptureClient(w)));
            using var f = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        w.GoToTracker();
        App.Tracker.SetFocus(null);
        await Shot("overview");
        w.TrackerView.SetOverviewGrouping(true);
        await Shot("overview-grouped");
        w.TrackerView.SetOverviewGrouping(false);

        App.Tracker.SetFocus(App.Tracker.Slots[0]);
        await Shot("focused");
        App.Tracker.SetFocus(App.Tracker.Slots[1]);
        await Shot("focused-2");
        App.Tracker.SetFocus(App.Tracker.Slots[0]);
        static JsonArray L(params (string Text, string Color)[] segs) =>
            new(segs.Select(s => (JsonNode)new JsonObject { ["text"] = s.Text, ["color"] = s.Color }).ToArray());
        w.TrackerView.RenderExplain("path", "Mantis_Claw", new JsonArray(
            L(("using ", ""), ("Glitches:", "yellow")),
            L(("Menu -> King's Pass", "blue")),
            L(("Dirtmouth -> Crossroads_01", "blue"), (":\n    ", ""), ("Crossroads access: ", ""), ("True", "green")),
            L(("Greenpath -> Fungal_Wastes", "blue"), (":\n    needs ", ""), ("Mothwing Cloak", "item"), (" or ", ""),
              ("Mantis Claw", "item"), (" (", ""), ("missing", "red"), (")", "")),
            L(("->", ""), ("Mantis_Claw", "green"))));
        await Shot("explain");
        w.NavFeed.IsChecked = true;
        await Shot("feed");

        // The feed docked in the Tracker: right, then bottom.
        w.GoToTracker();
        foreach (var dock in new[] { "Right", "Bottom" })
        {
            App.Config.ShowFeedInTracker = true;
            App.Config.FeedDock = dock;
            w.TrackerView.ApplyFeedDock();
            App.Tracker.SetFocus(null);
            await Shot($"tracker-feed-{dock.ToLowerInvariant()}");
            App.Tracker.SetFocus(App.Tracker.Slots[0]);
            await Shot($"tracker-feed-{dock.ToLowerInvariant()}-focused");
        }
        App.Config.ShowFeedInTracker = false;
        App.Config.FeedDock = "Right";
        w.TrackerView.ApplyFeedDock();
        App.Tracker.SetFocus(App.Tracker.Slots[0]);
        w.NavMultiworlds.IsChecked = true;
        await Shot("multiworlds");
        w.OpenMultiworldEditor(App.Config.Multiworlds[0]);
        await Shot("edit-multiworld");
        w.CloseMultiworldEditor();
        w.GoToSettings();
        await Shot("settings");
        w.OpenPatchNotes();
        await Shot("patch-notes");
        w.ClosePatchNotes();
        if (Environment.GetEnvironmentVariable("APMT_SHOT_FEED") == "1")
        {
            // Checks the docked feed's limits at a typical window size (not used for the README).
            w.GoToTracker();
            w.Width = 1486; w.Height = 933;
            App.Config.ShowFeedInTracker = true;
            foreach (var (dock, size, name) in new[] { ("Right", 400.0, "feedcheck-right-default"), ("Right", 2000.0, "feedcheck-right-dragged-wide"),
                                                      ("Bottom", 2000.0, "feedcheck-bottom-dragged-tall") })
            {
                App.Config.FeedDock = dock;
                if (dock == "Right") App.Config.FeedDockWidth = size; else App.Config.FeedDockHeight = size;
                w.TrackerView.ApplyFeedDock();
                App.Tracker.SetFocus(null);
                await Shot(name);
            }
            App.Config.FeedDock = "Right"; App.Config.FeedDockWidth = 400;
            w.TrackerView.ApplyFeedDock();
            App.Tracker.SetFocus(App.Tracker.Slots[3]);
            await Shot("feedcheck-right-focused");
            App.Config.ShowFeedInTracker = false;
            w.TrackerView.ApplyFeedDock();
            w.Width = 1400; w.Height = 940;
        }
        if (Environment.GetEnvironmentVariable("APMT_SHOT_SMALL") == "1")
        {
            // Checks the layout at a smaller window size (not used for the README).
            w.GoToTracker();
            w.Width = 1325; w.Height = 893;
            App.Tracker.SetFocus(App.Tracker.Slots[0]);
            w.TrackerView.RenderExplain("path", "Mantis_Claw", new JsonArray(
                new JsonArray(new JsonObject { ["text"] = "Menu -> King's Pass", ["color"] = "blue" }),
                new JsonArray(new JsonObject { ["text"] = "->", ["color"] = "" }, new JsonObject { ["text"] = "Mantis_Claw", ["color"] = "green" })));
            await Shot("small-focused-explain");
            App.Tracker.SetFocus(null);
            w.GoToTracker();
            await Shot("small-overview");
        }
    }
}
