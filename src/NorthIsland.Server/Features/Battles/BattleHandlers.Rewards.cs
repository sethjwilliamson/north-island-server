using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>Trains a skill a player or pet used and tells the owner's client the new skill experience, level and any skill learned.</summary>
    /// <param name="logSession">The session used for logging.</param>
    /// <param name="actor">The player or pet who used the skill.</param>
    /// <param name="skill">The skill used, or null for a hand attack (nothing is trained).</param>
    /// <param name="gain">The skill experience gained, at least 1.</param>
    private void TrainSkill(GameSession logSession, Fighter actor, SkillRecord? skill, int gain)
    {
        if (skill is null || !actor.IsHuman || actor.Session is not { } session || formulas.Table is not { } f)
            return;
        gain = Math.Max(1, gain);
        if (actor.OwnedPet is { } pet)
        {
            if (actor.Npc is not { } npc || SkillData.TrainPet(pet, npc.SkillSlots, skill, f, gain) is not { } trained)
                return;
            session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSkillExp, trained.Exp, skill.Id));
            if (trained.LevelRose)
            {
                session.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSkillLevel, trained.Level, skill.Id));
                logger.LogInformation(
                    "[{Session}] pet {Pet}: {Skill} reaches level {Level} ({Exp} skill exp)",
                    logSession.Id,
                    pet.Name,
                    skill.Name,
                    trained.Level,
                    trained.Exp);
            }
            return;
        }
        if (session.ActiveCharacter is not { } c || skills.Train(c, f, skill.Id, gain) is not { } done)
            return;
        session.Send(InnMessages.SetAttribute(Action8.AttrSkillExp, done.Exp, skill.Id));
        if (!done.LevelRose)
            return;
        session.Send(InnMessages.SetAttribute(Action8.AttrSkillLevel, done.Level, skill.Id));
        logger.LogInformation("[{Session}] {Name}: {Skill} reaches level {Level} ({Exp} skill exp)", logSession.Id, c.Name, skill.Name, done.Level, done.Exp);
        if (done.Successor is { } next)
        {
            session.Send(InnMessages.SetAttribute(Action8.AttrSkillLevel, 1, next.Id));
            logger.LogInformation(
                "[{Session}] {Name}: {Skill} at its max level {Max} - learns {Next} ({Id})",
                logSession.Id,
                c.Name,
                skill.Name,
                skill.MaxLevel,
                next.Name,
                next.Id);
        }
    }

    /// <summary>Waits for the dropped item icons to fly, or for the fixed action delay when one is set.</summary>
    private Task DropFlyPause() => options.Value.BattleActionDelayMs > 0 ? Pause() : Task.Delay(ItemFlyMs);

    /// <summary>Milliseconds the item icons of a drop need to fly.</summary>
    private const int ItemFlyMs = 800;

    /// <summary>Gives the drops of a fallen monster to the players who felled it, in the middle of the battle.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="monster">The monster.</param>
    /// <returns>True when at least one item was handed out and thrown on the field.</returns>
    private bool GiveDropInBattle(Battle battle, Fighter monster)
    {
        if (!options.Value.DropToKillingBlowOnly
            || battle.Standalone
            || battle.IsPvp
            || monster.IsHuman
            || monster.IsAlive
            || monster.Captured
            || monster.DropGiven
            || monster.Npc is not { } npc)
            return false;
        List<Fighter> felling = [.. monster.KilledBy.Distinct().Where(f => f is { IsHuman: true, Left: false, Session.ActiveCharacter: not null })];
        if (felling.Count == 0)
            return false;
        monster.DropGiven = true;
        var share = new DropShare(npc, monster, felling.Count);
        var thrown = false;
        foreach (var (itemId, regular) in new[] { (DropOf(share), true), (EventDropOf(share, calendar.Drops()), false) })
        {
            if (itemId is not { } id || items[id] is not { } item)
                continue;
            List<(GameSession Session, int Added)> got = [];
            foreach (var mine in felling.GroupBy(f => f.Session!))
            {
                var session = mine.Key;
                var character = session.ActiveCharacter!;
                var times = mine.Count() * (regular && TimedEffects.DoubleDrop(character, TimedEffects.Now) ? 2 : 1);
                var added = Bag.Add(character, items, id, times).Sum(c => c.Added);
                if (added == 0)
                {
                    logger.LogInformation(
                        "[{Session}] {Name} gets no {Item} from {Monster} in battle: the bag is full",
                        session.Id,
                        character.Name,
                        item.Name,
                        monster.Name);
                    continue;
                }
                got.Add((session, added));
                logger.LogInformation(
                    "[{Session}] {Name} gets {Item} x{Count} from {Monster} in battle{Table}",
                    session.Id,
                    character.Name,
                    item.Name,
                    added,
                    monster.Name,
                    regular ? "" : " (event table)");
            }
            if (got.Count == 0)
                continue;
            battle.Send(
                BattleMessages.ThrowItem(
                    id,
                    monster.Row,
                    monster.Col,
                    felling.Where(f => got.Exists(g => ReferenceEquals(g.Session, f.Session))).Select(f => (f.Row, f.Col))));
            foreach (var (session, added) in got)
                session.Send(Protocol.Messages.Items.GroundMessages.ItemObtained(id, (ushort)added));
            thrown = true;
        }
        return thrown;
    }

    /// <summary>Settles a won battle: pays experience to the players and their pets, ends the battle, then gives the rewards.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="winPlate">The fighter who used the Win Plate, or null.</param>
    private void Win(GameSession session, Battle battle, Fighter? winPlate = null)
    {
        var defeated = battle.Standalone ? []
            : battle.Monsters.Where(m => m.Npc is not null && !m.Captured).Select(m => m.Npc!).ToList();
        var o = options.Value;
        int Share(Fighter? f, int level) => f is null || battle.Standalone ? 0 : BattleFormulas.ExpShare(battle.Monsters, f, level, o, winPlate);
        var seasonalFactor = BattleFormulas.CappedExpMultipliers(calendar.ExpMultiplier(), 1, o.ExpMultiplierCap).Seasonal;
        int Seasonal(int rawExp) => (int)Math.Min(int.MaxValue, Math.Round(rawExp * seasonalFactor));

        var winners = battle.ActivePlayers.Where(p => p.IsAlive && p.Session?.ActiveCharacter is not null).Select(p => p.Session!).ToList();
        int DamageBurst(Fighter? f, int level) =>
            f is null || battle.Standalone
                ? 0
                : (int)Math.Min(
                    int.MaxValue,
                    Math.Round(BattleFormulas.BurstDamageExp(battle.Monsters, f, level, o) * seasonalFactor, MidpointRounding.AwayFromZero));
        List<(GameSession Session, Data.Pet Pet, int Exp)> earningPets = [];
        List<(GameSession Session, int Exp)> shares = [];
        foreach (var winner in winners)
        {
            var character = winner.ActiveCharacter!;
            var playerFighter = battle.Players.Find(p => ReferenceEquals(p.Session, winner) && !p.Left);
            var rawExp = Share(playerFighter, character.Level);
            var exp = Seasonal(rawExp);
            var own = character.ExpPausedSince is null
                ? exp
                    * BattleFormulas.CappedExpMultipliers(
                        calendar.ExpMultiplier(),
                        TimedEffects.ExpMultiplier(character, TimedEffects.Now),
                        o.ExpMultiplierCap).Potion
                : exp;
            own += DamageBurst(playerFighter, character.Level);
            // On the Training Island every living winner gets the flat amount on top, which events and potions do not raise
            var flat = TrainingFlatExp(character, defeated);

            (Data.Pet Pet, int Exp)? earns = null;
            var buffShareBase = 0;
            if (battle.Pets.Find(p => ReferenceEquals(p.Session, winner)) is { IsAlive: true, Left: false, OwnedPet: { } pet } petFighter)
            {
                // The owner's EXP potion works for the pet too, but not on bursting (combination damage) experience.
                var petPotion = character.ExpPausedSince is null
                    ? BattleFormulas.CappedExpMultipliers(calendar.ExpMultiplier(), TimedEffects.ExpMultiplier(character, TimedEffects.Now), o.ExpMultiplierCap)
                        .Potion
                    : 1;
                var petShare = Seasonal(Share(petFighter, pet.Level));
                var petBurst = DamageBurst(petFighter, pet.Level);
                buffShareBase = petShare + petBurst;
                // The pet gets the Training Island's flat amount on top as its owner does.
                if ((int)Math.Min(int.MaxValue, Math.Round(petShare * petPotion)) + petBurst + flat is var petExp and > 0)
                    earns = (pet, petExp);
            }
            if (earns is not null && playerFighter is { BuffedPet: true })
                own += BattleFormulas.BuffShare(buffShareBase, o.BuffSharePercent);
            own += flat;
            character.Exp = (int)Math.Min(int.MaxValue, (long)character.Exp + (long)Math.Round(own));
            shares.Add((winner, (int)Math.Round(own)));
            winner.Send(InnMessages.SetAttribute(Action8.AttrExp, character.Exp));
            experience.Notify(winner, (int)Math.Round(own));
            if (earns is { } got)
            {
                got.Pet.Exp += got.Exp;
                winner.Send(PetMessages.SetAttribute(got.Pet.Slot, Action8.AttrExp, got.Pet.Exp));
                earningPets.Add((winner, got.Pet, got.Exp));
            }
        }

        End(session, battle, BattleOutcome.Won, "won");
        foreach (var winner in winners.Where(_ => !battle.Standalone))
            instances.RecordWin(winner);
        foreach (var (owner, pet, petExp) in earningPets)
            PetGainsExp(owner, pet, petExp);
        foreach (var (winner, exp) in shares)
            Reward(winner, defeated, DropsOf(winner, battle, defeated), exp);
    }

    /// <summary>Lists the drops a winner rolls for.</summary>
    /// <param name="winner">The winner's session.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="defeated">The records of the defeated monsters.</param>
    /// <returns>One entry per defeated monster, or only those the winner's fighters felled when drops go to the killing blow.</returns>
    private List<DropShare> DropsOf(GameSession winner, Battle battle, List<NorthIsland.GameData.Npcs.NpcRecord> defeated)
    {
        if (!options.Value.DropToKillingBlowOnly || battle.Standalone)
            return [.. defeated.Select(n => new DropShare(n, null, 1))];
        List<Fighter> mine = [.. battle.Players.Concat(battle.Pets).Where(f => ReferenceEquals(f.Session, winner))];
        return [
            .. battle.Monsters.Where(m => m.Npc is not null && !m.Captured)
                .Select(m => new DropShare(m.Npc!, m, BattleFormulas.DropRollsOf(m, mine)))
                .Where(d => d.Copies > 0)
        ];
    }

    /// <summary>The drop of one defeated monster that a winner rolls for.</summary>
    /// <param name="Npc">The monster's record with its item list.</param>
    /// <param name="Monster">The monster fighter, or null when the roll is not tied to one.</param>
    /// <param name="Copies">How many copies of the rolled item the winner gets.</param>
    private sealed record DropShare(
        NorthIsland.GameData.Npcs.NpcRecord Npc,
        Fighter? Monster,
        int Copies);

    /// <summary>Rolls the item a monster drops from its own item list.</summary>
    /// <param name="npc">The monster's record.</param>
    /// <returns>The item id, or null.</returns>
    private ushort? RollDrop(NorthIsland.GameData.Npcs.NpcRecord npc) =>
        BattleFormulas.Drop(npc, options.Value.MonsterDropChancePercent, GameRandom.Shared, id => items[id]?.ShopColumn ?? 1);

    /// <summary>Rolls the item a monster drops from the seasonal event table, once per monster fighter.</summary>
    /// <param name="share">The drop.</param>
    /// <param name="table">The seasonal event's drop table.</param>
    /// <returns>The item id, or null.</returns>
    private static ushort? EventDropOf(DropShare share, IReadOnlyList<CalendarDrop> table)
    {
        if (share.Monster is not { } monster)
            return BattleFormulas.EventDrop(table, GameRandom.Shared);
        if (!monster.EventDropRolled)
        {
            monster.EventDropItem = BattleFormulas.EventDrop(table, GameRandom.Shared);
            monster.EventDropRolled = true;
        }
        return monster.EventDropItem;
    }

    /// <summary>Rolls the item a monster drops from its own list, once per monster fighter.</summary>
    /// <param name="share">The drop.</param>
    /// <returns>The item id, or null.</returns>
    private ushort? DropOf(DropShare share)
    {
        if (share.Monster is not { } monster)
            return RollDrop(share.Npc);
        if (!monster.DropRolled)
        {
            monster.DropItem = RollDrop(share.Npc);
            monster.DropRolled = true;
        }
        return monster.DropItem;
    }

    /// <summary>The experience a win on the Training Island or in one of its areas adds for a living winner.</summary>
    /// <param name="character">The winner's character.</param>
    /// <param name="defeated">The records of the defeated monsters.</param>
    /// <returns>The flat experience, or 0 when it does not apply.</returns>
    private int TrainingFlatExp(Data.Character character, List<NorthIsland.GameData.Npcs.NpcRecord> defeated)
    {
        var o = options.Value;
        return o.TrainingIslandFlatExp > 0 && TrainingIslandHandlers.IsIslandMap(character.MapId) && defeated.Count > 0
            ? o.TrainingIslandFlatExp : 0;
    }

    /// <summary>Gives a winner the results of a battle other than experience: monster book entries, levels and skills, gold and drops.</summary>
    /// <param name="session">The winner's session.</param>
    /// <param name="defeated">The records of the defeated monsters.</param>
    /// <param name="dropFrom">The drops the winner rolls for.</param>
    /// <param name="exp">The experience the winner earned, for the log.</param>
    private void Reward(GameSession session, List<NorthIsland.GameData.Npcs.NpcRecord> defeated, List<DropShare> dropFrom, int exp)
    {
        var character = session.ActiveCharacter!;
        MonsterBookHandlers.Record(session, character, defeated);
        experience.ApplyLevelUps(session, announceToSelf: false);
        foreach (var skill in skills.LearnCells(character))
        {
            session.Send(InnMessages.SetAttribute(Action8.AttrSkillLevel, 1, skill.Id));
            logger.LogInformation("[{Session}] {Name} learns {Skill} ({Id})", session.Id, character.Name, skill.Name, skill.Id);
        }

        var gold = options.Value.MonsterGoldRewards ? defeated.Sum(n => BattleFormulas.GoldReward(n, GameRandom.Shared)) : 0;
        if (gold > 0)
            Purse.Credit(session, character, gold);

        List<string> dropped = [];
        var seasonal = calendar.Drops();
        foreach (var share in dropFrom)
        {
            if (share.Monster is { DropGiven: true })
                continue;
            foreach (var (drop, regular) in new[] { (DropOf(share), true), (EventDropOf(share, seasonal), false) })
            {
                if (drop is not { } itemId || items[itemId] is null)
                    continue;
                var times = share.Copies * (regular && TimedEffects.DoubleDrop(character, TimedEffects.Now) ? 2 : 1);
                var added = Bag.Add(character, items, itemId, times).Sum(c => c.Added);
                if (added == 0)
                    continue;
                session.Send(Protocol.Messages.Items.GroundMessages.ItemObtained(itemId, (ushort)added));
                dropped.Add(items[itemId]!.Name + (regular ? "" : " (event)"));
            }
        }
        logger.LogInformation("[{Session}] {Name} gains {Exp} exp ({Total} total), {Gold} gold{Drops}", session.Id, character.Name,
            exp, character.Exp, gold, dropped.Count > 0 ? ", drops: " + string.Join(", ", dropped) : "");
    }

    /// <summary>Settles a lost battle: ends it and sends players back who were sent back or who lost in an arena.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    private void Lose(GameSession session, Battle battle)
    {
        var losers = battle.ActivePlayers.Where(p => p.Session?.ActiveCharacter is not null).ToList();

        End(session, battle, BattleOutcome.Lost, "defeated");
        foreach (var loser in losers)
        {
            var s = loser.Session!;
            var character = s.ActiveCharacter!;
            logger.LogInformation("[{Session}] {Name} is defeated: -{Exp} exp{SentBack}", s.Id, character.Name, loser.ExpLost,
                loser.SentBack ? ", sent back to Starter's beach" : "");
            if (EventArenas.IsArena(character.MapId))
            {
                var from = character.MapId;
                marks.SendRemoved(s, EventArenas.SendBack(character));
                character.MapId = from;
                s.Event = null;
                world.ChangeMap(s, EventArenas.Lobby);
                continue;
            }
            if (!loser.SentBack)
                continue;

            s.Event = null;
            world.ChangeMap(s, WorldHandlers.StartersBeach);
        }
    }

    /// <summary>The item id of the Toy Puppet.</summary>
    public const ushort ToyPuppet = 34071; // Toy Puppet

    /// <summary>Uses up a Toy Puppet when a player or their pet ends the battle down, so the losses are not taken.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="player">The player fighter.</param>
    /// <param name="pet">The player's pet fighter, or null.</param>
    /// <returns>True when a puppet was used up.</returns>
    private bool UseToyPuppet(GameSession session, Fighter player, Fighter? pet)
    {
        if (session.ActiveCharacter is not { } character || EventArenas.IsArena(character.MapId))
            return false;
        var fallenPet = pet is { IsAlive: false, OwnedPet: { LeaseUntil: null } p } ? p : null;
        if (fallenPet is null && (player.IsAlive || TimedEffects.ExpProtected(character, TimedEffects.Now)))
            return false;
        if (character.Equipment.Find(e => e.ItemId == ToyPuppet) is { } worn)
        {
            character.Equipment.Remove(worn);
            combat.Send(session);
            session.Send(ItemMessages.EquipSlotCleared(worn.Slot));
            world.Broadcast(session, ItemMessages.PlayerEquipChange(session.Account!.LoginId, worn.ItemId, on: false), sameMapOnly: false);
        }
        else if (fallenPet?.Equipment.Find(e => e.ItemId == ToyPuppet) is { } petWorn)
        {
            fallenPet.Equipment.Remove(petWorn);
            session.Send(PetMessages.EquipCleared(fallenPet.Slot, petWorn.Slot));
        }
        else
            return false;
        logger.LogInformation("[{Session}] {Name}: a Toy Puppet takes the losses of this battle", session.Id, character.Name);
        return true;
    }

    /// <summary>Takes the experience a player loses for ending a battle down and tells their client.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="character">The player's character.</param>
    /// <returns>The experience lost, 0 when none applies.</returns>
    private int LoseExp(GameSession session, Data.Character character)
    {
        if (formulas.Table is not { } f || EventArenas.IsArena(character.MapId) || TimedEffects.ExpProtected(character, TimedEffects.Now))
            return 0;
        var floor = f.TotalLevelExp(character.Level, character.RebornCount);
        var lost = Math.Clamp(
            f.LevelUpExp(character.Level, character.RebornCount) * options.Value.DefeatExpLossPercent / 100,
            0,
            Math.Max(0, character.Exp - floor));
        if (lost <= 0)
            return 0;
        character.Exp -= lost;
        session.Send(InnMessages.SetAttribute(Action8.AttrExp, character.Exp));
        return lost;
    }

    /// <summary>Gives a pet experience and its level-ups.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="pet">The pet.</param>
    /// <param name="exp">The experience.</param>
    private void PetGainsExp(GameSession session, Data.Pet pet, int exp) => experience.PetGains(session, pet, exp);
}
