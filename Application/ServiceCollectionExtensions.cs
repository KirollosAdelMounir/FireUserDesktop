using Microsoft.Extensions.DependencyInjection;
using UserIdentityDesktop.Core;
using UserIdentityDesktop.Data;
using UserIdentityDesktop.Service;

namespace UserIdentityDesktop.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUserIdentityApplication(this IServiceCollection services)
    {
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IBackendActionQueue, BackendActionQueue>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IImageService, ImageService>();
        services.AddSingleton<ITransferTracker, TransferTracker>();
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<ChatEncryption>();
        services.AddSingleton<ICollaborationApi, CollaborationApi>();
        services.AddSingleton<INativeMediaSession, UserIdentityDesktop.Service.Media.NativeMediaSession>();
        services.AddSingleton<IUserService, UserService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<SystemNotificationService>();
        services.AddSingleton<INotificationService>(provider =>
            provider.GetRequiredService<SystemNotificationService>());
        return services;
    }
}
