using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Battle;

namespace NorthIsland.Server.Features.Battles;

public sealed partial class BattleHandlers
{
    /// <summary>HP the Training Island gives each living player and pet after every round.</summary>
    /// <remarks>IGG news aid=2131 and item guide aid=2233 (2009): "the system will restore your HP (+100) and SP (+50) automatically after each round".</remarks>
    private const int TrainingIslandRoundHp = 100;

    /// <summary>SP the Training Island gives each living player and pet after every round (same source as <see cref="TrainingIslandRoundHp"/>).</summary>
    private const int TrainingIslandRoundSp = 50;

    /// <summary>The skill whose effect plays the island's heal: Recovery, which shows the same effect text as the island's heal did.</summary>
    private const ushort TrainingIslandHealSkill = 11043; // Recovery

    /// <summary>
    /// On the Training Island, restores HP and SP to every living player and pet after a round, shown as each fighter healing itself
    /// with the Recovery effect, all at once.
    /// </summary>
    /// <param name="session">The session used for logging.</param>
    /// <param name="battle">The battle.</param>
    private async Task TrainingIslandHealAsync(GameSession session, Battle battle)
    {
        if (battle.IsPvp || !TrainingIslandHandlers.IsIslandMap(battle.MapId))
            return;
        List<ActionRecord> records = [];
        List<Fighter> healed = [];
        foreach (var f in battle.All.Where(f => f.IsHuman && f.IsAlive && !f.Left && f.Session is not null).ToList())
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
            records.Add(new ActionRecord(f.Row, f.Col, TrainingIslandHealSkill, [new ActionTarget(f.Row, f.Col, Hit: true, Guard: 0, values)]));
            healed.Add(f);
            logger.LogInformation("[{Session}] Training Island: {Fighter} +{Hp} HP, +{Sp} SP ({NewHp}/{MaxHp} HP, {NewSp}/{MaxSp} SP)", session.Id,
                f.Name, hp, sp, f.Hp, f.MaxHp, f.Sp, f.MaxSp);
        }
        if (records.Count == 0)
            return;
        foreach (var f in healed)
            battle.SendAttackArea(f);
        battle.Send(client => BattleMessages.Actions(records, client));
        await ActionPause([.. healed.Select(f => (TrainingIslandHealSkill, f, f))]).ConfigureAwait(false);
    }
}
