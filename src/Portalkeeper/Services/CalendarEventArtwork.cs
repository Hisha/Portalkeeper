using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

/// <summary>
/// Maps realm-calendar events to Portalkeeper-owned artwork.
/// Server-provided texture names are semantic hints only; Portalkeeper never
/// loads artwork from the WoW client.
/// </summary>
public static class CalendarEventArtwork
{
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Prefer event names first. A few AzerothCore holidays intentionally share
    // a calendar texture (for example Pilgrim's Bounty / Harvest Festival), and
    // some events such as Fireworks Spectacular may publish an empty texture.
    private static readonly Dictionary<string, string> NameAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Darkmoon Faire"] = "darkmoon.png",
        ["Kalu'ak Fishing Derby"] = "fishing.png",
        ["Stranglethorn Fishing Extravaganza"] = "fishing.png",
        ["Brewfest"] = "brewfest.png",
        ["Harvest Festival"] = "harvest.png",
        ["Pirates' Day"] = "pirates.png",
        ["Hallow's End"] = "hallows_end.png",
        ["Day of the Dead"] = "day_of_the_dead.png",
        ["Pilgrim's Bounty"] = "pilgrims_bounty.png",
        ["Winter Veil"] = "winter_veil.png",
        ["Lunar Festival"] = "lunar_festival.png",
        ["Love is in the Air"] = "love_is_in_the_air.png",
        ["Noblegarden"] = "noblegarden.png",
        ["Children's Week"] = "childrens_week.png",
        ["Midsummer Fire Festival"] = "midsummer.png",
        ["Fireworks Spectacular"] = "fireworks.png"
    };

    public static Bitmap? Resolve(RealmCalendarEvent calendarEvent)
    {
        var assetName = ResolveAssetName(calendarEvent);
        if (assetName is null)
            return null;

        if (Cache.TryGetValue(assetName, out var cached))
            return cached;

        try
        {
            var uri = new Uri($"avares://Portalkeeper/Assets/Calendar/{assetName}");
            using var stream = AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);
            Cache[assetName] = bitmap;
            return bitmap;
        }
        catch
        {
            // Artwork is cosmetic. A missing/bad asset should never prevent
            // the calendar itself from opening; the UI falls back to glyphs.
            return null;
        }
    }

    private static string? ResolveAssetName(RealmCalendarEvent calendarEvent)
    {
        var name = calendarEvent.Name ?? string.Empty;
        var texture = calendarEvent.Texture ?? string.Empty;

        if (NameAssets.TryGetValue(name, out var namedAsset))
            return namedAsset;

        // Texture fallbacks make the client tolerant of renamed/custom events
        // that still use the normal realm-calendar semantic texture keys.
        if (texture.Contains("Darkmoon", StringComparison.OrdinalIgnoreCase))
            return "darkmoon.png";
        if (calendarEvent.Category.Equals("fishing", StringComparison.OrdinalIgnoreCase) ||
            texture.Contains("Fishing", StringComparison.OrdinalIgnoreCase))
            return "fishing.png";
        if (texture.Contains("Brewfest", StringComparison.OrdinalIgnoreCase))
            return "brewfest.png";
        if (texture.Contains("Hallows", StringComparison.OrdinalIgnoreCase))
            return "hallows_end.png";
        if (texture.Contains("DayOfTheDead", StringComparison.OrdinalIgnoreCase))
            return "day_of_the_dead.png";
        if (texture.Contains("WinterVeil", StringComparison.OrdinalIgnoreCase))
            return "winter_veil.png";
        if (texture.Contains("LunarFestival", StringComparison.OrdinalIgnoreCase))
            return "lunar_festival.png";
        if (texture.Contains("LoveInTheAir", StringComparison.OrdinalIgnoreCase))
            return "love_is_in_the_air.png";
        if (texture.Contains("Noblegarden", StringComparison.OrdinalIgnoreCase))
            return "noblegarden.png";
        if (texture.Contains("ChildrensWeek", StringComparison.OrdinalIgnoreCase))
            return "childrens_week.png";
        if (texture.Contains("Midsummer", StringComparison.OrdinalIgnoreCase))
            return "midsummer.png";
        if (texture.Contains("Pirate", StringComparison.OrdinalIgnoreCase))
            return "pirates.png";

        // Do not use Calendar_HarvestFestival as a direct artwork selector here:
        // AzerothCore also uses it for Pilgrim's Bounty. Exact-name mappings above
        // keep those two holidays visually distinct.
        if (texture.Contains("Harvest", StringComparison.OrdinalIgnoreCase))
            return "harvest.png";

        // Every public holiday should still have Portalkeeper artwork even when a
        // custom server introduces an event this version of the launcher does not
        // know yet.
        if (calendarEvent.Category.Equals("holiday", StringComparison.OrdinalIgnoreCase))
            return "holiday.png";

        return null;
    }
}
