using NorthIsland.GameData.Formulas;
using NorthIsland.GameData.Items;
using NorthIsland.GameData.Npcs;
using NorthIsland.Protocol.Messages.Player;

namespace NorthIsland.Server.Features.PetCare;

/// <summary>Helpers for a player's pets: capture rules, levelling, exile, creation and the wire description.</summary>
public static class Pets
{
    /// <summary>Tells why a monster cannot be taken as a pet.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="npc">The monster's NPC record.</param>
    /// <param name="pending">The NPC ids of pets already won in the running battle but not yet on the team.</param>
    /// <returns>The reason, or null when the monster can be taken.</returns>
    public static string? RefusalFor(Character c, NpcRecord npc, IReadOnlyCollection<ushort> pending) =>
        npc.Uncatchable ? "cannot be caught"
            : c.Pets.Count + pending.Count >= Pet.MaxPets ? "the team is full"
                : c.Pets.Exists(p => p.NpcId == npc.Id) || pending.Contains(npc.Id) ? "already has one"
                    : null;

    /// <summary>The item sub type of the Monster Net.</summary>
    public const byte MonsterNetSubType = 140;

    /// <summary>Finds the Monster Net the character wears that is not used up.</summary>
    /// <param name="c">The character.</param>
    /// <param name="items">Looks up an item record by id.</param>
    /// <returns>The worn net, or null when there is none.</returns>
    public static EquippedItem? WornMonsterNet(Character c, Func<ushort, ItemRecord?> items) =>
        c.Equipment.Find(e => e.Slot == Gems.AccessorySlot && !Durability.IsDestroyed(e.Wear) && items(e.ItemId) is { SubType: MonsterNetSubType });

    /// <summary>Uses up one use of a Monster Net by adding wear to it.</summary>
    /// <param name="net">The worn net.</param>
    /// <param name="item">The net's item record, which holds its number of uses.</param>
    /// <returns>True when the net is now used up.</returns>
    public static bool SpendMonsterNet(EquippedItem net, ItemRecord item)
    {
        var uses = Math.Max((ushort)1, item.Words429.FirstOrDefault());
        net.Wear = (byte)Math.Min(Durability.Destroyed, net.Wear + Durability.Destroyed / uses);
        return net.Wear >= Durability.Destroyed;
    }

    /// <summary>Tells why a monster cannot be captured: the team rules, its level, or its HP.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="npc">The monster's NPC record.</param>
    /// <param name="targetHp">The monster's current HP.</param>
    /// <param name="targetMaxHp">The monster's maximum HP.</param>
    /// <param name="pending">The NPC ids of pets already won in the running battle but not yet on the team.</param>
    /// <param name="hpBelowPercent">The percent of maximum HP at or above which the monster is refused; 0 turns the rule off.</param>
    /// <param name="levelsAbove">How many levels above the catcher the monster may be.</param>
    /// <returns>The reason, or null when the monster can be captured.</returns>
    public static string? CaptureRefusalFor(
        Character c,
        NpcRecord npc,
        int targetHp,
        int targetMaxHp,
        IReadOnlyCollection<ushort> pending,
        int hpBelowPercent,
        int levelsAbove = 0) =>
            RefusalFor(c, npc, pending)
                ?? (npc.Level > c.Level + Math.Max(0, levelsAbove) ? "its level is above the catcher's"
                    : hpBelowPercent > 0 && (long)targetHp * 100 >= (long)targetMaxHp * hpBelowPercent ? "it is not weak enough"
                        : null);

    /// <summary>
    /// Raises a pet to a higher level: each level gives its points one at a time to a random ability that is still below the cap of the pet's
    /// potential (its potential floor plus <see cref="ItemEffectHandlers.PetAbilityRoom"/>), with each ability's chance in proportion to its
    /// current value (a pet with INT 50 and AGI 1 puts a point on AGI 1 time in 51), then HP and SP are recalculated and refilled.
    /// </summary>
    /// <param name="pet">The pet.</param>
    /// <param name="level">The new level; nothing happens when it is not above the current one.</param>
    /// <param name="f">The formula table, for max HP and SP.</param>
    /// <param name="pointsPerLevel">The ability points the pet gains per level.</param>
    /// <param name="roll">Picks a number from 0 up to but not including its argument; tests replace it.</param>
    /// <returns>True when the pet gained levels.</returns>
    public static bool LevelUp(
        Pet pet,
        byte level,
        FormulaTable f,
        int pointsPerLevel,
        Func<int, int> roll)
    {
        if (level <= pet.Level)
            return false;
        var points = (level - pet.Level) * Math.Max(0, pointsPerLevel);
        pet.Level = level;
        var cap = ItemEffectHandlers.PotentialFloor[Math.Min(pet.Potential, (byte)(ItemEffectHandlers.PotentialFloor.Length - 1))]
            + ItemEffectHandlers.PetAbilityRoom;
        ushort[] abilities = [pet.Strength, pet.Constitution, pet.Intelligence, pet.Wisdom, pet.Agility];
        for (var i = 0; i < points; i++)
        {
            var room = Enumerable.Range(0, abilities.Length).Where(a => abilities[a] < cap).ToList();
            if (room.Count == 0)
                break;
            // a number below the sum of the open abilities picks the ability whose share it falls in; all at 0 share equally
            var weight = room.Sum(a => abilities[a]);
            var pick = weight == 0 ? room[roll(room.Count)] : room[0];
            if (weight > 0)
            {
                var at = roll(weight);
                foreach (var a in room)
                {
                    if (at < abilities[a])
                    {
                        pick = a;
                        break;
                    }
                    at -= abilities[a];
                }
            }
            abilities[pick]++;
        }
        (pet.Strength, pet.Constitution, pet.Intelligence, pet.Wisdom, pet.Agility) =
            (abilities[0], abilities[1], abilities[2], abilities[3], abilities[4]);
        var total = FormulaTable.TotalLevel(pet.Level, pet.Reborn);
        pet.MaxHp = f.MaxHp(total, pet.Constitution, 0, 0);
        pet.MaxSp = f.MaxSp(total, pet.Wisdom, 0, 0);
        pet.Hp = pet.MaxHp;
        pet.Sp = pet.MaxSp;
        return true;
    }

    /// <summary>Removes a pet from the team, resting and dismounting it first when it is active or ridden.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="character">The owner's character.</param>
    /// <param name="pet">The pet to remove.</param>
    /// <param name="world">The world state, for the map broadcast.</param>
    /// <param name="combat">The combat stats, resent when the owner gets off the pet.</param>
    public static void Exile(
        GameSession session,
        Character character,
        Pet pet,
        WorldState world,
        CombatStats combat)
    {
        if (character.ActivePetId == pet.NpcId)
        {
            character.ActivePetId = 0;
            session.Send(PetMessages.Rest());
            world.HidePet(session);
        }
        if (session.RidingPetSlot == pet.Slot)
        {
            session.RidingPetSlot = 0;
            var off = PetMessages.Dismounted(session.Account!.LoginId);
            session.Send(off);
            world.Broadcast(session, off, sameMapOnly: true);
            combat.Send(session);
        }
        character.Pets.Remove(pet);
        session.Send(PetMessages.Removed(session.Account!.LoginId, pet.Slot));
    }

    /// <summary>Finds the first free team slot.</summary>
    /// <param name="c">The character.</param>
    /// <returns>The slot 1..4, or 0 when the team is full.</returns>
    public static byte FreeSlot(Character c)
    {
        for (byte slot = 1; slot <= Pet.MaxPets; slot++)
        {
            if (!c.Pets.Exists(p => p.Slot == slot))
                return slot;
        }
        return 0;
    }

    /// <summary>
    /// Creates a pet from a monster record, with full HP and SP. A new pet starts at level 1 however it was obtained (an egg, a pack, a voucher,
    /// a capture); only a returning human pet or a traded pet keeps its level, and those are not made here.
    /// </summary>
    /// <param name="npc">The monster's NPC record.</param>
    /// <param name="slot">The team slot.</param>
    /// <param name="formulas">The formula table for HP, SP and exp; null uses a fixed rule for HP and SP.</param>
    /// <param name="level">The level to start at.</param>
    /// <returns>The new pet.</returns>
    public static Pet Create(NpcRecord npc, byte slot, FormulaTable? formulas, byte level = 1)
    {
        var startLevel = Math.Max((byte)1, level);
        var pet = new Pet
        {
            Slot = slot,
            NpcId = npc.Id,
            Name = npc.Name,
            Level = startLevel,
            Strength = npc.Strength,
            Constitution = npc.Constitution,
            Intelligence = npc.Intelligence,
            Wisdom = npc.Wisdom,
            Agility = npc.Agility,
            Element = npc.Element,
            SkillLevels = [.. Enumerable.Range(0, 3).Select(i => i < npc.SkillSlots.Count && npc.SkillSlots[i] != 0 ? (byte)1 : (byte)0)],
        };
        if (formulas is not null)
        {
            var total = FormulaTable.TotalLevel(startLevel, 0);
            pet.Exp = formulas.TotalLevelExp(startLevel, 0);
            pet.MaxHp = formulas.MaxHp(total, pet.Constitution, 0, 0);
            pet.MaxSp = formulas.MaxSp(total, pet.Wisdom, 0, 0);
        }
        else
        {
            pet.MaxHp = 40 + 10 * startLevel;
            pet.MaxSp = 10 + 5 * startLevel;
        }
        pet.Hp = pet.MaxHp;
        pet.Sp = pet.MaxSp;
        return pet;
    }

    /// <summary>Creates the pet for a captured monster: level 1, with the configured amity.</summary>
    /// <param name="npc">The monster's NPC record.</param>
    /// <param name="slot">The team slot.</param>
    /// <param name="formulas">The formula table for HP, SP and exp.</param>
    /// <param name="options">The pet options, which hold the captured pet's amity.</param>
    /// <param name="maxAmity">The highest amity allowed.</param>
    /// <returns>The new pet.</returns>
    public static Pet Caught(
        NpcRecord npc,
        byte slot,
        FormulaTable? formulas,
        PetOptions options,
        int maxAmity)
    {
        var pet = Create(npc, slot, formulas, 1);
        if (options.CapturedPetAmity > 0)
            pet.Amity = (byte)Math.Clamp(options.CapturedPetAmity, 0, Math.Clamp(maxAmity, 1, byte.MaxValue));
        return pet;
    }

    /// <summary>Gives a pet its starting equipment, once.</summary>
    /// <param name="pet">The pet.</param>
    /// <param name="table">The starting items by pet NPC id.</param>
    /// <param name="items">Looks up an item record by id.</param>
    /// <returns>True when equipment was given; false when the pet had it already or has no entry.</returns>
    public static bool GiveStartingGear(Pet pet, IReadOnlyDictionary<int, List<ushort>> table, Func<ushort, NorthIsland.GameData.Items.ItemRecord?> items)
    {
        if (pet.StartingGearGiven || !table.TryGetValue(pet.NpcId, out var gear))
            return false;
        pet.StartingGearGiven = true;
        foreach (var itemId in gear)
        {
            if (items(itemId) is { IsEquipment: true } record && !pet.Equipment.Exists(e => e.Slot == record.EquipSlot))
                pet.Equipment.Add(new EquippedItem { Slot = record.EquipSlot, ItemId = itemId });
        }
        return true;
    }

    /// <summary>Builds the wire description of a pet.</summary>
    /// <param name="p">The pet.</param>
    /// <returns>The pet info for the packets.</returns>
    public static PetInfo Info(Pet p) => new()
    {
        Slot = p.Slot,
        NpcId = p.NpcId,
        Name = p.Name,
        Exp = p.Exp,
        Level = p.Level,
        Hp = (ushort)Math.Clamp(p.Hp, 0, ushort.MaxValue),
        Sp = (ushort)Math.Clamp(p.Sp, 0, ushort.MaxValue),
        Strength = p.Strength,
        Constitution = p.Constitution,
        Intelligence = p.Intelligence,
        Wisdom = p.Wisdom,
        Agility = p.Agility,
        Amity = p.Amity,
        Tradable = p.Tradable,
        SkillPoints = p.SkillPoints,
        Reborn = p.Reborn,
        Potential = p.Potential,
        Skills = [.. Enumerable.Range(0, 3).Select(i => (p.SkillLevels.ElementAtOrDefault(i), p.SkillExp.ElementAtOrDefault(i)))],
        Equipment = [
            .. p.Equipment.OrderBy(e => e.Slot)
                .Select(
                    e =>
                        new Protocol.Messages.Items.EquipEntry
                        {
                            ItemId = e.ItemId,
                            Wear = e.Wear,
                            Insert = e.Insert,
                            FusionLevel = e.Fusion,
                            LookItemId = e.Pose
                        })
        ],
    };
}
