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
                new Multiworld { Name = "Async with the crew", Server = "archipelago.gg:51234",
                    Slots = { new SlotRef { Name = "FlippMario", Game = "Super Mario 64" }, new SlotRef { Name = "FlippBanjo", Game = "Banjo-Tooie" } } },
            },
            ActiveMultiworldId = mw.Id,
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
                ["checked"] = g.Checked, ["total"] = g.Total, ["in_logic"] = inLogic,
                ["glitched"] = locs.Count(l => l!["status"]!.GetValue<string>() == "glitched"),
                ["hinted"] = locs.Count(l => l!["hint"] != null), ["ignored"] = 1,
                ["go_mode"] = g.Id == "d4" ? "yes" : "no", ["locations"] = locs, ["items"] = items,
                ["errors"] = new JsonArray(), ["warning"] = "",
            });
        }
        store.SetFocus(null);
        store.RaiseChanged(null);
    }

    /// <summary>Renders each page to PNG for the README.</summary>
    public static async Task ScreenshotsAsync(MainWindow w, string dir)
    {
        Directory.CreateDirectory(dir);
        w.Width = 1400; w.Height = 900;
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
        w.NavMultiworlds.IsChecked = true;
        await Shot("multiworlds");
        w.OpenMultiworldEditor(App.Config.Multiworlds[0]);
        await Shot("edit-multiworld");
        w.CloseMultiworldEditor();
        w.GoToSettings();
        await Shot("settings");
    }
}
