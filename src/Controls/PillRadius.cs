using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace APMultiTracker;

/// <summary>CornerRadius = half the element's height, so buttons of any height are perfect pills.</summary>
public sealed class PillRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double h && h > 0 && !double.IsNaN(h) ? h / 2 : 22);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Rounds a Border into a perfect pill from its real size, every time the size changes (a binding to ActualHeight could
/// be worked out while the button was hidden and stick at a fallback radius, drawing an oval).
/// </summary>
public static class Pill
{
    public static readonly DependencyProperty AutoProperty = DependencyProperty.RegisterAttached(
        "Auto", typeof(bool), typeof(Pill), new PropertyMetadata(false, OnAutoChanged));

    public static bool GetAuto(DependencyObject o) => (bool)o.GetValue(AutoProperty);
    public static void SetAuto(DependencyObject o, bool value) => o.SetValue(AutoProperty, value);

    private static void OnAutoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Border b) return;
        b.SizeChanged -= Resize;
        if ((bool)e.NewValue) { b.SizeChanged += Resize; Apply(b); }
    }

    private static void Resize(object sender, SizeChangedEventArgs e) => Apply((System.Windows.Controls.Border)sender);

    private static void Apply(System.Windows.Controls.Border b)
    {
        double r = Math.Min(b.ActualWidth, b.ActualHeight) / 2;
        if (r > 0) b.CornerRadius = new CornerRadius(r);
    }
}
