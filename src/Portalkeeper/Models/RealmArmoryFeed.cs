using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class RealmArmoryIndex
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<ArmoryCharacterSummary> Characters { get; set; } = new();
}

public class ArmoryCharacterSummary
{
    public ulong Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Race { get; set; }
    public int Class { get; set; }
    public int Gender { get; set; }
    public bool Playerbot { get; set; }

    [JsonIgnore] public string ClassName => ArmoryNames.ClassName(Class);
    [JsonIgnore] public string RaceName => ArmoryNames.RaceName(Race);
    [JsonIgnore] public string TypeLabel => Playerbot ? "PLAYERBOT" : "PLAYER";
    [JsonIgnore] public string Subtitle => $"Level {Level} {RaceName} {ClassName}";
    [JsonIgnore] public string CompactSubtitle => $"Lv {Level} • {RaceName} • {ClassName}";
    [JsonIgnore] public string FactionName => ArmoryNames.FactionName(Race);
}

public sealed class RealmArmoryProfile
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public ArmoryCharacter Character { get; set; } = new();
}

public sealed class ArmoryCharacter : ArmoryCharacterSummary
{
    public List<ArmoryEquipmentItem> Equipment { get; set; } = new();

    [JsonIgnore] public int EquippedCount => Equipment.Count;
    [JsonIgnore] public int AverageItemLevel =>
        Equipment.Count == 0 ? 0 : (int)Math.Round(Equipment.Average(x => x.ItemLevel));
    [JsonIgnore] public int HighestItemLevel =>
        Equipment.Count == 0 ? 0 : Equipment.Max(x => x.ItemLevel);
    [JsonIgnore] public string GearSummary =>
        Equipment.Count == 0 ? "No equipped items" : $"{EquippedCount} equipped items";
}

public sealed class ArmoryEquipmentItem
{
    public int Slot { get; set; }
    public int Entry { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quality { get; set; }
    public int ItemLevel { get; set; }

    [JsonIgnore] public string SlotName => ArmoryNames.SlotName(Slot);
    [JsonIgnore] public string SlotAbbreviation => ArmoryNames.SlotAbbreviation(Slot);
    [JsonIgnore] public string QualityName => ArmoryNames.QualityName(Quality);
    [JsonIgnore] public string Detail => $"iLvl {ItemLevel}  •  Entry {Entry}";
    [JsonIgnore] public string QualityDetail => $"{QualityName}  •  iLvl {ItemLevel}";
}

public static class ArmoryNames
{
    public static string ClassName(int id) => id switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        6 => "Death Knight", 7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "Unknown"
    };

    public static string RaceName(int id) => id switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "Night Elf", 5 => "Undead",
        6 => "Tauren", 7 => "Gnome", 8 => "Troll", 10 => "Blood Elf", 11 => "Draenei", _ => "Unknown"
    };

    public static string FactionName(int race) => race switch
    {
        1 or 3 or 4 or 7 or 11 => "Alliance",
        2 or 5 or 6 or 8 or 10 => "Horde",
        _ => "Unknown"
    };

    public static string SlotName(int id) => id switch
    {
        0 => "Head", 1 => "Neck", 2 => "Shoulders", 3 => "Shirt", 4 => "Chest",
        5 => "Waist", 6 => "Legs", 7 => "Feet", 8 => "Wrists", 9 => "Hands",
        10 => "Finger 1", 11 => "Finger 2", 12 => "Trinket 1", 13 => "Trinket 2",
        14 => "Back", 15 => "Main Hand", 16 => "Off Hand", 17 => "Ranged", 18 => "Tabard", _ => $"Slot {id}"
    };

    public static string SlotAbbreviation(int id) => id switch
    {
        0 => "HD", 1 => "NK", 2 => "SH", 3 => "SR", 4 => "CH",
        5 => "WA", 6 => "LG", 7 => "FT", 8 => "WR", 9 => "HN",
        10 or 11 => "RG", 12 or 13 => "TR", 14 => "BK", 15 => "MH",
        16 => "OH", 17 => "RG", 18 => "TB", _ => "?"
    };

    public static string QualityName(int quality) => quality switch
    {
        0 => "Poor",
        1 => "Common",
        2 => "Uncommon",
        3 => "Rare",
        4 => "Epic",
        5 => "Legendary",
        6 => "Artifact",
        7 => "Heirloom",
        _ => "Unknown"
    };
}
