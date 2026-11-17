using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Characters;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.Player;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>Handles a command a player chose for a fighter; the round is played once every fighter that can act has chosen.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code: the acting cell, the skill or item id and the target cell.</param>
    /// <param name="isItem">True when the command uses an item instead of a skill.</param>
    internal ValueTask OnCommand(GameSession session, ReadOnlyMemory<byte> body, bool isItem)
    {
        if (session.Battle is not { } battle || body.Length < 6)
            return ValueTask.CompletedTask;
        var command = BattleCommand.Parse(body.Span, isItem);
        Dictionary<Fighter, BattleCommand> commands;
        lock (battle.Lock)
        {
            if (battle.Phase != BattlePhase.Command || battle.OwnAt(command.ActorRow, command.ActorCol) is not { IsAlive: true } actor
                || !ReferenceEquals(actor.Session, session) || actor.SummonedRound == battle.Round || !battle.Commands.TryAdd(actor, command))
                return ValueTask.CompletedTask;
            logger.LogInformation("[{Session}] round {Round}: {Actor} ({Row},{Col}) uses {What} {Id} on ({TargetRow},{TargetCol})", session.Id,
                battle.Round,
                actor.Name,
                command.ActorRow,
                command.ActorCol,
                isItem ? "item" : "skill",
                command.SkillOrItem,
                command.TargetRow,
                command.TargetCol);
            if (battle.AwaitsCommands)
                return ValueTask.CompletedTask;
            battle.Phase = BattlePhase.Acting;
            commands = new(battle.Commands);
        }
        _ = PlayRoundAsync(session, battle, commands);
        return ValueTask.CompletedTask;
    }

    /// <summary>Plays the command of a player or pet during a round.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="actor">The fighter who acts.</param>
    /// <param name="command">The command chosen for it.</param>
    /// <returns>True when the battle ended.</returns>
    private async Task<bool> OwnActsAsync(GameSession session, Battle battle, Fighter actor, BattleCommand command)
    {
        var isPlayer = actor.Kind == 2; // player
        switch (command)
        {
            case { IsItem: true }: // use an item, then see whether the battle is over
                await UsesItemAsync(session, battle, actor, command).ConfigureAwait(false);
                return TryFinish(session, battle);
            case { SkillOrItem: SkillTable.Defense }: // defend: nothing more to resolve here
                return false;
            case { SkillOrItem: SkillTable.Flee }: // flee: leave the battle with the team, or fail to
                if (FleeSucceeds(battle, actor))
                {
                    var owner = isPlayer ? actor : battle.OwnerOf(actor);
                    if (!isPlayer)
                        logger.LogInformation("[{Session}] pet {Pet} runs off and takes {Owner} with it", session.Id, actor.Name, owner.Name);
                    return LeaveWithTeam(session, battle, owner, "fled");
                }
                await FailToFleeAsync(session, battle, actor).ConfigureAwait(false);
                return false;
            case { SkillOrItem: SkillTable.Capture }: // capture: only a player tries to capture a monster
                if (isPlayer)
                    await PlayerCapturesAsync(session, battle, actor, command).ConfigureAwait(false);
                break;
            default: // hand attack or a skill: attack, or cast a support skill
                SkillRecord? skill = null;
                Fighter? target = null;
                if (!SkillTable.IsNormalAttack(command.SkillOrItem) && !IsPetNormalAttack(actor, command.SkillOrItem))
                {
                    if (Cast(session, battle, actor, command) is not { } cast)
                        return false;
                    (skill, target) = cast;
                }
                if (skill is null or { Type: 1 or 2 })
                    await AttacksAsync(session, battle, actor, command, skill).ConfigureAwait(false);
                else
                    await SupportAsync(session, battle, actor, skill, target!).ConfigureAwait(false);
                break;
        }
        return TryFinish(session, battle);
    }

    /// <summary>Checks that a fighter can cast the chosen skill and pays its SP.</summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="actor">The fighter who casts.</param>
    /// <param name="cast">The command that names the skill and the target cell.</param>
    /// <returns>The skill and its target, or null when the cast is refused.</returns>
    private (SkillRecord Skill, Fighter? Target)? Cast(GameSession session, Battle battle, Fighter actor, BattleCommand cast)
    {
        var (refusal, skill, target) = CastRefusal(battle, actor, cast);
        if (refusal is not null)
        {
            logger.LogWarning("[{Session}] {Actor} cannot use skill {Skill} ({Name}): {Why}", session.Id, actor.Name,
                cast.SkillOrItem, skill?.Name, refusal);
            return null;
        }
        Spend(actor, skill!);
        return (skill!, target);
    }

    /// <summary>Finds the skill and target of a cast and why the cast is not allowed.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="actor">The fighter who casts.</param>
    /// <param name="cast">The command that names the skill and the target cell.</param>
    /// <returns>The refusal text or null when allowed, the skill, and the target.</returns>
    private (string? Refusal, SkillRecord? Skill, Fighter? Target) CastRefusal(Battle battle, Fighter actor, BattleCommand cast)
    {
        var skill = skills[cast.SkillOrItem];
        var target = skill is null ? null : TargetOf(battle, actor, cast, skill);
        var refusal = skill is null ? "unknown skill"
            : !Knows(actor, skill.Id) ? "not learned"
                : RoleSkillRefusal(actor, skill.Id) is { } gear ? gear
                    : !PlayableTypes.Contains(skill.Type) ? $"type {skill.Type} is not played yet"
                        : target is null ? $"no target for target type {skill.TargetType}"
                            : actor.HasEffect(Seal) && skill.Type is not (1 or 2 or (>= 10 and <= 13) or 20) ? "sealed"
                                : actor.Sp < skill.SpCost ? $"{actor.Sp} SP, needs {skill.SpCost}"
                                    : null;
        return (refusal, skill, target);
    }

    /// <summary>Takes the skill's SP cost from a fighter and tells its owner.</summary>
    /// <param name="actor">The fighter who casts.</param>
    /// <param name="skill">The skill cast.</param>
    private static void Spend(Fighter actor, SkillRecord skill)
    {
        actor.Sp -= skill.SpCost;
        SendSp(actor);
    }

    /// <summary>Tells whether a skill is the normal attack of a pet.</summary>
    /// <param name="actor">The fighter.</param>
    /// <param name="skillId">The skill id of the command.</param>
    /// <returns>True when the actor is a pet and the skill is a basic damage skill that is not one of the pet's own.</returns>
    private bool IsPetNormalAttack(Fighter actor, ushort skillId) =>
        actor.OwnedPet is not null && skills[skillId] is { Tree: 0, Type: 1 } && !Knows(actor, skillId);

    /// <summary>Tells whether a fighter knows a skill.</summary>
    /// <param name="actor">The player or pet.</param>
    /// <param name="skillId">The skill id.</param>
    /// <returns>True when the player learned it (or it is their role skill) or the pet has it in a slot with a level above 0.</returns>
    private static bool Knows(Fighter actor, ushort skillId) => actor.OwnedPet is { } pet
        ? actor.Npc is { } npc
            && Enumerable.Range(0, Math.Min(3, npc.SkillSlots.Count)).Any(i => npc.SkillSlots[i] == skillId && pet.SkillLevels.ElementAtOrDefault(i) > 0)
        : actor.Session?.ActiveCharacter is { } c
            && (c.Skill(skillId) is not null
                || (skillId == RoleData.RoleSkill(RoleData.RoleIndex(c.BodyType, c.Head)) && c.Skill(RoleData.NewbieSkill) is not null));

    /// <summary>Tells why a player may not use their role skill.</summary>
    /// <param name="actor">The fighter.</param>
    /// <param name="skillId">The skill id.</param>
    /// <returns>The refusal text, or null when the skill is not a role skill or the starting clothes are worn.</returns>
    private static string? RoleSkillRefusal(Fighter actor, ushort skillId)
    {
        if (actor.OwnedPet is not null || actor.Session?.ActiveCharacter is not { } c || !RoleData.IsRoleSkill(skillId))
            return null;
        var clothes = RoleData.RoleSkillClothes(RoleData.RoleIndex(c.BodyType, c.Head));
        var worn = c.Equipment.Find(e => e.Slot == 2)?.ItemId ?? 0;
        return clothes != 0 && worn == clothes ? null : $"the role skill needs the starting clothes {clothes} in slot 2 (wears {worn})";
    }

    /// <summary>Sends a fighter's SP to its owner as a player or pet attribute.</summary>
    /// <param name="f">The player or pet fighter.</param>
    private static void SendSp(Fighter f)
    {
        if (f.OwnedPet is { } pet)
            f.Session?.Send(PetMessages.SetAttribute(pet.Slot, Action8.AttrSp, f.Sp));
        else
            f.Session?.Send(InnMessages.SetAttribute(Action8.AttrSp, f.Sp));
    }

    /// <summary>The Skill.dat skill types the server plays.</summary>
    private static readonly FrozenSet<byte> PlayableTypes = [1, 2, 3, 4, 5, 6, 7, 14, 15, 16, 19, 22];

    /// <summary>The id of the status effect that seals a fighter's skills.</summary>
    private const byte Seal = 178;

    /// <summary>Finds the fighter a skill lands on by its target type.</summary>
    /// <param name="battle">The battle.</param>
    /// <param name="actor">The fighter who casts.</param>
    /// <param name="cast">The command that holds the chosen target cell.</param>
    /// <param name="skill">The skill.</param>
    /// <returns>The target, or null when there is none for the skill's target type.</returns>
    private static Fighter? TargetOf(Battle battle, Fighter actor, BattleCommand cast, SkillRecord skill)
    {
        var chosen = battle.At(cast.TargetRow, cast.TargetCol) is { IsAlive: true } alive ? alive : null;
        var ally = chosen is not null && chosen.Side == actor.Side ? chosen : null;
        return skill.TargetType switch
        {
            0 => chosen is not null && chosen.Side != actor.Side ? chosen : battle.EnemiesOf(actor).FirstOrDefault(), // enemy skill: chosen or first enemy
            1 => ally ?? actor, // own side: the chosen ally, else the caster
            2 => chosen ?? actor, // anyone: the chosen fighter, else the caster
            3 => actor, // self
            5 => ally is not null && !ReferenceEquals(ally, actor) ? ally : battle.OwnSide.FirstOrDefault(f => f.IsAlive && !ReferenceEquals(f, actor)), // ally
            _ => null, // any other target type: no target
        };
    }

    /// <summary>Plays a player's attempt to capture a monster as a pet.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="player">The player who captures.</param>
    /// <param name="capture">The command that holds the target cell.</param>
    private async Task PlayerCapturesAsync(GameSession session, Battle battle, Fighter player, BattleCommand capture)
    {
        if (battle.At(capture.TargetRow, capture.TargetCol) is not { Kind: 7, IsAlive: true, Npc: { } npc } target // monster
            || player.Session?.ActiveCharacter is not { } character)
        {
            logger.LogWarning(
                "[{Session}] {Player} captures at ({Row},{Col}): no monster there",
                session.Id,
                player.Name,
                capture.TargetRow,
                capture.TargetCol);
            return;
        }
        var o = petOptions.Value;
        var net = o.MonsterNetLevels > 0 ? Pets.WornMonsterNet(character, id => items[id]) : null;
        var refusal = Pets.CaptureRefusalFor(
            character,
            npc,
            target.Hp,
            target.MaxHp,
            [.. battle.Caught.Where(c => ReferenceEquals(c.Catcher, player)).Select(c => c.Npc.Id)],
            o.CaptureHpBelowPercent, o.CaptureLevelsAbove + (net is null ? 0 : o.MonsterNetLevels));
        var caught = refusal is null
            && GameRandom.Shared.NextDouble() * 100 < BattleFormulas.CaptureChance(target.Hp, target.MaxHp, character.Level, npc.Level, o);
        battle.SendAttackArea(player);
        battle.Send(client => BattleMessages.Action(player.Row, player.Col, SkillTable.Capture,
            [new ActionTarget(target.Row, target.Col, Hit: caught, Guard: caught ? (byte)0 : (byte)4, [])], client));
        if (caught)
        {
            target.Captured = true;
            target.Hp = 0;
            target.Statuses.Clear();
            battle.Caught.Add((player, npc));
            if (net is not null && items[net.ItemId] is { } netItem)
            {
                var usedUp = Pets.SpendMonsterNet(net, netItem);
                session.Send(ItemMessages.EquipWear(net.Slot, net.Wear));
                if (usedUp)
                    session.Send(ItemMessages.EquipBroken(0, net.ItemId));
                logger.LogInformation("[{Session}] {Player}'s Monster Net: wear {Wear}", session.Id, player.Name, net.Wear);
            }
        }
        logger.LogInformation("[{Session}] {Player} tries to capture {Monster} ({Id}): {Result}", session.Id, player.Name, npc.Name, npc.Id,
            caught ? "caught" : refusal ?? "it got away");
        await ActionPause(SkillTable.Capture, player, target, numbers: false).ConfigureAwait(false);
    }

    /// <summary>Plays the use of an HP or SP restoring item on a fighter of the user's side.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="user">The fighter who uses the item.</param>
    /// <param name="use">The command that holds the item id, the bag slot and the target cell.</param>
    private async Task UsesItemAsync(GameSession session, Battle battle, Fighter user, BattleCommand use)
    {
        if (user.Session?.ActiveCharacter is not { } character)
            return;
        var player = battle.At(use.TargetRow, use.TargetCol) is { IsAlive: true } ally && ally.Side == user.Side ? ally : user;
        var stack = character.Bag.Find(b => b.Slot == use.Extra && b.ItemId == use.SkillOrItem);
        var item = stack is null ? null : items[stack.ItemId];
        if (stack is null || item is null || item.Restores.Count == 0)
        {
            logger.LogWarning("[{Session}] {Player} cannot use item {Item} from bag slot {Slot} in battle", session.Id, user.Name,
                use.SkillOrItem, use.Extra);
            return;
        }

        List<FighterValue> values = [];
        foreach (var (attr, amount) in item.Restores)
        {
            var gained = attr == BattleMessages.AttrHp
                ? Math.Min(amount, player.MaxHp - player.Hp)
                : Math.Min(amount, player.MaxSp - player.Sp);
            gained = Math.Max(0, gained);
            if (attr == BattleMessages.AttrHp)
                player.Hp += gained;
            else
                player.Sp += gained;
            values.Add(new FighterValue(attr, gained, Negative: false));
        }
        stack.Count--;
        if (stack.Count == 0)
            character.Bag.Remove(stack);
        user.Session.Send(Protocol.Messages.Items.ItemMessages.BagSlotDecrease(use.Extra, 1));
        battle.SendAttackArea(user);
        battle.Send(client => BattleMessages.Action(user.Row, user.Col, SkillTable.ItemUsage,
            [new ActionTarget(player.Row, player.Col, Hit: true, Guard: 0, values)], client));
        logger.LogInformation("[{Session}] {User} uses {Item} ({ItemName}) on {Target}: HP {Hp}/{MaxHp}, SP {Sp}/{MaxSp}", session.Id, user.Name,
            item.Id, item.Name, player.Name, player.Hp, player.MaxHp, player.Sp, player.MaxSp);
        await ActionPause(SkillTable.ItemUsage, user, player).ConfigureAwait(false);
    }
}
