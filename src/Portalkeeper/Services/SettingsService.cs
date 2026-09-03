using System;
using System.IO;
using System.Text.Json;

namespace Portalkeeper.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
    {
        var applicationData =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            Path.Combine(applicationData, "Portalkeeper");

        Directory.CreateDirectory(portalkeeperDirectory);

        _settingsPath =
            Path.Combine(portalkeeperDirectory, "settings.json");
    }

    public PortalkeeperSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new PortalkeeperSettings();
            }

            var json = File.ReadAllText(_settingsPath);

            return JsonSerializer.Deserialize<PortalkeeperSettings>(json)
                   ?? new PortalkeeperSettings();
        }
        catch
        {
            return new PortalkeeperSettings();
        }
    }

    public void Save(PortalkeeperSettings settings)
    {
        var json = JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(_settingsPath, json);
    }
}

public sealed class PortalkeeperSettings
{
    public string ClientPath { get; set; } = string.Empty;
}