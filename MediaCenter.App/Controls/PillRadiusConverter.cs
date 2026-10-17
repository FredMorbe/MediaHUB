using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MediaCenter.App.Controls;

/// <summary>Eckenradius für eine Pille: die halbe Höhe. Ein fester großer Radius würde in WPF zu Ellipsen mit Spitzen.</summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double height ? height / 2 : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
