using NorthIsland.GameData.Skills;
using NorthIsland.Server.Features.Progression;

namespace NorthIsland.Server.Features.Battles;

/// <summary>
/// When the fighter hit by a close-range physical attack strikes back (a counterattack).
/// The client has no counter flag, packet or skill: a counter is an ordinary 50/1 action of the defender's normal attack on the attacker,
/// sent after the attack it answers (each fighter has one action object per slot, RevAttackSkill / StartFightHumAttack, DZ_Fight.pas:4050-4130, 4353-4357).
/// </summary>
public static class CounterRules
{
    /// <summary>
    /// The chance in percent that a fighter counters a close-range physical attack.
    /// GUESS: no data file or client code holds a counter rate. Forum players call counters rare ("60 STR ... maybe around 2-3%",
    /// wlo-research corpus), so this is a flat small chance.
    /// </summary>
    public const double CounterChancePercent = 3;

    /// <summary>
    /// The factor on the counter chance when the attacker is a Killer. The client's career text says a Killer lowers the
    /// "Target's counterattack rate" (MZ_Menu.pas:4337); how much it lowers it is a GUESS.
    /// </summary>
    public const double KillerCounterScale = 0.5;

    /// <summary>
    /// How far (in px, along the motion's path toward the target) an attack motion must walk to be close range. Measured from
    /// SkillData.MBTM: the ranged attacks stay below about 250 px (Bow Attack 196, Gun Attack 197, Wind Attack 173, Wind Bead 176), the
    /// close ones reach 460 px and more (Hand Attack 509, Sword Attack 511, Air Attack 583). Forum guides say bows and the ranged
    /// ("Distance") skills cannot be countered.
    /// </summary>
    public const int MeleeReachPx = 400;

    /// <summary>Whether an attack can be countered: a physical attack whose motion walks up to the target.</summary>
    /// <param name="skillId">The skill id sent in the action (a normal attack id for a normal attack).</param>
    /// <param name="skill">The Skill.dat record of the skill, or null for a normal attack.</param>
    /// <param name="motions">The motion table, or null when it is not loaded (no attack counts as close range then).</param>
    /// <returns>True for a close-range physical attack.</returns>
    public static bool IsCounterable(ushort skillId, SkillRecord? skill, SkillMotionTable? motions)
    {
        if (skill is not null && skill.Type != 1) // only physical skills (Skill.dat type 1); spells (type 2) are not countered
            return false;
        if (motions?[skillId] is not { Points.Count: > 0 } motion)
            return false;
        return motion.Points.Max(p => p.X) >= MeleeReachPx;
    }

    /// <summary>The chance in percent that <paramref name="defender"/> counters an attack of <paramref name="attacker"/>.</summary>
    /// <param name="attacker">The fighter who attacked.</param>
    /// <param name="defender">The fighter who was attacked.</param>
    /// <returns>The chance.</returns>
    public static double ChancePercent(Fighter attacker, Fighter defender) =>
        CounterChancePercent * (attacker.Kind == 2 && attacker.Job == Career.Killer ? KillerCounterScale : 1); // 2 player

    /// <summary>Whether a fighter is able to strike back now.</summary>
    /// <param name="defender">The fighter who was attacked.</param>
    /// <param name="attacker">The fighter who attacked.</param>
    /// <returns>True when both are still standing on opposite sides and the defender can act.</returns>
    public static bool CanCounter(Fighter defender, Fighter attacker) =>
        defender.IsAlive && !defender.Left && !defender.Captured && attacker.IsAlive && !attacker.Left
        && defender.Side != attacker.Side && !StatusRules.CannotAct(defender)
        && (!defender.IsHuman || defender.NormalAttackId != 0);
}
