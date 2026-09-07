using System.Collections.Generic;
using System.IO;
using Portalkeeper.Models;

namespace Portalkeeper.Services;

// Build a render-only projection. Slot/tooltip objects always retain their real item data.
public static class ArmoryVisualEquipment
{
    public static ArmoryCharacter Select(ArmoryCharacter character, bool showTransmog)
    {
        if (!showTransmog) return character;
        var equipment = new List<ArmoryEquipmentItem>();
        foreach (var item in character.Equipment)
        {
            var appearance = item.Transmog;
            if (appearance is null) { equipment.Add(item); continue; }
            if (appearance.Hidden) continue;
            if (!appearance.Resolved || appearance.DisplayId <= 0 || appearance.Entry <= 0)
                throw new InvalidDataException("Applied transmog appearance is unresolved.");
            equipment.Add(new ArmoryEquipmentItem
            {
                Slot = item.Slot, Entry = appearance.Entry, DisplayId = appearance.DisplayId,
                InventoryType = appearance.InventoryType, ItemClass = appearance.ItemClass,
                SubClass = appearance.SubClass, Name = appearance.Name, Quality = appearance.Quality,
                Icon = appearance.Icon
            });
        }
        return new ArmoryCharacter
        {
            Id = character.Id, Name = character.Name, Race = character.Race, Gender = character.Gender,
            Class = character.Class, Level = character.Level, Playerbot = character.Playerbot,
            Appearance = character.Appearance, Equipment = equipment
        };
    }
}
