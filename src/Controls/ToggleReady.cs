using System.Windows;
using System.Windows.Controls;

namespace APMultiTracker;

/// <summary>
/// Marks a toggle as "ready" once it's on screen. Before that, the Toggle style snaps straight to its state;
/// after, changes animate. Stops freshly created toggles (e.g. a rebuilt list) from playing an off→on flick.
/// </summary>
public static class ToggleReady
{
    public static readonly DependencyProperty IsReadyProperty = DependencyProperty.RegisterAttached(
        "IsReady", typeof(bool), typeof(ToggleReady), new PropertyMetadata(false));

    public static bool GetIsReady(DependencyObject d) => (bool)d.GetValue(IsReadyProperty);
    public static void SetIsReady(DependencyObject d, bool v) => d.SetValue(IsReadyProperty, v);

    /// <summary>Set from the Toggle style. Hooks this toggle's own Loaded event (WPF only raises Loaded on
    /// elements that have an instance handler, so a class-level handler isn't enough).</summary>
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(bool), typeof(ToggleReady), new PropertyMetadata(false, OnTrackChanged));

    public static bool GetTrack(DependencyObject d) => (bool)d.GetValue(TrackProperty);
    public static void SetTrack(DependencyObject d, bool v) => d.SetValue(TrackProperty, v);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        if (fe.IsLoaded) { SetIsReady(fe, true); return; }
        fe.Loaded += (_, _) => SetIsReady(fe, true);
    }
}
