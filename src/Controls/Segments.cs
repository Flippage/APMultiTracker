using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>Archipelago's text colours (and the engine's item/location/player kinds) on the dark theme.</summary>
public static class SegmentColors
{
    private static SolidColorBrush F(uint rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    private static readonly Brush Self = F(0xF472B6);
    private static readonly Brush Useful = F(0x818CF8);
    private static readonly Brush Filler = F(0x5EEAD4);

    public static Brush? Get(string? name) => (name ?? "").ToLowerInvariant() switch
    {
        "green" or "location" => Brushes2.InLogic,
        "red" or "salmon" or "item_trap" => Brushes2.Danger,
        "yellow" or "player" => Brushes2.Glitched,
        "player_self" or "magenta" => Self,
        "blue" or "slateblue" or "cyan" or "entrance" => Brushes2.Accent,
        "plum" or "item_prog" => Brushes2.Hint,
        "item_useful" => Useful,
        "item" => Filler,
        "white" => Brushes2.Text,
        _ => null,
    };

    public static bool IsBold(string? name) => name is "player_self" or "item_prog";
}

/// <summary>&lt;TextBlock local:Segments.Source="{Binding Segments}"/&gt; renders coloured feed segments as runs.</summary>
public static class Segments
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(IReadOnlyList<FeedSegment>), typeof(Segments), new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<FeedSegment>? GetSource(DependencyObject o) => (IReadOnlyList<FeedSegment>?)o.GetValue(SourceProperty);
    public static void SetSource(DependencyObject o, IReadOnlyList<FeedSegment>? value) => o.SetValue(SourceProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<FeedSegment> segs) return;
        foreach (var s in segs)
        {
            var run = new Run(s.Text);
            if (SegmentColors.Get(s.Color) is Brush b) run.Foreground = b;
            if (SegmentColors.IsBold(s.Color)) run.FontWeight = FontWeights.SemiBold;
            tb.Inlines.Add(run);
        }
    }
}
