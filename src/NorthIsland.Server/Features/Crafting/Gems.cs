using NorthIsland.GameData.Items;

namespace NorthIsland.Server.Features.Crafting;

/// <summary>The rules for stones mounted in equipment: which stones exist, which pieces take them, and the attribute bonus they give.</summary>
public static class Gems
{
    /// <summary>Item type of the attribute stones (<see cref="StoneType"/>) and of the critical chance stones (<see cref="CriticalStoneType"/>).</summary>
    public const byte StoneType = 52, CriticalStoneType = 54;

    /// <summary>Item type of the guns, which take no stone.</summary>
    public const byte GunType = 11;

    /// <summary>Item sub type of the weapons that have a critical chance of their own; they take no critical stone.</summary>
    public const byte CriticalSubType = 137;

    /// <summary>Item flag of the item-mall equipment (the +1 to +10 weapons, the Kung-Fu Suits, ...): "can be enhanced but not socketed".</summary>
    public const ushort MallFlag = 0x200;

    /// <summary>Item sub type of the class manteaus, the only accessories that take a stone.</summary>
    public const byte ManteauSubType = 142;

    /// <summary>Equipment slot numbers of the weapon (<see cref="WeaponSlot"/>) and the accessory (<see cref="AccessorySlot"/>).</summary>
    public const byte WeaponSlot = 3, AccessorySlot = 6;

    /// <summary>Tells whether an item is a stone that can be mounted.</summary>
    /// <param name="item">The item record to test.</param>
    /// <returns>True for an attribute stone or a critical chance stone.</returns>
    public static bool IsStone(ItemRecord item) => item.Type is StoneType or CriticalStoneType;

    /// <summary>Checks whether a stone may be mounted in a piece of equipment.</summary>
    /// <param name="equipment">The item record of the piece.</param>
    /// <param name="mounted">The item id of the stone already in the piece, 0 for none.</param>
    /// <param name="stone">The item record of the stone to mount.</param>
    /// <returns><see cref="GemRefusal.None"/> when it may be mounted, otherwise the reason it may not.</returns>
    public static GemRefusal CanMount(ItemRecord equipment, ushort mounted, ItemRecord stone)
    {
        if (mounted != 0)
            return GemRefusal.AlreadyMounted;
        if (equipment.EquipSlot == AccessorySlot)
            return equipment.SubType == ManteauSubType ? GemRefusal.None : GemRefusal.SpecialItem;
        if (equipment.Type == GunType)
            return GemRefusal.Gun;
        if ((equipment.Flags & MallFlag) != 0)
            return GemRefusal.MallEquipment;
        if (stone.Type == CriticalStoneType)
            return equipment.EquipSlot != WeaponSlot
                ? GemRefusal.NotWeapon
                : equipment.SubType == CriticalSubType ? GemRefusal.CriticalWeapon : GemRefusal.None;
        return GemRefusal.None;
    }

    /// <summary>Works out the attribute points a mounted stone adds to each positive attribute line of a piece.</summary>
    /// <param name="equipment">The item record of the piece.</param>
    /// <param name="stone">The item record of the mounted stone, or null for none.</param>
    /// <returns>
    /// Twice the stone's rank for a piece with one attribute line, the rank for two lines, otherwise 0 (also for a critical stone).
    /// </returns>
    public static int Bonus(ItemRecord equipment, ItemRecord? stone)
    {
        if (stone is not { Type: StoneType })
            return 0;
        var lines = 0;
        for (var i = 0; i < Math.Min(equipment.Words36.Length, equipment.Ints42.Length); i++)
        {
            if (equipment.Words36[i] is not (0 or 250) && equipment.Ints42[i] > 100) // 250 EquipmentClass
                lines++;
        }
        return lines switch
        {
            1 => stone.ShopColumn * 2, // one attribute line: twice the stone's rank
            2 => stone.ShopColumn, // two lines: the stone's rank
            _ => 0 // any other number of lines: no bonus
        };
    }

    /// <summary>The attribute points each fusion level adds to a piece with one raised attribute line; with two lines each gets half.</summary>
    public const int FusionPerLevel = 2;

    /// <summary>
    /// Works out the attribute points a fusion level adds to each raised line of a piece: <see cref="FusionPerLevel"/> per level for a piece
    /// with one line that a fusion raises (HP, SP, max HP, max SP, ATK, DEF, SPD, MATK, MDEF), one per level for a piece with two such lines,
    /// as the client shows it.
    /// </summary>
    /// <param name="equipment">The item record of the piece.</param>
    /// <param name="fusion">The fusion level of the piece.</param>
    /// <returns>The points added to each raised line; 0 when no line is raised.</returns>
    public static int FusionBonus(ItemRecord equipment, byte fusion)
    {
        var lines = 0;
        for (var i = 0; i < Math.Min(equipment.Words36.Length, equipment.Ints42.Length); i++)
        {
            if (equipment.Words36[i] is 25 or 26 or 207 or 208 or 210 or 211 or (>= 214 and <= 216) && equipment.Ints42[i] > 100)
                lines++;
        }
        return lines switch
        {
            1 => fusion * FusionPerLevel, // one raised line: the full bonus
            2 => fusion * FusionPerLevel / 2, // two lines: half each
            _ => 0 // any other number of lines: no bonus
        };
    }

    /// <summary>Works out the value of one attribute of a piece including its mounted stone and fusion level.</summary>
    /// <param name="equipment">The item record of the piece.</param>
    /// <param name="stone">The item record of the mounted stone, or null for none.</param>
    /// <param name="attr">The attribute code.</param>
    /// <param name="fusion">The fusion level of the piece.</param>
    /// <returns>The attribute value; a piece that does not give the attribute (value 0 or less) gets no bonus.</returns>
    public static int Effect(ItemRecord equipment, ItemRecord? stone, byte attr, byte fusion = 0)
    {
        var own = equipment.Effect(attr);
        return own > 0 ? own + Bonus(equipment, stone) + FusionBonus(equipment, fusion) : own;
    }

    /// <summary>Reads the critical chance a mounted stone gives.</summary>
    /// <param name="stone">The item record of the mounted stone, or null for none.</param>
    /// <returns>The chance in percent; 0 when there is no stone or it is not a critical stone.</returns>
    public static int CriticalPercent(ItemRecord? stone) => stone is { Type: CriticalStoneType } ? stone.CriticalPercent : 0;

    /// <summary>Reads the critical chance a weapon has of its own.</summary>
    /// <param name="weapon">The item record of the equipped weapon, or null for none.</param>
    /// <returns>The chance in percent; 0 when there is no weapon or the item is not in the weapon slot.</returns>
    public static int WeaponCriticalPercent(ItemRecord? weapon) => weapon is { EquipSlot: WeaponSlot } ? weapon.CriticalPercent : 0;

    /// <summary>Item sub type of the pestles, weapons whose physical hit halves the target's SP.</summary>
    public const byte PestleSubType = 134;

    /// <summary>Tells whether a weapon is a pestle.</summary>
    /// <param name="weapon">The item record of the equipped weapon, or null for none.</param>
    /// <returns>True when the weapon has the pestle sub type.</returns>
    public static bool HalvesTargetSp(ItemRecord? weapon) => weapon is { EquipSlot: WeaponSlot, SubType: PestleSubType };
}
