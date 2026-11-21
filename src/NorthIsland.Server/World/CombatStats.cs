using System.Collections.Immutable;
using NorthIsland.GameData.Formulas;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.World;

/// <summary>
/// Works out the five combat values (ATK, DEF, MATK, MDEF, SPD) of characters, pets and monsters and sends the character's
/// to the client.
/// </summary>
/// <param name="formulas">The Formula.dat tables.</param>
/// <param name="items">Item.dat, for the effects of worn items.</param>
/// <param name="options">The battle options, for the saddle divisor.</param>
public sealed class CombatStats(
    FormulaData formulas,
    ItemData items,
    Microsoft.Extensions.Options.IOptions<BattleOptions> options,
    NpcData? npcs = null)
{
    /// <summary>The index of SPD in the five-value arrays (ATK, DEF, MATK, MDEF, SPD).</summary>
    public const int SpeedIndex = 4;

    /// <summary>The attribute ids (41 to 45) of ATK, DEF, MATK, MDEF and SPD in the set-attribute packet.</summary>
    public const byte AttrAtk = 41, AttrDef = 42, AttrMatk = 43, AttrMdef = 44, AttrSpd = 45;

    /// <summary>The attribute ids of the worn items' max HP bonus and max SP bonus (207, 208).</summary>
    public const byte AttrMaxHpBonus = 207, AttrMaxSpBonus = 208;

    /// <summary>The attribute ids that carry the riding extra of ATK, DEF, MATK, MDEF and SPD, in that order.</summary>
    public static readonly ImmutableArray<byte> RidingExtraAttrs = [57, 51, 52, 53, 54];

    /// <summary>The item attribute ids, in value order, whose worn-item bonus goes into each of the five values.</summary>
    private static readonly ImmutableArray<byte> ItemAttrs = [
        Battle.AttrAttack,
        Battle.AttrDefence,
        Battle.AttrMagicAttack,
        Battle.AttrMagicDefence,
        Battle.AttrSpeed
    ];

    /// <summary>The saddle item ids with the value index each feeds (0 ATK, 1 DEF, 2 MATK, 3 MDEF, 4 SPD).</summary>
    public static readonly ImmutableArray<(ushort Saddle, int Value)> Saddles = [
        (25535, 0), // Bash Saddle
        (25539, 1), // Defence Saddle
        (25536, 2), // Magic Saddle
        (25625, 3), // Spirit Saddle
        (25208, 4)]; // Swift Saddle

    /// <summary>Item id of the Tenacity Saddle, which lends the rider a third of the ridden pet's max HP.</summary>
    public const ushort TenacitySaddle = 25641; // Tenacity Saddle

    /// <summary>Adds up the bonus of one attribute over the worn items that are not destroyed, with their inserted stone and fusion.</summary>
    /// <param name="equipment">The worn items.</param>
    /// <param name="attr">The item attribute id.</param>
    /// <returns>The total bonus.</returns>
    public int Bonus(IEnumerable<EquippedItem> equipment, byte attr) =>
        equipment.Sum(
            e => !Durability.IsDestroyed(e.Wear) && items[e.ItemId] is { } r ? Gems.Effect(r, e.Insert == 0 ? null : items[e.Insert], attr, e.Fusion) : 0);

    /// <summary>Recalculates the character's derived values, such as max HP and SP, with the max bonuses of the worn items.</summary>
    /// <param name="c">The character.</param>
    public void Recalculate(Character c) => formulas.Recalculate(c, MaxBonus(c, AttrMaxHpBonus), MaxBonus(c, AttrMaxSpBonus));

    /// <summary>
    /// The bonus of an attribute from the worn items, plus the character's spring bonus for max HP and max SP, plus what a ridden pet's Tenacity
    /// Saddle lends for max HP.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <param name="attr">The attribute id.</param>
    /// <returns>The bonus.</returns>
    public int MaxBonus(Character c, byte attr) =>
        Bonus(c.Equipment, attr) + (attr is AttrMaxHpBonus or AttrMaxSpBonus ? c.SpringBonus : 0) + (attr == AttrMaxHpBonus ? RidingHp(c) : 0);

    /// <summary>The attribute ids whose pet bonuses are sent to the client.</summary>
    private static readonly ImmutableArray<byte> PetBonusAttrs = [
        Battle.AttrAttack,
        Battle.AttrDefence,
        Battle.AttrSpeed,
        Battle.AttrMagicAttack,
        Battle.AttrMagicDefence,
        AttrMaxHpBonus,
        AttrMaxSpBonus
    ];

    /// <summary>Sends the client the worn-item bonus of each attribute for a pet, and after a change of its worn items its new values.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="pet">The pet.</param>
    /// <param name="changed">True when the pet's worn items changed: its maxima and five values are worked out again and sent.</param>
    public void SendPetBonuses(GameSession session, Pet pet, bool changed = true)
    {
        foreach (var attr in PetBonusAttrs)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, attr, Bonus(pet.Equipment, attr)));
        if (!changed)
            return;
        var (maxHp, maxSp, hp, sp) = (pet.MaxHp, pet.MaxSp, pet.Hp, pet.Sp);
        RecalculatePet(pet);
        if (pet.MaxHp != maxHp)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, Action8.AttrMaxHp, pet.MaxHp));
        if (pet.MaxSp != maxSp)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, Action8.AttrMaxSp, pet.MaxSp));
        if (pet.Hp != hp)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, Action8.AttrHp, pet.Hp));
        if (pet.Sp != sp)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, Action8.AttrSp, pet.Sp));
        SendPetValues(session, pet);
    }

    /// <summary>
    /// Works out a pet's max HP and SP from its level, CON / WIS and the max HP / SP bonus of its worn items (the 205 / 206 the
    /// client shows include the bonus), and keeps HP and SP within them.
    /// </summary>
    /// <param name="pet">The pet.</param>
    /// <param name="fill">True to fill HP and SP up to the new maxima (a level gained).</param>
    public void RecalculatePet(Pet pet, bool fill = false)
    {
        if (formulas.Table is not { } f)
            return;
        var total = FormulaTable.TotalLevel(pet.Level, pet.Reborn);
        pet.MaxHp = f.MaxHp(total, pet.Constitution, Bonus(pet.Equipment, AttrMaxHpBonus), 0);
        pet.MaxSp = f.MaxSp(total, pet.Wisdom, Bonus(pet.Equipment, AttrMaxSpBonus), 0);
        pet.Hp = fill ? pet.MaxHp : Math.Min(pet.Hp, pet.MaxHp);
        pet.Sp = fill ? pet.MaxSp : Math.Min(pet.Sp, pet.MaxSp);
    }

    /// <summary>Sends a pet's five shown values (ATK, DEF, MATK, MDEF, SPD; 8/2 attributes 41-45), which the client does not work out after a change.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="pet">The pet.</param>
    public void SendPetValues(GameSession session, Pet pet)
    {
        var values = PetValues(pet);
        for (var i = 0; i < values.Length; i++)
            session.Send(PetMessages.SetAttribute((byte)pet.Slot, (byte)(AttrAtk + i), values[i]));
    }

    /// <summary>A pet's five values (ATK, DEF, MATK, MDEF, SPD) from its level, stats, worn items and element.</summary>
    /// <param name="p">The pet.</param>
    /// <returns>The five values; zeros without Formula.dat.</returns>
    public int[] PetValues(Pet p)
    {
        if (formulas.Table is not { } f)
            return new int[5];
        var level = FormulaTable.TotalLevel(p.Level, p.Reborn);
        int[] stats = [p.Strength, p.Constitution, p.Intelligence, p.Wisdom, p.Agility];
        int[] values = [
            .. Enumerable.Range(0, 5).Select(i => f.CombatValue(i, level, stats[i], Bonus(p.Equipment, ItemAttrs[i]), ElementMatches(i, p.Element), false))
        ];
        if (WearsWorkingBrooch(p))
        {
            values[0] *= 2;
            values[2] *= 2;
        }
        return values;
    }

    /// <summary>The sub type of the Friendship Brooches: "Double ATK and MATK after equipped by human pets with amity at 100."</summary>
    public const byte FriendshipBroochSubType = 136;

    /// <summary>The amity a pet needs for the Friendship Brooch to work.</summary>
    public const int FriendshipBroochAmity = 100;

    /// <summary>The Npc.dat type of human characters (Roca, Sano, Houyi); animal pets are type 7.</summary>
    public const byte HumanNpcType = 4;

    /// <summary>Whether a pet's Friendship Brooch doubles its ATK and MATK: a human pet at full amity wearing an unbroken brooch.</summary>
    /// <param name="p">The pet.</param>
    /// <returns>True when the brooch works.</returns>
    public bool WearsWorkingBrooch(Pet p) =>
        p.Amity >= FriendshipBroochAmity && npcs?[p.NpcId] is { Type: HumanNpcType }
            && p.Equipment.Exists(e => !Durability.IsDestroyed(e.Wear) && items[e.ItemId] is { SubType: FriendshipBroochSubType });

    /// <summary>A monster's SPD.</summary>
    /// <param name="npc">The monster's Npc.dat record.</param>
    /// <returns>The SPD value.</returns>
    public int MonsterSpeed(NorthIsland.GameData.Npcs.NpcRecord npc) => MonsterValues(npc)[SpeedIndex];

    /// <summary>A monster's five values (ATK, DEF, MATK, MDEF, SPD) from its Npc.dat level, stats and element.</summary>
    /// <param name="npc">The monster's Npc.dat record.</param>
    /// <returns>The five values; zeros without Formula.dat.</returns>
    public int[] MonsterValues(NorthIsland.GameData.Npcs.NpcRecord npc)
    {
        if (formulas.Table is not { } f)
            return new int[5];
        int[] stats = [npc.Strength, npc.Constitution, npc.Intelligence, npc.Wisdom, npc.Agility];
        return [.. Enumerable.Range(0, 5).Select(i => f.CombatValue(i, npc.Level, stats[i], 0, ElementMatches(i, npc.Element), false))];
    }

    /// <summary>
    /// The riding extra of each of the five values: a share of the ridden pet's value for each saddle it wears (a third, or for the Defence and
    /// Spirit Saddles <see cref="BattleOptions.DefenceSaddleDivisor"/>), plus the reborn Knight's share of the pet's SPD.
    /// </summary>
    /// <param name="c">The rider.</param>
    /// <param name="ridden">The pet being ridden, null on foot.</param>
    /// <returns>The five extras, all 0 on foot.</returns>
    public int[] RidingExtras(Character c, Pet? ridden)
    {
        var extras = new int[5];
        if (ridden is null)
            return extras;
        var pet = PetValues(ridden);
        foreach (var (saddle, value) in Saddles)
            if (ridden.Equipment.Exists(e => e.ItemId == saddle))
                extras[value] = pet[value] / (value is 1 or 3 ? Math.Max(1, options.Value.DefenceSaddleDivisor) : 3) + KnightSaddleBonus(c, pet);
        return extras;
    }

    /// <summary>The max HP a ridden pet's Tenacity Saddle lends the rider: a third of the pet's max HP, plus the reborn Knight's share of its SPD.</summary>
    /// <param name="c">The rider.</param>
    /// <returns>The extra max HP; 0 on foot or when the pet wears no Tenacity Saddle.</returns>
    public int RidingHp(Character c) =>
        Ridden(c) is { } ridden && ridden.Equipment.Exists(e => e.ItemId == TenacitySaddle) ? ridden.MaxHp / 3 + KnightSaddleBonus(c, PetValues(ridden)) : 0;

    /// <summary>
    /// What a reborn Knight adds to the value its mount's saddle lends: <see cref="BattleOptions.KnightSaddleSpeedPercent"/> of the mount's SPD,
    /// rounded.
    /// </summary>
    /// <param name="c">The rider.</param>
    /// <param name="pet">The ridden pet's five values.</param>
    /// <returns>The bonus; 0 for anyone but a reborn Knight.</returns>
    private int KnightSaddleBonus(Character c, int[] pet) =>
        c.RebornCount > 0 && c.Job == Career.Knight
            ? (int)Math.Round(pet[SpeedIndex] * options.Value.KnightSaddleSpeedPercent / 100.0, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>Whether the element is the one that boosts a value (3 for ATK and MATK, 1 for DEF and MDEF, 4 for SPD).</summary>
    /// <param name="value">The value index: 0 ATK, 1 DEF, 2 MATK, 3 MDEF, 4 SPD.</param>
    /// <param name="element">The element id.</param>
    /// <returns>True when it boosts the value.</returns>
    private static bool ElementMatches(int value, byte element) => element == (value switch
    {
        0 or 2 => 3, // ATK and MATK: element 3
        1 or 3 => 1, // DEF and MDEF: element 1
        _ => 4 // SPD: element 4
    });

    /// <summary>
    /// A character's five values (ATK, DEF, MATK, MDEF, SPD) from level, stats, worn items, job, element, riding and timed
    /// effects.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <param name="ridden">The pet being ridden, null on foot.</param>
    /// <returns>The five values; zeros without Formula.dat.</returns>
    public int[] Values(Character c, Pet? ridden = null)
    {
        if (formulas.Table is not { } f)
            return new int[5];
        var level = FormulaTable.TotalLevel(c.Level, c.RebornCount);
        var extras = RidingExtras(c, ridden);
        int[] stats = [c.Strength, c.Constitution, c.Intelligence, c.Wisdom, c.Agility];
        bool Job(int i) => i switch
        {
            0 => c.Job == Career.Killer, // ATK: true for a Killer
            1 => c.Job == Career.Warrior, // DEF: true for a Warrior
            2 => c.Job == Career.Wit, // MATK: true for a Wit
            3 => c.Job == Career.Priest, // MDEF: true for a Priest
            _ => c.Job is Career.Knight or Career.Seer, // SPD: true for a Knight or a Seer
        };
        var now = TimedEffects.Now;
        return [
            .. Enumerable.Range(0, 5)
                .Select(
                    i =>
                        f.CombatValue(
                            i,
                            level,
                            stats[i],
                            Bonus(c.Equipment, ItemAttrs[i]),
                            ElementMatches(i, c.Element),
                            Job(i),
                            extras[i] + TimedEffects.StatBonus(c, i, now)))
        ];
    }

    /// <summary>Finds the pet the session's character is riding.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The pet, or null when on foot.</returns>
    public static Pet? Ridden(GameSession session) => session.ActiveCharacter is { } c ? Ridden(c) : null;

    /// <summary>Finds the pet a character is riding.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The pet, or null when on foot.</returns>
    public static Pet? Ridden(Character c) => c.RidingPetSlot == 0 ? null : c.Pets.Find(p => p.Slot == c.RidingPetSlot);

    /// <summary>
    /// Recalculates the character's values and sends the client the max bonuses, changed HP and SP, the riding extras and
    /// the five values.
    /// </summary>
    /// <param name="session">The player's session.</param>
    public void Send(GameSession session)
    {
        if (session.ActiveCharacter is not { } c)
            return;
        var (hp, sp, maxHp, maxSp) = (c.Hp, c.Sp, c.MaxHp, c.MaxSp);
        Recalculate(c);
        foreach (var attr in new[]
        {
            Battle.AttrAttack,
            Battle.AttrDefence,
            Battle.AttrSpeed,
            Battle.AttrMagicAttack,
            Battle.AttrMagicDefence,
            AttrMaxHpBonus,
            AttrMaxSpBonus
        })
            session.Send(InnMessages.SetAttribute(attr, MaxBonus(c, attr)));
        // Changed maxima too: a client that does not add the 207 / 208 bonus to the maxima itself shows them only from these.
        if (c.MaxHp != maxHp)
            session.Send(InnMessages.SetAttribute(Action8.AttrMaxHp, c.MaxHp));
        if (c.MaxSp != maxSp)
            session.Send(InnMessages.SetAttribute(Action8.AttrMaxSp, c.MaxSp));
        if (c.Hp != hp)
            session.Send(InnMessages.SetAttribute(Action8.AttrHp, c.Hp));
        if (c.Sp != sp)
            session.Send(InnMessages.SetAttribute(Action8.AttrSp, c.Sp));
        var ridden = Ridden(session);
        var extras = RidingExtras(c, ridden);
        for (var i = 0; i < extras.Length; i++)
            session.Send(InnMessages.SetAttribute(RidingExtraAttrs[i], extras[i]));
        var values = Values(c, ridden);
        for (var i = 0; i < values.Length; i++)
            session.Send(InnMessages.SetAttribute((byte)(AttrAtk + i), values[i]));
    }
}
