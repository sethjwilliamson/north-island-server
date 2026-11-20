using Microsoft.Extensions.Logging;
using NorthIsland.GameData.Characters;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>HP the Training Island gives each living player and pet after every round.</summary>
    /// <remarks>IGG news aid=2131 and item guide aid=2233 (2009): "the system will restore your HP (+100) and SP (+50) automatically after each round".</remarks>
    private const int TrainingIslandRoundHp = 100;

    /// <summary>SP the Training Island gives each living player and pet after every round (same source as <see cref="TrainingIslandRoundHp"/>).</summary>
    private const int TrainingIslandRoundSp = 50;

    /// <summary>
    /// The skill the island's heal is sent as: Newbie's Stunt, whose motion is the standing pose alone with no effect and no sound, so
    /// nothing is seen of the action but the numbers.
    /// </summary>
    private const ushort TrainingIslandHealSkill = RoleData.NewbieSkill;

    /// <summary>The Skill.dat area code of the whole side: one action reaches every player and pet.</summary>
    private const byte TrainingIslandHealArea = 8;

    /// <summary>The guard value that makes the client apply an action's values at once and show each as a rising number, with no reaction.</summary>
    private const byte ValuesAtOnce = 4;

    /// <summary>
    /// On the Training Island, restores HP and SP to every living player and pet after a round, shown as the numbers rising over each
    /// fighter, all at once, with nobody casting anything.
    /// </summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    private async Task TrainingIslandHealAsync(GameSession session, Battle battle)
    {
        if (battle.IsPvp || !TrainingIslandHandlers.IsIslandMap(battle.MapId))
            return;
        List<ActionTarget> targets = [];
        List<Fighter> healed = [];
        var firstSp = 0; // SP given to the first healed fighter
        // Only a player in training, and that player's pet, is healed; someone on the island without a running voucher is not.
        foreach (var f in battle.All
                     .Where(f => f.IsHuman && f.IsAlive && !f.Left && f.Session?.ActiveCharacter is { } c && TrainingIslandHandlers.IsTraining(c))
                     .ToList())
        {
            var hp = Math.Max(0, Math.Min(TrainingIslandRoundHp, f.MaxHp - f.Hp));
            var sp = Math.Max(0, Math.Min(TrainingIslandRoundSp, f.MaxSp - f.Sp));
            if (hp == 0 && sp == 0)
                continue;
            f.Hp += hp;
            f.Sp += sp;
            List<FighterValue> values = [];
            if (hp > 0)
                values.Add(new FighterValue(BattleMessages.AttrHp, hp, Negative: false));
            if (sp > 0)
            {
                values.Add(new FighterValue(BattleMessages.AttrSp, sp, Negative: false));
                SendSp(f);
            }
            if (healed.Count == 0)
                firstSp = sp;
            targets.Add(new ActionTarget(f.Row, f.Col, Hit: true, Guard: ValuesAtOnce, values));
            healed.Add(f);
            logger.LogInformation("[{Session}] Training Island: {Fighter} +{Hp} HP, +{Sp} SP ({NewHp}/{MaxHp} HP, {NewSp}/{MaxSp} SP)", session.Id,
                f.Name, hp, sp, f.Hp, f.MaxHp, f.Sp, f.MaxSp);
        }
        if (healed.Count == 0)
            return;
        // One action of the first healed fighter on itself that reaches its whole side: the client shows the numbers of an action that
        // plays alone, so the fighters do not each get an action of their own.
        var first = healed[0];
        // The client takes the skill's SP cost off the fighter an action is of. The cost is put on top of the fighter's SP first, so its SP
        // does not drop while the action plays, and its true SP is sent again once the action is over.
        var cost = skills[TrainingIslandHealSkill]?.SpCost ?? 0;
        if (cost > 0)
            battle.Send(BattleMessages.FighterAttributes([(first.Row, first.Col, BattleMessages.AttrSp, first.Sp - firstSp + cost)]));
        battle.SendAttackArea(first, TrainingIslandHealArea);
        battle.Send(client => BattleMessages.Actions([new ActionRecord(first.Row, first.Col, TrainingIslandHealSkill, targets)], client));
        await ActionPause(TrainingIslandHealSkill, first, first, numbers: false).ConfigureAwait(false);
        if (cost > 0)
            battle.Send(BattleMessages.FighterAttributes([(first.Row, first.Col, BattleMessages.AttrSp, first.Sp)]));
    }
}
