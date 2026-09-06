using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    [JsonIgnore] public string ClassInitial => string.IsNullOrWhiteSpace(ClassName) ? "?" : ClassName[..1].ToUpperInvariant();
    [JsonIgnore] public IBrush ClassAccentBrush => ArmoryNames.ClassBrush(Class);
    [JsonIgnore] public IBrush FactionAccentBrush => ArmoryNames.FactionBrush(Race);
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
    public string Icon { get; set; } = string.Empty;
    public int Quality { get; set; }
    public int ItemLevel { get; set; }

    [JsonIgnore] public string SlotName => ArmoryNames.SlotName(Slot);
    [JsonIgnore] public string SlotAbbreviation => ArmoryNames.SlotAbbreviation(Slot);
    [JsonIgnore] public string QualityName => ArmoryNames.QualityName(Quality);
    [JsonIgnore] public string Detail => $"iLvl {ItemLevel} • Entry {Entry}";
    [JsonIgnore] public string QualityDetail => $"{QualityName} • iLvl {ItemLevel}";
    [JsonIgnore] public IBrush QualityBrush => ArmoryNames.QualityBrush(Quality);
}

public sealed class ArmoryEquipmentSlotView : INotifyPropertyChanged
{
    private Bitmap? _iconImage;

    public ArmoryEquipmentSlotView(int slot, ArmoryEquipmentItem? item)
    {
        Slot = slot;
        Item = item;
    }

    public int Slot { get; }
    public ArmoryEquipmentItem? Item { get; }

    public string SlotName => ArmoryNames.SlotName(Slot);
    public string SlotAbbreviation => ArmoryNames.SlotAbbreviation(Slot);
    public bool HasItem => Item is not null;
    public bool IsEmpty => Item is null;
    public string ItemName => Item?.Name ?? "Empty";
    public string ItemDetail => Item is null ? "No item equipped" : $"{Item.QualityName} • iLvl {Item.ItemLevel}";
    public string EntryText => Item is null ? string.Empty : $"#{Item.Entry}";
    public string IconName => Item?.Icon ?? string.Empty;
    public IBrush AccentBrush => Item?.QualityBrush ?? ArmoryNames.EmptySlotBrush;
    public IBrush ItemNameBrush => Item?.QualityBrush ?? ArmoryNames.EmptySlotTextBrush;

    public Bitmap? IconImage
    {
        get => _iconImage;
        set
        {
            if (ReferenceEquals(_iconImage, value))
                return;
            _iconImage = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class ArmoryNames
{
    public static readonly IBrush EmptySlotBrush = Brush.Parse("#3A3328");
    public static readonly IBrush EmptySlotTextBrush = Brush.Parse("#6E675D");

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
        16 => "OH", 17 => "RN", 18 => "TB", _ => "?"
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

    public static IBrush QualityBrush(int quality) => Brush.Parse(quality switch
    {
        0 => "#9D9D9D",
        1 => "#F2F2F2",
        2 => "#63D85B",
        3 => "#4B8DFF",
        4 => "#A96CFF",
        5 => "#FF9D2E",
        6 => "#E6CC80",
        7 => "#D9B86C",
        _ => "#A49A89"
    });

    public static IBrush ClassBrush(int id) => Brush.Parse(id switch
    {
        1 => "#C79C6E",
        2 => "#F58CBA",
        3 => "#ABD473",
        4 => "#FFF569",
        5 => "#F0F0F0",
        6 => "#C41F3B",
        7 => "#0070DE",
        8 => "#69CCF0",
        9 => "#9482C9",
        11 => "#FF7D0A",
        _ => "#C6A15A"
    });

    public static IBrush FactionBrush(int race) => Brush.Parse(race switch
    {
        1 or 3 or 4 or 7 or 11 => "#5E8FD8",
        2 or 5 or 6 or 8 or 10 => "#C35A4F",
        _ => "#8D8579"
    });
}
