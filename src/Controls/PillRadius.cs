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
