using System;
namespace UserIdentityDesktop.Core.Models;
public static class WorkClock
{
    public static bool TwelveHour { get; set; }
    public static string DateTime(System.DateTime value) => value.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)+" "+Time(value);
    public static string Time(DateTime value) => value.ToString(TwelveHour ? "h:mm tt" : "HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
public sealed record WorkTimeZoneOption(string Id, string Name, bool SupportsDaylightSavingTime, bool IsDaylightSavingTime, int OffsetMinutes)
{
    public override string ToString() => $"{Id} · UTC{(OffsetMinutes < 0 ? "-" : "+")}{Math.Abs(OffsetMinutes)/60:00}:{Math.Abs(OffsetMinutes)%60:00} · {(IsDaylightSavingTime ? "Summer time applied" : SupportsDaylightSavingTime ? "Standard time (seasonal summer time)" : "No summer time")}";
}
public sealed record ScheduleClockPreview(DateTime StartUtc, DateTime EndUtc, bool IsDaylightSavingTime, string TimeZoneId);
