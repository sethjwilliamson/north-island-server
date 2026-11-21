namespace NorthIsland.GameData.Items;

/// <summary>One decoded Item.Dat record; the fields without a known meaning carry their offset in the record in the name.</summary>
public sealed record ItemRecord
{
    /// <summary>The item id.</summary>
    public required ushort Id { get; init; }

    /// <summary>The item name.</summary>
    public required string Name { get; init; }

    /// <summary>The item type.</summary>
    public byte Type { get; init; }

    /// <summary>Id of the item's icon.</summary>
    public ushort IconId { get; init; }

    /// <summary>Five words at record offset 26; the first is used as a shop picture for equipment.</summary>
    public ushort[] Words26 { get; init; } = [];

    /// <summary>Two words at offset 36: the attribute ids of the item's effects, paired with <see cref="Ints42"/>.</summary>
    public ushort[] Words36 { get; init; } = [];

    /// <summary>Two bytes at offset 40; nothing reads them.</summary>
    public byte[] Bytes40 { get; init; } = [];

    /// <summary>Two integers at offset 42: for each effect in <see cref="Words36"/>, 100 plus its amount.</summary>
    public int[] Ints42 { get; init; } = [];

    /// <summary>Byte at offset 50, meaning not established.</summary>
    public byte Unknown50 { get; init; }

    /// <summary>Shop column of the item; 2 or more marks items sold elsewhere than in the plain NPC shop lists.</summary>
    public byte ShopColumn { get; init; }

    /// <summary>
    /// Equipment slot, 1 to 6 for worn equipment (see <see cref="IsEquipment"/>); other values mark non-wearable or special
    /// items.
    /// </summary>
    public byte EquipSlot { get; init; }

    /// <summary>Sub type within the item type.</summary>
    public byte SubType { get; init; }

    /// <summary>First of four colour pattern tables, four integers at offset 54.</summary>
    public int[] ColourPattern1 { get; init; } = [];

    /// <summary>Second colour pattern table, four integers at offset 70.</summary>
    public int[] ColourPattern2 { get; init; } = [];

    /// <summary>Third colour pattern table, four integers at offset 86.</summary>
    public int[] ColourPattern3 { get; init; } = [];

    /// <summary>Fourth colour pattern table, four integers at offset 102.</summary>
    public int[] ColourPattern4 { get; init; } = [];

    /// <summary>Byte at offset 118, meaning not established.</summary>
    public byte Unknown118 { get; init; }

    /// <summary>Character level needed to use or wear the item.</summary>
    public byte RequiredLevel { get; init; }

    /// <summary>NPC store price.</summary>
    public int Price { get; init; }

    /// <summary>Price an NPC store pays when the item is sold to it.</summary>
    public int SellPrice { get; init; }

    /// <summary>
    /// Who may wear the item, at offset 128 (the client's FUsableBy). Players (the client's canWear): 1 body types 1 and 3; 2 body types 2 and 4;
    /// 4 to 7 body type 1 to 4; 12 the Wind element; 8, 9 and 13 no player; any other value (0, 3, 10, 11, ...) anyone. Pets (the client's
    /// petEquipRefusal): 0 and 8 any pet; 9 and 10 pets of direction kind 0; 11 and 13 pets of direction kind 1; 12 Wind pets; any other value no
    /// pet. Whatever the value, a saddle on a pet of direction kind other than 0 always fits. An item that names a pet (<see cref="PetNpcId"/>
    /// not 0) skips this field for players and pets: no player wears it, and only the pet it names (or its career form) does.
    /// </summary>
    public byte UsableBy { get; init; }

    /// <summary>
    /// Flag bits of the item; the server tests individual bits (for example to refuse stalls, storage or furniture use).
    /// </summary>
    public ushort Flags { get; init; }

    /// <summary>Integer at offset 131, meaning not established.</summary>
    public int Unknown131 { get; init; }

    /// <summary>Byte at offset 135, meaning not established.</summary>
    public byte Unknown135 { get; init; }

    /// <summary>Integer at offset 136, meaning not established.</summary>
    public int Unknown136 { get; init; }

    /// <summary>Id of the skill linked to the item.</summary>
    public ushort LinkedSkillId { get; init; }

    /// <summary>Five words at offset 142; the first is the item's alchemy class.</summary>
    public ushort[] Words142 { get; init; } = [];

    /// <summary>The item description shown to the player.</summary>
    public string Description { get; init; } = "";

    /// <summary>Critical hit percentage parsed from <see cref="Description"/>, 0 when it names none.</summary>
    public int CriticalPercent { get; init; }

    /// <summary>Seconds without random encounters parsed from <see cref="Description"/>, 0 when it names none.</summary>
    public int NoEncounterSeconds { get; init; }

    /// <summary>
    /// The furniture size on the x, y and z axes, the three bytes at offset 407 (the client's furnSizeX, furnSizeY and furnSizeZ); a piece placed
    /// in a house has all three above 0.
    /// </summary>
    public byte[] FurnitureSize { get; init; } = [];

    /// <summary>Word at offset 410, meaning not established.</summary>
    public ushort Unknown410 { get; init; }

    /// <summary>Width of the item in inventory cells.</summary>
    public byte CellWidth { get; init; }

    /// <summary>Height of the item in inventory cells.</summary>
    public byte CellHeight { get; init; }

    /// <summary>Byte at offset 414, meaning not established.</summary>
    public byte Unknown414 { get; init; }

    /// <summary>Three words at offset 415; the third (offset 419) is the item's pet NPC id, see <see cref="PetNpcId"/>.</summary>
    public ushort[] Words415 { get; init; } = [];

    /// <summary>Six bytes at offset 421; nothing reads them.</summary>
    public byte[] Bytes421 { get; init; } = [];

    /// <summary>Usage period of the item, in hours.</summary>
    public ushort UsePeriod { get; init; }

    /// <summary>Four words at offset 429; the first is the number of uses of an item with a use limit.</summary>
    public ushort[] Words429 { get; init; } = [];

    /// <summary>Five integers at offset 437; nothing reads them.</summary>
    public int[] Ints437 { get; init; } = [];

    /// <summary>
    /// The NPC id at offset 419, the third word of <see cref="Words415"/> (the client's petNpcId): for equipment the pet that alone may wear it,
    /// players never; for other items the NPC they name (the pet of a pet voucher, the shape of a Riceball). 0 when the item names none.
    /// </summary>
    public ushort PetNpcId => Words415.Length > 2 ? Words415[2] : (ushort)0;

    /// <summary>The flag bit of the items that pets cannot wear (the client's PetEquipping).</summary>
    public const ushort NotForPetsFlag = 64;

    /// <summary>True when the item can be worn in an equipment slot 1 to 6.</summary>
    public bool IsEquipment => EquipSlot is >= 1 and <= 6;

    /// <summary>
    /// The attribute effects of the item: each attribute id of <see cref="Words36"/> whose paired amount is not 100, with the
    /// amount minus 100.
    /// </summary>
    public IReadOnlyList<(byte Attr, int Amount)> Effects
    {
        get
        {
            List<(byte, int)> result = [];
            for (var i = 0; i < Math.Min(Words36.Length, Ints42.Length); i++)
            {
                if (Words36[i] is > 0 and <= byte.MaxValue && Ints42[i] != 100)
                    result.Add(((byte)Words36[i], Ints42[i] - 100));
            }
            return result;
        }
    }

    /// <summary>Sums the amounts of the item's effects on one attribute.</summary>
    /// <param name="attr">The attribute id.</param>
    /// <returns>The total amount, 0 when there is none.</returns>
    public int Effect(byte attr) => Effects.Where(e => e.Attr == attr).Sum(e => e.Amount);

    /// <summary>The sub type of the items that restore HP and SP in full (the Pizzas: "Recovers HP and SP at most").</summary>
    public const byte FullRestoreSubType = 123;

    /// <summary>The amount given for a full restore, larger than any maximum HP or SP.</summary>
    public const int FullRestoreAmount = 1_000_000;

    /// <summary>The HP (attribute 25) and SP (attribute 26) the item restores, with the amount.</summary>
    public IReadOnlyList<(byte Attr, int Amount)> Restores
    {
        get
        {
            if (SubType == FullRestoreSubType)
                return [(25, FullRestoreAmount), (26, FullRestoreAmount)];
            List<(byte, int)> result = [];
            for (var i = 0; i < Math.Min(Words36.Length, Ints42.Length); i++)
            {
                if (Words36[i] is 25 or 26 && Ints42[i] > 100) // 25 HP, 26 SP
                    result.Add(((byte)Words36[i], Ints42[i] - 100));
            }
            return result;
        }
    }
}
