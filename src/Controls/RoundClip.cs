using System.Windows;
using System.Windows.Media;

namespace APMultiTracker;

/// <summary>Clips an element to a rounded rectangle of its own size (WPF borders don't clip their content to their corners).</summary>
public static class RoundClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundClip), new PropertyMetadata(0.0, OnRadiusChanged));

    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject o, double value) => o.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.SizeChanged -= Update;
        el.SizeChanged += Update;
        Apply(el);
    }

    private static void Update(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement el)
    {
        double r = GetRadius(el);
        el.Clip = new RectangleGeometry(new Rect(0, 0, el.ActualWidth, el.ActualHeight), r, r);
    }
}
