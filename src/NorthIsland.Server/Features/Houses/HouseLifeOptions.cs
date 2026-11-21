namespace NorthIsland.Server.Features.Houses;

/// <summary>The numbers for life in the house: mailbox size, tent limits, the Spa Bun, baths, training and the Magic Sandglass.</summary>
public sealed class HouseLifeOptions
{
    /// <summary>
    /// The most letters a character's Mailbox keeps, as a count of letters; 0 or less uses <see cref="FurMailHandlers.DefaultCapacity"/> (100
    /// letters), since a box without a limit could be filled without end.
    /// </summary>
    public int FurMailCapacity { get; set; }

    /// <summary>The most tents on one map; 0 means no limit.</summary>
    public int TentsPerMap { get; set; }

    /// <summary>The most tents on the server; 0 means no limit.</summary>
    public int TentsPerServer { get; set; }

    /// <summary>
    /// The farthest a player may stand from a tent, in pixels across, to go in by clicking it. The client sends the click from anywhere on its
    /// 800 by 600 pixel game screen without walking up, so the default is the screen width; 0 means no limit.
    /// </summary>
    public int TentReachX { get; set; } = 800;

    /// <summary>The farthest a player may stand from a tent, in pixels up or down, to go in by clicking it: by default the screen height; 0 means no limit.</summary>
    public int TentReachY { get; set; } = 600;

    /// <summary>Minutes of bath boost one Spa Bun gives.</summary>
    public int SpaBunMinutes { get; set; } = 30;

    /// <summary>The most Spa Buns whose time can be stored.</summary>
    public int SpaBunMax { get; set; } = 10;

    /// <summary>The factor by which a bath restores more while the Spa Bun runs.</summary>
    public double SpaFactor { get; set; } = 2;

    /// <summary>Seconds between two bath steps.</summary>
    public int BathSeconds { get; set; } = 5;

    /// <summary>HP restored per bath step, in percent of the maximum.</summary>
    public double BathHpPercent { get; set; } = 5;

    /// <summary>SP restored per bath step, in percent of the maximum.</summary>
    public double BathSpPercent { get; set; } = 5;

    /// <summary>The factor for the HP recovery when a Screen stands in the house.</summary>
    public int ScreenHpFactor { get; set; } = 2;

    /// <summary>The factor for the SP recovery when a Tiger-fur Carpet lies in the house.</summary>
    public int CarpetSpFactor { get; set; } = 2;

    /// <summary>Seconds between two training steps.</summary>
    public int TrainSeconds { get; set; } = 30;

    /// <summary>Percent of the experience a level needs that one training step gives; it is also the progress added per step.</summary>
    public int TrainPercent { get; set; } = 2;

    /// <summary>Percent of the normal manufacture time that remains when a Magic Sandglass is used.</summary>
    public int SandglassPercent { get; set; } = 10;

    /// <summary>How many times a Magic Sandglass can be used.</summary>
    public int SandglassUses { get; set; } = 10;
}
