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
            AuthPort = GetPort(values, "Server.AuthPort", 3724),
            WorldPort = GetPort(values, "Server.WorldPort", 8085),

            ManifestUrl =
                Get(values, "Updates.ManifestURL"),

            NewsUrl =
                Get(values, "Updates.NewsURL"),

            StatusUrl =
                Get(values, "Updates.StatusURL"),

            CalendarUrl =
                Get(values, "Updates.CalendarURL")
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

    private static int GetPort(
        Dictionary<string, string> values,
        string key,
        int defaultPort)
    {
        if (!values.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return defaultPort;
        }

        if (!int.TryParse(value, out var port) ||
            port < 1 ||
            port > 65535)
        {
            throw new InvalidDataException(
                $"{key} must be a valid TCP port (1-65535)." );
        }

        return port;
    }
}