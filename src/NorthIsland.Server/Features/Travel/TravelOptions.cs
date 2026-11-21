namespace NorthIsland.Server.Features.Travel;

/// <summary>The settings of travel in the world: the maps teleports may not leave or reach, and how far one walk may go.</summary>
public sealed class TravelOptions
{
    /// <summary>The name of the section under the server section that holds these settings.</summary>
    public const string Section = "Travel";

    /// <summary>
    /// The maps no teleport may leave or reach, on top of those the rules always bar (scene flag 4, the sea as a destination, houses,
    /// instance copies, event arenas and the castle maps). These are the maps the client bars by id and that lack scene flag 4, plus the
    /// flagged ones it names too.
    /// </summary>
    public ushort[] NoTeleportMaps { get; set; } = [10004, // Prison
        10009, // Maze
        10085, // Wedding Hall
        11073, // 2F, Activity Centre
        12064, // 2F, the Capitol
        12766, // Altar
        13224]; // Maze in Cave

    /// <summary>
    /// The farthest, in pixels on either axis, a walk may end from the player's last accepted position. The client walks to a path end at
    /// most 40 cells of 20 pixels from where it stands on each axis (CellDistances::kReach), and it may stand up to that far from its last
    /// sent end while it walks there, so a fair walk ends within twice 820 pixels (40 cells plus the cell it stands in). 0 turns the check off.
    /// </summary>
    public int WalkReachPixels { get; set; } = 1640;
}
