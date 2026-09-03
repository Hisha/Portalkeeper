using System;
using System.Collections.Generic;
using System.IO;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmConfigurationService
{
    public RealmInfo Load(string configurationPath)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            throw new ArgumentException(
                "No realm configuration path was provided.");
        }

        if (!File.Exists(configurationPath))
        {
            throw new FileNotFoundException(
                "Realm configuration file was not found.",
                configurationPath);
        }

        var values =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        string section = string.Empty;

        foreach (var rawLine in File.ReadLines(configurationPath))
        {
            var line = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith('#') ||
                line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var separator = line.IndexOf('=');

            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            values[$"{section}.{key}"] = value;
        }

        return new RealmInfo
        {
            Name = Get(values, "Server.Name"),
            Address = Get(values, "Server.Address"),

            ManifestUrl =
                Get(values, "Updates.ManifestURL"),

            NewsUrl =
                Get(values, "Updates.NewsURL"),

            StatusUrl =
                Get(values, "Updates.StatusURL")
        };
    }

    private static string Get(
        Dictionary<string, string> values,
        string key)
    {
        return values.TryGetValue(key, out var value)
            ? value
            : string.Empty;
    }
}