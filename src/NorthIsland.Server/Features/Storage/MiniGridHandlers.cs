using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Storage;

/// <summary>Serves the Item Bag, a second bag of 25 cells (5 x 5) unlocked by the Space Contract.</summary>
/// <param name="items">The item records, for item sizes and flags.</param>
/// <param name="space">The Space Contract rules.</param>
/// <param name="logger">The logger.</param>
public sealed class MiniGridHandlers(
    ItemData items,
    SpaceContract space,
    ILogger<MiniGridHandlers> logger)
{
    /// <summary>The number of cells in the Item Bag (25) and the number of cells in one row (5).</summary>
    public const int Cells = 25, Rows = 5;

    /// <summary>The item flag (32) that keeps an item out of the Item Bag.</summary>
    public const ushort NoStoreFlag = 32;

    /// <summary>Converts a stored stack to the entry of a bag packet.</summary>
    /// <param name="b">The stored stack.</param>
    /// <returns>The packet entry.</returns>
    private static BagEntry EntryOf(BagItem b) => new()
    {
        Slot = b.Slot,
        ItemId = b.ItemId,
        Count = b.Count,
        Wear = b.Wear,
        Insert = b.Insert,
        FusionLevel = b.Fusion,
        PoseItemId = b.Pose,
        BoxItemId = b.BoxItem,
        UsedAmount = b.Used,
        ExtraAmount = b.Extra,
    };

    /// <summary>
    /// Opens the Item Bag: records it as open for the character on any map (<see cref="OpenServices"/>), sends whether its cells are unlocked,
    /// the stored stacks, and the open command.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    public void Open(GameSession session, Character c)
    {
        OpenServices.Open(session, c, ServiceWindow.ItemBag);
        session.Send(new PacketWriter(Action23.Code, Action23.SpaceContract).U8(space.Has(c, SpaceContract.Stock) ? (byte)1 : (byte)2).ToArray());
        var w = new PacketWriter(Action68.Code, Action68.List);
        foreach (var b in c.MiniGrid.OrderBy(b => b.Slot))
            EntryOf(b).WriteTo(w);
        session.Send(w.ToArray());
        session.Send(new PacketWriter(Action68.Code, Action68.Open).ToArray());
        logger.LogInformation("[{Session}] {Name}: Item Bag ({Count} stack(s))", session.Id, c.Name, c.MiniGrid.Count);
    }

    /// <summary>
    /// Carries out an Item Bag order and echoes it to the client when it was done, or logs it as refused. Needs the Item Bag open for the
    /// character, on any map, out of battle (<see cref="OpenServices"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: u8 source, u8 destination, u8 count.</param>
    /// <param name="kind">The sub code of the order: take to the bag, move inside the Item Bag, or put in.</param>
    internal ValueTask Order(GameSession session, ReadOnlyMemory<byte> body, byte kind)
    {
        var c = session.ActiveCharacter;
        if (session.State != SessionState.InWorld || c is null || body.Length < 3)
            return ValueTask.CompletedTask;
        if (!OpenServices.IsOpen(session, c, ServiceWindow.ItemBag))
        {
            if (session.ProblemLog.TryTake(logger, session.Id)) // a client can repeat a refused order at will
            {
                logger.LogWarning(
                    "[{Session}] {Name}: Item Bag order {Kind} refused, no Item Bag open (opened on map {WindowMap}, in battle {Battle})",
                    session.Id,
                    c.Name,
                    kind,
                    OpenServices.MapOf(session, ServiceWindow.ItemBag),
                    session.Battle is not null);
            }
            return ValueTask.CompletedTask;
        }
        var (src, dst, count) = (body.Span[0], body.Span[1], body.Span[2]);
        if (count == 0)
            return ValueTask.CompletedTask;
        var done = kind switch
        {
            Action68.ToGrid => ToGrid(session, c, src, dst, count), // put bag items into the Item Bag
            Action68.ToBag => ToBag(c, src, dst, count), // take Item Bag items out into the bag
            _ => Inside(c, src, dst, count), // any other kind: move items inside the Item Bag
        };
        if (done)
            session.Send(new PacketWriter(Action68.Code, kind).U8(src).U8(dst).U8(count).ToArray());
        else if (session.ProblemLog.TryTake(logger, session.Id))
            logger.LogInformation("[{Session}] {Name}: Item Bag order {Kind} {From}->{To} x{Count} refused", session.Id, c.Name, kind, src, dst, count);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Puts part or all of a bag stack into an Item Bag cell. Needs the stock keeper part of the contract, an item without the no-store flag, and a
    /// stack not committed elsewhere (<see cref="BagLocks"/>).
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="bagSlot">The bag slot to take from.</param>
    /// <param name="cell">The Item Bag cell to put into, 1-based.</param>
    /// <param name="count">How many to move.</param>
    /// <returns>True when exactly count items moved.</returns>
    private bool ToGrid(
        GameSession session,
        Character c,
        byte bagSlot,
        byte cell,
        byte count)
    {
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (stack is null || count > stack.Count || cell is 0 or > Cells || !space.Has(c, SpaceContract.Stock) || BagLocks.IsLocked(session, bagSlot)
            || (items[stack.ItemId]?.Flags & NoStoreFlag) != 0
            || GridStore.Place(items, c.MiniGrid, stack, cell, count, null, Rows, exact: true) is not { } placed
            || placed.Moved != count)
            return false;
        stack.Count -= count;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        return true;
    }

    /// <summary>Takes part or all of an Item Bag stack out into a bag slot.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="cell">The Item Bag cell to take from.</param>
    /// <param name="bagSlot">The bag slot to put into.</param>
    /// <param name="count">How many to move.</param>
    /// <returns>True when exactly count items moved.</returns>
    private bool ToBag(Character c, byte cell, byte bagSlot, byte count)
    {
        var stack = c.MiniGrid.Find(b => b.Slot == cell);
        if (stack is null
            || count > stack.Count
            || bagSlot is 0 or > BagItem.LastSlot
            || GridStore.Place(items, c.Bag, stack, bagSlot, count, null, Bag.Rows, exact: true) is not { } placed
            || placed.Moved != count)
            return false;
        stack.Count -= count;
        if (stack.Count == 0)
            c.MiniGrid.Remove(stack);
        return true;
    }

    /// <summary>Moves part or all of an Item Bag stack to another cell of the Item Bag.</summary>
    /// <param name="c">The player's character.</param>
    /// <param name="from">The cell to move from.</param>
    /// <param name="to">The cell to move to.</param>
    /// <param name="count">How many to move.</param>
    /// <returns>True when exactly count items moved.</returns>
    private bool Inside(Character c, byte from, byte to, byte count)
    {
        var stack = c.MiniGrid.Find(b => b.Slot == from);
        if (stack is null || count > stack.Count || from == to || to is 0 or > Cells)
            return false;
        var whole = count == stack.Count;
        if (GridStore.Place(items, c.MiniGrid, stack, to, count, whole ? stack : null, Rows, exact: true) is not { } placed || placed.Moved != count)
            return false;
        stack.Count -= count;
        if (stack.Count == 0)
            c.MiniGrid.Remove(stack);
        return true;
    }
}
