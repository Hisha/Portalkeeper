using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmCalendarService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string _cachePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public RealmCalendarService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Portalkeeper");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "realm-calendar-cache.json");
    }

    public async Task<RealmCalendarLoadResult> LoadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new(null, false, "This realm does not provide a calendar feed.");

        try
        {
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var feed = Parse(json);
            await File.WriteAllTextAsync(_cachePath, json);
            return new(feed, false, "Calendar updated from realm.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    var feed = Parse(await File.ReadAllTextAsync(_cachePath));
                    return new(feed, true, $"Realm calendar unavailable; showing cached data. {UserErrorService.Format(ex)}");
                }
            }
            catch { }

            return new(null, false, UserErrorService.Format(ex, "Unable to load realm calendar"));
        }
    }

    private static RealmCalendarFeed Parse(string json)
    {
        var feed = JsonSerializer.Deserialize<RealmCalendarFeed>(json, JsonOptions)
                   ?? throw new InvalidDataException("Calendar feed was empty.");
        if (feed.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported calendar schema version {feed.SchemaVersion}.");
        if (feed.Range.End < feed.Range.Start)
            throw new InvalidDataException("Calendar feed contains an invalid date range.");
        return feed;
    }
}

public sealed record RealmCalendarLoadResult(RealmCalendarFeed? Feed, bool FromCache, string Status);
