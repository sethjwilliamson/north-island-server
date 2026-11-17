using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Events;

namespace NorthIsland.Server.Features.Events;

/// <summary>Decides whether the conditions of an event script hold for a character.</summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Splits an event's conditions into groups: a group starts at the first condition and at every condition whose record has a link other than 0,
    /// and collects the results of its conditions.
    /// </summary>
    /// <param name="ev">The event to split.</param>
    /// <returns>The groups in script order.</returns>
    public static IEnumerable<ConditionGroup> Groups(EveEvent ev)
    {
        List<EveScriptRecord>? conditions = null;
        List<EveScriptRecord>? results = null;
        foreach (var condition in ev.Conditions)
        {
            if (conditions is null || condition.Record.Link != 0)
            {
                if (conditions is not null)
                    yield return new ConditionGroup(ev.Index, conditions, results!);
                conditions = [];
                results = [];
            }
            conditions.Add(condition.Record);
            results!.AddRange(condition.Results);
        }
        if (conditions is not null)
            yield return new ConditionGroup(ev.Index, conditions, results!);
    }

    /// <summary>Tells whether every condition of a group holds.</summary>
    /// <param name="group">The group to test.</param>
    /// <param name="character">The character the event runs for.</param>
    /// <param name="run">The running event, which holds the state some conditions read.</param>
    /// <param name="logger">The log, for conditions that are not implemented.</param>
    /// <returns>True when all conditions hold.</returns>
    public static bool Holds(ConditionGroup group, Character character, EventRun run, ILogger logger) =>
        group.Conditions.All(c => Holds(c, character, run, logger, group.EventIndex));

    /// <summary>Tells whether one condition record holds; a condition kind that is not implemented counts as false and is logged.</summary>
    /// <param name="c">The condition record.</param>
    /// <param name="character">The character the event runs for.</param>
    /// <param name="run">The running event, which holds the state some conditions read.</param>
    /// <param name="logger">The log, for conditions that are not implemented.</param>
    /// <param name="eventIndex">The index of the event the condition belongs to, for the script clock of date conditions.</param>
    /// <returns>True when the condition holds.</returns>
    public static bool Holds(
        EveScriptRecord c,
        Character character,
        EventRun run,
        ILogger logger,
        int eventIndex = 0)
    {
        switch (c.Kind)
        {
            case 1: // kind 1: always holds
                return true;

            case 2 when c.SubKind == 1 && c.Arg1 == 1: // 2/1/1: item count compared
                return Compare(ItemCount(character, c.Arg2, c.Arg3, c.Value), c.Arg3, c.Value);
            case 2 when c.SubKind == 1 && c.Arg1 == 2: // 2/1/2: gold compared
                return Compare(character.Gold, c.Arg3, c.Value);
            case 2 when c.SubKind == 1 && c.Arg1 == 3: // 2/1/3: level compared
                return Compare(character.Level, c.Arg3, c.Value);
            case 2 when c.SubKind == 1 && c.Arg1 == 5: // 2/1/5: hire tier of the run compared
                return Compare(run.HireTier, c.Arg3, c.Value);
            case 2 when c.SubKind == 1 && c.Arg1 == 4: // 2/1/4: riding vehicle Arg2, compared as 0 when riding it and 1 when not (op 5 riding, 6 not)
                return Compare(run.RidingVehicle != 0 && run.RidingVehicle == c.Arg2 ? 0 : 1, c.Arg3, c.Value);
            case 2 when c.SubKind == 4: // 2/4: element compared with the argument
                return Compare(character.Element, c.Arg3, c.Arg1);
            case 2 when c.SubKind == 5: // 2/5: career (job) compared with the argument, as 2/4 the element (the data's only use: the
                // Bead Guardian of Chinshi Huang's Tomb teaches each career's skill, Arg1 1 Killer's Murder to 6 Seer's Foretell)
                return Compare(character.Job, c.Arg3, c.Arg1);

            case 20 when c.SubKind == 2: // 20/2: day of the week (0 Sunday to 6 Saturday) compared with the argument (the data's only
                // use: the Athens Guard's Sunday Cursed Palace switch, 20/2/0 equal and not equal)
                return Compare((int)run.Clock().DayOfWeek, c.Arg3, c.Arg1);

            case 2 when c.SubKind == 3 && c.Arg1 is 7 or 8: // 2/3/7 and 2/3/8: body type is male (7) or not (8)
                return MarriageRegistry.IsMale(character.BodyType) == (c.Arg1 == 7);

            case 16 when c.SubKind is >= 2 and <= 7: // 16/2-7: item is worn in the slot sub kind - 1
                return Compare(character.Equipment.Exists(e => e.Slot == c.SubKind - 1 && e.ItemId == c.Arg1) ? 0 : 1, c.Arg3, c.Value);

            case 2 when c.SubKind == 2 && c.Arg1 == 1: // 2/2/1: the team has pet Arg2
                return character.Pets.Exists(p => p.NpcId == c.Arg2);
            case 2 when c.SubKind == 2 && c.Arg1 == 2: // 2/2/2: the team has no pet Arg2
                return !character.Pets.Exists(p => p.NpcId == c.Arg2);
            case 2 when c.SubKind == 2 && c.Arg1 == 8: // 2/2/8: pet Arg2 is in the team or in storage
                return character.Pets.Exists(p => p.NpcId == c.Arg2) || character.StoredPets.Exists(p => p.NpcId == c.Arg2);
            case 2 when c.SubKind == 2 && c.Arg1 == 9: // 2/2/9: pet Arg2 is in neither the team nor storage
                return !(character.Pets.Exists(p => p.NpcId == c.Arg2) || character.StoredPets.Exists(p => p.NpcId == c.Arg2));
            case 2 when c.SubKind == 2 && c.Arg1 == 5: // 2/2/5: free pet places in the team compared
                return Compare(Pet.MaxPets - character.Pets.Count, c.Arg3, c.Value);
            case 2 when c.SubKind == 2 && c.Arg1 == 7: // 2/2/7: amity of team pet Arg2 compared
                return character.Pets.Find(p => p.NpcId == c.Arg2) is { } pet && Compare(pet.Amity, c.Arg3, c.Value);
            case 2 when c.SubKind == 2 && c.Arg1 == 6: // 2/2/6: id of the active pet compared
                return Compare(character.ActivePetId, c.Arg3, c.Value);
            case 2 when c.SubKind == 2 && c.Arg1 == 10: // 2/2/10: level of team pet Arg2 compared, as 2/2/7 its amity (the data's only use: Elin above level 39, Elin's Plasma Wafer)
                return character.Pets.Find(p => p.NpcId == c.Arg2) is { } leveled && Compare(leveled.Level, c.Arg3, c.Value);
            case 2 when c.SubKind == 2 && c.Arg1 == 11: // 2/2/11: level of the active pet compared, 0 when none
                return Compare(character.Pets.Find(p => p.NpcId == character.ActivePetId && character.ActivePetId != 0)?.Level ?? 0, c.Arg3, c.Value);

            case 9 when c.SubKind == 2 && c.Arg1 is >= 1 and <= 3: // 9/2/1-3: castle war is on (1), is off (2), or castle guild member
                return c.Arg1 switch
                {
                    1 => run.Castles?.WarOn == true, // castle war is on
                    2 => run.Castles?.WarOn != true, // castle war is off
                    _ => run.CastleGuildMember // the player is in a castle guild
                };

            case 9 when c.SubKind == 3 && c.Arg1 == 1: // 9/3/1: the castle has an owner guild (1) or not (0), compared; inferred: the
                // Trojan round battles pick the small battle 1 when it has (op 6, != 0) and the full garrison battle 2 when not (op 5, == 0)
                return Compare(run.Castles?.Owner is null ? 0 : 1, c.Arg3, c.Value);

            case 9 when c.SubKind == 1 && c.Arg1 == 0: // 9/1/0: guild id compared
                return Compare((int)Math.Min(run.GuildId, int.MaxValue), c.Arg3, c.Value);

            case 18: // 18: date condition: now is at or after (Value3 1) or before the moment
                return EventDate(c) is { } moment && run.ScriptNow(eventIndex) is var now && (c.Value3 == 1 ? now >= moment : now < moment);

            case 13 when c.SubKind == 4: // 13/4: team members without a guild compared
                return Compare(run.TeamWithoutGuild, c.Arg3, c.Value);

            case 13 when c.SubKind == 1: // 13/1: number of team players compared
                return Compare(run.TeamPlayers, c.Arg3, c.Value);

            case 5 when c.Arg1 == 1: // 5 with Arg1 1: count of mark SubKind compared
                if (run.IsCounter(c.SubKind) && !character.Marks.Exists(m => m.Id == c.SubKind))
                    return false;
                return c.Arg3 != 0 && Compare(character.MarkCount(c.SubKind), c.Arg3, c.Value);

            case 5 when c.Arg1 == 2: // 5 with Arg1 2: the character has no mark SubKind
                return character.MarkCount(c.SubKind) == 0;

            case 5 when c.Arg1 == 5 && c.Arg2 is >= 1 and <= 8: // 5 with Arg1 5: bit Arg2 of mark SubKind compared (the bits 3/x/1 results set)
                return Compare((character.MarkCount(c.SubKind) >> (c.Arg2 - 1)) & 1, c.Arg3, c.Value);

            case 3 when c.Arg1 == 3: // 3 with Arg1 3: state of NPC SubKind compared
                return Compare(run.NpcStates.GetValueOrDefault(c.SubKind), c.Arg3, c.Value);

            case 4 when c.SubKind == 1 && c.Arg1 is 1 or 2: // 4/1: battle outcome is won (Arg1 1) or lost (2)
                return run.BattleOutcome == (c.Arg1 == 1 ? BattleOutcome.Won : BattleOutcome.Lost);

            case 6 when c.SubKind is 1 or 2: // 6/1 and 6/2: no group is held
                return !run.AnyGroupHeld;

            case 7: // 7: the last menu answer matches the sub kind and code
                return run.Answer is { } answer && answer.Interact == c.SubKind && answer.Code == c.Arg1;

            case 8 when run.Rolls.TryGetValue(c.SubKind, out var roll): // 8: the stored roll for SubKind compared
                return Compare(roll, c.Arg3, c.Value);

            case 19 when c.Arg1 is 1 or 2: // 19: instance value of SubKind, zero (Arg1 2) or compared (1)
                return InstanceValue(c.SubKind, run) is var value && (c.Arg1 == 2 ? value == 0 : Compare(value, c.Arg3, c.Value));

            case 15 when c.SubKind is 2 or 3: // 15/2 and 15/3: the bag has room (2) or no room (3) for one item Arg1
                return run.RoomFor(c.Arg1) == (c.SubKind == 2);

            case 15 when c.SubKind == 1: // 15/1: free bag places compared
                return Compare(BagItem.LastSlot - character.Bag.Count, c.Arg3, c.Value);

            case 14 when c.Arg1 is 1 or 2: // 14: script cooldown SubKind is running (Arg1 1) or not (2)
                return character.CooldownRunning(c.SubKind, GameClock.UtcNow) == (c.Arg1 == 1);

            case 10 when c.Arg1 == 1: // 10: the level of skill SubKind compared; a skill not known counts as level 0
                return Compare(character.Skill(c.SubKind)?.Level ?? 0, c.Arg3, c.Value);

            default: // unimplemented kind: logged, counts as false
                logger.LogDebug("{Source}: condition {Kind}/{SubKind}/{Arg1} not implemented, treated as false", run.Source, c.Kind, c.SubKind, c.Arg1);
                return false;
        }
    }

    /// <summary>Tells whether <see cref="Holds(EveScriptRecord, Character, EventRun, ILogger, int)"/> implements a condition.</summary>
    /// <param name="c">The condition record.</param>
    /// <returns>True when the kind and sub kind (and the argument where it matters) are implemented.</returns>
    public static bool IsSupported(EveScriptRecord c) => c.Kind switch
    {
        1 or 7 or 8 => true, // kinds 1, 7, 8: always implemented
        2 => c.SubKind == 1 && c.Arg1 is >= 1 and <= 5 || c.SubKind == 2 && c.Arg1 is 1 or 2 or 5 or 6 or 7 or 8 or 9 or 10 or 11 || c.SubKind == 4
            || c.SubKind == 3 && c.Arg1 is 7 or 8 || c.SubKind == 5, // kind 2: sub kinds 1 to 5 with the listed arguments
        16 => c.SubKind is >= 2 and <= 7, // kind 16: sub kinds 2 to 7
        5 => c.Arg1 is 1 or 2 || c.Arg1 == 5 && c.Arg2 is >= 1 and <= 8, // kind 5: arguments 1 and 2, and 5 (a bit)
        6 => c.SubKind is 1 or 2, // kind 6: sub kinds 1 and 2
        9 => c.SubKind == 2 && c.Arg1 is >= 1 and <= 3 || c.SubKind == 1 && c.Arg1 == 0 || c.SubKind == 3 && c.Arg1 == 1, // kind 9: sub kind 2 with arguments 1 to 3, the guild id 9/1/0, the castle owner 9/3/1
        18 => true, // kind 18: a date
        13 => c.SubKind is 1 or 4, // kind 13: sub kinds 1 and 4
        15 => c.SubKind is >= 1 and <= 3, // kind 15: sub kinds 1 to 3
        14 => c.Arg1 is 1 or 2, // kind 14: arguments 1 and 2
        10 => c.Arg1 == 1, // kind 10: argument 1
        3 => c.Arg1 == 3, // kind 3: argument 3
        4 => c.SubKind == 1 && c.Arg1 is 1 or 2, // kind 4: sub kind 1, arguments 1 and 2
        19 => c.Arg1 is 1 or 2, // kind 19: arguments 1 and 2
        20 => c.SubKind == 2, // kind 20: sub kind 2 (the day of the week)
        _ => false, // any other kind: not implemented
    };

    /// <summary>Reads an instance condition value for the player of the run.</summary>
    /// <param name="index">The condition's index in the instance.</param>
    /// <param name="run">The running event.</param>
    /// <returns>The value; 0 when the map has no instance.</returns>
    private static int InstanceValue(int index, EventRun run)
    {
        if (run.Instance is not { } instance)
            return 0;
        lock (instance.Lock)
            return instance.ConditionValue(index, run.PlayerId);
    }

    /// <summary>Reads the date and time of a date condition.</summary>
    /// <param name="c">The date condition record (kind 18).</param>
    /// <returns>The local date and time; null when the record is not marked as a date or a field is out of range.</returns>
    public static DateTime? EventDate(EveScriptRecord c)
    {
        var (year, month, day) = (2000 + (c.SubKind & 0xFF), c.Arg1 >> 8, c.Arg1 & 0xFF);
        var (hour, minute, second) = (c.Arg2 >> 8, c.Arg2 & 0xFF, (int)((uint)c.Value2 >> 24));
        if (c.SubKind >> 8 != 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
    }

    /// <summary>Counts an item for an item condition: in the bag only, or everywhere the character keeps items when the test asks for absence.</summary>
    /// <param name="character">The character.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="op">The comparison code of the condition.</param>
    /// <param name="value">The value the count is compared with.</param>
    /// <returns>The count.</returns>
    public static int ItemCount(Character character, ushort itemId, int op, int value) =>
        IsAbsenceTest(op, value) ? CountEverywhere(character, itemId) : World.Bag.Count(character, itemId);

    /// <summary>Tells whether a comparison asks whether the character has none: equal to 0, below 1 or at most 0.</summary>
    /// <param name="op">The comparison code.</param>
    /// <param name="value">The value compared with.</param>
    /// <returns>True for such a test.</returns>
    public static bool IsAbsenceTest(int op, int value) => op switch
    {
        0 or 5 => value == 0, // ops 0 and 5: absence when the value is 0
        1 => value <= 1, // op 1: absence when the value is at most 1
        3 => value <= 0, // at most
        _ => false, // any other op: not an absence test
    };

    /// <summary>
    /// Counts an item in every place a character keeps items: bag, bank, warehouse, storeroom, mini grid, worn gear, pets' gear, furniture,
    /// house boxes, stall cases, fuel tank, parking, tent and book shelves.
    /// </summary>
    /// <param name="character">The character.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The total, at most <see cref="int.MaxValue"/>.</returns>
    public static int CountEverywhere(Character character, ushort itemId)
    {
        long n = 0;
        foreach (var list in new[] { character.Bag, character.Bank, character.Warehouse, character.Storeroom, character.MiniGrid })
            n += World.Bag.Count(list, itemId);
        n += character.PicnicSlots.Concat(character.ExpBagSlots).Where(b => b.ItemId == itemId).Sum(b => (long)b.Count);
        n += character.HouseBoxes.Where(b => b.ItemId == itemId).Sum(b => (long)b.Count);
        n += character.Equipment.Count(e => e.ItemId == itemId);
        n += character.Pets.Concat(character.StoredPets).Concat(character.DepartedTeamNpcs).Sum(p => p.Equipment.Count(e => e.ItemId == itemId));
        foreach (var piece in character.Furniture)
        {
            n += piece.ItemId == itemId ? 1 : 0;
            n += piece.Contents.Where(i => i.ItemId == itemId).Sum(i => (long)i.Count);
            n += piece.Stock.Where(i => i.ItemId == itemId).Sum(i => (long)i.Count);
        }
        n += character.StallCases.Where(s => s.ItemId == itemId).Sum(s => (long)s.Stock);
        n += character.FuelTank.Where(f => f.ItemId == itemId).Sum(f => (long)f.Count);
        n += character.Parking.Count(v => v.ItemId == itemId);
        n += character.Tent?.ItemId == itemId ? 1 : 0;
        n += character.BookCase.Count(b => b == itemId) + character.BookDesk.Count(b => b == itemId);
        return (int)Math.Min(n, int.MaxValue);
    }

    /// <summary>
    /// Compares a number with a value by the comparison code of a script: 1 below, 2 above, 3 at most, 4 at least, 6 not equal,
    /// anything else equal.
    /// </summary>
    /// <param name="actual">The number to test.</param>
    /// <param name="op">The comparison code.</param>
    /// <param name="value">The value to compare with.</param>
    /// <returns>True when the comparison holds.</returns>
    public static bool Compare(int actual, int op, int value) => op switch
    {
        1 => actual < value, // op 1: below
        2 => actual > value, // above
        3 => actual <= value, // at most
        4 => actual >= value, // at least
        6 => actual != value, // not equal
        _ => actual == value, // any other op: equal
    };
}
