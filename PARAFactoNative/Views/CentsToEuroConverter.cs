using System;
using System.Globalization;
using System.Windows.Data;

namespace PARAFactoNative.Views;

public sealed class CentsToEuroConverter : IValueConverter
{
    private static readonly CultureInfo FrBe = CultureInfo.GetCultureInfo("fr-BE");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "";
        try
        {
            var cents = value switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                decimal d => (int)d,
                double db => (int)db,
                string str when int.TryParse(str, out var i2) => i2,
                _ => 0
            };

            var euros = cents / 100m;
            return euros.ToString("0.00", FrBe);
        }
        catch
        {
            return "";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value?.ToString() ?? "").Trim();
        if (s.Length == 0) return 0;
        s = s.Replace("€", "").Replace(" ", "").Replace(",", ".");
        if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return 0;
        return (int)Math.Round(d * 100m, MidpointRounding.AwayFromZero);
    }
}
