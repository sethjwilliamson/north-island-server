using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Travel;

/// <summary>Serves the GPS items: record the spot a player stands on as a mark, teleport to a mark, and upgrade a GPS to the next tier.</summary>
/// <param name="items">The item records.</param>
/// <param name="world">The online sessions and maps.</param>
/// <param name="travel">The teleport rule, for the maps a mark may be made on and a teleport may leave or reach.</param>
/// <param name="logger">The logger.</param>
public sealed class GpsHandlers(
    ItemData items,
    WorldState world,
    TravelRules travel,
    ILogger<GpsHandlers> logger)
{
    /// <summary>The item ids of the three GPS tiers (one, two and three marks) and of the crystal that upgrades a GPS.</summary>
    public const ushort Normal = 34006, // Normal GPS
        Advance = 34195, // Advance GPS
        Smart = 34373, // Smart GPS
        Crystal = 37257; // Memory Crystal

    /// <summary>Gives the number of marks a GPS item holds.</summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>1, 2 or 3, and 0 when the item is not a GPS.</returns>
    public static int Capacity(ushort itemId) => itemId switch
    {
        Normal => 1, // Normal GPS holds one mark
        Advance => 2, // Advance GPS holds two marks
        Smart => 3, // Smart GPS holds three marks
        _ => 0 // not a GPS
    };

    /// <summary>Builds the <c>74/1</c> packet that fills one mark page.</summary>
    /// <param name="c">The character.</param>
    /// <param name="point">The mark number, 1-based.</param>
    /// <returns>The packet.</returns>
    private static byte[] MarkPacket(Character c, int point) =>
        new PacketWriter(Action74.Code, Action74.Mark)
            .U8((byte)point)
            .U16(c.GpsMarks[(point - 1) * 3])
            .U16(c.GpsMarks[(point - 1) * 3 + 1]) // x
            .U16(c.GpsMarks[(point - 1) * 3 + 2]) // y
            .ToArray();

    /// <summary>Sends every recorded mark of a character.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The character.</param>
    public static void SendMarks(GameSession session, Character c)
    {
        for (var i = 1; i <= 3; i++)
        {
            if (c.GpsMarks[(i - 1) * 3] != 0)
                session.Send(MarkPacket(c, i));
        }
    }

    /// <summary>Returns the active character when the session is in the world with an account.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The active character, or null when the request must be ignored.</returns>
    private static Character? Usable(GameSession session)
    {
        var c = session.ActiveCharacter;
        return session.State == SessionState.InWorld && c is not null && session.Account is not null ? c : null;
    }

    /// <summary>
    /// Records the player's position as a GPS mark (<c>74/1</c>). Refused in a battle, on a vehicle, and on a map no teleport may reach (a
    /// house, an instance copy, an event arena, the sea and the like, <see cref="TravelRules.ArrivalRefusal"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 bag slot of the GPS, u8 mark number.</param>
    internal ValueTask OnMark(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (Usable(session) is not { } c || body.Length < 2)
            return ValueTask.CompletedTask;
        var (slot, point) = (body.Span[0], body.Span[1]);
        var stack = c.Bag.Find(b => b.Slot == slot);
        if (stack is null || point < 1 || point > Capacity(stack.ItemId) || session.Battle is not null || session.Vehicle is not null
            || travel.ArrivalRefusal(c.MapId) is not null)
            return ValueTask.CompletedTask;
        c.GpsMarks[(point - 1) * 3] = c.MapId;
        c.GpsMarks[(point - 1) * 3 + 1] = c.X;
        c.GpsMarks[(point - 1) * 3 + 2] = c.Y;
        session.Send(MarkPacket(c, point));
        logger.LogInformation("[{Session}] {Name} marks GPS point {Point} on map {Map}", session.Id, c.Name, point, c.MapId);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Teleports the player to a mark (<c>74/2</c>) and shows the arrival to the others on the target map. Refused by the teleport rule (busy
    /// states, and a map that cannot be left or a mark on one that cannot be reached, as a mark made before the rule barred it), on a vehicle
    /// and in a team.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 bag slot of the GPS, u8 mark number.</param>
    internal ValueTask OnTeleport(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (Usable(session) is not { } c || body.Length < 2)
            return ValueTask.CompletedTask;
        var (slot, point) = (body.Span[0], body.Span[1]);
        var stack = c.Bag.Find(b => b.Slot == slot);
        var id = session.Account!.LoginId;
        if (stack is null || point < 1 || point > Capacity(stack.ItemId) || c.GpsMarks[(point - 1) * 3] == 0)
            return ValueTask.CompletedTask;
        var (map, x, y) = (c.GpsMarks[(point - 1) * 3], c.GpsMarks[(point - 1) * 3 + 1], c.GpsMarks[(point - 1) * 3 + 2]);
        var refusal = travel.TeleportRefusal(session, c, map)
            ?? (session.Vehicle is not null ? "on a vehicle" : world.Teams.LeaderOf(id) != 0 ? "in a team" : null);
        if (refusal is not null)
        {
            logger.LogInformation("[{Session}] {Name}: GPS teleport to map {Map} refused: {Why}", session.Id, c.Name, map, refusal);
            return ValueTask.CompletedTask;
        }
        logger.LogInformation("[{Session}] {Name} teleports by GPS to map {Map}", session.Id, c.Name, map);
        world.ChangeMap(session, new DoorWarp(map, x, y, 0));
        var arrived = new PacketWriter(Action74.Code, Action74.Teleport).I32(id).ToArray();
        foreach (var other in world.OnMap(map))
        {
            if (other != session)
                other.Send(arrived);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Turns a GPS into the next tier with a crystal (<c>74/4</c>), keeping its wear and fusion. The GPS and one crystal are used up.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 bag slot of the crystal, u8 bag slot of the GPS.</param>
    internal ValueTask OnUpgrade(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (Usable(session) is not { } c || body.Length < 2 || session.Battle is not null)
            return ValueTask.CompletedTask;
        var (crystalSlot, gpsSlot) = (body.Span[0], body.Span[1]);
        var crystal = c.Bag.Find(b => b.Slot == crystalSlot);
        var gps = c.Bag.Find(b => b.Slot == gpsSlot);
        if (crystal is null || crystal.ItemId != Crystal || gps is null || gps == crystal || session.Vehicle?.BagSlot == gpsSlot)
            return ValueTask.CompletedTask;
        var next = gps.ItemId switch
        {
            Normal => Advance, // Normal GPS upgrades to Advance
            Advance => Smart, // Advance GPS upgrades to Smart
            _ => (ushort)0 // any other item: no upgrade
        };
        if (next == 0 || items[next] is null)
            return ValueTask.CompletedTask;
        var (wear, insert, fusion) = (gps.Wear, gps.Insert, gps.Fusion);
        var slot = gps.Slot;
        if (gps.Count > 1)
        {
            // only one GPS of the stack is upgraded: the new one needs a free slot of its own
            var trial = Bag.Trial(c.Bag);
            Bag.TakeAt(trial, gps.Slot, 1);
            Bag.TakeAt(trial, crystal.Slot, 1);
            slot = Bag.FreeSlot(trial, items, next);
            if (slot == 0)
                return ValueTask.CompletedTask;
        }
        gps.Count--;
        if (gps.Count == 0)
            c.Bag.Remove(gps);
        session.Send(ItemMessages.BagSlotDecrease(gps.Slot, 1));
        crystal.Count--;
        if (crystal.Count == 0)
            c.Bag.Remove(crystal);
        session.Send(ItemMessages.BagSlotDecrease(crystal.Slot, 1));
        var made = new BagItem { Slot = slot, ItemId = next, Count = 1, Wear = wear, Insert = insert, Fusion = fusion };
        c.Bag.Add(made);
        session.Send(ItemMessages.BagAdd(new BagEntry { Slot = made.Slot, ItemId = next, Count = 1, Wear = wear, Insert = insert, FusionLevel = fusion }));
        logger.LogInformation("[{Session}] {Name} upgrades the GPS to {Item}", session.Id, c.Name, items[next]?.Name.Trim());
        return ValueTask.CompletedTask;
    }
}
