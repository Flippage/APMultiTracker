using System.Windows;
using System.Windows.Controls;

namespace APMultiTracker;

/// <summary>The Feed page: the active multiworld's messages, full size.</summary>
public partial class FeedPage : UserControl
{
    public FeedPage()
    {
        InitializeComponent();
        Panel.CountChanged += UpdateSub;
    }

    private void UpdateSub()
    {
        var w = App.Tracker.ActiveLive;
        SubText.Text = w == null ? "No multiworld active"
                     : $"{w.World.Name} · {w.World.Server} · {w.Feed.Count} message{(w.Feed.Count == 1 ? "" : "s")}";
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Tracker.ActiveLive?.Feed.Clear();
        Panel.Rebuild();
    }

    public string AsSlotName => Panel.AsSlotName;

    public void SelfTest() => Panel.SelfTest();
}
