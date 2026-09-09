using Avalonia;
using System;
using System.Linq;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Service;

namespace UserIdentityDesktop;

sealed class Program
{
    public static IAppInstanceService? AppInstanceService { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var deepLink = ParseDeepLinkArgument(args);
        App.PendingDeepLink = deepLink;

        AppInstanceService = CreateInstanceService();
        if (AppInstanceService != null && !AppInstanceService.TryAcquire())
        {
            if (!string.IsNullOrWhiteSpace(deepLink))
                _ = AppInstanceService.SendToExistingInstanceAsync(deepLink);
            else
                _ = AppInstanceService.SendToExistingInstanceAsync("fireuserconnect://activate");

            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static IAppInstanceService? CreateInstanceService()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsAppInstanceService();

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return new UnixAppInstanceService();

        return null;
    }

    private static string? ParseDeepLinkArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--open-chat", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                    return args[i + 1];

                return null;
            }

            if (arg.StartsWith("fireuserconnect://", StringComparison.OrdinalIgnoreCase))
                return arg;
        }

        return null;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
