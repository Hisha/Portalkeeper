using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmArmoryService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _cacheDirectory;

    public RealmArmoryService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheDirectory = Path.Combine(appData, "Portalkeeper", "armory-cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<RealmArmoryIndexLoadResult> LoadIndexAsync(string url)
    {
        var cache = Path.Combine(_cacheDirectory, "index.json");
        try
        {
            var json = await Http.GetStringAsync(url);
            var feed = ParseIndex(json);
            await File.WriteAllTextAsync(cache, json);
            return new(feed, false, "Realm armory updated from server.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(cache))
                    return new(ParseIndex(await File.ReadAllTextAsync(cache)), true,
                        $"Armory unavailable; showing cached roster. {UserErrorService.Format(ex)}");
            }
            catch { }
            return new(null, false, UserErrorService.Format(ex, "Unable to load realm armory"));
        }
    }

    public async Task<RealmArmoryProfileLoadResult> LoadProfileAsync(string indexUrl, ulong characterId)
    {
        var cache = Path.Combine(_cacheDirectory, $"character-{characterId}.json");
        try
        {
            var baseUri = new Uri(indexUrl);
            var profileUri = new Uri(baseUri, $"characters/{characterId}.json");
            var json = await Http.GetStringAsync(profileUri);
            var profile = ParseProfile(json);
            await File.WriteAllTextAsync(cache, json);
            return new(profile, false, "Character profile updated from server.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(cache))
                    return new(ParseProfile(await File.ReadAllTextAsync(cache)), true,
                        $"Profile unavailable; showing cached data. {UserErrorService.Format(ex)}");
            }
            catch { }
            return new(null, false, UserErrorService.Format(ex, "Unable to load character profile"));
        }
    }

    private static RealmArmoryIndex ParseIndex(string json)
    {
        var value = JsonSerializer.Deserialize<RealmArmoryIndex>(json, JsonOptions)
                    ?? throw new InvalidDataException("Armory index was empty.");
        if (value.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported armory schema version {value.SchemaVersion}.");
        value.Characters ??= new();
        return value;
    }

    private static RealmArmoryProfile ParseProfile(string json)
    {
        var value = JsonSerializer.Deserialize<RealmArmoryProfile>(json, JsonOptions)
                    ?? throw new InvalidDataException("Character profile was empty.");
        if (value.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported armory schema version {value.SchemaVersion}.");
        value.Character ??= new();
        value.Character.Equipment ??= new();
        return value;
    }
}

public sealed record RealmArmoryIndexLoadResult(RealmArmoryIndex? Feed, bool FromCache, string Status);
public sealed record RealmArmoryProfileLoadResult(RealmArmoryProfile? Profile, bool FromCache, string Status);
