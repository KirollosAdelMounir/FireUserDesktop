using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UserIdentityDesktop.Application;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Service;
using UserIdentityDesktop.ViewModels;
using UserIdentityDesktop.Views;

namespace UserIdentityDesktop;

public partial class App : Avalonia.Application
{
    public static ServiceProvider? Services { get; private set; }
    public static string? PendingDeepLink { get; set; }
    public static MainViewModel? MainViewModelInstance { get; private set; }
    private static readonly Queue<string> PendingDeepLinks = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddUserIdentityApplication();
        services.AddSingleton<MainViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var systemNotifications = Services.GetRequiredService<SystemNotificationService>();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                systemNotifications.InitializeWindowsRegistration();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                systemNotifications.InitializeLinuxRegistration();
                systemNotifications.InitializeLinuxAutostart();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                systemNotifications.InitializeMacRegistration();
            }

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            MainViewModelInstance = (MainViewModel)mainWindow.DataContext;
            if (!string.IsNullOrWhiteSpace(PendingDeepLink))
                EnqueueDeepLink(PendingDeepLink);

            var notifications = Services.GetRequiredService<INotificationService>();
            notifications.NotificationActivated += userId =>
            {
                mainWindow.ShowFromTray();
                _ = MainViewModelInstance?.OpenConversationAsync(userId);
            };

            var signOut = new NativeMenuItem { Header = "Sign out" };
            signOut.Click += async (_, _) =>
            {
                await MainViewModelInstance?.LogoutAsync()!;
                mainWindow.ShowFromTray();
            };
            var exit = new NativeMenuItem { Header = "Exit" };
            exit.Click += (_, _) =>
            {
                mainWindow.AllowClose = true;
                desktop.Shutdown();
            };

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://UserIdentityDesktop/Assets/favicon.ico"))),
                ToolTipText = "Fire User Connect",
                Menu = new NativeMenu { Items = { signOut, exit } }
            };
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            tray.Clicked += (_, _) => mainWindow.ShowFromTray();

            _ = StartDeepLinkQueueAsync();
            if (Program.AppInstanceService != null)
            {
                _ = Program.AppInstanceService.StartServerAsync(CancellationToken.None, async uri =>
                {
                    await Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        await HandleDeepLinkAsync(uri);
                    });
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void EnqueueDeepLink(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        lock (PendingDeepLinks)
        {
            PendingDeepLinks.Enqueue(uri);
        }
    }

    public static async Task HandleDeepLinkAsync(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return;

        var normalized = NormalizeUri(uri);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (Avalonia.Application.Current is not App app)
        {
            EnqueueDeepLink(normalized);
            return;
        }

        await app.ProcessDeepLinkAsync(normalized);
    }

    private async Task ProcessDeepLinkAsync(string uri)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is not MainWindow mainWindow)
        {
            EnqueueDeepLink(uri);
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!mainWindow.IsVisible)
                mainWindow.Show();

            if (mainWindow.WindowState == WindowState.Minimized)
                mainWindow.WindowState = WindowState.Normal;

            mainWindow.Activate();
        });

        var userId = ParseDeepLinkUserId(uri);
        if (userId is null)
            return;

        if (MainViewModelInstance is null)
        {
            EnqueueDeepLink(uri);
            return;
        }

        var viewModel = MainViewModelInstance;
        if (!viewModel.IsChatView)
        {
            var currentView = viewModel.CurrentView;
            if (currentView == null)
            {
                EnqueueDeepLink(uri);
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            var attempts = 0;
            while (attempts < 200 && MainViewModelInstance != null && !MainViewModelInstance.IsChatView)
            {
                await Task.Delay(100);
                attempts++;
            }

            if (MainViewModelInstance != null)
                await MainViewModelInstance.OpenConversationAsync(userId.Value);
        });
    }

    private async Task StartDeepLinkQueueAsync()
    {
        while (true)
        {
            string? nextUri = null;
            lock (PendingDeepLinks)
            {
                if (PendingDeepLinks.Count > 0)
                    nextUri = PendingDeepLinks.Dequeue();
            }

            if (nextUri is null)
                return;

            await HandleDeepLinkAsync(nextUri);
        }
    }

    private static string? NormalizeUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var candidate = uri.Trim();
        if (candidate.StartsWith("\"", StringComparison.Ordinal) && candidate.EndsWith("\"", StringComparison.Ordinal))
            candidate = candidate[1..^1];

        if (!candidate.StartsWith("fireuserconnect://", StringComparison.OrdinalIgnoreCase))
            return null;

        return candidate;
    }

    private static Guid? ParseDeepLinkUserId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return null;

        if (!string.Equals(parsed.Scheme, "fireuserconnect", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = parsed.AbsolutePath.Trim('/');
        if (path.StartsWith("chat", StringComparison.OrdinalIgnoreCase))
        {
            var value = path["chat".Length..].TrimStart('/');
            if (Guid.TryParse(value, out var userId))
                return userId;
        }

        if (parsed.Query.Contains("userId", StringComparison.OrdinalIgnoreCase))
        {
            var query = System.Web.HttpUtility.ParseQueryString(parsed.Query);
            var value = query["userId"];
            if (Guid.TryParse(value, out var userId))
                return userId;
        }

        return null;
    }
}