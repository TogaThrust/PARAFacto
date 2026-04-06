using System;
using System.Globalization;
using System.Windows.Data;

namespace PARAFactoNative.Views;

/// <summary>Convertit bool : false → chaîne vide, true → coche stylisée (✓).</summary>
public sealed class BoolToCheckmarkConverter : IValueConverter
{
    private const string Checkmark = "✓";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b) return Checkmark;
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString()?.Trim() == Checkmark;
}
