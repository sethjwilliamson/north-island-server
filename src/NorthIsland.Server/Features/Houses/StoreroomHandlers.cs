using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Houses;

/// <summary>
/// Handles the Storeroom, a second bag of 50 cells kept per player: showing it, and putting items in, moving them inside and taking them out,
/// at a Storeroom piece in the player's own house or through the Super Handbag.
/// </summary>
/// <param name="items">The item data, for item flags and sizes.</param>
/// <param name="tents">The tent handlers, whose house-entered event sends the list.</param>
/// <param name="logger">The logger.</param>
public sealed class StoreroomHandlers(
    ItemData items,
    TentHandlers tents,
    ILogger<StoreroomHandlers> logger)
{
    /// <summary>The item ids of the Storeroom furniture piece and of the Super Handbag, which opens the storeroom away from home.</summary>
    public const ushort Storeroom = 39030, SuperHandbag = 34106; // 39030 Storeroom, 34106 Super Handbag (unfinished)

    /// <summary>The number of cells in the storeroom.</summary>
    public const int Cells = 50;

    /// <summary>The item flag bit that keeps an item out of the storeroom.</summary>
    public const ushort NoStoreFlag = 32;

    /// <summary>Hooks the handler into the tent house-entered event.</summary>
    internal void Start()
    {
        tents.HouseEntered += OnHouseEntered;
    }

    /// <summary>Returns the active character of a session that stands in its own house.</summary>
    /// <param name="session">The player's session.</param>
    /// <returns>The character; null when the session is not in the world or not in its own house.</returns>
    private static Character? InOwnHouse(GameSession session)
    {
        var character = session.ActiveCharacter;
        return session.State == SessionState.InWorld && character is not null && session.Account is not null
            && TentHandlers.HouseMapOf(session.Account.LoginId) == character.MapId ? character : null;
    }

    /// <summary>Builds the bag entry the client is sent for a stack.</summary>
    /// <param name="b">The stack.</param>
    /// <param name="count">The count to show in the entry.</param>
    /// <returns>The entry.</returns>
    private static BagEntry EntryOf(BagItem b, byte count) => new()
    {
        Slot = b.Slot, ItemId = b.ItemId, Count = count, Wear = b.Wear, Insert = b.Insert, FusionLevel = b.Fusion, PoseItemId = b.Pose,
        BoxItemId = b.BoxItem, UsedAmount = b.Used, ExtraAmount = b.Extra,
    };

    /// <summary>Builds the packet that adds a stack to a storeroom cell (<c>62/11</c>).</summary>
    /// <param name="b">The stack as it sits in the storeroom; its slot is the cell.</param>
    /// <param name="count">The count that was added.</param>
    /// <returns>The encoded packet.</returns>
    private static byte[] Added(BagItem b, byte count)
    {
        var e = EntryOf(b, count);
        return new PacketWriter(Action62.Code, Action62.StoreAdd)
            .U16(e.ItemId).U8(e.Count).U8(e.Wear).U8(e.Unknown5).U8(e.Unknown6).I32(e.UsedAmount).U16(e.ExtraAmount).U8(e.Slot)
            .U16(e.Insert).F64(e.Expiry).U8(e.FusionLevel).U8(e.Unknown24).U16(e.PoseItemId).U16(e.BoxItemId).U8(0).ToArray();
    }

    /// <summary>Builds the packet that removes items from a storeroom cell (<c>62/13</c>).</summary>
    /// <param name="cell">The storeroom cell.</param>
    /// <param name="count">The count that was removed.</param>
    /// <returns>The encoded packet.</returns>
    private static byte[] Removed(byte cell, byte count) => new PacketWriter(Action62.Code, Action62.StoreRemove).U8(cell).U8(count).ToArray();

    /// <summary>Copies a stack into a new stack with another slot and count.</summary>
    /// <param name="b">The stack to copy.</param>
    /// <param name="slot">The slot or cell of the copy.</param>
    /// <param name="count">The count of the copy.</param>
    /// <returns>The new stack.</returns>
    private static BagItem Copy(BagItem b, byte slot, byte count) => GridStore.Copy(b, slot, count);

    /// <summary>Tries to place part of a stack into a cell of a grid, either merged into a stack there or in a free area of the grid.</summary>
    /// <param name="grid">The grid to place into: the storeroom or a copy of the bag.</param>
    /// <param name="from">The stack to take the items from.</param>
    /// <param name="cell">The cell to place at.</param>
    /// <param name="count">The count to place.</param>
    /// <param name="ignore">A stack to treat as not present, such as the one being moved.</param>
    /// <returns>The stack placed into and the count moved; null when it does not fit.</returns>
    private (BagItem Stack, byte Moved)? Place(
        List<BagItem> grid,
        BagItem from,
        byte cell,
        byte count,
        BagItem? ignore = null) =>
            GridStore.Place(items, grid, from, cell, count, ignore, Bag.Rows);

    /// <summary>
    /// Handles a storeroom order: the packet holds the source cell, the target cell, the count and the index of the Storeroom piece. It works at a
    /// Storeroom piece in the player's own house or with a Super Handbag in the bag.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    /// <param name="kind">The order: 7 puts a bag stack into the storeroom, 8 moves inside it, anything else takes from it into the bag.</param>
    internal ValueTask Order(GameSession session, ReadOnlyMemory<byte> body, byte kind)
    {
        if (body.Length < 5 || session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || session.Account is null)
            return ValueTask.CompletedTask;
        var (src, dst, count) = (body.Span[0], body.Span[1], body.Span[2]);
        var furn = (ushort)(body.Span[3] | body.Span[4] << 8);
        var atPiece = InOwnHouse(session) is not null && c.Furniture.Exists(f => f.Index == furn && f.ItemId == Storeroom);
        if (!(atPiece || HasHandbag(session, c)) || count == 0 || src is 0 or > Cells || dst is 0 or > Cells)
            return ValueTask.CompletedTask;
        switch (kind)
        {
            case 7: // 62/7: put a bag stack into the storeroom
                Put(session, c, src, dst, count);
                break;
            case 8: // 62/8: move a stack inside the storeroom
                Move(session, c, src, dst, count);
                break;
            default: // 62/9: take a stack from the storeroom into the bag
                Take(session, c, src, dst, count);
                break;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Returns whether the character carries a Super Handbag and is not in a battle.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's active character.</param>
    /// <returns>True when the handbag can be used.</returns>
    private static bool HasHandbag(GameSession session, Character c) => session.Battle is null && c.Bag.Exists(b => b.ItemId == SuperHandbag);

    /// <summary>Handles the double click on the Super Handbag (<c>62/50</c>): sends the storeroom list and shows the storeroom form.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body after the action and sub code.</param>
    internal static ValueTask OnHandbag(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || !HasHandbag(session, c))
            return ValueTask.CompletedTask;
        var piece = c.Furniture.Find(f => f.ItemId == Storeroom);
        SendList(session, c, piece?.Index ?? 0);
        session.Send(new PacketWriter(Action62.Code, Action62.Handbag).ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends the storeroom contents to the player (<c>62/7</c>).</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's active character.</param>
    /// <param name="furn">The index of the Storeroom piece the list belongs to; 0 when there is none.</param>
    private static void SendList(GameSession session, Character c, ushort furn)
    {
        var w = new PacketWriter(Action62.Code, Action62.StoreList);
        foreach (var s in c.Storeroom.OrderBy(s => s.Slot))
            EntryOf(s, s.Count).WriteTo(w);
        session.Send(w.U16(furn).ToArray());
    }

    /// <summary>
    /// Puts a bag stack, or part of it, into a storeroom cell; a locked bag slot (<see cref="BagLocks"/>: in a trade, a stall, a house shelf,
    /// the pet express or the vehicle being ridden) and flagged items stay out.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's active character.</param>
    /// <param name="bagSlot">The bag slot to take from.</param>
    /// <param name="cell">The storeroom cell to put into.</param>
    /// <param name="count">The count to put.</param>
    private void Put(
        GameSession session,
        Character c,
        byte bagSlot,
        byte cell,
        byte count)
    {
        var stack = c.Bag.Find(b => b.Slot == bagSlot);
        if (stack is null || (items[stack.ItemId]?.Flags & NoStoreFlag) != 0)
            return;
        if (BagLocks.LockedBy(session, bagSlot) is { } locked)
        {
            logger.LogWarning("[{Session}] {Name}: storeroom put of bag slot {Bag}: it is {Why}", session.Id, c.Name, bagSlot, locked);
            return;
        }
        var want = (byte)Math.Min(count, stack.Count);
        if (Place(c.Storeroom, stack, cell, want) is not { } placed)
        {
            logger.LogInformation("[{Session}] {Name}: storeroom put of bag slot {Bag} into cell {Cell} refused", session.Id, c.Name, bagSlot, cell);
            return;
        }
        stack.Count -= placed.Moved;
        if (stack.Count == 0)
            c.Bag.Remove(stack);
        session.Send(ItemMessages.BagSlotDecrease(bagSlot, placed.Moved));
        session.Send(Added(placed.Stack, placed.Moved));
    }

    /// <summary>
    /// Takes a storeroom stack, or part of it, into the bag, at the wanted cell or else wherever it fits; a locked bag stack (<see cref="BagLocks"/>)
    /// is never added onto.
    /// </summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's active character.</param>
    /// <param name="cell">The storeroom cell to take from.</param>
    /// <param name="bagCell">The bag cell to prefer.</param>
    /// <param name="count">The count to take.</param>
    private void Take(
        GameSession session,
        Character c,
        byte cell,
        byte bagCell,
        byte count)
    {
        var stack = c.Storeroom.Find(s => s.Slot == cell);
        if (stack is null)
            return;
        var want = (byte)Math.Min(count, stack.Count);
        var trial = c.Bag.Select(b => Copy(b, b.Slot, b.Count)).ToList();
        var placed = BagLocks.IsLocked(session, bagCell) ? null : Place(trial, stack, bagCell, want);
        if (placed is null)
        {
            var anywhere = Bag.FreeSlot(trial, items, stack.ItemId);
            if (anywhere != 0)
                placed = Place(trial, stack, anywhere, want);
        }
        if (placed is null)
        {
            logger.LogInformation("[{Session}] {Name}: no room in the bag for storeroom cell {Cell}", session.Id, c.Name, cell);
            return;
        }
        var target = placed.Value.Stack.Slot;
        var real = Place(c.Bag, stack, target, want)!.Value;
        session.Send(ItemMessages.BagAdd(EntryOf(real.Stack, real.Moved)));
        stack.Count -= real.Moved;
        if (stack.Count == 0)
            c.Storeroom.Remove(stack);
        session.Send(Removed(cell, real.Moved));
    }

    /// <summary>Moves a storeroom stack, or part of it, to another storeroom cell.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's active character.</param>
    /// <param name="from">The cell to move from.</param>
    /// <param name="to">The cell to move to.</param>
    /// <param name="count">The count to move.</param>
    private void Move(
        GameSession session,
        Character c,
        byte from,
        byte to,
        byte count)
    {
        var stack = c.Storeroom.Find(s => s.Slot == from);
        if (stack is null || from == to)
            return;
        var want = (byte)Math.Min(count, stack.Count);
        var whole = want == stack.Count;
        if (Place(c.Storeroom, stack, to, want, whole ? stack : null) is not { } placed)
            return;
        stack.Count -= placed.Moved;
        if (stack.Count == 0)
            c.Storeroom.Remove(stack);
        session.Send(Removed(from, placed.Moved));
        session.Send(Added(placed.Stack, placed.Moved));
    }

    /// <summary>Sends the owner the storeroom list when they enter their house, if they have a Storeroom piece and items in it.</summary>
    /// <param name="session">The session of the player who entered.</param>
    /// <param name="owner">The session of the house's owner; null when the owner is not online.</param>
    private void OnHouseEntered(GameSession session, GameSession? owner)
    {
        if (owner != session || session.ActiveCharacter is not { } c)
            return;
        var piece = c.Furniture.Find(f => f.ItemId == Storeroom);
        if (piece is null || c.Storeroom.Count == 0)
            return;
        SendList(session, c, piece.Index);
    }
}
