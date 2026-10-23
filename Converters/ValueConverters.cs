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

public class DateTimeFormatConverter : IValueConverter, IMultiValueConverter
{
    public static readonly DateTimeFormatConverter Instance = new();
    public object? Convert(System.Collections.Generic.IList<object?> values,Type targetType,object? parameter,CultureInfo culture) => Convert(values.Count>0?values[0]:null,targetType,parameter,culture);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var local = dt.ToLocalTime();
            var now = DateTime.Now;
            if(parameter as string=="full")return UserIdentityDesktop.Core.Models.WorkClock.DateTime(local);

            if (local.Date == now.Date)
                return UserIdentityDesktop.Core.Models.WorkClock.Time(local);
            if (local.Date == now.Date.AddDays(-1))
                return "Yesterday "+UserIdentityDesktop.Core.Models.WorkClock.Time(local);
            if (local.Year == now.Year)
                return local.ToString("MMM d")+", "+UserIdentityDesktop.Core.Models.WorkClock.Time(local);
            return local.ToString("MMM d, yyyy");
        }
        return string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
