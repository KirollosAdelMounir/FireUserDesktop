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
using UserIdentityDesktop.Core.Models;
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
    private bool _shutdownPrepared;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, args) =>
            {
                if (args is ProtocolActivatedEventArgs protocol)
                    Dispatcher.UIThread.Post(async () => await HandleDeepLinkAsync(protocol.Uri.AbsoluteUri));
            };
        var services = new ServiceCollection();
        services.AddUserIdentityApplication();
        services.AddSingleton<MainViewModel>();

        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var systemNotifications = Services.GetRequiredService<SystemNotificationService>();
            if (!Program.StartupSmokeTest && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                systemNotifications.InitializeWindowsRegistration();
            else if (!Program.StartupSmokeTest && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                systemNotifications.InitializeLinuxRegistration();
                systemNotifications.InitializeLinuxAutostart();
            }
            else if (!Program.StartupSmokeTest && RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                systemNotifications.InitializeMacRegistration();
            }

            var mainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>(),
            };
            desktop.MainWindow = mainWindow;
            var toasts = new DesktopToastPresenter(mainWindow, () => Program.StartupSmokeTest || Environment.GetEnvironmentVariable("FIREUSER_REDUCED_MOTION") == "1" || MainViewModelInstance?.CurrentView is WorkspaceViewModel { ReduceMotion: true });
            systemNotifications.ShowDesktopToast = (title, message, target) =>
            {
                var link = Uri.TryCreate(target, UriKind.Absolute, out var parsed) ? parsed : null;
                var chat = link?.Host is "chat" or "conversation";
                var workspace = MainViewModelInstance?.CurrentView as WorkspaceViewModel;
                var peer = workspace?.Chat.Recipients.FirstOrDefault(x => link?.Host == "conversation" ? x.ConversationId == link.AbsolutePath.Trim('/') : x.ReceiverId.ToString() == link?.AbsolutePath.Trim('/'));
                toasts.Show(title, message, () => systemNotifications.ActivateLink(target), chat, peer?.Avatar);
            };
            systemNotifications.NotificationLinkActivated += target => _ = HandleDeepLinkAsync(target);
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (Program.StartupSmokeTest)
            {
                mainWindow.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    Console.WriteLine("Startup smoke test: native window opened.");
                    mainWindow.AllowClose = true;
                    _shutdownPrepared = true;
                    desktop.Shutdown();
                }, TimeSpan.FromSeconds(1));
            }

            MainViewModelInstance = (MainViewModel)mainWindow.DataContext;
            var shutdown = new ApplicationShutdown(
                () => MainViewModelInstance.ShutdownAsync(),
                () => mainWindow.AllowClose = true,
                () => { _shutdownPrepared = true; desktop.Shutdown(); });
            desktop.ShutdownRequested += (_, args) =>
            {
                if (_shutdownPrepared) return;
                // Platform shutdown/quit requests must never be vetoed. Explicit
                // tray exit uses the bounded cleanup path below.
                shutdown.Request(true, () => args.Cancel = true);
            };
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
                exit.IsEnabled = false;
                shutdown.Request(false, () => { });
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

        var meetingLocator = MeetingLink.Parse(uri);
        if (meetingLocator != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (MainViewModelInstance == null) EnqueueDeepLink(uri);
                else MainViewModelInstance.OpenMeeting(meetingLocator);
            });
            return;
        }
        if (Uri.TryCreate(uri, UriKind.Absolute, out var notificationUri) && notificationUri.Scheme == "fireuserconnect" && notificationUri.Host is "notification" or "conversation" or "call")
        {
            if (MainViewModelInstance?.CurrentView is WorkspaceViewModel workspace)
                await workspace.OpenNotificationTargetAsync(uri);
            else MainViewModelInstance?.DeferNotification(uri);
            return;
        }
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

    internal static Guid? ParseDeepLinkUserId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return null;

        if (!string.Equals(parsed.Scheme, "fireuserconnect", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.Equals(parsed.Host, "meeting", StringComparison.OrdinalIgnoreCase)) return null;
        var path = parsed.AbsolutePath.Trim('/');
        // In fireuserconnect://chat/{id}, "chat" is the authority, not a path segment.
        if (string.Equals(parsed.Host, "chat", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(path, out var chatUser))
            return chatUser;
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
