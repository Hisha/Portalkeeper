using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Portalkeeper.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService(string? settingsPath = null)
    {
        if (settingsPath is not null)
        {
            _settingsPath = settingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!);
            return;
        }
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

            var settings = JsonSerializer.Deserialize<PortalkeeperSettings>(json) ?? new PortalkeeperSettings();
            settings.ShowTransmogrifiedAppearancesByRealm ??= new();
            return settings;
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
    public Dictionary<string, bool> ShowTransmogrifiedAppearancesByRealm { get; set; } = new();
    public bool ShowTransmogFor(string realmKey) =>
        !ShowTransmogrifiedAppearancesByRealm.TryGetValue(realmKey, out var value) || value;
    public string ClientPath { get; set; } = string.Empty;
    public bool HidePortalkeeperWhileGameRuns { get; set; } = true;
}