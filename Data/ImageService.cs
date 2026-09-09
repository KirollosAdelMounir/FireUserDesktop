using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;

public class ImageService : IImageService
{
    private readonly IAuthService _authService;
    private readonly IConfigurationService _configService;
    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new();

    public ImageService(IAuthService authService, IConfigurationService configService)
    {
        _authService = authService;
        _configService = configService;
    }

    public async Task<Bitmap?> GetProfilePictureAsync(Guid userId)
    {
        var key = $"profile_{userId}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/User/GetProfilePictureCompressed?userId={userId}";

            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength != 0)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new Bitmap(ms);
                    _cache[key] = bitmap;
                    return bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load profile picture for {userId}: {ex.Message}");
        }

        _cache[key] = null;
        return null;
    }

    public Bitmap? GetBitmapFromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public Bitmap? LoadLocalImage(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                using var stream = File.OpenRead(filePath);
                return new Bitmap(stream);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    public async Task<Bitmap?> GetAttachmentThumbnailAsync(Guid attachmentId)
    {
        var key = $"attachment_{attachmentId}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        try
        {
            using var client = _authService.CreateAuthenticatedClient();
            var url = $"{_configService.ServerUrl}/api/User/ChatAttachment?attachmentId={attachmentId}";

            var response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length > 0)
                {
                    using var ms = new MemoryStream(bytes);
                    var bitmap = new Bitmap(ms);
                    _cache[key] = bitmap;
                    return bitmap;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load attachment thumbnail {attachmentId}: {ex.Message}");
        }

        _cache[key] = null;
        return null;
    }

    public void ClearCache()
    {
        _cache.Clear();
    }
}
