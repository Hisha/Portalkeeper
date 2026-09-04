using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class PersonalAddonService
{
    private readonly string _path;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public PersonalAddonService()
    {
        var applicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var portalkeeperDirectory =
            Path.Combine(applicationData, "Portalkeeper");

        Directory.CreateDirectory(portalkeeperDirectory);

        _path = Path.Combine(
            portalkeeperDirectory,
            "personal-addons.json");
    }

    public IReadOnlyList<PersonalAddonSource> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<PersonalAddonSource>();

            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<PersonalAddonSource>>(
                       json,
                       JsonOptions)
                   ?? new List<PersonalAddonSource>();
        }
        catch
        {
            return Array.Empty<PersonalAddonSource>();
        }
    }

    public void Add(PersonalAddonSource source)
    {
        var items = Load().ToList();

        if (items.Any(item =>
                item.GitUrl.Equals(
                    source.GitUrl,
                    StringComparison.OrdinalIgnoreCase) &&
                item.AddonPath.Equals(
                    source.AddonPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        items.Add(source);
        Save(items);
    }

    public void Remove(string id)
    {
        var items = Load()
            .Where(item =>
                !item.Id.Equals(
                    id,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Save(items);
    }

    private void Save(IReadOnlyList<PersonalAddonSource> items)
    {
        var json = JsonSerializer.Serialize(items, JsonOptions);
        File.WriteAllText(_path, json);
    }
}
