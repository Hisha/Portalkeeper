using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Portalkeeper.Models;

public sealed class RealmArmoryIndex
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public List<ArmoryCharacterSummary> Characters { get; set; } = new();
}

public sealed class ArmoryCharacterSummary
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
}

public sealed class ArmoryEquipmentItem
{
    public int Slot { get; set; }
    public int Entry { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quality { get; set; }
    public int ItemLevel { get; set; }

    [JsonIgnore] public string SlotName => ArmoryNames.SlotName(Slot);
    [JsonIgnore] public string Detail => $"Item Level {ItemLevel} • Entry {Entry}";
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

    public static string SlotName(int id) => id switch
    {
        0 => "Head", 1 => "Neck", 2 => "Shoulders", 3 => "Shirt", 4 => "Chest",
        5 => "Waist", 6 => "Legs", 7 => "Feet", 8 => "Wrists", 9 => "Hands",
        10 => "Finger 1", 11 => "Finger 2", 12 => "Trinket 1", 13 => "Trinket 2",
        14 => "Back", 15 => "Main Hand", 16 => "Off Hand", 17 => "Ranged", 18 => "Tabard", _ => $"Slot {id}"
    };
}
