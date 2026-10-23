using System;
using System.Linq;

namespace UserIdentityDesktop.Core.Models;

/// <summary>Meeting links carry only the public locator, never a login or guest credential.</summary>
public static class MeetingLink
{
    public static bool IsValidLocator(string? locator) => locator is { Length: > 0 and <= 32 } &&
        locator.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string? Parse(string? value)
    {
        const string prefix = "fireuserconnect://meeting/";
        if (value == null || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var locator = value[prefix.Length..];
        return IsValidLocator(locator) ? locator : null;
    }
}
