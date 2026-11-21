namespace NorthIsland.GameData.Npcs;

/// <summary>One Npc.Dat record, with the fields the server uses.</summary>
public sealed record NpcRecord
{
    /// <summary>The NPC id.</summary>
    public required ushort Id { get; init; }

    /// <summary>The NPC name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The NPC type; 7 is a monster.</summary>
    public byte Type { get; init; }

    /// <summary>The NPC level.</summary>
    public byte Level { get; init; }

    /// <summary>Hit points.</summary>
    public int Hp { get; init; }

    /// <summary>Spell points.</summary>
    public int Sp { get; init; }

    /// <summary>Strength attribute.</summary>
    public ushort Strength { get; init; }

    /// <summary>Constitution attribute.</summary>
    public ushort Constitution { get; init; }

    /// <summary>Intelligence attribute.</summary>
    public ushort Intelligence { get; init; }

    /// <summary>Wisdom attribute.</summary>
    public ushort Wisdom { get; init; }

    /// <summary>Agility attribute.</summary>
    public ushort Agility { get; init; }

    /// <summary>Element id.</summary>
    public byte Element { get; init; }

    /// <summary>Ids of the skills the NPC has, empty slots left out.</summary>
    public IReadOnlyList<ushort> Skills { get; init; } = [];

    /// <summary>Ids of the items the NPC can drop or carry, empty slots left out.</summary>
    public IReadOnlyList<ushort> Items { get; init; } = [];

    /// <summary>The three skill slots with empty ones kept as 0, so a slot keeps its position.</summary>
    public IReadOnlyList<ushort> SkillSlots { get; init; } = [];

    /// <summary>True when the NPC cannot be captured.</summary>
    public bool Uncatchable { get; init; }

    /// <summary>True when the NPC may not be attacked from the PK cursor.</summary>
    public bool NoPk { get; init; }

    /// <summary>Pet key the reborn wish knows the NPC by, 1 to 27; 0 when it is none of them.</summary>
    public byte GrowKey { get; init; }

    /// <summary>The NPC's index in the monster book; 0 when it is not in the book.</summary>
    public ushort Key { get; init; }

    /// <summary>
    /// How the NPC as a pet picks its normal attack and which gear it may wear: 0 and 2 attack with the worn weapon, 1 with an attack chosen by
    /// <see cref="Weapon"/>; gear usable by 9 or 10 fits kind 0 and gear usable by 11 or 13 fits kind 1.
    /// </summary>
    public byte DirectionKind { get; init; }

    /// <summary>The weapon flag of a pet of direction kind 1: 0 attacks with 10023 Monster Attack, 1 and 2 with 10010 Monster Attack.</summary>
    public byte Weapon { get; init; }

    /// <summary>The skill the NPC as a pet uses for its normal attack whatever it wears; 0 when it has none.</summary>
    public ushort FixedSkill { get; init; }

    /// <summary>Non-zero for a soldier (mercenary) pet, whose gear cannot change and which cannot go to another player.</summary>
    public ushort Mercenary { get; init; }

    /// <summary>True when the NPC is a soldier (mercenary) pet.</summary>
    public bool IsMercenary => Mercenary != 0;

    /// <summary>True when the NPC type is monster (7).</summary>
    public bool IsMonster => Type == 7;
}
