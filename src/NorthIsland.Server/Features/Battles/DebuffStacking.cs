namespace NorthIsland.Server.Features.Battles;

/// <summary>Which control and debuff statuses can be on an enemy at the same time.</summary>
public enum DebuffStacking
{
    /// <summary>
    /// One status of each kind: a control (freeze, sleep, stone, mess and the like), a damage amplifier (Shrink, Cuss), a stat debuff
    /// (Slowdown, Defence Down, Seal) and poison. A status of a kind the enemy already carries misses, except that a control wakes a
    /// sleeping enemy and takes the place of the sleep, and the same debuff cast again lasts longer.
    /// </summary>
    PerKind,

    /// <summary>A control or debuff status misses on an enemy that carries any of them until that one is gone.</summary>
    OnePerEnemy,
}
