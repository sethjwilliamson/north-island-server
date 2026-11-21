namespace NorthIsland.Server.World;

/// <summary>
/// The server-side sort of a grid of stacks (bag, bank, mini grid, storeroom and boxes): merges part stacks and orders them
/// by item type and id.
/// </summary>
public static class GridSort
{
    /// <summary>
    /// Sorts a grid in place: merges part stacks of the same kind, then lays the stacks out from the first cell, the items of
    /// <paramref name="first"/> in that order and the others by Item.dat type, id and bigger stack first.
    /// </summary>
    /// <param name="grid">The stacks of the grid.</param>
    /// <param name="items">Item.dat, for the item sizes.</param>
    /// <param name="cells">The number of cells of the grid.</param>
    /// <param name="rows">The number of rows of the grid.</param>
    /// <param name="maxStack">The most items one stack holds.</param>
    /// <param name="keep">Marks stacks that stay where they are, or null.</param>
    /// <param name="box">True for a box grid where every stack is one cell and stacks, whatever its size in Item.dat.</param>
    /// <param name="first">The item ids that go before all others, or null.</param>
    /// <param name="locked">Cells the player locked against sorting (qol), or null: what lies there stays, and nothing is moved in.</param>
    /// <returns>True when the grid changed.</returns>
    public static bool Sort(
        List<BagItem> grid,
        ItemData items,
        int cells,
        int rows,
        int maxStack,
        Func<BagItem, bool>? keep = null,
        bool box = false,
        ushort[]? first = null,
        IReadOnlySet<int>? locked = null)
    {
        var fixedStacks = grid.Where(b => (locked?.Contains(b.Slot) ?? false)
            || (!box && ((keep?.Invoke(b) ?? false) || Bag.CellsOfStack(items, b, rows).Count > 1))).ToList();
        var covered = fixedStacks.SelectMany(b => box ? new List<int> { b.Slot } : Bag.CellsOfStack(items, b, rows)).ToHashSet();
        if (locked is not null)
            covered.UnionWith(locked);
        var loose = grid.Except(fixedStacks).OrderBy(b => b.Slot).ToList();
        var merged = new List<BagItem>();
        foreach (var b in loose)
        {
            var count = (int)b.Count;
            var stackable = Bag.IsStackable(items[b.ItemId]);
            if (box || stackable)
            {
                foreach (var into in merged.Where(m => GridStore.Stacks(m, b, stackable) && m.Count < maxStack))
                {
                    var moved = Math.Min(count, maxStack - into.Count);
                    into.Count += (byte)moved;
                    count -= moved;
                    if (count == 0)
                        break;
                }
            }
            if (count > 0)
                merged.Add(GridStore.Copy(b, b.Slot, (byte)count));
        }
        var ordered = merged.OrderBy(b => first is not null && Array.IndexOf(first, b.ItemId) is >= 0 and var at ? at : int.MaxValue)
            .ThenBy(b => items[b.ItemId]?.Type ?? 0)
            .ThenBy(b => b.ItemId)
            .ThenByDescending(b => b.Count)
            .ToList();
        var free = Enumerable.Range(1, cells).Where(c => !covered.Contains(c)).ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Slot = (byte)free[i];

        var changed = !loose.Select(b => (b.Slot, b.ItemId, b.Count)).SequenceEqual(ordered.Select(b => (b.Slot, b.ItemId, b.Count)));
        if (changed)
        {
            grid.Clear();
            grid.AddRange(fixedStacks);
            grid.AddRange(ordered);
        }
        return changed;
    }
}
