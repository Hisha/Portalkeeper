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

public sealed class ArmoryCharacterAppearance
{
    public int Skin { get; set; }
    public int Face { get; set; }
    public int HairStyle { get; set; }
    public int HairColor { get; set; }
    public int FacialStyle { get; set; }

    [JsonIgnore]
    public string DebugSummary =>
        $"Skin {Skin} • Face {Face} • Hair {HairStyle}/{HairColor} • Facial {FacialStyle}";
}

public sealed class ArmoryCharacter : ArmoryCharacterSummary
{
    public ArmoryCharacterAppearance Appearance { get; set; } = new();
    public List<ArmoryEquipmentItem> Equipment { get; set; } = new();

    [JsonIgnore] public int EquippedCount => Equipment.Count;
    [JsonIgnore] public int AverageItemLevel =>
        Equipment.Count == 0 ? 0 : (int)Math.Round(Equipment.Average(x => x.ItemLevel));
    [JsonIgnore] public int HighestItemLevel =>
        Equipment.Count == 0 ? 0 : Equipment.Max(x => x.ItemLevel);
    [JsonIgnore] public string GearSummary =>
        Equipment.Count == 0 ? "No equipped items" : $"{EquippedCount} equipped items";
}

public sealed class ArmoryItemStat
{
    public int Type { get; set; }
    public int Value { get; set; }

    [JsonIgnore] public string Name => ArmoryNames.StatName(Type);
    [JsonIgnore] public string Display => $"{(Value >= 0 ? "+" : "")}{Value} {Name}";
}

public sealed class ArmoryItemDamage
{
    public double Min { get; set; }
    public double Max { get; set; }
    public int Type { get; set; }

    [JsonIgnore] public string SchoolName => ArmoryNames.DamageSchoolName(Type);
}

public sealed class ArmoryEnchantEffect
{
    public int Type { get; set; }
    public uint Amount { get; set; }
    public int Argument { get; set; }
    // Only stat effects have enough information to format without spell resolution.
    [JsonIgnore] public string Text => Type == 5
        ? $"+{Amount} {ArmoryNames.StatName(Argument)}" : string.Empty;
}

public sealed class ArmoryEnchant
{
    public uint Id { get; set; }
    public uint Duration { get; set; }
    public uint Charges { get; set; }
    public bool Resolved { get; set; }
    public string Description { get; set; } = string.Empty;
    public int ConditionId { get; set; }
    public List<ArmoryEnchantEffect> Effects { get; set; } = new();
    [JsonIgnore] public string Text => !Resolved ? string.Empty :
        !string.IsNullOrWhiteSpace(Description) ? Description :
        string.Join(", ", Effects.Where(e => e.Text.Length > 0).Select(e => e.Text));
}

public sealed class ArmoryGem : INotifyPropertyChanged
{
    public int SocketIndex { get; set; }
    public ArmoryEnchant? Enchant { get; set; }
    public int Entry { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int? Quality { get; set; }
    public int Color { get; set; }
    [JsonIgnore] public IBrush QualityBrush => ArmoryNames.QualityBrush(Quality ?? -1);
    [JsonIgnore] public string EffectText => Enchant?.Text ?? string.Empty;
    [JsonIgnore] public string ColorText => Color switch
    {
        1 => "Meta", 2 => "Red", 4 => "Yellow", 8 => "Blue", 6 => "Orange",
        10 => "Purple", 12 => "Green", 14 => "Prismatic", _ => string.Empty
    };
    private Bitmap? _iconImage;
    [JsonIgnore] public Bitmap? IconImage
    {
        get => _iconImage;
        set { _iconImage = value; PropertyChanged?.Invoke(this, new(nameof(IconImage))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ArmorySocket
{
    public int Index { get; set; }
    public int Color { get; set; }
}

public sealed class ArmorySocketView
{
    public int Index { get; init; }
    public int Color { get; init; }
    public ArmoryGem? Gem { get; init; }
    public bool HasGem => Gem is not null;
    public string Label => Color > 0 ? ArmoryNames.SocketColorName(Color) : $"Socket {Index + 1}";
}

public sealed class ArmoryItemSpell
{
    public int Id { get; set; }
    public int Trigger { get; set; }
    public int Charges { get; set; }
    public double PpmRate { get; set; }
    public int Cooldown { get; set; }
    public int Category { get; set; }
    public int CategoryCooldown { get; set; }
    public string Name { get; set; } = string.Empty;
    [JsonIgnore] public string Text => string.IsNullOrWhiteSpace(Name) ? string.Empty :
        (Trigger switch { 0 or 5 => "Use: ", 1 => "Equip: ", 2 => "Chance on hit: ",
            6 => "Learn: ", _ => string.Empty }) + Name;
}

public sealed class ArmoryEquipmentItem
{
    public int Slot { get; set; }
    public int Entry { get; set; }
    public int DisplayId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int Quality { get; set; }
    public int ItemLevel { get; set; }
    public int ItemClass { get; set; }
    public int SubClass { get; set; }
    public int InventoryType { get; set; }
    public int RequiredLevel { get; set; }
    public int Bonding { get; set; }
    public int Armor { get; set; }
    public int Block { get; set; }
    public int Delay { get; set; }
    public int CurrentDurability { get; set; }
    public int MaxDurability { get; set; }
    public int HolyRes { get; set; }
    public int FireRes { get; set; }
    public int NatureRes { get; set; }
    public int FrostRes { get; set; }
    public int ShadowRes { get; set; }
    public int ArcaneRes { get; set; }
    public string Description { get; set; } = string.Empty;
    public List<ArmoryItemStat> Stats { get; set; } = new();
    public List<ArmoryItemDamage> Damage { get; set; } = new();
    public List<int> SocketColors { get; set; } = new();
    public string Enchantments { get; set; } = string.Empty;

    public bool? EnchantmentsValid { get; set; }
    public ArmoryEnchant? PermanentEnchant { get; set; }
    public ArmoryEnchant? TemporaryEnchant { get; set; }
    public ArmoryEnchant? PrismaticEnchant { get; set; }
    public List<ArmoryGem> Gems { get; set; } = new();
    public List<ArmorySocket>? Sockets { get; set; }
    public ArmoryEnchant? SocketBonus { get; set; }
    public int SocketBonusId { get; set; }
    public List<ArmoryItemSpell> Spells { get; set; } = new();
    public double? WeaponSpeed { get; set; }
    public double? WeaponDps { get; set; }

    [JsonIgnore] public string EnchantText => EnchantmentsValid == false ? string.Empty :
        string.Join(Environment.NewLine, new[] {
            EnchantLine("Enchanted", PermanentEnchant), EnchantLine("Temporary enchant", TemporaryEnchant),
            EnchantLine("Prismatic enchant", PrismaticEnchant) }.Where(x => x.Length > 0));
    private static string EnchantLine(string label, ArmoryEnchant? enchant) =>
        string.IsNullOrWhiteSpace(enchant?.Text) ? string.Empty : $"{label}: {enchant.Text}";
    [JsonIgnore] public string SocketBonusText => EnchantmentsValid == false ? string.Empty :
        EnchantLine("Socket Bonus", SocketBonus);
    [JsonIgnore] public string SpellsText => string.Join(Environment.NewLine,
        Spells.Select(x => x.Text).Where(x => x.Length > 0));
    [JsonIgnore] public IReadOnlyList<ArmorySocketView> SocketViews
    {
        get
        {
            var sockets = Sockets ?? SocketColors.Select((color, index) =>
                new ArmorySocket { Index = index, Color = color }).ToList();
            var gems = EnchantmentsValid == false ? new List<ArmoryGem>() : Gems;
            return sockets.Select(x => x.Index).Union(gems.Select(x => x.SocketIndex)).OrderBy(x => x)
                .Select(index => new ArmorySocketView { Index = index,
                    Color = sockets.FirstOrDefault(x => x.Index == index)?.Color ?? 0,
                    Gem = gems.FirstOrDefault(x => x.SocketIndex == index) })
                .Where(x => x.Color != 0 || x.HasGem).ToArray();
        }
    }

    [JsonIgnore] public string SlotName => ArmoryNames.SlotName(Slot);
    [JsonIgnore] public string SlotAbbreviation => ArmoryNames.SlotAbbreviation(Slot);
    [JsonIgnore] public string QualityName => ArmoryNames.QualityName(Quality);
    [JsonIgnore] public string Detail => $"iLvl {ItemLevel} • Entry {Entry}";
    [JsonIgnore] public string QualityDetail => $"{QualityName} • iLvl {ItemLevel}";
    [JsonIgnore] public IBrush QualityBrush => ArmoryNames.QualityBrush(Quality);
    [JsonIgnore] public string BindingText => ArmoryNames.BondingText(Bonding);
    [JsonIgnore] public string TypeText => ArmoryNames.ItemTypeText(ItemClass, SubClass, InventoryType);
    [JsonIgnore] public string RequiredLevelText => RequiredLevel > 0 ? $"Requires Level {RequiredLevel}" : string.Empty;
    [JsonIgnore] public string DurabilityText =>
        MaxDurability > 0 ? $"Durability {CurrentDurability} / {MaxDurability}" : string.Empty;
    [JsonIgnore] public string ArmorText => Armor > 0 ? $"{Armor:N0} Armor" : string.Empty;
    [JsonIgnore] public string BlockText => Block > 0 ? $"{Block:N0} Block" : string.Empty;
    [JsonIgnore] public string DamageText => string.Join(Environment.NewLine,
        Damage.Select(d => $"{d.Min:0.#} - {d.Max:0.#} {d.SchoolName} Damage"));
    [JsonIgnore] public string SpeedText => (WeaponSpeed ?? Delay / 1000.0) is var speed && speed > 0
        ? $"Speed {speed:0.00}" : string.Empty;
    [JsonIgnore] public string DpsText
    {
        get
        {
            var speed = WeaponSpeed ?? Delay / 1000.0;
            var dps = WeaponDps ?? (speed > 0 && Damage.Count > 0
                ? Damage.Sum(d => (d.Min + d.Max) / 2.0) / speed : (double?)null);
            return dps.HasValue ? $"({dps:0.0} damage per second)" : string.Empty;
        }
    }

    [JsonIgnore] public IReadOnlyList<string> ResistanceLines
    {
        get
        {
            var values = new List<string>();
            if (HolyRes != 0) values.Add($"{HolyRes:+#;-#;0} Holy Resistance");
            if (FireRes != 0) values.Add($"{FireRes:+#;-#;0} Fire Resistance");
            if (NatureRes != 0) values.Add($"{NatureRes:+#;-#;0} Nature Resistance");
            if (FrostRes != 0) values.Add($"{FrostRes:+#;-#;0} Frost Resistance");
            if (ShadowRes != 0) values.Add($"{ShadowRes:+#;-#;0} Shadow Resistance");
            if (ArcaneRes != 0) values.Add($"{ArcaneRes:+#;-#;0} Arcane Resistance");
            return values;
        }
    }

    [JsonIgnore] public IReadOnlyList<string> SocketLines =>
        SocketColors.Select(ArmoryNames.SocketColorName).ToArray();

    [JsonIgnore] public string StatsText =>
        string.Join(Environment.NewLine, Stats.Select(x => x.Display));

    [JsonIgnore] public string ResistancesText =>
        string.Join(Environment.NewLine, ResistanceLines);

    [JsonIgnore] public string SocketsText =>
        string.Join(Environment.NewLine, SocketLines);
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
    public bool HasTooltip => Item is not null;
    public IReadOnlyList<ArmoryItemStat> TooltipStats => Item?.Stats ?? new List<ArmoryItemStat>();
    public IReadOnlyList<string> TooltipResistances => Item?.ResistanceLines ?? Array.Empty<string>();
    public IReadOnlyList<string> TooltipSockets => Item?.SocketLines ?? Array.Empty<string>();

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

    public static string StatName(int type) => type switch
    {
        0 => "Mana",
        1 => "Health",
        3 => "Agility",
        4 => "Strength",
        5 => "Intellect",
        6 => "Spirit",
        7 => "Stamina",
        12 => "Defense Rating",
        13 => "Dodge Rating",
        14 => "Parry Rating",
        15 => "Block Rating",
        16 => "Melee Hit Rating",
        17 => "Ranged Hit Rating",
        18 => "Spell Hit Rating",
        19 => "Melee Critical Strike Rating",
        20 => "Ranged Critical Strike Rating",
        21 => "Spell Critical Strike Rating",
        28 => "Melee Haste Rating",
        29 => "Ranged Haste Rating",
        30 => "Spell Haste Rating",
        31 => "Hit Rating",
        32 => "Critical Strike Rating",
        35 => "Resilience Rating",
        36 => "Haste Rating",
        37 => "Expertise Rating",
        38 => "Attack Power",
        39 => "Ranged Attack Power",
        43 => "Mana per 5 sec",
        44 => "Armor Penetration Rating",
        45 => "Spell Power",
        46 => "Health Regeneration",
        47 => "Spell Penetration",
        48 => "Block Value",
        _ => $"Stat {type}"
    };

    public static string DamageSchoolName(int type) => type switch
    {
        0 => "Physical",
        1 => "Holy",
        2 => "Fire",
        3 => "Nature",
        4 => "Frost",
        5 => "Shadow",
        6 => "Arcane",
        _ => "Physical"
    };

    public static string BondingText(int bonding) => bonding switch
    {
        1 => "Binds when picked up",
        2 => "Binds when equipped",
        3 => "Binds when used",
        4 or 5 => "Quest Item",
        _ => string.Empty
    };

    public static string ItemTypeText(int itemClass, int subClass, int inventoryType)
    {
        var slot = inventoryType switch
        {
            1 => "Head",
            2 => "Neck",
            3 => "Shoulder",
            4 => "Shirt",
            5 or 20 => "Chest",
            6 => "Waist",
            7 => "Legs",
            8 => "Feet",
            9 => "Wrist",
            10 => "Hands",
            11 => "Finger",
            12 => "Trinket",
            13 => "One-Hand",
            14 => "Off Hand",
            15 or 26 => "Ranged",
            16 => "Back",
            17 => "Two-Hand",
            19 => "Tabard",
            21 => "Main Hand",
            22 => "Off Hand",
            23 => "Held In Off-hand",
            25 => "Thrown",
            28 => "Relic",
            _ => string.Empty
        };

        string subtype = itemClass switch
        {
            4 => subClass switch
            {
                1 => "Cloth",
                2 => "Leather",
                3 => "Mail",
                4 => "Plate",
                6 => "Shield",
                7 => "Libram",
                8 => "Idol",
                9 => "Totem",
                10 => "Sigil",
                _ => string.Empty
            },
            2 => subClass switch
            {
                0 => "Axe",
                1 => "Two-Handed Axe",
                2 => "Bow",
                3 => "Gun",
                4 => "Mace",
                5 => "Two-Handed Mace",
                6 => "Polearm",
                7 => "Sword",
                8 => "Two-Handed Sword",
                10 => "Staff",
                13 => "Fist Weapon",
                15 => "Dagger",
                16 => "Thrown",
                18 => "Crossbow",
                19 => "Wand",
                20 => "Fishing Pole",
                _ => "Weapon"
            },
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(slot))
            return subtype;
        if (string.IsNullOrWhiteSpace(subtype))
            return slot;
        return $"{slot} • {subtype}";
    }

    public static string SocketColorName(int color)
    {
        var parts = new List<string>();
        if ((color & 1) != 0) parts.Add("Meta Socket");
        if ((color & 2) != 0) parts.Add("Red Socket");
        if ((color & 4) != 0) parts.Add("Yellow Socket");
        if ((color & 8) != 0) parts.Add("Blue Socket");
        return parts.Count == 0 ? $"Socket {color}" : string.Join(" / ", parts);
    }

}