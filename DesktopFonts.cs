using System;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace UserIdentityDesktop;

public static class DesktopFonts
{
    public static FontManagerOptions Defaults => new() { DefaultFamilyName = "fonts:Outfit#Outfit" };

    public static void Configure(FontManager manager) => manager.AddFontCollection(
        new EmbeddedFontCollection(new Uri("fonts:Outfit"), new Uri("avares://Fire%20User%20Connect/Assets/Fonts")));
}
