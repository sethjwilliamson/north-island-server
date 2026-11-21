using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The floor and wallpaper of a player's house: laying a floor or wallpaper item from the bag replaces the old one.</summary>
/// <param name="items">The item data, for names.</param>
/// <param name="world">The world state, to find the sessions in a house.</param>
/// <param name="tents">The house and furniture handlers, for house owners and the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class HouseDecorHandlers(
    ItemData items,
    WorldState world,
    TentHandlers tents,
    ILogger<HouseDecorHandlers> logger)
{
    /// <summary>The item ids of the plain floor and the plain wallpaper.</summary>
    public const ushort DefaultFloor = 39062, DefaultWall = 39063; // 39062 Wood Floor, 39063 Pillar

    /// <summary>Whether an item is a floor item.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>True for the floor items.</returns>
    public static bool IsFloor(ushort id) =>
        id is (>= 39033 and <= 39036) // 39033 Tile, 39036 Tile
            or 39062 // Wood Floor
            or 39067 // Tile
            or 39068 // Tile
            or 39131 // Tile
            or 39133 // Tatami
            or 39151 // Tatami
            or 39177 // Night Sky Tile
            or 39178 // Snow Ground Tile
            or (>= 39189 and <= 39191) // 39189 Tile, 39191 Love Tile
            or 39214; // Snowy Tile

    /// <summary>Whether an item is a wallpaper item.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>True for the wallpaper items.</returns>
    public static bool IsWallpaper(ushort id) =>
        id is (>= 39041 and <= 39044) // 39041 Wallpaper, 39044 Wallpaper
            or 39064 // Wallpaper
            or 39128 // Wallpaper
            or 39130 // Wallpaper
            or 39136 // Wallpaper
            or 39138 // Wallpaper
            or 39140 // Wallpaper
            or 39142 // Wallpaper
            or 39144 // Wallpaper
            or 39169 // Wallpaper
            or 39171 // Wallpaper
            or 39174 // Sky Wallpaper
            or 39176 // Snowflake Wallpaper
            or 39181 // Wallpaper
            or 39193 // Wallpaper
            or 39195 // Wallpaper
            or 39197 // Wallpaper
            or 39199 // Wallpaper
            or 39213; // Snowy Wallpaper

    /// <summary>Hooks the house entry event so everybody who enters is shown the floor and wallpaper.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>The active character of a session that is in the world and stands in their own house.</summary>
    /// <param name="session">The session.</param>
    /// <returns>The character, or null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Builds the packet <c>62/14</c> that sets the floor.</summary>
    /// <param name="id">The floor item id.</param>
    /// <returns>The packet.</returns>
    private static byte[] FloorPacket(ushort id) => new PacketWriter(Action62.Code, Action62.FloorSet).U16(id).ToArray();

    /// <summary>Builds the packet <c>62/15</c> that sets the wallpaper.</summary>
    /// <param name="id">The wallpaper item id.</param>
    /// <returns>The packet.</returns>
    private static byte[] WallPacket(ushort id) => new PacketWriter(Action62.Code, Action62.WallSet).U16(id).ToArray();

    /// <summary>Handles <c>62/10</c> (floor) and <c>62/11</c> (wallpaper): lays the item in a bag slot as the house floor or wallpaper.</summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The bag slot.</param>
    /// <param name="floor">True for a floor, false for wallpaper.</param>
    internal ValueTask Drop(GameSession session, ReadOnlyMemory<byte> body, bool floor)
    {
        if (InOwnHouse(session) is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        var stack = c.Bag.Find(b => b.Slot == body.Span[0]);
        if (stack is null || !(floor ? IsFloor(stack.ItemId) : IsWallpaper(stack.ItemId)))
            return ValueTask.CompletedTask;
        if (BagLocks.LockedBy(session, stack.Slot) is { } locked)
        {
            logger.LogWarning("[{Session}] {Name}: {Kind} from bag slot {Slot}: it is {Why}", session.Id, c.Name, floor ? "floor" : "wallpaper",
                stack.Slot, locked);
            return ValueTask.CompletedTask;
        }
        var old = floor ? c.HouseFloor : c.HouseWall;
        var plain = floor ? old is 0 or DefaultFloor : old is 0 or DefaultWall;
        if (old == stack.ItemId)
            return ValueTask.CompletedTask;
        if (!plain)
        {
            var trial = c.Bag.Where(b => b != stack || b.Count > 1)
                .Select(b => new BagItem { Slot = b.Slot, ItemId = b.ItemId, Count = b == stack ? (byte)(b.Count - 1) : b.Count, Wear = b.Wear })
                .ToList();
            if (Bag.Add(trial, items, old, 1).Sum(x => x.Added) != 1)
            {
                logger.LogInformation("[{Session}] {Name}: no room in the bag for the old {Kind}", session.Id, c.Name, floor ? "floor" : "wallpaper");
                return ValueTask.CompletedTask;
            }
        }
        var id = stack.ItemId;
        stack.Count--;
        session.Send(ItemMessages.BagSlotDecrease(stack.Slot, 1));
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        if (floor)
            c.HouseFloor = id;
        else
            c.HouseWall = id;
        if (!plain)
            foreach (var (back, added) in Bag.Add(c.Bag, items, old, 1))
                session.Send(ItemMessages.BagAdd(new BagEntry { Slot = back.Slot, ItemId = back.ItemId, Count = added, Wear = back.Wear }));
        var packet = floor ? FloorPacket(id) : WallPacket(id);
        foreach (var s in world.OnMap(c.MapId).Append(session).Distinct())
            s.Send(packet);
        logger.LogInformation("[{Session}] {Name} lays {Item} as the house {Kind}", session.Id, c.Name, items[id]?.Name.Trim(), floor ? "floor" : "wallpaper");
        return ValueTask.CompletedTask;
    }

    /// <summary>Shows the floor and wallpaper of the house to the player who entered.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        var host = owner?.ActiveCharacter ?? tents.OwnerOfHouse(session.ActiveCharacter?.MapId ?? 0)?.Character;
        if (host is null)
            return;
        if (host.HouseFloor != 0)
            session.Send(FloorPacket(host.HouseFloor));
        if (host.HouseWall != 0)
            session.Send(WallPacket(host.HouseWall));
    }
}
