namespace NorthIsland.Server.Features.Battles;

/// <summary>Which formula decides the experience a monster pays in battle.</summary>
public enum ExpMode
{
    /// <summary>A value from the monster's level plus or minus a term per level of gap, as the original server pays.</summary>
    Classic,

    /// <summary>A share of the experience the fighter needs for its next level, so the kills per level follow a set curve.</summary>
    LevelShare,
}
