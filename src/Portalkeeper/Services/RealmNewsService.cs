using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

public sealed class RealmNewsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _cachePath;

    public RealmNewsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "Portalkeeper");
        Directory.CreateDirectory(directory);
        _cachePath = Path.Combine(directory, "realm-news-cache.json");
    }

    public async Task<RealmNewsLoadResult> LoadAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new(null, false, "This realm does not provide a news feed.");

        try
        {
            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var feed = Parse(json);
            await File.WriteAllTextAsync(_cachePath, json);
            return new(feed, false, "Realm news updated from server.");
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    var feed = Parse(await File.ReadAllTextAsync(_cachePath));
                    return new(feed, true, $"Realm news unavailable; showing cached data. {UserErrorService.Format(ex)}");
                }
            }
            catch
            {
                // If the cache is also unusable, report the original load failure below.
            }

            return new(null, false, UserErrorService.Format(ex, "Unable to load realm news"));
        }
    }

    private static RealmNewsFeed Parse(string json)
    {
        var feed = JsonSerializer.Deserialize<RealmNewsFeed>(json, JsonOptions)
                   ?? throw new InvalidDataException("News feed was empty.");

        if (feed.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported news schema version {feed.SchemaVersion}.");

        feed.Articles ??= new();
        feed.Articles = feed.Articles
            .Where(article => !string.IsNullOrWhiteSpace(article.Title))
            .OrderByDescending(article => article.Pinned)
            .ThenByDescending(article => article.PublishedAt)
            .ThenByDescending(article => article.Id)
            .ToList();

        return feed;
    }
}

public sealed record RealmNewsLoadResult(RealmNewsFeed? Feed, bool FromCache, string Status);
