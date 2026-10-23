using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Media;
using UserIdentityDesktop.Service;
using Xunit;

namespace UserIdentityDesktop.Tests;

public sealed class AppIdentityTests
{
    [AvaloniaFact]
    public void BrandedAssemblyKeepsIconsAndEmbeddedFontsAccessible()
    {
        Assert.Equal("Fire User Connect", typeof(App).Assembly.GetName().Name);
        foreach (var asset in new[] { "Assets/favicon.ico", "Assets/PageLogo.png" })
        {
            using var stream = AssetLoader.Open(new Uri("avares://Fire%20User%20Connect/" + asset));
            Assert.True(stream.Length > 0);
        }
        Assert.NotEmpty(AssetLoader.GetAssets(new Uri("avares://Fire%20User%20Connect/Assets/Fonts"), null));
        Assert.Equal("Outfit", new Typeface(FontManager.Current.DefaultFontFamily).GlyphTypeface.FamilyName);
    }

    [Fact]
    public void AppImageRegistrationUsesPersistentLauncherRatherThanTemporaryMount()
    {
        var image = Path.GetTempFileName();
        try
        {
            Assert.Equal(image, SystemNotificationService.ResolveLinuxLauncherPath(image, "/tmp/.mount_Fire/usr/app/Fire User Connect"));
            Assert.Equal("/opt/fire-user-connect/Fire User Connect", SystemNotificationService.ResolveLinuxLauncherPath("relative.AppImage", "/opt/fire-user-connect/Fire User Connect"));
            File.Delete(image);
            Assert.Equal("/opt/fire-user-connect/Fire User Connect", SystemNotificationService.ResolveLinuxLauncherPath(image, "/opt/fire-user-connect/Fire User Connect"));
        }
        finally { File.Delete(image); }
    }
}
