using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class RealmNewsFeed
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<RealmNewsArticle> Articles { get; set; } = new();
}

public sealed class RealmNewsArticle
{
    public ulong Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Category { get; set; } = "general";
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool Pinned { get; set; }

    [JsonIgnore]
    public string PublishedDisplay => PublishedAt.ToLocalTime().ToString("MMM d, yyyy • h:mm tt", CultureInfo.CurrentCulture);

    [JsonIgnore]
    public string CategoryDisplay => string.IsNullOrWhiteSpace(Category)
        ? "GENERAL"
        : Category.Trim().ToUpperInvariant();

    [JsonIgnore]
    public string Byline => string.IsNullOrWhiteSpace(Author)
        ? PublishedDisplay
        : $"{Author} • {PublishedDisplay}";

    [JsonIgnore]
    public string PinLabel => Pinned ? "FEATURED" : string.Empty;
}
