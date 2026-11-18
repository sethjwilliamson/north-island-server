using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Skills;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>
    /// Rolls whether the fighter hit by a close-range physical attack strikes back and plays the counter: the defender's normal attack on
    /// the attacker, sent as its own 50/1 after the attack (the client has no counter flag; see <see cref="CounterRules"/>).
    /// </summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    /// <param name="attacker">The fighter who attacked.</param>
    /// <param name="defender">The single fighter the attack was aimed at.</param>
    /// <param name="skillId">The skill id the attack was sent with.</param>
    /// <param name="skill">The skill used, or null for a normal attack.</param>
    private async Task TryCounterAsync(
        GameSession session,
        Battle battle,
        Fighter attacker,
        Fighter defender,
        ushort skillId,
        SkillRecord? skill)
    {
        if (!CounterRules.CanCounter(defender, attacker) || !CounterRules.IsCounterable(skillId, skill, skills.Motions)
            || GameRandom.Shared.NextDouble() * 100 >= CounterRules.ChancePercent(attacker, defender))
            return;
        var counterId = defender.IsHuman ? defender.NormalAttackId : SkillTable.HandAttack;
        int damage;
        bool critical, missed = false;
        if (defender.IsHuman)
            (damage, critical, missed) = HitTarget(defender, attacker, null, combo: false);
        else
            (damage, critical) = MonsterBlow(defender, attacker, null, combo: false);
        battle.SendAttackArea(defender);
        battle.Send(client => BattleMessages.Action(defender.Row, defender.Col, counterId,
            [new ActionTarget(attacker.Row, attacker.Col, Hit: !missed, Guard: 0, BlowValues(damage, critical, missed, null))], client));
        logger.LogInformation("[{Session}] {Defender} counters {Attacker} for {Damage}{Critical}{Missed} ({Hp}/{Max} HP left)", session.Id,
            defender.Name, attacker.Name, damage, critical ? " (critical)" : "", missed ? " (missed)" : "", attacker.Hp, attacker.MaxHp);
        await ActionPause(counterId, defender, attacker).ConfigureAwait(false);
        if (attacker.IsAlive)
            return;
        if (defender.IsHuman)
            await KnockOffAsync(battle, attacker).ConfigureAwait(false);
        else
        {
            attacker.Statuses.Clear();
            logger.LogInformation("[{Session}] {What} {Name} is down", session.Id, attacker.OwnedPet is null ? "player" : "pet", attacker.Name);
        }
    }
}
