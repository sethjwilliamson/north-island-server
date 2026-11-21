using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Messages.World;

namespace NorthIsland.Server.Features.Houses;

/// <summary>The Big and Small Gas Storage pieces, which take Gas from the bag as fuel.</summary>
/// <param name="items">The item data, for names.</param>
/// <param name="tents">The house and furniture handlers, for the house entry event.</param>
/// <param name="logger">The logger.</param>
public sealed class GasHandlers(
    ItemData items,
    TentHandlers tents,
    ILogger<GasHandlers> logger)
{
    /// <summary>The item ids of the Gas and of the Big and Small Gas Storage.</summary>
    public const ushort Gas = 47009, BigStorage = 39122, SmallStorage = 39123; // 47009 Gas, 39122 Big Gas Storage, 39123 Gas Storage

    /// <summary>The most gas the Big and the Small Gas Storage hold.</summary>
    public const int BigCapacity = 6000, SmallCapacity = 250;

    /// <summary>Hooks the house entry event so the owner is told the stored gas.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>
    /// Handles the order that moves marked Gas stacks from the bag into a storage: <c>62/62</c> for the big one, <c>62/63</c> for the small one.
    /// </summary>
    /// <param name="session">The owner's session.</param>
    /// <param name="body">The bag slots of the marked stacks, one byte each.</param>
    /// <param name="tank">1 for the Big Gas Storage, 2 for the Small Gas Storage.</param>
    internal ValueTask Deposit(GameSession session, ReadOnlyMemory<byte> body, byte tank)
    {
        var c = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || c is null || session.Account is null || TentHandlers.HouseMapOf(session.Account.LoginId) != c.MapId)
            return ValueTask.CompletedTask;
        var piece = tank == 1 ? BigStorage : SmallStorage;
        var capacity = tank == 1 ? BigCapacity : SmallCapacity;
        int fuel = tank == 1 ? c.GasCan : c.GasSmall;
        var stored = 0;
        if (c.Furniture.Exists(f => f.ItemId == piece && !f.Retained))
        {
            foreach (var slot in body.Span.ToArray().Distinct())
            {
                var stack = c.Bag.Find(b => b.Slot == slot && b.ItemId == Gas);
                if (stack is null || fuel + stored >= capacity)
                    continue;
                if (BagLocks.LockedBy(session, slot) is { } locked)
                {
                    logger.LogWarning("[{Session}] {Name}: gas from bag slot {Slot}: it is {Why}", session.Id, c.Name, slot, locked);
                    continue;
                }
                var put = Math.Min((int)stack.Count, capacity - fuel - stored);
                stack.Count -= (byte)put;
                if (stack.Count == 0)
                    c.Bag.Remove(stack);
                session.Send(ItemMessages.BagSlotDecrease(stack.Slot, (byte)put));
                stored += put;
            }
        }
        if (stored == 0)
        {
            session.Send(HouseMessages.GasRefused(tank));
            return ValueTask.CompletedTask;
        }
        fuel += stored;
        if (tank == 1)
            c.GasCan = (ushort)fuel;
        else
            c.GasSmall = (ushort)fuel;
        session.Send(HouseMessages.GasState(tank, (ushort)fuel, finished: true));
        logger.LogInformation(
            "[{Session}] {Name} stores {Count} {Item} in the {Tank} ({Fuel}/{Capacity})",
            session.Id,
            c.Name,
            stored,
            items[Gas]?.Name.Trim(),
            tank == 1 ? "Big Gas Storage" : "Gas Storage",
            fuel,
            capacity);
        return ValueTask.CompletedTask;
    }

    /// <summary>Tells the owner the gas stored in their tanks when they enter their house.</summary>
    /// <param name="session">The session that entered the house.</param>
    /// <param name="owner">The house owner's session, or null when the owner is not online.</param>
    private static void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner != session || session.ActiveCharacter is not { } c)
            return;
        if (c.GasCan > 0)
            session.Send(HouseMessages.GasState(1, c.GasCan, finished: false));
        if (c.GasSmall > 0)
            session.Send(HouseMessages.GasState(2, c.GasSmall, finished: false)); // tank
    }
}
