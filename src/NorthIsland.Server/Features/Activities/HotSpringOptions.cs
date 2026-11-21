namespace NorthIsland.Server.Features.Activities;

/// <summary>Configuration of the hot springs.</summary>
public sealed class HotSpringOptions
{
    /// <summary>Switches the hot springs on or off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes in the water per bonus step.</summary>
    public double MinutesPerPoint { get; set; } = 3;

    /// <summary>The most steps one visit gives.</summary>
    public int MaxPerVisit { get; set; } = 10;

    /// <summary>The bonus points added to the character's maximum HP and SP per step.</summary>
    public int PointsPerStep { get; set; } = 1;

    /// <summary>
    /// The most bonus points a character can collect at the hot springs in all; the bonus is permanent, so without a cap it grows for as long
    /// as the player bathes. 100 points is ten full visits at the default settings (half an hour each). A character already above it keeps
    /// what it has and gains no more.
    /// </summary>
    public int MaxTotal { get; set; } = 100;

    /// <summary>The map ids where standing in the water counts.</summary>
    public ushort[] Maps { get; set; } = [12139, // Hotspring House
        11181, // Hotspring Cave
        12284, // Shayii Peak
        11140, // KaMa Peak
        12462, // Korean Hotspring
        11089, // Hilltop Hot Spring
        12263, // Bathhouse
        12366, // 2F, Ice House
        11045]; // Maka Peak
}
