namespace NorthIsland.Server.Features.PetCare;

/// <summary>Settings of pets: defeat penalties, growth, running away, capture and the reborn wish.</summary>
public sealed class PetOptions
{
    /// <summary>Settings of the reborn wish.</summary>
    public RebornWishOptions RebornWish { get; set; } = new();

    /// <summary>The amity a pet loses when it falls in a battle.</summary>
    public int PetDefeatAmityLoss { get; set; } = 1;

    /// <summary>The percent of the current level's exp a pet loses when it falls; 0 means it loses amity only.</summary>
    public int PetDefeatExpLossPercent { get; set; } = 10;

    /// <summary>The amity a pet gains for each level it gains.</summary>
    public int PetAmityPerLevel { get; set; } = 1;

    /// <summary>The amity below which a pet that fell can run away; 0 means no pet runs away.</summary>
    public int PetRunAwayAmity { get; set; } = 20;

    /// <summary>The chance in percent (0..100) that a pet below <see cref="PetRunAwayAmity"/> runs away after a fall.</summary>
    public int PetRunAwayChancePercent { get; set; } = 50;

    /// <summary>The ability points a pet gains for each level, each given to a random ability below the cap of its potential.</summary>
    public int PetStatsPerLevel { get; set; } = 1;

    /// <summary>The amity a captured monster joins with; 0 means the usual amity of a new pet.</summary>
    public int CapturedPetAmity { get; set; }

    /// <summary>The base chance in percent of a capture, before the missing-HP part and the level factor.</summary>
    public double CaptureBasePercent { get; set; }

    /// <summary>The chance in percent a capture gains when all of the monster's HP is missing, scaled by the share that is missing.</summary>
    public double CaptureHpPercent { get; set; }

    /// <summary>How much the capture level factor changes for each level the catcher is above the monster.</summary>
    public double CaptureLevelStep { get; set; }

    /// <summary>The lowest capture level factor, which is also the factor for a monster above the catcher's level.</summary>
    public double CaptureLevelFactorMin { get; set; }

    /// <summary>The highest capture level factor.</summary>
    public double CaptureLevelFactorMax { get; set; }

    /// <summary>How many levels above the catcher a monster may be and still be captured.</summary>
    public int CaptureLevelsAbove { get; set; }

    /// <summary>The levels added to <see cref="CaptureLevelsAbove"/> while the catcher wears a Monster Net that is not used up.</summary>
    public int MonsterNetLevels { get; set; }

    /// <summary>A monster whose HP is this percent of its maximum or more cannot be captured; 0 turns the rule off.</summary>
    public int CaptureHpBelowPercent { get; set; } = 50;
}
