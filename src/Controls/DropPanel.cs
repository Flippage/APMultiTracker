using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>
/// A dropdown list drawn inside the page (not a separate popup window): it sits just under <see cref="Target"/>,
/// moves with the window, never spills outside its panel, and closes on any click outside it and its target,
/// when the window moves or loses focus, or when <see cref="IsOpen"/> is set to false.
/// Put it in a Grid that also contains the target (it's positioned relative to that Grid).
/// </summary>
public sealed class DropPanel : Border
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(FrameworkElement), typeof(DropPanel));

    /// <summary>What it drops down from (e.g. the search box and its arrow). Clicks on it don't close the list.</summary>
    public FrameworkElement? Target
    {
        get => (FrameworkElement?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private Window? _window;

    public DropPanel()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Panel.SetZIndex(this, 50);
        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
    }

    public bool IsOpen
    {
        get => Visibility == Visibility.Visible;
        set { if (value) Open(); else Visibility = Visibility.Collapsed; }
    }

    private void Open()
    {
        if (Target == null || Parent is not FrameworkElement host || !Target.IsVisible) return;
        Hook();
        var at = Target.TranslatePoint(new Point(0, Target.ActualHeight + 4), host);
        Margin = new Thickness(at.X, at.Y, 0, 0);
        Width = Target.ActualWidth;
        MaxHeight = Math.Max(90, host.ActualHeight - at.Y - 6);
        Visibility = Visibility.Visible;
    }

    private void Hook()
    {
        var w = Window.GetWindow(this);
        if (w == null || w == _window) return;
        Unhook();
        _window = w;
        w.PreviewMouseDown += Window_PreviewMouseDown;
        w.Deactivated += Window_Changed;
        w.LocationChanged += Window_Changed;
        w.SizeChanged += Window_Changed;
    }

    private void Unhook()
    {
        if (_window == null) return;
        _window.PreviewMouseDown -= Window_PreviewMouseDown;
        _window.Deactivated -= Window_Changed;
        _window.LocationChanged -= Window_Changed;
        _window.SizeChanged -= Window_Changed;
        _window = null;
    }

    private void Window_Changed(object? sender, EventArgs e) => IsOpen = false;

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsOpen || e.OriginalSource is not DependencyObject d) return;
        if (IsInside(d, this) || (Target != null && IsInside(d, Target))) return;
        IsOpen = false;
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var p = d; p != null; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p))
            if (p == ancestor) return true;
        return false;
    }
}
