using NorthIsland.GameData.Items;
using NorthIsland.GameData.Npcs;
using NorthIsland.GameData.Skills;

namespace NorthIsland.Server.Features.Battles;

/// <summary>
/// The normal attack a fighter makes with its worn weapon, as the client picks it (rules/SkillUse.cpp, weaponAttackSkill and
/// checkCanUseSkill): the server plays this attack whatever normal attack id the client sends, so a bow's attack cannot be sent with a
/// sword to make the attack one that is not countered.
/// </summary>
public static class NormalAttacks
{
    /// <summary>The weapon kind the client gives the two bazookas, which are weapons of their own kind (real item types are 0 to 11).</summary>
    private const int BazookaKind = 250;

    /// <summary>The item ids of the Bazooka and the Great Bazooka.</summary>
    private const ushort Bazooka = 20037, GreatBazooka = 20038; // 20037 Bazooka, 20038 Great Bazooka

    /// <summary>The weapon kind of a worn weapon, as the client reckons it.</summary>
    /// <param name="weapon">The Item.dat record of the worn weapon, or null for bare hands.</param>
    /// <returns>0 for bare hands or a weapon without a rank, <see cref="BazookaKind"/> for a bazooka, otherwise the weapon's item type.</returns>
    /// <remarks>The client reads the rank from Item.dat offset 51, which <see cref="ItemRecord.ShopColumn"/> holds.</remarks>
    public static int WeaponKindOf(ItemRecord? weapon) =>
        weapon is null || weapon.ShopColumn == 0 ? 0 // no weapon, or a weapon without a rank: bare hands
            : weapon.Id is Bazooka or GreatBazooka ? BazookaKind : weapon.Type;

    /// <summary>The normal attack of a weapon kind.</summary>
    /// <param name="weaponKind">The weapon kind (see <see cref="WeaponKindOf"/>).</param>
    /// <param name="pet">True for the attack of a pet, which differs from a player's for bows, guns and bazookas.</param>
    /// <returns>The skill id of the attack, 0 when the weapon kind has none.</returns>
    public static ushort WeaponAttack(int weaponKind, bool pet) => weaponKind switch
    {
        0 => SkillTable.HandAttack, // bare hands: 10001 Hand Attack
        1 or 2 or 6 or 8 => 10002, // knives and falchions, swords, claws, axes: 10002 Blade Attack
        7 or 9 => 10003, // kind 7 (no items in the data), clubs and pestles: 10003 Pestle Attack
        5 or 10 => 10004, // wands and staves, fans: 10004 Spell Attack
        3 => 10005, // spears and halberds: 10005 Sword Attack
        4 => pet ? (ushort)10020 : (ushort)10019, // bows: 10020 NPC Bow Attack for a pet, 10019 Bow Attack for a player
        11 => pet ? (ushort)10018 : (ushort)10007, // guns: 10018 NPC Gun Attack for a pet, 10007 Gun Attack for a player
        BazookaKind => pet ? (ushort)10027 : (ushort)10026, // bazookas: 10027 NPC Bazooka Attack for a pet, 10026 Bazooka Attack for a player
        _ => 0, // any other weapon kind: no attack
    };

    /// <summary>The Monster Attack a pet of direction kind 1 makes with weapon flag 0.</summary>
    private const ushort MonsterAttackUnarmed = 10023; // 10023 Monster Attack

    /// <summary>The Monster Attack a pet of direction kind 1 makes with weapon flag 1 or 2.</summary>
    private const ushort MonsterAttackArmed = 10010; // 10010 Monster Attack

    /// <summary>
    /// The normal attack of a pet, as the client picks it (checkCanUseSkill for fighter kind 4): its fixed skill if it has one; otherwise, for
    /// direction kind 1, 10023 Monster Attack with weapon flag 0 and 10010 Monster Attack with weapon flag 1 or 2, and for direction kinds 0 and 2
    /// the pet's attack of the weapon it wears. The client keeps the id it was given for the other direction kinds (and for kind 1 with another
    /// weapon flag, which no NPC has); the server gives those the weapon's attack too, so no pet chooses its own.
    /// </summary>
    /// <param name="npc">The pet's Npc.dat record, or null when it is unknown (then it counts as direction kind 0 without a fixed skill).</param>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count (bare hands then).</param>
    /// <param name="equipment">The pet's worn items.</param>
    /// <returns>The skill id of the attack, 0 when the weapon has none.</returns>
    public static ushort OfPet(NpcRecord? npc, Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment)
    {
        if (npc is { FixedSkill: not 0 })
            return npc.FixedSkill;
        if (npc is { DirectionKind: 1 }) // direction kind 1: the attack is chosen by the pet's weapon flag
        {
            switch (npc.Weapon)
            {
                case 0: // weapon flag 0
                    return MonsterAttackUnarmed;
                case 1 or 2: // weapon flags 1 and 2
                    return MonsterAttackArmed;
            }
        }
        return WeaponAttack(WeaponKindOf(Worn(items, equipment)), pet: true);
    }

    /// <summary>The normal attack of a player from the weapon they wear.</summary>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count (bare hands then).</param>
    /// <param name="equipment">The player's worn items.</param>
    /// <returns>The skill id of the attack, 0 when the weapon has none.</returns>
    public static ushort OfPlayer(Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment) =>
        WeaponAttack(WeaponKindOf(Worn(items, equipment)), pet: false);

    /// <summary>The record of the weapon a player or pet wears.</summary>
    /// <param name="items">Looks up an item record by id, or null when worn items do not count.</param>
    /// <param name="equipment">The worn items.</param>
    /// <returns>The weapon's record, or null for bare hands, an unknown item or when worn items do not count.</returns>
    private static ItemRecord? Worn(Func<ushort, ItemRecord?>? items, List<EquippedItem> equipment) =>
        equipment.Find(e => e.Slot == Gems.WeaponSlot) is { } worn ? items?.Invoke(worn.ItemId) : null;
}
