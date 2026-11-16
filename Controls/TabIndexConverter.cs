using System;
using System.Globalization;
using System.Windows.Data;

namespace US16x08_AbletonPanel.Controls;

public sealed class TabIndexConverter : IValueConverter
{
    public static readonly TabIndexConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int idx && parameter != null && int.TryParse(parameter.ToString(), out int target))
            return idx == target;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter != null && int.TryParse(parameter.ToString(), out int target))
            return target;
        return Binding.DoNothing;
    }
}
