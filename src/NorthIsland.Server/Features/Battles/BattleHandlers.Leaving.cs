using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>Takes a player, their team followers and joiners out of the battle.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The player fighter who leaves.</param>
    /// <param name="how">Text for the log that says why they left.</param>
    /// <param name="fled">True when they ran off, which matters for what happens to a monster that started the battle.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell (the round's play).</param>
    /// <returns>True when the battle ended.</returns>
    private bool LeaveWithTeam(
        GameSession session,
        Battle battle,
        Fighter player,
        string how,
        bool fled = true,
        GameSession? holder = null)
    {
        List<Fighter> leaving = [player, .. TeamFollowers(battle, player)];
        leaving.AddRange(JoinersOf(battle, player).Where(j => !leaving.Contains(j)));
        if (!battle.IsPvp && battle.ActivePlayers.All(leaving.Contains))
        {
            End(session, battle, BattleOutcome.Lost, how, fled, holder);
            return true;
        }
        foreach (var leaver in leaving)
            Leave(session, battle, leaver, BattleOutcome.Lost, alsoTell: true, holder);
        if (leaving.Count > 1)
            logger.LogInformation("[{Session}] {Name} led {Count} team member(s) out of the battle", session.Id, player.Name, leaving.Count - 1);
        return battle.IsPvp && TryFinish(session, battle, holder);
    }

    /// <summary>
    /// Drops the join a player who leaves the game waits with, takes them out of their battle, and plays the round if they were the last one it
    /// waited for. Runs from <see cref="WorldState.Leave"/>, whose caller holds the player's gate.
    /// </summary>
    /// <param name="session">The session of the player who leaves.</param>
    private void OnLeavingWorld(GameSession session)
    {
        _waitingJoins.TryRemove(session, out _);
        if (session.Battle is not { } battle)
            return;
        var player = battle.Players.Find(p => ReferenceEquals(p.Session, session) && !p.Left);
        if (player is null || battle.Phase == BattlePhase.Over)
            return;
        Dictionary<Fighter, BattleCommand>? commands = null;
        GameSession? next = null;
        session.LeavingGame = true;
        lock (battle.Lock)
        {
            logger.LogInformation("[{Session}] {Name} leaves the game in battle {Index}", session.Id, player.Name, battle.EventBattleIndex);
            // world.Leave runs under the player's gate: the disconnect cleanup's, or the kicker's (WorldState.Disconnect)
            if (LeaveWithTeam(session, battle, player, "left the game", fled: false, holder: session))
                return;
            if (battle.Phase == BattlePhase.Command && !battle.AwaitsCommands && battle.ActivePlayers.FirstOrDefault()?.Session is { } other)
            {
                battle.Phase = BattlePhase.Acting;
                commands = new(battle.Commands);
                next = other;
            }
        }
        if (commands is not null)
            _ = PlayRoundAsync(next!, battle, commands);
    }

    /// <summary>Finds the players who joined with the join cursor and leave along with the player who started the battle.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="leaver">The player who leaves.</param>
    /// <returns>The joiners on the leaver's side, empty unless the leaver started the battle.</returns>
    private static List<Fighter> JoinersOf(Battle battle, Fighter leaver) =>
        ReferenceEquals(leaver, battle.Player)
            ? [.. battle.ActivePlayers.Where(p => p.Joined && p != leaver && p.Side == leaver.Side)]
            : [];

    /// <summary>Finds the team members fighting beside a team leader.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="leader">The player who may lead a team.</param>
    /// <returns>The active players on the leader's side whose team leader they are, empty when they do not lead a team.</returns>
    private List<Fighter> TeamFollowers(Battle battle, Fighter leader) =>
        world.Teams.IsLeader(leader.Id)
            ? [.. battle.ActivePlayers.Where(p => p != leader && p.Side == leader.Side && world.Teams.LeaderOf(p.Id) == leader.Id)]
            : [];

    /// <summary>Rolls whether a flee attempt succeeds.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="fleeing">The player or pet who flees.</param>
    /// <returns>True when the roll is below the flee chance against the enemies standing.</returns>
    private bool FleeSucceeds(Battle battle, Fighter fleeing) =>
        GameRandom.Shared.NextDouble() * 100 < BattleFormulas.FleeChance(fleeing, battle.EnemiesOf(fleeing), options.Value);

    /// <summary>Plays a flee attempt that failed.</summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The fighter who failed to flee.</param>
    private async Task FailToFleeAsync(GameSession session, Battle battle, Fighter player)
    {
        battle.SendAttackArea(player);
        battle.Send(client => BattleMessages.Action(player.Row, player.Col, SkillTable.FailToFlee,
            [new ActionTarget(player.Row, player.Col, Hit: false, Guard: 0, [])], client));
        logger.LogInformation("[{Session}] {Player} fails to flee", session.Id, player.Name);
        await ActionPause(SkillTable.FailToFlee, player, player).ConfigureAwait(false);
    }

    /// <summary>Ends a battle: gives out the drops, takes every player out, stops the watchers and handles the monster that started it.</summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle; nothing happens when it is already over.</param>
    /// <param name="outcome">How the battle ended for the players.</param>
    /// <param name="how">Text for the log that says why it ended.</param>
    /// <param name="fled">True when the players fled.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell (the round's play).</param>
    private void End(
        GameSession session,
        Battle battle,
        BattleOutcome outcome,
        string how,
        bool fled = false,
        GameSession? holder = null)
    {
        if (TryClaimEnd(battle))
            Close(session, battle, outcome, how, fled, holder);
    }

    /// <summary>Marks a battle over, once: of several near-simultaneous ends, only the first goes on to settle the battle.</summary>
    /// <param name="battle">The battle.</param>
    /// <returns>True for the caller that ended it; false when it was already over.</returns>
    private static bool TryClaimEnd(Battle battle)
    {
        lock (battle.Lock)
        {
            if (battle.Phase == BattlePhase.Over)
                return false;
            battle.Phase = BattlePhase.Over;
            return true;
        }
    }

    /// <summary>
    /// Settles a battle its caller marked over with <see cref="TryClaimEnd"/>: gives out the drops, takes every player out, stops the
    /// watchers, answers the joins that wait for it and handles the monster that started it. The drops and each player's exit change the
    /// player under their own gate (<see cref="GameSession.HandOver"/>).
    /// </summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="outcome">How the battle ended for the players.</param>
    /// <param name="how">Text for the log that says why it ended.</param>
    /// <param name="fled">True when the players fled.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell (the round's play).</param>
    private void Close(
        GameSession session,
        Battle battle,
        BattleOutcome outcome,
        string how,
        bool fled = false,
        GameSession? holder = null)
    {
        foreach (var monster in battle.Monsters.ToList())
            GiveDropInBattle(battle, monster, holder);
        foreach (var player in battle.ActivePlayers.ToList())
            Leave(session, battle, player, outcome, alsoTell: false, holder);
        List<GameSession> watchers;
        lock (battle.Watchers)
            watchers = [.. battle.Watchers];
        foreach (var w in watchers)
            StopWatching(w, battle);
        DropWaitingJoins(battle);
        MonsterAfterBattle(battle, outcome, fled);
        logger.LogInformation("[{Session}] battle {Index} over: {How} ({Outcome}) after {Rounds} round(s)", session.Id,
            battle.EventBattleIndex, how, outcome, battle.Round);
    }

    /// <summary>
    /// Takes one player and their pet out of the battle and settles what the battle cost or gave them, once per player: a later call for the
    /// same player does nothing. Who leaves a fight that goes on against a monster with a respawn for each player sees it gone until the end.
    /// The battle sees them gone at once; the changes to the player (their exit, the losses, HP, SP and wear, the
    /// caught pets) run under the player's gate (<see cref="GameSession.HandOver"/>), as this may run under another player's gate or none.
    /// </summary>
    /// <param name="logSession">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The player fighter who leaves.</param>
    /// <param name="outcome">How the battle ended for them.</param>
    /// <param name="alsoTell">True to tell the others in the battle that they ran off.</param>
    /// <param name="holder">The session whose gate the caller holds, or null when it holds none or cannot tell (the round's play).</param>
    private void Leave(
        GameSession logSession,
        Battle battle,
        Fighter player,
        BattleOutcome outcome,
        bool alsoTell,
        GameSession? holder = null)
    {
        lock (battle.Lock)
        {
            if (player.Settled)
                return;
            player.Settled = true;
            if (battle.Phase != BattlePhase.Over && HasPersonalRespawn(battle))
                world.HideMonster(battle.MapId, battle.SourceRole, BattleHideSeconds, player: player.Id);
        }
        var pet = battle.PetOf(player);
        var session = player.Session;
        var petOnField = pet is { IsAlive: true, Left: false };
        if (session is not null && alsoTell)
        {
            if (petOnField)
                battle.Send(BattleMessages.FighterLeave(pet!.Row, pet.Col), except: session); // the player's own client is told in Settle
            battle.Send(BattleMessages.PlayerLeft(player.Id), except: session);
        }
        player.Left = true;
        pet?.Left = true;
        if (session is null)
            return;
        // what Settle reads of the battle, taken now: the others still fight on, catch and get reinforcements meanwhile
        var settling = new Settling(
            player,
            pet,
            petOnField,
            ReferenceEquals(player, battle.Player),
            [.. battle.FallenPetsOf(player)],
            [.. battle.Caught.Where(c => ReferenceEquals(c.Catcher, player)).Select(c => c.Npc)],
            battle.Monsters.Select(m => (int)m.Level).DefaultIfEmpty(0).Max());
        session.HandOver(holder, "battle exit", () => Settle(session, battle, settling, outcome));
        if (alsoTell)
            logger.LogInformation("[{Session}] {Name} runs off; the others fight on", logSession.Id, player.Name);
    }

    /// <summary>
    /// Settles a player's leaving under the player's gate: takes them out of the battle on their client, takes what the battle cost (Toy
    /// Puppet, experience, fallen pets) and gives the HP, SP, wear and caught pets.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="settling">What the battle left the player with, taken when they left.</param>
    /// <param name="outcome">How the battle ended for them.</param>
    private void Settle(GameSession session, Battle battle, Settling settling, BattleOutcome outcome)
    {
        var (player, pet, petOnField, starter, fallenBefore, caughtNpcs, highest) = settling;
        if (ReferenceEquals(session.Battle, battle))
            session.Battle = null;
        session.Encounters.NoBattleBefore = GameClock.UtcNow.AddSeconds(options.Value.BattleImmunitySeconds);
        if (session.HiddenInBattle)
            ShowJoiner(session, battle);
        else
            SendFightState(session, 0, skip: null);

        // the pet on the field when it is down, and every pet that fell earlier and was taken off the field for another
        List<Fighter> fallenPets = [.. fallenBefore];
        if (pet is { IsAlive: false })
            fallenPets.Add(pet);
        var deathCosts = !UseToyPuppet(session, player, fallenPets);
        if (!player.IsAlive && deathCosts && session.ActiveCharacter is { } fallenPlayer)
            player.ExpLost = LoseExp(session, fallenPlayer);

        List<Data.Pet> ranAway = [];
        foreach (var fallenPet in fallenPets)
        {
            if (deathCosts && fallenPet.OwnedPet is { LeaseUntil: null } fallen && PetFalls(session, fallen))
                ranAway.Add(fallen);
        }

        if (petOnField)
            session.Send(BattleMessages.FighterLeave(pet!.Row, pet.Col));
        session.Send(BattleMessages.FighterLeave(player.Row, player.Col));
        if (starter && session.Event is { } run)
            run.BattleOutcome = outcome;
        TrojanRoundWon(session, battle, player, outcome);

        if (session.ActiveCharacter is { } character)
        {
            character.Hp = Math.Max(1, player.Hp);
            character.Sp = player.Sp;
            session.Send(InnMessages.SetAttribute(Action8.AttrHp, character.Hp));
            session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
            var wear = itemOptions.Value.Durability;
            var playerGap = Durability.Wears(character.Level, highest, wear.WearMinLevelGap);
            var petGap = pet?.OwnedPet is { } fighting && Durability.Wears(fighting.Level, highest, wear.WearMinLevelGap);
            Durability.AfterBattle(session.Send, character, pet?.OwnedPet, (e, onPet) => Durability.WearOf(items[e.ItemId], onPet ? petGap : playerGap, wear));

            foreach (var petFighter in pet is null ? fallenBefore : fallenBefore.Append(pet))
            {
                if (petFighter.OwnedPet is not { } fought)
                    continue;
                fought.Hp = Math.Max(1, petFighter.Hp);
                fought.Sp = petFighter.Sp;
                session.Send(PetMessages.SetAttribute(fought.Slot, Action8.AttrHp, fought.Hp));
                session.Send(PetMessages.SetAttribute(fought.Slot, Action8.AttrSp, fought.Sp));
            }

            foreach (var npc in caughtNpcs)
            {
                var slot = Pets.FreeSlot(character);
                if (slot == 0)
                    break;
                var caught = Pets.Caught(npc, slot, formulas.Table, petOptions.Value, itemOptions.Value.ItemUse.MaxAmity);
                character.Pets.Add(caught);
                session.Send(PetMessages.Joined(session.Account!.LoginId, Pets.Info(caught)));
                logger.LogInformation("[{Session}] {Name} gets pet {Pet} ({Id}) in slot {Slot}: level {Level}, HP {Hp}, SP {Sp}",
                    session.Id, character.Name, caught.Name, caught.NpcId, slot, caught.Level, caught.MaxHp, caught.MaxSp);
            }
        }
        foreach (var runaway in ranAway)
        {
            if (session.ActiveCharacter is not { } master)
                break;
            Pets.Exile(session, master, runaway, world, combat);
            logger.LogInformation(
                "[{Session}] {Name}: pet {Pet} (amity {Amity}) runs away with {Gear} worn item(s)",
                session.Id,
                master.Name,
                runaway.Name,
                runaway.Amity,
                runaway.Equipment.Count);
        }
        instances.AfterBattle(session); // an instance that ended during the fight sends its player out now
    }

    /// <summary>What a player who leaves a battle is settled with, taken from the battle when they leave.</summary>
    /// <param name="Player">The player fighter.</param>
    /// <param name="Pet">The player's pet fighter on the field, or null.</param>
    /// <param name="PetOnField">True when the pet was standing on the field.</param>
    /// <param name="Starter">True when the player started the battle.</param>
    /// <param name="FallenPets">The player's pets that fell earlier and were taken off the field for another.</param>
    /// <param name="CaughtNpcs">The monsters the player caught.</param>
    /// <param name="HighestMonsterLevel">The highest monster level in the battle, for the gear's wear; 0 without monsters.</param>
    private sealed record Settling(
        Fighter Player,
        Fighter? Pet,
        bool PetOnField,
        bool Starter,
        List<Fighter> FallenPets,
        List<NorthIsland.GameData.Npcs.NpcRecord> CaughtNpcs,
        int HighestMonsterLevel);

    /// <summary>Takes what a pet's fall in the battle costs: amity and experience, and rolls whether it runs away.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="fallen">The pet that fell.</param>
    /// <returns>True when the pet runs away from its owner.</returns>
    private bool PetFalls(GameSession session, Data.Pet fallen)
    {
        var o = petOptions.Value;
        if (o.PetDefeatAmityLoss > 0)
        {
            fallen.Amity = (byte)Math.Max(0, fallen.Amity - o.PetDefeatAmityLoss);
            session.Send(PetMessages.SetAttribute(fallen.Slot, Action8.AttrAmity, fallen.Amity));
            logger.LogInformation("[{Session}] pet {Pet} fell: amity {Amity}", session.Id, fallen.Name, fallen.Amity);
        }
        if (o.PetDefeatExpLossPercent > 0 && formulas.Table is { } f && session.ActiveCharacter is { } owner
            && !EventArenas.IsArena(owner.MapId) && !TimedEffects.ExpProtected(owner, TimedEffects.Now))
        {
            var floor = f.TotalLevelExp(fallen.Level, fallen.Reborn);
            var lost = Math.Clamp(f.LevelUpExp(fallen.Level, fallen.Reborn) * o.PetDefeatExpLossPercent / 100, 0, Math.Max(0, fallen.Exp - floor));
            if (lost > 0)
            {
                fallen.Exp -= lost;
                session.Send(PetMessages.SetAttribute(fallen.Slot, Action8.AttrExp, fallen.Exp));
                logger.LogInformation("[{Session}] pet {Pet} fell: -{Exp} exp", session.Id, fallen.Name, lost);
            }
        }
        return fallen.Amity < o.PetRunAwayAmity && GameRandom.Shared.Next(100) < o.PetRunAwayChancePercent;
    }
}
