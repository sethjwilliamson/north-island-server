using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>
    /// Plays a round: every fighter acts in turn order, then statuses tick, knock-offs play and the next round starts or the battle
    /// ends.
    /// </summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="commands">The commands the players chose.</param>
    private async Task PlayRoundAsync(GameSession session, Battle battle, Dictionary<Fighter, BattleCommand> commands)
    {
        try
        {
            if (!battle.ActivePlayers.Any())
            {
                End(session, battle, BattleOutcome.Lost, "everyone left");
                return;
            }
            foreach (var own in battle.OwnSide)
                own.Defending = commands.TryGetValue(own, out var c) && c is { IsItem: false, SkillOrItem: SkillTable.Defense };
            var order = BattleFormulas.TurnOrder(battle.All);
            HashSet<Fighter> inCombo = [];
            Dictionary<Fighter, MonsterPlan> monsterPlans = [];
            for (var turn = 0; turn < order.Count; turn++)
            {
                var actor = order[turn];
                if (!actor.IsAlive || actor.Left || inCombo.Contains(actor))
                    continue;
                if (await TryComboAsync(session, battle, order, turn, commands, monsterPlans, inCombo).ConfigureAwait(false) is { } over)
                {
                    if (over)
                        return;
                    continue;
                }
                if (actor.IsHuman)
                {
                    if (commands.TryGetValue(actor, out var command) && await OwnActsAsync(session, battle, actor, command).ConfigureAwait(false))
                        return;
                    continue;
                }
                if (StatusRules.CannotAct(actor))
                {
                    logger.LogInformation("[{Session}] {Monster} cannot act ({Statuses})", session.Id, actor.Name,
                        string.Join(", ", actor.Statuses.Select(s => s.Effect)));
                    continue;
                }
                await MonsterAttacksAsync(session, battle, actor, monsterPlans.GetValueOrDefault(actor)).ConfigureAwait(false);
                if (TryFinish(session, battle))
                    return;
            }
            if (await TickStatusesAsync(session, battle).ConfigureAwait(false))
                return;
            await FlushKnockOffsAsync(battle).ConfigureAwait(false);
            await TrainingIslandHealAsync(session, battle).ConfigureAwait(false);
            if (battle.IsPvp && TryFinish(session, battle))
                return;
            if (!battle.ActivePlayers.Any())
            {
                End(session, battle, BattleOutcome.Lost, "everyone left");
                return;
            }
            var entered = battle.TakeReinforcements();
            if (entered.Count > 0)
            {
                battle.Send(client => BattleMessages.Join(entered.Select(m => m.ToRecord(own: false)), client));
                logger.LogInformation("[{Session}] reinforcements: {Monsters} ({Left} still waiting)", session.Id,
                    string.Join(", ", entered.Select(m => $"{m.Name} at ({m.Row},{m.Col})")), battle.Reinforcements.Count);
                await Pause().ConfigureAwait(false);
            }
            SendRoundHp(battle);
            StartRound(session, battle);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Session}] battle round failed; ending the battle", session.Id);
            End(session, battle, BattleOutcome.Lost, "error");
        }
    }

    /// <summary>Ends the battle when one side is down.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <returns>True when the battle ended.</returns>
    private bool TryFinish(GameSession session, Battle battle)
    {
        if (battle.IsPvp)
        {
            if (!battle.SideDown(1) && !battle.SideDown(2))
                return false;
            EndPvp(session, battle, winner: battle.SideDown(1) ? (byte)2 : (byte)1);
            return true;
        }
        if (battle.AllMonstersDown)
        {
            Win(session, battle);
            return true;
        }
        if (battle.AllPlayersDown)
        {
            Lose(session, battle);
            return true;
        }
        return false;
    }

    /// <summary>Applies the poison and regeneration of the round, removes the statuses that wear off, then checks whether the battle is over.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <returns>True when the battle ended.</returns>
    private async Task<bool> TickStatusesAsync(GameSession session, Battle battle)
    {
        foreach (var f in battle.All.Where(f => f.IsAlive).ToList())
        {
            var change = 0;
            foreach (var s in f.Statuses)
            {
                if (s.Effect == StatusRules.Poison && !f.IsInAdminMode)
                    change -= Math.Min(f.Hp - (f.IsHuman ? 1 : 0), s.Amount);
                else if (s.Effect == StatusRules.Regeneration)
                    change += s.Amount;
            }
            if (change == 0)
                continue;
            f.Hp = Math.Clamp(f.Hp + change, 0, f.MaxHp);
            battle.Send(BattleMessages.FighterAttributes([(f.Row, f.Col, BattleMessages.AttrHp, f.Hp)]));
            logger.LogInformation("[{Session}] {Fighter}: {Change:+#;-#} HP from statuses ({Hp}/{Max})", session.Id, f.Name, change, f.Hp, f.MaxHp);
            if (!f.IsAlive)
            {
                f.Statuses.Clear();
                await KnockOffAsync(battle, f).ConfigureAwait(false);
            }
        }
        foreach (var f in battle.All)
        {
            foreach (var s in f.Statuses.Where(s => s.LastRound <= battle.Round).ToList())
            {
                f.Statuses.Remove(s);
                if (f.IsAlive)
                    SendStatusRemoval(battle, f, s);
                logger.LogInformation("[{Session}] {Fighter}: status {Effect} ({Skill}) wears off", session.Id, f.Name, s.Effect, s.SkillId);
            }
        }
        return TryFinish(session, battle);
    }

    /// <summary>Sends each player's and pet's HP to their owner after the round's actions, if it changed.</summary>
    /// <param name="battle">The battle.</param>
    private static void SendRoundHp(Battle battle)
    {
        foreach (var player in battle.ActivePlayers.Where(p => p.Kind == 2 && p.Session is not null).ToList()) // player
        {
            var hp = Math.Max(0, player.Hp);
            if (player.HpSent != hp)
            {
                player.Session!.Send(InnMessages.SetAttribute(Action8.AttrHp, hp));
                player.HpSent = hp;
            }
            if (battle.PetOf(player) is { Left: false, OwnedPet: { } pet } petFighter)
            {
                var petHp = Math.Max(0, petFighter.Hp);
                if (petFighter.HpSent != petHp)
                {
                    player.Session!.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrHp, petHp));
                    petFighter.HpSent = petHp;
                }
            }
        }
    }

    /// <summary>Opens the command phase of a new round, or plays it at once when nobody on the player's side can act.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    private void StartRound(GameSession session, Battle battle)
    {
        int round;
        lock (battle.Lock)
        {
            if (battle.Phase == BattlePhase.Over)
                return;
            round = ++battle.Round;
            battle.Phase = BattlePhase.Command;
            battle.Commands.Clear();
        }
        battle.Send(BattleMessages.CommandPhase());
        bool awaits;
        lock (battle.Lock)
        {
            awaits = battle.AwaitsCommands;
            if (!awaits)
                battle.Phase = BattlePhase.Acting;
        }
        if (!awaits)
        {
            logger.LogInformation("[{Session}] round {Round}: nobody on the player's side can act", session.Id, round);
            _ = PlayRoundAsync(session, battle, new Dictionary<Fighter, BattleCommand>());
            return;
        }
        _ = DeadlineAsync(session, battle, round);
    }

    /// <summary>Waits for the command time to run out, then plays the round with the commands received so far.</summary>
    /// <param name="session">The session used for logging and for ending the battle.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="round">The round the deadline belongs to; nothing happens if the battle has moved on.</param>
    private async Task DeadlineAsync(GameSession session, Battle battle, int round)
    {
        await Task.Delay(TimeSpan.FromSeconds(CommandSeconds + options.Value.BattleCommandGraceSeconds)).ConfigureAwait(false);
        Dictionary<Fighter, BattleCommand> commands;
        lock (battle.Lock)
        {
            if (battle.Round != round || battle.Phase != BattlePhase.Command)
                return;
            battle.Phase = BattlePhase.Acting;
            commands = new(battle.Commands);
        }
        logger.LogInformation("[{Session}] round {Round}: time is up ({Count} command(s))", session.Id, round, commands.Count);
        await PlayRoundAsync(session, battle, commands).ConfigureAwait(false);
    }

    /// <summary>Waits for the fixed action delay, or 2.5 seconds when none is set.</summary>
    private Task Pause() => Task.Delay(options.Value.BattleActionDelayMs > 0 ? options.Value.BattleActionDelayMs : 2500);

    /// <summary>Waits for the client to play one action.</summary>
    /// <param name="skillId">The skill id of the action.</param>
    /// <param name="attacker">The acting fighter.</param>
    /// <param name="target">The fighter it acts on.</param>
    /// <param name="numbers">True when damage numbers are shown, which adds to the animation time.</param>
    /// <param name="defends">True when the target defends, which changes the animation time.</param>
    private Task ActionPause(
        ushort skillId,
        Fighter attacker,
        Fighter target,
        bool numbers = true,
        bool defends = false) =>
            ActionPause([(skillId, attacker, target)], numbers, defends);

    /// <summary>Waits for the client to play one action that hits every fighter of a skill's area.</summary>
    /// <param name="skillId">The skill id of the action.</param>
    /// <param name="attacker">The acting fighter.</param>
    /// <param name="targets">The fighters it acts on.</param>
    /// <param name="numbers">True when damage numbers are shown, which adds to the animation time.</param>
    /// <param name="defends">True when a target defends, which changes the animation time.</param>
    private Task ActionPause(
        ushort skillId,
        Fighter attacker,
        List<Fighter> targets,
        bool numbers = true,
        bool defends = false) =>
            ActionPause([.. targets.Select(t => (skillId, attacker, t))], numbers, defends, targets.Count);

    /// <summary>Waits for the client to play several actions at once, as in a combination.</summary>
    /// <param name="actions">The skill id, the attacker and the target of each action.</param>
    /// <param name="numbers">True when damage numbers are shown, which adds to the animation time.</param>
    /// <param name="defends">True when the target defends, which changes the animation time.</param>
    /// <param name="hitFighters">How many fighters each action hits; each plays its own reaction.</param>
    private Task ActionPause(
        IReadOnlyList<(ushort SkillId, Fighter Attacker, Fighter Target)> actions,
        bool numbers = true,
        bool defends = false,
        int hitFighters = 1)
    {
        if (options.Value.BattleActionDelayMs > 0 || skills.Motions is not { } motions)
            return Pause();
        var o = options.Value;
        var ticks = new ClientTickTiming
        {
            DrawMs = o.BattleTickDrawMs, VerticalBlankMs = o.BattleTickVerticalBlankHz > 0 ? 1000.0 / o.BattleTickVerticalBlankHz : 0,
            CameraReturnMs = o.BattleTickCameraMs,
        };
        List<int> lengths = [];
        foreach (var (skillId, attacker, target) in actions)
        {
            var ms = motions.EstimateActionMs(skillId, new BattleCell(attacker.Row, attacker.Col), new BattleCell(target.Row, target.Col),
                skills[skillId], numbers, defends, ticks: ticks, hitFighters: hitFighters);
            lengths.Add(ms);
            logger.LogInformation(
                "  skill {Skill} ({AttackerRow},{AttackerCol}) -> ({TargetRow},{TargetCol}): animation {Ms} ms",
                skillId,
                attacker.Row,
                attacker.Col,
                target.Row,
                target.Col,
                ms);
        }
        var wait = ComboRules.WaitMs(lengths, o.BattleActionMarginMs);
        logger.LogInformation("  waiting {Wait} ms", wait);
        return Task.Delay(wait);
    }

    /// <summary>Queues a fallen fighter to be knocked off the field after the round's last action.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="fallen">The fighter who fell.</param>
    private static Task KnockOffAsync(Battle battle, Fighter fallen)
    {
        battle.PendingKnockOffs.Add(fallen);
        return Task.CompletedTask;
    }

    /// <summary>Sends the queued knock-offs, handing out the in-battle drops first, and waits for each to play.</summary>
    /// <param name="battle">The battle.</param>
    private async Task FlushKnockOffsAsync(Battle battle)
    {
        var fallen = battle.PendingKnockOffs.ToList();
        battle.PendingKnockOffs.Clear();
        foreach (var f in fallen)
        {
            if (GiveDropInBattle(battle, f))
                await DropFlyPause().ConfigureAwait(false);
            battle.Send(BattleMessages.KnockedOff(f.Row, f.Col));
            await KnockOffPause().ConfigureAwait(false);
        }
    }

    /// <summary>Waits for a knock-off animation to play.</summary>
    private Task KnockOffPause() =>
        options.Value.BattleActionDelayMs > 0 || skills.Motions is null ? Pause() : Task.Delay(SkillMotionTable.KnockOffMs + 100);
}
