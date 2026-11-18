namespace NorthIsland.Server.World;

/// <summary>Helpers for stacks in a grid: comparing, copying and placing.</summary>
public static class GridStore
{
    /// <summary>Whether two stacks are the same kind and can merge: every field except slot and count is equal.</summary>
    /// <param name="a">The first stack.</param>
    /// <param name="b">The second stack.</param>
    /// <returns>True when they are the same kind.</returns>
    public static bool Same(BagItem a, BagItem b) =>
        a.ItemId == b.ItemId
            && a.Wear == b.Wear
            && a.Insert == b.Insert
            && a.Fusion == b.Fusion
            && a.Pose == b.Pose
            && a.Used == b.Used
            && a.Extra == b.Extra
            && a.BoxItem == b.BoxItem;

    /// <summary>
    /// Whether items of a stack can be added onto another stack, the way the bag merges them (a bag move, <see cref="Bag.Add"/>, and the client's
    /// CheckGridItemFit): a stackable item merges with any stack of the same item id, whatever its wear, box or other fields; an item that does not
    /// stack only with a stack of the same kind (<see cref="Same"/>).
    /// </summary>
    /// <param name="a">The stack added onto.</param>
    /// <param name="b">The stack the items come from.</param>
    /// <param name="stackable">Whether the item stacks (<see cref="Bag.IsStackable"/>).</param>
    /// <returns>True when they merge.</returns>
    public static bool Stacks(BagItem a, BagItem b, bool stackable) => stackable ? a.ItemId == b.ItemId : Same(a, b);

    /// <summary>Makes a copy of a stack with another slot and count.</summary>
    /// <param name="b">The stack to copy.</param>
    /// <param name="slot">The slot of the copy.</param>
    /// <param name="count">The count of the copy.</param>
    /// <returns>The new stack, not yet in any grid.</returns>
    public static BagItem Copy(BagItem b, byte slot, byte count) => new()
    {
        Slot = slot,
        ItemId = b.ItemId,
        Count = count,
        Wear = b.Wear,
        Insert = b.Insert,
        Fusion = b.Fusion,
        Pose = b.Pose,
        Used = b.Used,
        Extra = b.Extra,
        BoxItem = b.BoxItem,
    };

    /// <summary>Puts items of a stack into a cell of a grid: onto a stack of the same kind there, or as a new stack when the item fits.</summary>
    /// <param name="items">Item.dat, for the item sizes.</param>
    /// <param name="grid">The stacks of the target grid.</param>
    /// <param name="from">The stack the items come from; it is not changed.</param>
    /// <param name="cell">The target cell.</param>
    /// <param name="count">How many to move; an item that does not stack moves 1.</param>
    /// <param name="ignore">A stack left out when looking at the grid, such as the source, or null.</param>
    /// <param name="rows">The number of rows of the grid.</param>
    /// <param name="exact">When true, refuses instead of moving fewer than count onto an existing stack.</param>
    /// <returns>The stack that received the items and how many moved, or null when they cannot be placed there.</returns>
    public static (BagItem Stack, byte Moved)? Place(
        ItemData items,
        List<BagItem> grid,
        BagItem from,
        byte cell,
        byte count,
        BagItem? ignore,
        int rows,
        bool exact = false)
    {
        var stackable = Bag.IsStackable(items[from.ItemId]);
        var moved = (byte)(stackable ? count : 1);
        var at = grid.Find(g => g.Slot == cell && !ReferenceEquals(g, ignore));
        if (at is not null)
        {
            if (!stackable || !Stacks(at, from, stackable) || at.Count >= Bag.MaxStack)
                return null;
            if (exact && moved > Bag.MaxStack - at.Count)
                return null;
            moved = (byte)Math.Min(moved, Bag.MaxStack - at.Count);
            at.Count += moved;
            return (at, moved);
        }
        if (!Bag.Fits(grid, items, from.ItemId, cell, ignore, rows))
            return null;
        var stack = Copy(from, cell, moved);
        grid.Add(stack);
        return (stack, moved);
    }
}
