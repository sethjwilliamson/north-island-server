using Microsoft.Extensions.Logging;
using NorthIsland.Protocol.IO;
using NorthIsland.Protocol.Messages.Items;
using NorthIsland.Protocol.Opcodes;

namespace NorthIsland.Server.Features.Items;

/// <summary>The bag sort action: sorts one of the player's item grids in place and sends it again.</summary>
public static class SortHandlers
{
    /// <summary>The grid numbers a sort request can name.</summary>
    public const byte Bag = 1, Bank = 2, ItemBag = 3, Storeroom = 4, Picnic = 5, ExpBag = 6, FuelBank = 7;

    /// <summary>The results of a sort request: sorted, already in order, busy, or not at the place of the grid.</summary>
    public const byte Sorted = 0, InOrder = 1, Busy = 2, NotHere = 3;

    /// <summary>Handles the sort action: sub code 0 asks if sorting is offered, 1 sorts the grid in the body.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="body">The packet body: the sub code, then for sub code 1 the grid number.</param>
    /// <param name="items">Item definitions.</param>
    /// <param name="bagFirst">The item ids the bag sort puts first.</param>
    /// <param name="logger">Logger.</param>
    internal static ValueTask Handle(
        GameSession session,
        ReadOnlyMemory<byte> body,
        ItemData items,
        ushort[] bagFirst,
        ILogger logger)
    {
        if (session.State != SessionState.InWorld || session.ActiveCharacter is not { } c || body.Length < 1)
            return ValueTask.CompletedTask;
        if (body.Span[0] == 0)
        {
            session.Send(new PacketWriter(Action23.Code, Action23.QolSort).U8(0).ToArray());
            return ValueTask.CompletedTask;
        }
        if (body.Span[0] != 1 || body.Length < 2)
            return ValueTask.CompletedTask;
        var grid = body.Span[1];
        var result = Sort(session, c, grid, items, bagFirst);
        session.Send(new PacketWriter(Action23.Code, Action23.QolSort).U8(2).U8(grid).U8(result).ToArray());
        logger.LogInformation("[{Session}] {Name}: sort grid {Grid}: {Result}", session.Id, c.Name, grid, result);
        return ValueTask.CompletedTask;
    }

    /// <summary>Sorts one grid and sends the new order.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="grid">The grid number.</param>
    /// <param name="items">Item definitions.</param>
    /// <param name="bagFirst">The item ids the bag sort puts first.</param>
    /// <returns>The result code: sorted, already in order, busy (battle, stall or trade open) or not at the place of the grid.</returns>
    private static byte Sort(
        GameSession session,
        Character c,
        byte grid,
        ItemData items,
        ushort[] bagFirst)
    {
        if (session.Battle is not null || session.Stalls.Any || session.Trades.Items is not null)
            return Busy;
        switch (grid)
        {
            case Bag: // bag: sort, then clear the client's bag and send it again even when nothing moved, so a sort also mends a client bag that drifted
            {
                var sorted = GridSort.Sort(c.Bag, items, BagItem.LastSlot, World.Bag.Rows, World.Bag.MaxStack,
                    b => b.Slot == session.Vehicle?.BagSlot || (c.PuppetBagSlot != 0 && b.Slot == c.PuppetBagSlot), first: bagFirst);
                session.Send(new PacketWriter(Action23.Code, Action23.QolSort).U8(1).U8(Bag).ToArray());
                if (c.Bag.Count > 0)
                    session.Send(ItemMessages.BagList(c.Bag.OrderBy(b => b.Slot).Select(Entry)));
                return sorted ? Sorted : InOrder;
            }
            case Bank: // bank: sort and send its items
                if (!GridSort.Sort(c.Bank, items, BagItem.LastSlot, World.Bag.Rows, World.Bag.MaxStack))
                    return InOrder;
                BankHandlers.SendItems(session, c);
                return Sorted;
            case ItemBag: // item bag: sort and send its list
            {
                if (!GridSort.Sort(c.MiniGrid, items, MiniGridHandlers.Cells, MiniGridHandlers.Rows, World.Bag.MaxStack))
                    return InOrder;
                var w = new PacketWriter(Action68.Code, Action68.List);
                foreach (var b in c.MiniGrid.OrderBy(b => b.Slot))
                    Entry(b).WriteTo(w);
                session.Send(w.ToArray());
                return Sorted;
            }
            case Storeroom: // storeroom: only at the player's own house
            {
                if (session.Account is null || TentHandlers.HouseMapOf(session.Account.LoginId) != c.MapId)
                    return NotHere;
                if (!GridSort.Sort(c.Storeroom, items, StoreroomHandlers.Cells, World.Bag.Rows, World.Bag.MaxStack))
                    return InOrder;
                var w = new PacketWriter(Action62.Code, Action62.StoreList);
                foreach (var b in c.Storeroom.OrderBy(b => b.Slot))
                    Entry(b).WriteTo(w);
                session.Send(w.U16(c.Furniture.Find(f => f.ItemId == StoreroomHandlers.Storeroom)?.Index ?? 0).ToArray());
                return Sorted;
            }
            case Picnic: // picnic box slots
            case ExpBag: // experience bag slots
                return SortBox(session, grid == Picnic ? c.PicnicSlots : c.ExpBagSlots, grid == Picnic, items);
            case FuelBank: // fuel bank
                return SortFuel(session, c, items);
            default: // unknown grid: reported as already in order
                return InOrder;
        }
    }

    /// <summary>Sorts the slots of a portable box and sends them again.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="slots">The slots of the box.</param>
    /// <param name="picnic">True for the picnic box, false for the experience bag.</param>
    /// <param name="items">Item definitions.</param>
    /// <returns>The result code.</returns>
    private static byte SortBox(GameSession session, List<BoxSlot> slots, bool picnic, ItemData items)
    {
        var grid = slots.Select(s => new BagItem { Slot = s.Cell, ItemId = s.ItemId, Count = s.Count }).ToList();
        if (!GridSort.Sort(grid, items, picnic ? PortableBoxHandlers.PicnicCells : PortableBoxHandlers.ExpCells, 1, PortableBoxHandlers.MaxStack, box: true))
            return InOrder;
        var before = slots.Select(s => s.Cell).ToList();
        slots.Clear();
        slots.AddRange(grid.OrderBy(b => b.Slot).Select(b => new BoxSlot { Cell = b.Slot, ItemId = b.ItemId, Count = b.Count }));
        foreach (var cell in before.Union(slots.Select(s => s.Cell)).Order())
            session.Send(PortableBoxMessages.Removed(picnic, cell));
        foreach (var s in slots)
            session.Send(PortableBoxMessages.Added(picnic, s.Cell, s.ItemId, s.Count));
        return Sorted;
    }

    /// <summary>Sorts the rows of the fuel bank and sends them again.</summary>
    /// <param name="session">The player's session.</param>
    /// <param name="c">The player's character.</param>
    /// <param name="items">Item definitions.</param>
    /// <returns>The result code.</returns>
    private static byte SortFuel(GameSession session, Character c, ItemData items)
    {
        var grid = c.FuelTank.Where(r => r.Count > 0)
            .Select(r => new BagItem { Slot = r.Row, ItemId = r.ItemId, Count = (byte)Math.Min(r.Count, FuelBankHandlers.RowMax) })
            .ToList();
        if (!GridSort.Sort(grid, items, FuelBankHandlers.Rows, 1, FuelBankHandlers.RowMax, box: true))
            return InOrder;
        for (var row = (byte)1; row <= FuelBankHandlers.Rows; row++)
        {
            var now = grid.Find(b => b.Slot == row);
            c.FuelTank.RemoveAll(r => r.Row == row);
            if (now is not null)
                c.FuelTank.Add(new FuelRow { Row = row, ItemId = now.ItemId, Count = now.Count });
            session.Send(new PacketWriter(Action23.Code, Action23.FuelBankNotice).U8(1).U8(row).U16(now?.ItemId ?? 0).U8(now?.Count ?? 0).ToArray());
        }
        return Sorted;
    }

    /// <summary>The packet entry of a bag item.</summary>
    /// <param name="b">The item.</param>
    /// <returns>The entry.</returns>
    private static BagEntry Entry(BagItem b) => new()
    {
        Slot = b.Slot, ItemId = b.ItemId, Count = b.Count, Wear = b.Wear, Insert = b.Insert, FusionLevel = b.Fusion, PoseItemId = b.Pose, BoxItemId = b.BoxItem,
        UsedAmount = b.Used, ExtraAmount = b.Extra,
    };
}
