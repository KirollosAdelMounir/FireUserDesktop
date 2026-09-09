using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace UserIdentityDesktop.Converters;

public class BoolInverterConverter : IValueConverter
{
    public static readonly BoolInverterConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return !b;
        return false;
    }
}

public class NullToBoolConverter : IValueConverter
{
    public static readonly NullToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            return isNull;
        return !isNull;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

public class DateTimeFormatConverter : IValueConverter
{
    public static readonly DateTimeFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var local = dt.ToLocalTime();
            var now = DateTime.Now;

            if (local.Date == now.Date)
                return local.ToString("h:mm tt");
            if (local.Date == now.Date.AddDays(-1))
                return $"Yesterday {local:h:mm tt}";
            if (local.Year == now.Year)
                return local.ToString("MMM d, h:mm tt");
            return local.ToString("MMM d, yyyy");
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
