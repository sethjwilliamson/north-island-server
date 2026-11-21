using System.Buffers.Binary;
using NorthIsland.GameData.Events;

namespace NorthIsland.Server.Features.Events;

/// <summary>
/// How far from an NPC or a door the player may be when their client starts its event: a box of map pixels around the places the object can
/// be, grown by the reach of the trigger.
/// </summary>
/// <param name="Left">The left edge of the object's places, px.</param>
/// <param name="Top">The top edge of the object's places, px.</param>
/// <param name="Right">The right edge of the object's places, px.</param>
/// <param name="Bottom">The bottom edge of the object's places, px.</param>
public readonly record struct TriggerReach(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    /// <summary>The farthest distance on each axis from which the client clicks an NPC, px (EventRunner.cpp and WorldScene.cpp kNear).</summary>
    public const int NpcClick = 170;

    /// <summary>
    /// The reach of the triggers the client sends while the player walks (20/2 near an NPC, 20/8 a door walked onto), px on each axis. The
    /// server holds the goal of the player's last walk, and the client walks at most 40 cells of 20 px each way from where it stands
    /// (CellDistances::kReach), so the player stands within 820 px (40 cells plus the cell it stands in) of that goal; twice that covers a
    /// walk sent while the last one was still under way, as <see cref="TravelOptions.WalkReachPixels"/> does.
    /// </summary>
    public const int Walk = 2 * (40 + 1) * 20; // twice 40 cells plus one, of 20 px each

    /// <summary>
    /// The reach of a door clicked (20/4), px on each axis. The client clicks a door anywhere in its view, which grows with its window and
    /// its zoom (each of the ten steps out adds a twentieth of the window on each axis), so no screen size bounds it; this bound is kept
    /// independent of the window and generous: a 4K window (3840 px) zoomed all the way out shows 5760 px, so a door in it is at most
    /// 2880 px from the player in its middle, who stands within 820 px of the walk goal the server holds.
    /// </summary>
    public const int DoorClick = 4096; // px: 2880 + 820 = 3700, rounded up to a power of two for margin

    /// <summary>The size of an event walk record of an NPC, bytes.</summary>
    private const int WalkSize = 92;

    /// <summary>The offset of the point count in an event walk record.</summary>
    private const int WalkCount = 3;

    /// <summary>The offset of the first point (x then y, 32-bit each) in an event walk record.</summary>
    private const int WalkPoints = 12;

    /// <summary>The most points of one event walk.</summary>
    private const int MostWalkPoints = 10;

    /// <summary>Builds the box of the places an NPC can be: where it is placed, its path, its event walks and the area it roams.</summary>
    /// <param name="npc">The NPC.</param>
    /// <returns>The box.</returns>
    public static TriggerReach Of(EveNpc npc)
    {
        var box = new TriggerReach(npc.X, npc.Y, npc.X, npc.Y);
        foreach (var point in npc.Path)
            box = box.With(point.X, point.Y);
        foreach (var walk in npc.Items.Where(w => w.Used != 0 && w.Raw.Length >= WalkSize))
        {
            for (var k = 0; k < Math.Min((int)walk.Raw[WalkCount], MostWalkPoints); k++)
            {
                var at = walk.Raw.AsSpan(WalkPoints + k * 8); // 8 bytes per point
                box = box.With(BinaryPrimitives.ReadInt32LittleEndian(at), BinaryPrimitives.ReadInt32LittleEndian(at[4..]));
            }
        }
        if (World.NpcWander.Roams(npc))
            box = box.With(npc.X - npc.Unknown688, npc.Y - npc.Unknown692).With(npc.X + npc.Unknown688, npc.Y + npc.Unknown692);
        return box;
    }

    /// <summary>Builds the box of a door's trigger area.</summary>
    /// <param name="door">The door.</param>
    /// <returns>The box.</returns>
    public static TriggerReach Of(EveDoor door)
    {
        var area = door.Area;
        return new TriggerReach(area.Left, area.Top, area.Left + area.Width, area.Top + area.Height);
    }

    /// <summary>Grows the box to hold a point.</summary>
    /// <param name="x">The x, px.</param>
    /// <param name="y">The y, px.</param>
    /// <returns>The grown box.</returns>
    private TriggerReach With(int x, int y) => new(Math.Min(Left, x), Math.Min(Top, y), Math.Max(Right, x), Math.Max(Bottom, y));

    /// <summary>Tells whether a spot is within reach of the box.</summary>
    /// <param name="x">The spot's x, px.</param>
    /// <param name="y">The spot's y, px.</param>
    /// <param name="reachX">The reach on the x axis, px.</param>
    /// <param name="reachY">The reach on the y axis, px.</param>
    /// <returns>True when the spot is at most the reach away from the box on each axis.</returns>
    public bool Reaches(int x, int y, int reachX, int reachY) =>
        x >= Left - reachX && x <= Right + reachX && y >= Top - reachY && y <= Bottom + reachY;
}
