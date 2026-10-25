using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Media;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.Core;
using Avalonia.Controls;
using UserIdentityDesktop.Views;
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
    [AvaloniaFact]
    public void MainAndSecondaryWindowsReceiveBrandedIcon()
    {
        foreach(var window in new Window[]{new MainWindow{AllowClose=true},new CallWindow(),new Window()})
        {try{window.Show();Assert.NotNull(window.Icon);}finally{window.Close();}}
    }
    [AvaloniaFact]
    public void LinuxRegistrationWritesIconBeforeLauncherAndPreservesCustomEntries()
    {
        var root=Path.Combine(Path.GetTempPath(),"icon-registration-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var icon=AssetLoader.Open(new Uri("avares://Fire%20User%20Connect/Assets/Icons/fire-user-connect-512.png"));
            var desktop=SystemNotificationService.WriteLinuxDesktopRegistration("/opt/Fire User/Fire User Connect",root,icon);
            var entries=File.ReadAllLines(desktop);var iconPath=Assert.Single(entries,x=>x.StartsWith("Icon="))[5..];
            Assert.True(File.Exists(iconPath));using var bitmap=new Avalonia.Media.Imaging.Bitmap(iconPath);Assert.Equal(new Avalonia.PixelSize(512,512),bitmap.PixelSize);
            Assert.Contains("StartupWMClass="+ApplicationIdentity.LinuxDesktopId,entries);Assert.Contains("Exec=\"/opt/Fire User/Fire User Connect\" --open-chat %u",entries);
            File.AppendAllText(desktop,"X-Custom-Setting=keep\n");icon.Position=0;
            SystemNotificationService.WriteLinuxDesktopRegistration("/new/Fire User Connect",root,icon);
            entries=File.ReadAllLines(desktop);Assert.Contains("X-Custom-Setting=keep",entries);Assert.Single(entries,x=>x.StartsWith("Icon="));Assert.Contains("TryExec=/new/Fire User Connect",entries);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void LinuxDataHomeUsesAbsoluteXdgOverrideAndRejectsRelativeSetting()
    {
        var home=Path.GetTempPath();var custom=Path.Combine(home,"custom-data");
        Assert.Equal(custom,SystemNotificationService.ResolveLinuxDataHome(custom,home));
        Assert.Equal(Path.Combine(home,".local","share"),SystemNotificationService.ResolveLinuxDataHome("relative",home));
        Assert.Equal(Path.Combine(home,".local","share"),SystemNotificationService.ResolveLinuxDataHome(null,home));
    }
    [Fact]
    public void WindowsShellIdentityImportResolvesWithoutChangingTestProcessIdentity()
    {
        if(!OperatingSystem.IsWindows())return;
        var method=typeof(SystemNotificationService).GetMethod("SetCurrentProcessExplicitAppUserModelID",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var attribute=Assert.IsType<System.Runtime.InteropServices.DllImportAttribute>(Attribute.GetCustomAttribute(method,typeof(System.Runtime.InteropServices.DllImportAttribute)));
        Assert.Equal(System.Runtime.InteropServices.CharSet.Unicode,attribute.CharSet);Assert.True(attribute.ExactSpelling);
        var library=System.Runtime.InteropServices.NativeLibrary.Load(attribute.Value);
        try{Assert.NotEqual(IntPtr.Zero,System.Runtime.InteropServices.NativeLibrary.GetExport(library,method.Name));}
        finally{System.Runtime.InteropServices.NativeLibrary.Free(library);}
    }
}
