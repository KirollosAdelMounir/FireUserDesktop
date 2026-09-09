using System;
using System.IO;
using System.Text.Json;

using UserIdentityDesktop.Core;
namespace UserIdentityDesktop.Data;

public class AppConfig
{
    public string ServerUrl { get; set; } = "http://34.34.49.52:5000";
    public string? SavedRefreshToken { get; set; }
    public string? SavedEmail { get; set; }
    public bool IsDarkMode { get; set; } = true;
}

public class ConfigurationService : IConfigurationService
{
    private readonly string _configFilePath;
    private AppConfig _config;

    public ConfigurationService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "UserIdentityDesktop");
        Directory.CreateDirectory(dir);
        _configFilePath = Path.Combine(dir, "config.json");

        _config = Load();
    }

    public string ServerUrl
    {
        get => _config.ServerUrl;
        set
        {
            var clean = value?.TrimEnd('/') ?? "http://34.34.49.52:5000";
            if (_config.ServerUrl != clean)
            {
                _config.ServerUrl = clean;
                Save();
            }
        }
    }

    public string? SavedRefreshToken
    {
        get => _config.SavedRefreshToken;
        set
        {
            _config.SavedRefreshToken = value;
            Save();
        }
    }

    public string? SavedEmail
    {
        get => _config.SavedEmail;
        set
        {
            _config.SavedEmail = value;
            Save();
        }
    }

    public bool IsDarkMode
    {
        get => _config.IsDarkMode;
        set
        {
            _config.IsDarkMode = value;
            Save();
        }
    }

    private AppConfig Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                if (loaded != null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.ServerUrl))
                        loaded.ServerUrl = "http://34.34.49.52:5000";
                    return loaded;
                }
            }
        }
        catch
        {
            // fallback to default
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch
        {
            // best effort
        }
    }
}
