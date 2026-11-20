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
    /// <returns>True when the battle ended.</returns>
    private bool LeaveWithTeam(
        GameSession session,
        Battle battle,
        Fighter player,
        string how,
        bool fled = true)
    {
        List<Fighter> leaving = [player, .. TeamFollowers(battle, player)];
        leaving.AddRange(JoinersOf(battle, player).Where(j => !leaving.Contains(j)));
        if (!battle.IsPvp && battle.ActivePlayers.All(leaving.Contains))
        {
            End(session, battle, BattleOutcome.Lost, how, fled);
            return true;
        }
        foreach (var leaver in leaving)
            Leave(session, battle, leaver, BattleOutcome.Lost, alsoTell: true);
        if (leaving.Count > 1)
            logger.LogInformation("[{Session}] {Name} led {Count} team member(s) out of the battle", session.Id, player.Name, leaving.Count - 1);
        return battle.IsPvp && TryFinish(session, battle);
    }

    /// <summary>Takes a player out of their battle when they leave the game, and plays the round if they were the last one it waited for.</summary>
    /// <param name="session">The session of the player who leaves.</param>
    private void OnLeavingWorld(GameSession session)
    {
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
            if (LeaveWithTeam(session, battle, player, "left the game", fled: false))
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
    private void End(
        GameSession session,
        Battle battle,
        BattleOutcome outcome,
        string how,
        bool fled = false)
    {
        lock (battle.Lock)
        {
            if (battle.Phase == BattlePhase.Over)
                return;
            battle.Phase = BattlePhase.Over;
        }
        foreach (var monster in battle.Monsters.ToList())
            GiveDropInBattle(battle, monster);
        foreach (var player in battle.ActivePlayers.ToList())
            Leave(session, battle, player, outcome, alsoTell: false);
        List<GameSession> watchers;
        lock (battle.Watchers)
            watchers = [.. battle.Watchers];
        foreach (var w in watchers)
            StopWatching(w, battle);
        MonsterAfterBattle(battle, outcome, fled);
        logger.LogInformation("[{Session}] battle {Index} over: {How} ({Outcome}) after {Rounds} round(s)", session.Id,
            battle.EventBattleIndex, how, outcome, battle.Round);
    }

    /// <summary>Takes one player and their pet out of the battle and settles what the battle cost or gave them.</summary>
    /// <param name="logSession">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The player fighter who leaves.</param>
    /// <param name="outcome">How the battle ended for them.</param>
    /// <param name="alsoTell">True to tell the others in the battle that they ran off.</param>
    private void Leave(
        GameSession logSession,
        Battle battle,
        Fighter player,
        BattleOutcome outcome,
        bool alsoTell)
    {
        var pet = battle.PetOf(player);
        var session = player.Session;
        if (session is null)
        {
            player.Left = true;
            pet?.Left = true;
            return;
        }
        if (ReferenceEquals(session.Battle, battle))
            session.Battle = null;
        session.Encounters.NoBattleBefore = GameClock.UtcNow.AddSeconds(options.Value.BattleImmunitySeconds);
        if (session.HiddenInBattle)
            ShowJoiner(session, battle);
        else
            SendFightState(session, 0, skip: null);

        var deathCosts = !UseToyPuppet(session, player, pet);
        if (!player.IsAlive && deathCosts && session.ActiveCharacter is { } fallenPlayer)
            player.ExpLost = LoseExp(session, fallenPlayer);

        Data.Pet? ranAway = null;
        if (deathCosts && pet is { IsAlive: false, OwnedPet: { LeaseUntil: null } fallen })
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
            if (fallen.Amity < o.PetRunAwayAmity && GameRandom.Shared.Next(100) < o.PetRunAwayChancePercent)
                ranAway = fallen;
        }

        if (pet is { IsAlive: true, Left: false })
        {
            var petLeaves = BattleMessages.FighterLeave(pet.Row, pet.Col);
            if (alsoTell)
                battle.Send(petLeaves);
            else
                session.Send(petLeaves);
        }
        if (alsoTell)
            battle.Send(BattleMessages.PlayerLeft(player.Id), except: session);
        session.Send(BattleMessages.FighterLeave(player.Row, player.Col));
        player.Left = true;
        pet?.Left = true;
        if (ReferenceEquals(player, battle.Player) && session.Event is { } run)
            run.BattleOutcome = outcome;
        TrojanRoundWon(session, battle, player, outcome);

        if (session.ActiveCharacter is { } character)
        {
            character.Hp = Math.Max(1, player.Hp);
            character.Sp = player.Sp;
            session.Send(InnMessages.SetAttribute(Action8.AttrHp, character.Hp));
            session.Send(InnMessages.SetAttribute(Action8.AttrSp, character.Sp));
            var wear = itemOptions.Value.Durability;
            var highest = battle.Monsters.Select(m => (int)m.Level).DefaultIfEmpty(0).Max();
            var playerGap = Durability.Wears(character.Level, highest, wear.WearMinLevelGap);
            var petGap = pet?.OwnedPet is { } fighting && Durability.Wears(fighting.Level, highest, wear.WearMinLevelGap);
            Durability.AfterBattle(session.Send, character, pet?.OwnedPet, (e, onPet) => Durability.WearOf(items[e.ItemId], onPet ? petGap : playerGap, wear));

            if (pet is { OwnedPet: { } activePet })
            {
                activePet.Hp = Math.Max(1, pet.Hp);
                activePet.Sp = pet.Sp;
                session.Send(PetMessages.SetAttribute(activePet.Slot, Action8.AttrHp, activePet.Hp));
                session.Send(PetMessages.SetAttribute(activePet.Slot, Action8.AttrSp, activePet.Sp));
            }

            foreach (var (_, npc) in battle.Caught.Where(c => ReferenceEquals(c.Catcher, player)))
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
        if (ranAway is not null && session.ActiveCharacter is { } master)
        {
            Pets.Exile(session, master, ranAway, world, combat);
            logger.LogInformation(
                "[{Session}] {Name}: pet {Pet} (amity {Amity}) runs away with {Gear} worn item(s)",
                session.Id,
                master.Name,
                ranAway.Name,
                ranAway.Amity,
                ranAway.Equipment.Count);
        }
        if (alsoTell)
            logger.LogInformation("[{Session}] {Name} runs off; the others fight on", logSession.Id, player.Name);
    }
}
