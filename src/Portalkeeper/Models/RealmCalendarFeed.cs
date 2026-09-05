using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class RealmCalendarFeed
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("range")]
    public RealmCalendarRange Range { get; set; } = new();
    [JsonPropertyName("events")]
    public List<RealmCalendarEvent> Events { get; set; } = new();
}

public sealed class RealmCalendarRange
{
    [JsonPropertyName("start")]
    public DateOnly Start { get; set; }
    [JsonPropertyName("end")]
    public DateOnly End { get; set; }
}

public sealed class RealmCalendarEvent
{
    [JsonPropertyName("holidayId")]
    public int HolidayId { get; set; }
    [JsonPropertyName("gameEventId")]
    public int GameEventId { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("allDay")]
    public bool AllDay { get; set; }
    [JsonPropertyName("start")]
    public DateTimeOffset? Start { get; set; }
    [JsonPropertyName("end")]
    public DateTimeOffset? End { get; set; }
    [JsonPropertyName("startDate")]
    public DateOnly? StartDate { get; set; }
    [JsonPropertyName("endDate")]
    public DateOnly? EndDate { get; set; }
    [JsonPropertyName("texture")]
    public string Texture { get; set; } = string.Empty;
}
