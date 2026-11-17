using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NorthIsland.GameData.Formulas;
using NorthIsland.GameData.Items;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Items;

/// <summary>
/// What items do when used: experience items, protective items, no-encounter food, double-drop grass, pet amity items, stat buffs, forgetting
/// scrolls and the Stopwatch, and the timed effects they start. Items of other features are passed on to their handlers.
/// </summary>
/// <param name="items">Item definitions.</param>
/// <param name="skills">Skill data, to forget skills when an ability point is forgotten.</param>
/// <param name="experience">Gives experience to players and pets.</param>
/// <param name="combat">Combat stats, recalculated and sent after a stat buff or a forgotten point.</param>
/// <param name="formulas">The formula tables, for the experience a level needs and for pet HP and SP.</param>
/// <param name="npcs">NPC definitions, for the base abilities of pets.</param>
/// <param name="bodyShift">The Riceball handlers, sent at login.</param>
/// <param name="training">The training island handlers.</param>
/// <param name="bottles">The HP and SP bottle handlers.</param>
/// <param name="vehicleCare">The vehicle fuel handlers.</param>
/// <param name="shows">The firework handlers.</param>
/// <param name="rooms">The house room handlers.</param>
/// <param name="spaces">The space contract handlers.</param>
/// <param name="fishTank">The fish tank handlers.</param>
/// <param name="icons">The time icon handlers.</param>
/// <param name="vouchers">The exchange voucher handlers.</param>
/// <param name="world">The online players.</param>
/// <param name="options">Item settings.</param>
/// <param name="logger">Logger.</param>
public sealed class ItemEffectHandlers(
    ItemData items,
    SkillData skills,
    Experience experience,
    CombatStats combat,
    FormulaData formulas,
    NpcData npcs,
    BodyShiftHandlers bodyShift,
    TrainingIslandHandlers training,
    BottleHandlers bottles,
    VehicleCareHandlers vehicleCare,
    ShowItemHandlers shows,
    HouseRoomHandlers rooms,
    SpaceContractHandlers spaces,
    FishTankHandlers fishTank,
    TimeIconHandlers icons,
    ExchangeVoucherHandlers vouchers,
    WorldState world,
    IOptions<ItemOptions> options,
    ILogger<ItemEffectHandlers> logger)
{
    /// <summary>The item ids that protect from losing experience for a while.</summary>
    public static readonly ImmutableArray<ushort> ProtectiveItems = [34026, 34029]; // 34026 Protective EXP Pill, 34029 Protective EXP Bolus

    /// <summary>The item ids of the scrolls that forget one ability point of the player.</summary>
    public static readonly ImmutableArray<ushort> ForgetScrolls = [34089, 34122]; // 34089 Forgotten Scroll, 34122 Lethe Scroll

    /// <summary>
    /// The stat buff items by item id. The code is the buff row times 100 plus the level, so 301 is level 1 of the third row.
    /// </summary>
    public static readonly FrozenDictionary<ushort, int> StatBuffCodes = new Dictionary<ushort, int>
    {
        [34236] = 301, // Chili Sauce
        [34329] = 302, // Spice Pack
        [34264] = 401, // Enhanced Skin Milk
        [34327] = 402, // Body Lotion
        [34249] = 501, // Wasabi
        [34326] = 502, // Mustard Pack
        [34265] = 601, // Block Magic Perfume
        [34328] = 602, // Magic Perfume
    }.ToFrozenDictionary();

    /// <summary>The bonus a stat buff level gives.</summary>
    /// <param name="level">The buff level, 1 or 2.</param>
    /// <returns>The bonus, 0 for any other level.</returns>
    public static int StatBuffAmount(int level) => level switch
    {
        1 => 300, // level 1 gives 300
        2 => 150, // level 2 gives 150
        _ => 0 // any other level gives nothing
    };

    /// <summary>The lowest value an ability can be forgotten down to, by potential.</summary>
    internal static readonly ImmutableArray<ushort> PotentialFloor = [0, 1, 2, 3, 4, 6, 9, 13, 18, 24, 31, 39, 48];

    /// <summary>How far above its potential floor a character's ability may be raised with stat points, as the client's status window allows.</summary>
    public const int AbilityRoom = 602;

    /// <summary>The highest value stat points can raise an ability to: its potential floor plus <see cref="AbilityRoom"/> (602 to 650).</summary>
    /// <param name="potential">The potential level.</param>
    /// <returns>The cap.</returns>
    internal static int AbilityCap(byte potential) => PotentialFloor[Math.Min(potential, (byte)(PotentialFloor.Length - 1))] + AbilityRoom;

    /// <summary>
    /// Uses an item that has an effect handled here, or hands it to the handler of its feature.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack that was used.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="petSlot">The pet the item is used on, 0 for the player.</param>
    /// <returns>True when the item was handled (even if it did nothing); false when no handler takes the item.</returns>
    public bool TryUse(
        GameSession session,
        Character character,
        BagItem stack,
        ItemRecord item,
        byte petSlot)
    {
        var now = TimedEffects.Now;
        var pet = petSlot == 0 ? null : character.Pets.Find(p => p.Slot == petSlot);
        if (petSlot != 0 && pet is null)
            return false;

        if (item.SubType == 113 && ExpPercent(item) is { } percent)
            return UseExpPercent(session, character, stack, item, pet, percent);

        if (item.SubType == 127 && PotionRate(item) is { } rate)
        {
            if (pet is not null)
                return true;
            return UseExpPotion(session, character, stack, item, rate, now);
        }

        if (bottles.TryUse(session, character, stack, item, pet))
            return true;

        if (pet is null && vehicleCare.TryUseFuel(session, character, stack, item))
            return true;

        if (pet is null && training.TryUse(session, character, stack, item))
            return true;

        if (pet is null && shows.TryFirework(session, character, stack, item))
            return true;

        if (pet is null && rooms.TryUseVoucher(session, character, stack, item))
            return true;

        if (pet is null && spaces.TryUse(session, character, stack, item))
            return true;

        if (pet is null && fishTank.TryUseFood(session, character, stack, item))
            return true;

        if (pet is null && icons.TryUseBun(session, character, stack, item))
            return true;

        if (pet is null && vouchers.TryUse(session, character, stack, item))
            return true;

        if (ProtectiveItems.IndexOf(item.Id) >= 0)
        {
            var hours = options.Value.ItemUse.ProtectiveExpHours;
            var effect = TimedEffects.Add(character, TimedEffects.Protect, 1, 0, item.Id, TimeSpan.FromHours(hours), now);
            Consume(session, character, stack);
            logger.LogInformation("[{Session}] {Name} uses {Item}: no exp loss until {Until:HH:mm}", session.Id, character.Name, item.Name, effect.Until);
            return true;
        }

        if (pet is null && item.NoEncounterSeconds > 0)
        {
            var duration = TimeSpan.FromSeconds(item.NoEncounterSeconds);
            if (TimedEffects.Find(character, TimedEffects.NoEncounters, now) is { } running)
                running.Until = now + duration;
            else
                TimedEffects.Add(character, TimedEffects.NoEncounters, 1, 0, item.Id, duration, now);
            Consume(session, character, stack);
            logger.LogInformation(
                "[{Session}] {Name} eats {Item}: no field encounters for {Seconds} s",
                session.Id,
                character.Name,
                item.Name,
                item.NoEncounterSeconds);
            return true;
        }

        if (item.SubType == 128 && item.UsePeriod > 0)
        {
            if (pet is not null)
                return true;
            var effect = TimedEffects.Add(character, TimedEffects.DoubleDrops, 2, 0, item.Id, TimeSpan.FromHours(item.UsePeriod), now);
            Consume(session, character, stack);
            TimeIconHandlers.SendLucky(session, character);
            logger.LogInformation("[{Session}] {Name} uses {Item}: double drops until {Until:HH:mm}", session.Id, character.Name, item.Name, effect.Until);
            return true;
        }

        var amity = Enumerable.Range(0, Math.Min(item.Words36.Length, item.Ints42.Length))
            .Where(i => item.Words36[i] == 64 && item.Ints42[i] > 100)
            .Select(i => item.Ints42[i] - 100)
            .FirstOrDefault();
        if (amity > 0)
        {
            if (pet is null || pet.Tradable == 0)
                return true;
            var max = options.Value.ItemUse.MaxAmity;
            if (pet.Amity >= max)
                return true;
            pet.Amity = (byte)Math.Min(max, pet.Amity + amity);
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrAmity, pet.Amity));
            Consume(session, character, stack);
            logger.LogInformation("[{Session}] {Name} gives {Item} to {Pet}: amity {Amity}", session.Id, character.Name, item.Name, pet.Name, pet.Amity);
            return true;
        }
        return false;
    }

    /// <summary>The percentage of experience an experience item gives.</summary>
    /// <param name="item">The item definition.</param>
    /// <returns>The percentage, or null when the item has none.</returns>
    public static int? ExpPercent(ItemRecord item)
    {
        for (var i = 0; i < Math.Min(item.Words36.Length, item.Ints42.Length); i++)
            if (item.Words36[i] == 36 && item.Ints42[i] > 100)
                return item.Ints42[i] - 100;
        return null;
    }

    /// <summary>The experience rate of an experience potion.</summary>
    /// <param name="item">The item definition.</param>
    /// <returns>The rate as a multiplier (2.0 for double), or null when the item has none.</returns>
    public static double? PotionRate(ItemRecord item)
    {
        for (var i = 0; i < Math.Min(item.Words36.Length, item.Ints42.Length); i++)
            if (item.Words36[i] == 250 && item.Ints42[i] > 100)
                return item.Ints42[i] / 100.0;
        return null;
    }

    /// <summary>Uses an experience percentage item: gives the player or the pet a share of the experience their level needs, and takes the item.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack that was used.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="pet">The pet to give it to, or null for the player.</param>
    /// <param name="percent">The percentage of the level's experience.</param>
    /// <returns>Always true.</returns>
    private bool UseExpPercent(
        GameSession session,
        Character character,
        BagItem stack,
        ItemRecord item,
        Pet? pet,
        int percent)
    {
        if (formulas.Table is not { } f)
            return true;
        var (level, reborn) = pet is null ? (character.Level, character.RebornCount) : (pet.Level, pet.Reborn);
        if (level >= FormulaTable.MaxLevel(reborn))
            return true;
        var gain = (int)Math.Max(1, Math.Round(f.LevelUpExp(level, reborn) * percent / 100.0 * options.Value.ItemUse.ExpPercentScale));
        Consume(session, character, stack);
        if (pet is null)
        {
            experience.Gain(session, gain);
        }
        else
        {
            pet.Exp = (int)Math.Min(int.MaxValue, (long)pet.Exp + gain);
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrExp, pet.Exp));
            experience.PetGains(session, pet, gain);
        }
        logger.LogInformation("[{Session}] {Name} uses {Item} ({Percent}%) on {Target}: +{Exp} exp", session.Id, character.Name, item.Name, percent,
            pet?.Name ?? "himself", gain);
        return true;
    }

    /// <summary>Uses an experience potion: starts its effect and takes the item unless it was refused.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack that was used.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="rate">The experience rate.</param>
    /// <param name="now">The current time.</param>
    /// <returns>Always true.</returns>
    private bool UseExpPotion(
        GameSession session,
        Character character,
        BagItem stack,
        ItemRecord item,
        double rate,
        DateTime now)
    {
        if (DrinkExpPotion(session, character, item, rate, now))
            Consume(session, character, stack);
        return true;
    }

    /// <summary>Starts the effect of an experience potion without taking an item from the bag.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="item">The definition of the item.</param>
    /// <returns>True when the effect started; false when the item is no experience potion or one is refused.</returns>
    public bool DrinkExp(GameSession session, Character character, ItemRecord item) =>
        item.SubType == 127 && PotionRate(item) is { } rate && DrinkExpPotion(session, character, item, rate, TimedEffects.Now);

    /// <summary>
    /// Starts the experience effect of a potion. A potion of another rate is refused while one runs, and a Statue is refused while a potion that is
    /// not a Statue runs.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="rate">The experience rate.</param>
    /// <param name="now">The current time.</param>
    /// <returns>True when the effect started or was extended; false when it was refused.</returns>
    private bool DrinkExpPotion(
        GameSession session,
        Character character,
        ItemRecord item,
        double rate,
        DateTime now)
    {
        var running = TimedEffects.Find(character, TimedEffects.Exp, now);
        var statue = item.Name.Contains("Statue", StringComparison.OrdinalIgnoreCase);
        if (running is not null && statue && items[running.Item] is { } other && !other.Name.Contains("Statue", StringComparison.OrdinalIgnoreCase))
        {
            session.Send(PlayerMessages.ExpPotionRefused(2)); // code
            return false;
        }
        if (running is not null && running.Value != rate)
        {
            session.Send(PlayerMessages.ExpPotionRefused(1));
            return false;
        }
        TimedEffects.Add(character, TimedEffects.Exp, rate, 0, item.Id, TimeSpan.FromHours(Math.Max(1, (int)item.UsePeriod)), now);
        SendExpIcon(session, character);
        logger.LogInformation("[{Session}] {Name} drinks {Item}: x{Rate} exp for {Hours} h", session.Id, character.Name, item.Name, rate, item.UsePeriod);
        return true;
    }

    /// <summary>Takes one item from a stack, removing the stack when it is empty, and tells the client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack.</param>
    private static void Consume(GameSession session, Character character, BagItem stack)
    {
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        session.Send(ItemMessages.UsedSound());
    }

    /// <summary>The packet that shows the player's running timed effect icons.</summary>
    /// <param name="character">The player's character.</param>
    /// <returns>The packet.</returns>
    public static byte[] IconPacket(Character character) => TimedEffects.IconPacket(character, TimedEffects.Now);

    /// <summary>Sends the timed effect icons to the player.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public static void SendExpIcon(GameSession session, Character character) => session.Send(IconPacket(character));

    /// <summary>Handles a click on the experience icon: sends the minutes the potion has left, or the icons when none runs.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask OnExpClick(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character)
            return ValueTask.CompletedTask;
        var now = TimedEffects.Now;
        var potion = TimedEffects.Find(character, TimedEffects.Exp, now);
        if (potion is not null)
            session.Send(
                PlayerMessages.ExpPotionRemaining(true, TimedEffects.Minutes(potion, now), (byte)Math.Clamp((int)Math.Round(potion.Value * 10), 1, 255)));
        else
            SendExpIcon(session, character);
        return ValueTask.CompletedTask;
    }

    /// <summary>The item ids of the scrolls that move a point between two pet abilities.</summary>
    public static readonly ImmutableArray<ushort> PetScrolls = [34137, 34139]; // 34137 Pet Lethe Scroll, 34139 Pet Return Scroll

    /// <summary>How far above its potential floor a pet ability may be raised.</summary>
    public const int PetAbilityRoom = 602;

    /// <summary>
    /// Handles a pet ability scroll: takes one point from one of the pet's abilities and gives it to another, then recalculates the pet's HP and SP.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: bag slot, one unread byte, pet slot, the ability to forget and the ability to add (1 to 5).</param>
    internal ValueTask OnPetSwap(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 5 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var (slot, petSlot, forget, add) = (body.Span[0], body.Span[2], body.Span[3], body.Span[4]);
        var stack = c.Bag.Find(b => b.Slot == slot);
        var pet = c.Pets.Find(p => p.Slot == petSlot);
        if (stack is null || PetScrolls.IndexOf(stack.ItemId) < 0 || pet is null || forget is < 1 or > 5 || add is < 1 or > 5 || forget == add
            || formulas.Table is not { } f || FormulaTable.TotalLevel(pet.Level, pet.Reborn) < 20 || npcs[pet.NpcId] is not { } npc)
        {
            logger.LogInformation(
                "[{Session}] {Name}: pet ability swap refused (slot {Slot}, pet {Pet}, {Forget} -> {Add})",
                session.Id,
                c.Name,
                slot,
                petSlot,
                forget,
                add);
            return ValueTask.CompletedTask;
        }
        var row = PotentialFloor[Math.Min(pet.Potential, (byte)(PotentialFloor.Length - 1))];
        int Stat(int i) => i switch
        {
            1 => pet.Strength, // strength
            2 => pet.Constitution, // constitution
            3 => pet.Intelligence, // intelligence
            4 => pet.Wisdom, // wisdom
            _ => pet.Agility // any other number: agility
        };
        int Base(int i) => i switch
        {
            1 => npc.Strength, // strength
            2 => npc.Constitution, // constitution
            3 => npc.Intelligence, // intelligence
            4 => npc.Wisdom, // wisdom
            _ => npc.Agility // any other number: agility
        };
        if (Stat(forget) <= 0 || (Base(forget) >> 1) + row >= Stat(forget) || Stat(add) + 1 > row + PetAbilityRoom)
        {
            logger.LogInformation("[{Session}] {Name}: pet ability swap: {Forget} is at its floor or {Add} at its limit", session.Id, c.Name, forget, add);
            return ValueTask.CompletedTask;
        }
        void Set(int i, int delta)
        {
            var v = (ushort)(Stat(i) + delta);
            switch (i)
            {
                case 1: pet.Strength = v; break; // strength
                case 2: pet.Constitution = v; break; // constitution
                case 3: pet.Intelligence = v; break; // intelligence
                case 4: pet.Wisdom = v; break; // wisdom
                default: pet.Agility = v; break; // any other number: agility
            }
        }
        Set(forget, -1);
        Set(add, +1);
        Consume(session, c, stack);
        combat.RecalculatePet(pet);
        foreach (var (attr, v) in new (byte, int)[]
        {
            (Action8.AttrStr, pet.Strength), (Action8.AttrCon, pet.Constitution), (Action8.AttrInt, pet.Intelligence), (Action8.AttrWis, pet.Wisdom),
            (Action8.AttrAgi, pet.Agility), (Action8.AttrMaxHp, pet.MaxHp), (Action8.AttrMaxSp, pet.MaxSp), (Action8.AttrHp, pet.Hp), (Action8.AttrSp, pet.Sp),
        })
            session.Send(PetMessages.SetAttribute(pet.Slot, attr, v));
        combat.SendPetValues(session, pet);
        logger.LogInformation("[{Session}] {Name}: {Pet} moves a point of ability {Forget} to {Add}", session.Id, c.Name, pet.Name, forget, add);
        return ValueTask.CompletedTask;
    }

    /// <summary>The item id of the Stopwatch.</summary>
    public const ushort Stopwatch = 34180; // Stopwatch

    /// <summary>
    /// Handles the Stopwatch: a bag slot pauses the double experience effect and spends a use, a slot of 0 lets it go on.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the bag slot of the Stopwatch, or 0 to end the pause.</param>
    internal ValueTask OnStopwatch(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 1 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var now = TimedEffects.Now;
        ShiftPaused(c, now);
        if (body.Span[0] == 0)
        {
            if (c.ExpPausedSince is null)
                return ValueTask.CompletedTask;
            c.ExpPausedSince = null;
            session.Send(ItemMessages.ExpPauseUsed(false));
            logger.LogInformation("[{Session}] {Name}: the double exp goes on", session.Id, c.Name);
            return ValueTask.CompletedTask;
        }
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        var anything = TimedEffects.Find(c, TimedEffects.Exp, now) is not null;
        if (stack is null || stack.ItemId != Stopwatch || c.ExpPausedSince is not null || !anything || items[Stopwatch] is not { } watch)
        {
            logger.LogInformation("[{Session}] {Name}: stopwatch refused", session.Id, c.Name);
            return ValueTask.CompletedTask;
        }
        UseLimit.Spend(session, c, stack, watch);
        c.ExpPausedSince = now;
        session.Send(ItemMessages.ExpPauseUsed(true));
        logger.LogInformation("[{Session}] {Name} pauses the double exp with the Stopwatch", session.Id, c.Name);
        return ValueTask.CompletedTask;
    }

    /// <summary>While the experience effect is paused, pushes its end back by the time passed since the last call.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="now">The current time.</param>
    public static void ShiftPaused(Character c, DateTime now)
    {
        if (c.ExpPausedSince is not { } since)
            return;
        var gap = now - since;
        if (gap > TimeSpan.Zero)
            foreach (var e in c.Effects.Where(e => e.Kind == TimedEffects.Exp))
                e.Until += gap;
        c.ExpPausedSince = now;
    }

    /// <summary>Handles the stat buff action: sub code 2 asks for the minutes left, 3 uses a stat buff item from a bag slot.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sub code, then for sub code 3 the bag slot.</param>
    internal ValueTask OnStatBuff(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 1 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var now = TimedEffects.Now;
        if (body.Span[0] == 2)
        {
            session.Send(ItemMessages.StatBuffMinutes(TimedEffects.StatMinutes(character, now)));
            return ValueTask.CompletedTask;
        }
        if (body.Span[0] != 3 || body.Length < 2)
            return ValueTask.CompletedTask;
        var slot = body.Span[1];
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (stack is null || !StatBuffCodes.ContainsKey(stack.ItemId) || items[stack.ItemId] is not { } item)
        {
            logger.LogWarning("[{Session}] stat buff: bag slot {Slot} is no stat buff item", session.Id, slot);
            return ValueTask.CompletedTask;
        }
        UseStatBuff(session, character, stack, item, now);
        return ValueTask.CompletedTask;
    }

    /// <summary>Starts a stat buff, unless the buff of that row still runs, and takes the item.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="stack">The bag stack that was used.</param>
    /// <param name="item">The definition of the item.</param>
    /// <param name="now">The current time.</param>
    private void UseStatBuff(
        GameSession session,
        Character character,
        BagItem stack,
        ItemRecord item,
        DateTime now)
    {
        var code = StatBuffCodes[item.Id];
        var (row, level) = (code / 100, code % 100);
        var kind = TimedEffects.StatKinds[row - 3];
        if (TimedEffects.Find(character, kind, now) is not null)
            return;
        TimedEffects.Add(character, kind, StatBuffAmount(level), level, item.Id, TimeSpan.FromHours(Math.Max(1, (int)item.UsePeriod)), now);
        Consume(session, character, stack);
        session.Send(ItemMessages.StatBuffMask(TimedEffects.StatMask(character, now), sound: true));
        combat.Send(session);
        logger.LogInformation(
            "[{Session}] {Name} uses {Item}: {Kind} +{Amount} for {Hours} h",
            session.Id,
            character.Name,
            item.Name,
            kind,
            StatBuffAmount(level),
            item.UsePeriod);
    }

    /// <summary>
    /// Handles a forgetting scroll: takes one point from an ability of the player, returns it as a stat point and forgets the skills that the lower
    /// ability no longer reaches.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: bag slot, one unread byte, pet slot (only 0 is accepted) and the ability (1 to 5).</param>
    internal ValueTask OnForget(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } character || body.Length < 4 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var (slot, petSlot, attribute) = (body.Span[0], body.Span[2], body.Span[3]);
        var stack = character.Bag.Find(b => b.Slot == slot);
        if (stack is null || ForgetScrolls.IndexOf(stack.ItemId) < 0 || petSlot != 0 || attribute is < 1 or > 5)
        {
            logger.LogWarning("[{Session}] forget ability: bag slot {Slot} / attribute {Attr} refused", session.Id, slot, attribute);
            return ValueTask.CompletedTask;
        }
        var floor = PotentialFloor[Math.Min(character.Potential, (byte)(PotentialFloor.Length - 1))];
        ushort value = attribute switch
        {
            1 => character.Strength, // strength
            2 => character.Constitution, // constitution
            3 => character.Intelligence, // intelligence
            4 => character.Wisdom, // wisdom
            _ => character.Agility // any other number: agility
        };
        if (value <= floor || value <= 1)
        {
            logger.LogInformation("[{Session}] forget ability {Attr}: no point above {Floor}", session.Id, attribute, floor);
            return ValueTask.CompletedTask;
        }
        value--;
        switch (attribute)
        {
            case 1: character.Strength = value; break; // strength
            case 2: character.Constitution = value; break; // constitution
            case 3: character.Intelligence = value; break; // intelligence
            case 4: character.Wisdom = value; break; // wisdom
            default: character.Agility = value; break; // any other number: agility
        }
        character.StatPoints = (ushort)Math.Min(ushort.MaxValue, character.StatPoints + 1);
        var forgotten = skills.ForgetLost(character);
        Consume(session, character, stack);
        combat.Recalculate(character);
        foreach (var (attr, v) in new (byte, int)[]
        {
            (Action8.AttrStr, character.Strength), (Action8.AttrCon, character.Constitution), (Action8.AttrInt, character.Intelligence),
            (Action8.AttrWis, character.Wisdom), (Action8.AttrAgi, character.Agility), (Action8.AttrStatPoints, character.StatPoints),
            (Action8.AttrMaxHp, character.MaxHp), (Action8.AttrMaxSp, character.MaxSp), (Action8.AttrHp, character.Hp), (Action8.AttrSp, character.Sp),
        })
            session.Send(InnMessages.SetAttribute(attr, v));
        foreach (var id in forgotten)
            session.Send(InnMessages.SetAttribute(Action8.AttrSkillLevel, 0, id));
        if (forgotten.Count > 0)
        {
            var w = new PacketWriter(67, 5).U16((ushort)forgotten.Count);
            foreach (var id in forgotten)
                w.U16(id);
            session.Send(w.ToArray());
        }
        combat.Send(session);
        logger.LogInformation("[{Session}] {Name} forgets one point of ability {Attr}: {Value} ({Left} stat points), skills forgotten: {Skills}", session.Id,
            character.Name, attribute, value, character.StatPoints, forgotten.Count > 0 ? string.Join(",", forgotten) : "none");
        return ValueTask.CompletedTask;
    }

    /// <summary>At login, restores the pause, stat buff and icon state and lets the other item features send theirs.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void SendOnLogin(GameSession session, Character character)
    {
        var now = TimedEffects.Now;
        ShiftPaused(character, now);
        TimedEffects.Prune(character, now);
        if (character.ExpPausedSince is not null)
            session.Send(ItemMessages.ExpPaused(true));
        if (TimedEffects.Find(character, TimedEffects.Exp, now) is not null)
            SendExpIcon(session, character);
        if (TimedEffects.StatMask(character, now) != 0)
            session.Send(ItemMessages.StatBuffMask(TimedEffects.StatMask(character, now), sound: false));
        bodyShift.SendOnLogin(session, character);
        TrainingIslandHandlers.SendOnLogin(session, character);
        BottleHandlers.SendOnLogin(session, character);
        FuelBankHandlers.SendOnLogin(session, character);
        MonsterBookHandlers.SendOnLogin(session, character);
        rooms.SendOnLogin(session, character);
        spaces.SendOnLogin(session, character);
        icons.SendOnLogin(session, character);
    }

    /// <summary>Sends away the mercenary pets whose lease ran out, unless the player is in a battle.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <param name="now">The current time.</param>
    private void ExpireLeases(GameSession session, Character character, DateTime now)
    {
        if (session.Battle is not null || HireTiers.Expired(character, now) is not { Count: > 0 } gone)
            return;
        foreach (var pet in gone)
        {
            if (character.Pets.Contains(pet))
                PetTradeHandlers.Give(world, session, character, pet);
            else
                character.StoredPets.Remove(pet);
            logger.LogInformation("[{Session}] {Name}: mercenary {Pet} ({Npc}) leaves, the lease ran out", session.Id, character.Name, pet.Name, pet.NpcId);
        }
    }

    /// <summary>On each tick of the item effect ticker: expires leases and timed effects and tells the client about the effects that ran out.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    public void Tick(GameSession session, Character character)
    {
        var now = TimedEffects.Now;
        ExpireLeases(session, character, now);
        ShiftPaused(character, now);
        var gone = TimedEffects.Prune(character, now);
        if (gone.Count == 0)
            return;
        if (gone.Exists(e => e.Kind == TimedEffects.Exp))
        {
            session.Send(PlayerMessages.ExpPotionExpired(false));
            SendExpIcon(session, character);
        }
        TimeIconHandlers.Expired(session, gone);
        if (gone.Exists(e => e.Kind == TrainingIslandHandlers.Kind))
            session.Send(TrainingMessages.Over());
        if (gone.Exists(e => TimedEffects.StatKinds.IndexOf(e.Kind) >= 0))
        {
            session.Send(ItemMessages.StatBuffMask(TimedEffects.StatMask(character, now), sound: false));
            combat.Send(session);
        }
        logger.LogInformation("[{Session}] {Name}: ran out: {Effects}", session.Id, character.Name, string.Join(", ", gone.Select(e => e.Kind)));
    }

    /// <summary>The sessions that are online, for the ticker to walk.</summary>
    /// <returns>The online sessions.</returns>
    public IEnumerable<GameSession> Online() => world.OnlineSessions();
}
