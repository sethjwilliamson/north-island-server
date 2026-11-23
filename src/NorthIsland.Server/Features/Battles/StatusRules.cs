using NorthIsland.GameData.Skills;

namespace NorthIsland.Server.Features.Battles;

/// <summary>Which slot and icon each Skill.dat status effect uses, and how a status changes a fighter's turn and stats.</summary>
public static class StatusRules
{
    /// <summary>Slot attribute of the control statuses (frozen, asleep and the like), effects 1-50, and of mess.</summary>
    public const byte Control = 221;

    /// <summary>Slot attribute of the shield statuses, effects 101-170.</summary>
    public const byte Shield = 222;

    /// <summary>Slot attribute of the damage amplifier debuffs, Shrink and Cuss (client status place 3).</summary>
    public const byte Debuff = 223;

    /// <summary>Slot attribute of the stat debuffs and of the other debuffs of effects 171-240 (client status place 8).</summary>
    public const byte StatDebuff = 236;

    /// <summary>Slot attribute of poison (client status place 4).</summary>
    public const byte PoisonDebuff = 224;

    /// <summary>Effect byte of freeze.</summary>
    public const byte Freeze = 1;

    /// <summary>Effect byte of sleep.</summary>
    public const byte Sleep = 6;

    /// <summary>Effect byte of regeneration.</summary>
    public const byte Regeneration = 58;

    /// <summary>Effect byte of poison.</summary>
    public const byte Poison = 171;

    /// <summary>Effect byte of mess, which makes the fighter lose its turn.</summary>
    public const byte Mess = 173;

    /// <summary>Effect byte of slow, which halves agility.</summary>
    public const byte Slow = 176;

    /// <summary>Effect byte of defence down.</summary>
    public const byte DefenceDown = 177;

    /// <summary>Effect byte of seal, which blocks most skills.</summary>
    public const byte Seal = 178;

    /// <summary>Effect byte of shrink.</summary>
    public const byte Shrink = 172;

    /// <summary>Effect byte of cuss.</summary>
    public const byte Cuss = 179;

    /// <summary>Effect byte of speed up, which raises agility.</summary>
    public const byte Speed = 51;

    /// <summary>Effect byte of defence up.</summary>
    public const byte DefenceUp = 52;

    /// <summary>Effect byte of the remover that takes off every control, shield and debuff status.</summary>
    public const byte PurgeAll = 80;

    /// <summary>Effect byte of Stone Wall (Skill.dat 11055, described as "A wall against physical atk"): it voids physical blows.</summary>
    public const byte StoneWall = 101;

    /// <summary>Picks the status slot attribute a skill effect goes into on a target.</summary>
    /// <param name="effect">The Skill.dat effect byte.</param>
    /// <param name="target">The fighter receiving the status; its current statuses decide between the two attack buff slots.</param>
    /// <returns>The slot attribute, or 0 when the effect has no slot.</returns>
    public static byte AttrFor(byte effect, Fighter target) => effect switch
    {
        >= 1 and <= 50 or Mess => Control, // effects 1-50 and mess: control slot
        >= 101 and <= 170 => Shield, // effects 101-170: shield slot
        Shrink or Cuss => Debuff, // damage amplifiers: debuff slot
        Poison => PoisonDebuff, // poison: poison slot
        >= 171 and <= 240 => StatDebuff, // Slowdown, Defence Down, Seal and the other debuffs: stat debuff slot
        59 => 225, // effect 59: slot 225
        52 => 226, // 52 DefenceUp
        >= 60 and <= 63 => target.Statuses.Exists(s => s.Attr == 227 && s.Effect != effect) ? (byte)228 : (byte)227, // effects 60-63: slot 227 or 228
        51 => 229, // 51 Speed
        53 or 55 => 230, // effects 53 and 55: slot 230
        58 => 232, // 58 Regeneration
        64 => 233, // effect 64: slot 233
        _ => 0, // any other effect: no slot
    };

    /// <summary>Maps a status slot attribute to the icon the client shows.</summary>
    /// <param name="attr">The status slot attribute.</param>
    /// <returns>The icon kind and sub index, (0, 0) when the slot has no icon.</returns>
    public static (byte Kind, byte SubIndex) IconOf(byte attr) => attr switch
    {
        221 => (1, 0), // 221 Control
        222 => (2, 0), // 222 Shield
        223 => (3, 0), // 223 Debuff
        224 => (4, 0), // slot 224: icon kind 4
        236 => (8, 0), // 236 StatDebuff
        225 => (5, 1), // slot 225: icon kind 5, sub index 1
        226 => (5, 2), // slot 226: icon kind 5, sub index 2
        227 => (5, 3), // slot 227: icon kind 5, sub index 3
        228 => (5, 4), // slot 228: icon kind 5, sub index 4
        229 => (5, 5), // slot 229: icon kind 5, sub index 5
        232 => (5, 6), // slot 232: icon kind 5, sub index 6
        230 => (5, 7), // slot 230: icon kind 5, sub index 7
        233 => (5, 8), // slot 233: icon kind 5, sub index 8
        _ => (0, 0), // any other slot: no icon
    };

    /// <summary>Whether a remover skill takes a status off.</summary>
    /// <param name="status">The status on the fighter.</param>
    /// <param name="effect">The effect byte of the remover skill.</param>
    /// <returns>True when the status is removed.</returns>
    public static bool IsRemovedBy(FighterStatus status, byte effect) => effect == PurgeAll
        ? status.Attr is Control or Shield or Debuff or StatDebuff or PoisonDebuff
        : status.Effect == effect;

    /// <summary>Whether a status is a debuff (the control and debuff slots).</summary>
    /// <param name="status">The status to test.</param>
    /// <returns>True for the control and debuff slots.</returns>
    public static bool IsDebuff(FighterStatus status) => IsDebuffSlot(status.Attr);

    /// <summary>Whether a slot attribute is the control slot or one of the debuff slots.</summary>
    /// <param name="attr">The slot attribute.</param>
    /// <returns>True for the control and debuff slots.</returns>
    public static bool IsDebuffSlot(byte attr) => attr is Control or Debuff or StatDebuff or PoisonDebuff;

    /// <summary>
    /// Tells whether a control or debuff status put on an enemy misses because of what the enemy already carries. With
    /// <see cref="DebuffStacking.PerKind"/> each slot holds one kind: a status misses when its slot is taken, except that a control other
    /// than sleep takes the place of a sleep, and the same debuff (not control) cast again lasts longer. With
    /// <see cref="DebuffStacking.OnePerEnemy"/> it misses when the enemy carries any control or debuff.
    /// </summary>
    /// <param name="effect">The Skill.dat effect byte of the new status.</param>
    /// <param name="attr">The slot attribute the new status would go into.</param>
    /// <param name="caster">The fighter putting the status on.</param>
    /// <param name="target">The fighter receiving it.</param>
    /// <param name="stacking">The stacking rule from the battle options.</param>
    /// <returns>True when the status misses.</returns>
    public static bool BlockedByDebuff(byte effect, byte attr, Fighter caster, Fighter target, DebuffStacking stacking)
    {
        if (!IsDebuffSlot(attr) || caster.Side == target.Side)
            return false;
        if (stacking == DebuffStacking.OnePerEnemy)
            return target.Statuses.Exists(IsDebuff);
        var current = target.Statuses.Find(s => s.Attr == attr);
        if (current is null)
            return false;
        return attr == Control
            ? !(current.Effect == Sleep && effect != Sleep) // a control wakes a sleeping enemy and replaces the sleep
            : current.Effect != effect; // the same debuff refreshes, another of its kind misses
    }

    /// <summary>
    /// Whether a status put on an enemy has to beat the target's resistance to land: the controls (freeze, sleep, stone, mess and the
    /// like) and Seal. Every other debuff (Shrink, Cuss, Slowdown, Defence Down, poison) lands whenever its slot lets it.
    /// </summary>
    /// <param name="effect">The Skill.dat effect byte.</param>
    /// <param name="attr">The slot attribute the status goes into.</param>
    /// <returns>True for the control slot and for Seal.</returns>
    public static bool IsResisted(byte effect, byte attr) => attr == Control || effect == Seal;

    /// <summary>Whether the client takes a removed status off by itself when it plays the remover skill, so no removal needs to be sent.</summary>
    /// <param name="status">The removed status.</param>
    /// <returns>False for the slots and effects the client does not cure on its own.</returns>
    public static bool ClientCuresItself(FighterStatus status) =>
        status.Attr is not (StatDebuff or PoisonDebuff or 227 or 228) && status.Effect != Mess; // the client looks for mess in place 3 only

    /// <summary>Whether a skill effect counts as a debuff for the damage burst.</summary>
    /// <param name="effect">The Skill.dat effect byte.</param>
    /// <returns>True for the effects that go into the control and debuff slots.</returns>
    public static bool IsDebuffEffect(byte effect) => effect is >= 1 and <= 50 or >= 171 and <= 240;

    /// <summary>Whether a fighter loses its turn because of a status.</summary>
    /// <param name="f">The fighter.</param>
    /// <returns>True when it has a control status (mess included).</returns>
    public static bool CannotAct(Fighter f) => f.Statuses.Exists(s => s.Attr == Control);

    /// <summary>The factor a fighter's agility is multiplied by because of speed up and slow.</summary>
    /// <param name="f">The fighter.</param>
    /// <returns>The factor, 1 when neither is on.</returns>
    public static double AgilityScale(Fighter f) =>
        (f.HasEffect(Speed) ? 1.5 : 1) * (f.HasEffect(Slow) ? 0.5 : 1);

    /// <summary>The factor a fighter's attack is multiplied by because of attack buffs.</summary>
    /// <param name="f">The fighter.</param>
    /// <param name="hotFireCounted">True when Hot Fire Attack is applied elsewhere and must not count here.</param>
    /// <returns>1.25 with an attack buff, otherwise 1.</returns>
    public static double AttackScale(Fighter f, bool hotFireCounted = false) =>
        f.Statuses.Exists(s => (s.Effect is 59 or (>= 60 and <= 63)) && !(hotFireCounted && s.SkillId == SkillTable.HotFireAttack)) ? 1.25 : 1;

    /// <summary>Whether Hot Fire Attack is on a fighter.</summary>
    /// <param name="f">The fighter.</param>
    /// <returns>True when a status from that skill is present.</returns>
    public static bool IsHotFire(Fighter f) => f.Statuses.Exists(s => s.SkillId == SkillTable.HotFireAttack);

    /// <summary>Whether Stone Wall voids a blow: a hand attack or a physical (type 1) skill on a fighter under effect 101.</summary>
    /// <param name="target">The fighter hit.</param>
    /// <param name="skill">The skill used, or null for a hand attack.</param>
    /// <returns>True when the blow does nothing.</returns>
    public static bool BlocksPhysical(Fighter target, SkillRecord? skill) =>
        skill is null or { Type: 1 } && target.HasEffect(StoneWall);

    /// <summary>The factor a fighter's defence is multiplied by because of shield, defence up and defence down.</summary>
    /// <param name="f">The fighter.</param>
    /// <returns>The factor, 1 when none is on.</returns>
    public static double DefenceScale(Fighter f) =>
        (f.Statuses.Exists(s => s.Attr == Shield && s.Effect != StoneWall) ? 0.5 : 1) * (f.HasEffect(DefenceUp) ? 0.75 : 1) * (f.HasEffect(DefenceDown) ? 1.25 : 1);
}
